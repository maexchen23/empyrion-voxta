using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// Phase 2: item transfers between containers of the structure a player is in (+ its docked vessels).
    ///
    ///   Voxta → POST /transfer (dedi, HTTP thread) → queued → dedi main thread sends "VBQ:" to the player's
    ///   playfield server → PF main thread executes (Transfers.Move) → "VBA:" back → HTTP thread answers.
    ///
    /// Everything that touches game objects runs on the main thread of its process (Update event).
    /// </summary>
    public partial class BridgeMod
    {
        const string RequestPrefix = "VBQ:";
        const string ReplyPrefix = "VBA:";
        static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(10);

        // ----- dedi ------------------------------------------------------------------------------

        sealed class Pending
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string ResultJson;
        }

        sealed class DediPlayer
        {
            public int EntityId;
            public string Name, SteamId, Playfield;
            public bool Online;
        }

        static readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();
        static readonly Queue<(string playfield, byte[] data)> dediOutbox = new Queue<(string, byte[])>();
        static Dictionary<int, DediPlayer> dediPlayers = new Dictionary<int, DediPlayer>();
        static readonly object auditLock = new object();

        /// <summary>HTTP thread: POST /transfer.</summary>
        static (int, string) HandleTransfer(string body)
        {
            if (!config.TransfersEnabled)
                return (403, Error("Transfers are switched off on this server (TransfersEnabled=false)."));

            var f = Form.Parse(body);
            var item = f.Get("item");
            var to = f.Get("to");
            if (item == null || to == null) return (400, Error("'item' and 'to' are required."));

            var toPlayer = Transfers.IsPlayerSpec(to, out var toBag, out var toToolbarFlag);
            var toToolbar = toToolbarFlag && !toBag; // only an explicit "toolbar"/"hotbar"; "player" means the bag
            var fromPlayer = Transfers.IsPlayerSpec(f.Get("from"), out var useBag, out var useToolbar);
            if (toPlayer || fromPlayer)
            {
                if (toPlayer && fromPlayer) return (400, Error("Source and target are both the player's inventory."));
                if (legacy == null) return (503, Error("The player inventory can't be changed: API1 is not attached on this server."));
                DediPlayer who;
                string whyNot;
                lock (sync) who = PickPlayer(f.Get("player"), out whyNot);
                if (who == null) return (409, Error(whyNot));
                var (pc, pr) = toPlayer ? TransferToPlayer(who, f, toToolbar) : TransferFromPlayer(who, f, useBag, useToolbar);
                RefreshPlayerInfo(who.EntityId);
                Audit(who, item, f.Get("amount") ?? "1", f.Get("from") ?? "(any)", to, pr);
                return (pc, pr);
            }

            var (code, result, player) = ForwardToPlayfield("transfer", f.Get("player"),
                ("item", item), ("amount", f.Get("amount") ?? "1"), ("to", to), ("from", f.Get("from")),
                ("max", config.MaxItemsPerTransfer.ToString()));

            if (player != null)
                Audit(player, item, f.Get("amount") ?? "1", f.Get("from") ?? "(any)", to, result);
            return (code, result);
        }

        /// <summary>HTTP thread: POST /containers (structure=name): containers of one structure in transfer scope.</summary>
        static (int, string) HandleContainers(string body)
        {
            var f = Form.Parse(body);
            var (code, result, _) = ForwardToPlayfield("containers", f.Get("player"), ("structure", f.Get("structure")));
            return (code, result);
        }

        /// <summary>
        /// HTTP thread: send a request to the player's playfield server and block until it answers or times out.
        /// Adds player, playfield, ownership and range from the dedi's config.
        /// </summary>
        static (int code, string json, DediPlayer player) ForwardToPlayfield(string op, string steamId, params (string key, string value)[] fields)
        {
            DediPlayer player;
            string why;
            lock (sync) player = PickPlayer(steamId, out why);
            if (player == null) return (409, Error(why), null);

            var id = Guid.NewGuid().ToString("N");
            var p = new Pending();
            lock (sync) pending[id] = p;

            var all = new List<(string, string)>
            {
                ("id", id), ("op", op), ("player", player.EntityId.ToString()), ("playfield", player.Playfield),
                ("ownership", config.RequireOwnership ? "1" : "0"),
                ("range", config.TransferRangeMeters.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            all.AddRange(fields);
            var packet = Form.Encode(all.ToArray());
            lock (sync) dediOutbox.Enqueue((player.Playfield, Encoding.UTF8.GetBytes(RequestPrefix + packet)));

            var answered = p.Done.Wait(TransferTimeout);
            lock (sync) pending.Remove(id);
            if (!answered)
                return (504, Error($"The playfield server for {player.Playfield} did not answer in time; nothing is known to have happened."), player);
            return (200, p.ResultJson, player);
        }

        /// <summary>By SteamID, or the only player online. Caller holds <c>sync</c>.</summary>
        static DediPlayer PickPlayer(string steamId, out string why)
        {
            why = null;
            var online = dediPlayers.Values.Where(x => x.Online).ToList();
            if (!string.IsNullOrEmpty(steamId))
            {
                var p = online.FirstOrDefault(x => x.SteamId == steamId);
                if (p == null) why = $"The player with SteamID {steamId} is not online.";
                return p;
            }
            if (online.Count == 1) return online[0];
            why = online.Count == 0 ? "Nobody is online." : "Several players are online; the request must name one (player=SteamID).";
            return null;
        }

        /// <summary>Dedi main thread: send queued requests to playfield servers.</summary>
        static void FlushDediOutbox()
        {
            while (true)
            {
                (string playfield, byte[] data) next;
                lock (sync)
                {
                    if (dediOutbox.Count == 0) return;
                    next = dediOutbox.Dequeue();
                }
                if (!api.Network.SendToPlayfieldServer(ModName, next.playfield, next.data))
                    Log($"SendToPlayfieldServer({next.playfield}) returned false");
            }
        }

        /// <summary>Dedi: a "VBA:" reply from a playfield server.</summary>
        static void ReceiveReply(string payload)
        {
            var f = Form.Parse(payload);
            var id = f.Get("id");
            if (id == null) return;
            lock (sync)
            {
                if (!pending.TryGetValue(id, out var p)) return; // timed out already
                p.ResultJson = f.Get("result") ?? Error("empty reply");
                p.Done.Set();
            }
        }

        static void Audit(DediPlayer player, string item, string amount, string from, string to, string resultJson)
        {
            try
            {
                var line = string.Join("\t", DateTime.UtcNow.ToString("o"), player.SteamId, player.Name, player.Playfield,
                    "transfer", item, amount, from, to, resultJson.Replace('\t', ' ').Replace('\n', ' '));
                var path = Path.Combine(Path.GetDirectoryName(config.Path) ?? ".", "VoxtaBridge.audit.log");
                lock (auditLock) File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
            }
            catch (Exception e) { Log($"audit log write failed: {e.Message}"); }
        }

        static string Error(string message) => new Json().Obj().Prop("ok", false).Prop("error", message).EndObj().ToString();

        // ----- playfield server ------------------------------------------------------------------

        static readonly Queue<Dictionary<string, string>> pfInbox = new Queue<Dictionary<string, string>>();

        /// <summary>Network callback (any thread): queue for the main thread.</summary>
        static void OnDediPacket(string sender, string playfieldName, byte[] data)
        {
            try
            {
                var s = Encoding.UTF8.GetString(data);
                if (!s.StartsWith(RequestPrefix)) return;
                lock (pfInbox) pfInbox.Enqueue(Form.Parse(s.Substring(RequestPrefix.Length)));
            }
            catch (Exception e) { Log($"bad dedi packet: {e.Message}"); }
        }

        /// <summary>PF main thread: run queued transfers and answer the dedi.</summary>
        static void ProcessPfInbox()
        {
            while (true)
            {
                Dictionary<string, string> req;
                lock (pfInbox)
                {
                    if (pfInbox.Count == 0) return;
                    req = pfInbox.Dequeue();
                }
                string result;
                lastResolved = null;
                try
                {
                    switch (req.Get("op"))
                    {
                        case "containers": result = ExecuteContainers(req); break;
                        case "remove": result = ExecuteRemove(req); break;
                        case "put": result = ExecutePut(req); break;
                        case "probe": result = ExecuteProbe(req); break;
                        case "find": result = ExecuteFind(req); break;
                        case "craft": result = ExecuteCraft(req); break;
                        case "jump": result = ExecuteJump(req); break;
                        case "microjump": result = ExecuteMicroJump(req); break;
                        case "microjump-refund": result = ExecuteMicroJumpRefund(req); break;
                        case "marker-refresh": result = ExecuteMarkerRefresh(req); break;
                        case "pois-pos": result = ExecutePoisPos(req); break;
                        case "structure-info": result = ExecuteStructureInfo(req); break;
                        case "set-active": result = ExecuteSetActive(req); break;
                        default: result = ExecuteTransfer(req); break;
                    }
                }
                catch (Exception e)
                {
                    Log($"transfer failed: {e}");
                    result = Error($"Internal error: {e.Message}");
                }
                // every item feature names the structure it worked from (the LLM can troubleshoot)
                var op = req.Get("op");
                if (lastResolved != null && op != "set-active" && op != "structure-info" && op != "pois-pos")
                    result = WithWorkingFrom(result, lastResolved);
                var reply = ReplyPrefix + Form.Encode(("id", req.Get("id")), ("result", result));
                api.Network.SendToDedicatedServer(ModName, Encoding.UTF8.GetBytes(reply), req.Get("playfield") ?? "");
            }
        }

        static string ExecuteTransfer(Dictionary<string, string> req)
        {
            var pfName = req.Get("playfield");
            var pf = playfields.FirstOrDefault(x => x.Name == pfName);
            if (pf == null) return Error($"The playfield {pfName} is not loaded on this server process.");

            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("The player is not on this playfield right now.");

            var requireOwnership = req.Get("ownership") != "0";
            var range = ParseRange(req.Get("range"));
            var active = ResolveActive(pf, player, requireOwnership, range);
            var structure = active.Structure;
            var all = ActiveScope(pf, active, player, requireOwnership, range, includeRangeContainers: true, null);
            if (all.Count == 0) return Error(structure == null
                ? "No own ship or base of the player is aboard or within transfer range."
                : "The active structure and the own structures in range have no containers.");

            var targets = Transfers.Match(all, req.Get("to"));
            if (targets.Count == 0)
                return Error($"No container '{req.Get("to")}'. Containers: {string.Join(", ", all.Select(c => c.Label))}.");
            if (targets.Count > 1)
                return Error($"'{req.Get("to")}' matches several containers ({string.Join(", ", targets.Select(c => c.Label))}); name one.");
            var target = targets[0];

            List<ITransferContainer> sources;
            var from = req.Get("from");
            if (from == null) sources = all.Where(c => c != target).ToList();
            else
            {
                sources = Transfers.Match(all, from);
                if (sources.Count == 0)
                    return Error($"No container '{from}'. Containers: {string.Join(", ", all.Select(c => c.Label))}.");
            }

            var candidates = sources.Where(s => s != target).SelectMany(s => s.ItemIds()).Distinct().ToList();
            var itemId = Transfers.ResolveItem(req.Get("item"), candidates, ItemName,
                id => gameText?.DisplayName(ItemName(id)), out var itemError);
            if (itemId == 0) return Error(itemError);

            var internalName = ItemName(itemId);
            var display = gameText?.DisplayName(internalName) ?? internalName;
            var stackMode = Transfers.NeedsStackMode(itemId, sources, gameText?.IsToken(internalName) ?? false);

            var amountText = req.Get("amount") ?? "1";
            int amount;
            if (amountText.Equals("all", StringComparison.OrdinalIgnoreCase)) amount = Transfers.All;
            else if (!int.TryParse(amountText, out amount) || amount <= 0) return Error($"Bad amount '{amountText}'.");

            int.TryParse(req.Get("max"), out var max);
            if (max <= 0) max = 10000;

            var r = stackMode
                ? Transfers.MoveStacks(itemId, amount, target, sources, max, StackSizeOf)
                : Transfers.Move(itemId, amount, target, sources, max);
            Log($"transfer: player {player.Name} {display} x{amountText} -> {target.Label}: " +
                (r.Ok ? $"moved {r.Moved}" : $"failed: {r.Error}") + (r.Note != null ? $" ({r.Note})" : ""));

            var j = new Json().Obj().Prop("ok", r.Ok);
            if (!r.Ok) j.Prop("error", r.Error);
            j.Prop("item", display).Prop("itemId", itemId).Prop("moved", r.Moved)
                .Prop("requested", amount == Transfers.All ? "all" : amount.ToString())
                .Prop("to", target.Label).Prop("structure", structure?.Entity?.Name);
            j.Key("from").Arr();
            foreach (var (label, count) in r.Sources) j.Obj().Prop("container", label).Prop("count", count).EndObj();
            j.EndArr();
            if (r.Note != null) j.Prop("note", r.Note);
            if (r.Lost > 0)
            {
                j.Prop("lost", r.Lost);
                api.LogError($"[{ModName}] transfer LOST {r.Lost} x {display} (player {player.Name}, to {target.Label})");
            }
            return j.EndObj().ToString();
        }

        static int StackSizeOf(int itemId) => gameText?.StackSize(ItemName(itemId)) ?? Stacks.DefaultStackSize;

        /// <summary>Structure owned by the player, or by the player's faction.</summary>
        static bool Owns(IPlayer player, IStructure structure)
        {
            var owner = structure.Entity?.Faction ?? default(FactionData);
            if (owner.Group == FactionGroup.Player) return owner.Id == player.Id;
            if (owner.Group == FactionGroup.Faction)
                return player.FactionData.Group == FactionGroup.Faction && player.FactionData.Id == owner.Id;
            return false;
        }

        // ----- which structure is the player on? -------------------------------------------------

        const int AboardMarginBlocks = 3;

        /// <summary>
        /// The structure the player pilots/sits in (<c>IPlayer.CurrentStructure</c>), otherwise the smallest
        /// structure whose bounding box (+ a few blocks, so standing on the hull counts) contains the player.
        /// With <paramref name="requireOwnership"/> only the player's (or faction's) structures are considered
        /// for the fallback. <paramref name="how"/>: "pilot", "aboard", "standing" or null.
        /// </summary>
        static IStructure FindStructureFor(IPlayfield pf, IPlayer player, bool requireOwnership, out string how)
        {
            how = null;
            IStructure current = null;
            try { current = player.CurrentStructure; } catch (Exception e) { Log($"CurrentStructure failed: {e.Message}"); }
            // a structure the player doesn't own (a POI, a trader station, someone else's ship) is never "theirs", even when
            // the game reports the player inside it (walking into P3 made it the "current ship")
            if (current != null && requireOwnership && !Owns(player, current)) current = null;
            if (current != null)
            {
                bool pilot = false;
                try { pilot = player.IsPilot; } catch { }
                how = pilot ? "pilot" : "aboard"; // the game sets CurrentStructure while walking inside too
                return current;
            }

            IStructure best = null;
            long bestVolume = long.MaxValue;
            foreach (var s in StructuresOf(pf))
            {
                if (requireOwnership && !Owns(player, s)) continue;
                if (!Inside(s, player.Position, AboardMarginBlocks, out var volume)) continue;
                if (volume < bestVolume) { best = s; bestVolume = volume; }
            }
            if (best != null) how = "standing";
            return best;
        }

        /// <summary>The structure the game reports the player inside (CurrentStructure) when it isn't theirs, else null.</summary>
        static IStructure ForeignStructureFor(IPlayer player)
        {
            IStructure current = null;
            try { current = player.CurrentStructure; } catch { }
            return current != null && !Owns(player, current) ? current : null;
        }

        static IEnumerable<IStructure> StructuresOf(IPlayfield pf)
        {
            Dictionary<int, IEntity> entities = null;
            try { entities = pf.Entities; } catch { }
            if (entities == null) yield break;
            foreach (var e in entities.Values)
            {
                if (e == null) continue;
                if (e.Type != EntityType.BA && e.Type != EntityType.CV && e.Type != EntityType.SV && e.Type != EntityType.HV) continue;
                IStructure s = null;
                try { s = e.Structure; } catch { }
                if (s != null) yield return s;
            }
        }

        /// <summary>Is a world position inside the structure's block bounding box (+ margin)?</summary>
        static bool Inside(IStructure s, UnityEngine.Vector3 world, int margin, out long volume)
        {
            volume = long.MaxValue;
            try
            {
                // Block coordinates carry a +128 offset on y; MinPos/MaxPos don't (EmpyrionScripting adds it the
                // same way). Seen live: player at y 131 inside a CV spanning y 0..17.
                var local = s.GlobalToStructPos(world);
                var min = new VectorInt3(s.MinPos.x, s.MinPos.y + 128, s.MinPos.z);
                var max = new VectorInt3(s.MaxPos.x, s.MaxPos.y + 128, s.MaxPos.z);
                volume = (long)(max.x - min.x + 1) * (max.y - min.y + 1) * (max.z - min.z + 1);
                return local.x >= min.x - margin && local.x <= max.x + margin
                    && local.y >= min.y - margin && local.y <= max.y + margin
                    && local.z >= min.z - margin && local.z <= max.z + margin;
            }
            catch { return false; }
        }

        // ----- containers (snapshot + transfers share the labels) --------------------------------

        sealed class GameContainer : ITransferContainer
        {
            public IPlayfield Playfield;
            public IStructure Structure;
            public VectorInt3 Pos;
            public IContainer Box;
            public string Label { get; set; }
            public string Name { get; set; }
            public string Kind { get; set; }
            public string Vessel { get; set; }
            public bool IsCurrent { get; set; }

            public bool Locked
            {
                get
                {
                    try { return Playfield.IsStructureDeviceLocked(Structure.Id, Pos); }
                    catch { return true; } // unknown: be careful
                }
            }

            public int BlockType;
            public int MaxSlots => gameText?.ContainerSlots(ItemName(BlockType)) ?? 64;

            public List<InvStack> GetStacks() =>
                (Box.GetContent() ?? new List<ItemStack>()).Select(x => new InvStack(x.id, x.count, x.slotIdx, x.ammo, x.decay)).ToList();

            public void SetStacks(List<InvStack> stacks) =>
                Box.SetContent(stacks.Select(x => new ItemStack(x.Id, x.Count) { slotIdx = (byte)x.Slot, ammo = x.Ammo, decay = x.Decay }).ToList());

            public int Count(int itemId) => Box.GetTotalItems(itemId);
            public IEnumerable<int> ItemIds() => (Box.GetContent() ?? new List<ItemStack>()).Where(s => s.count > 0).Select(s => s.id).Distinct();
            public void Remove(int itemId, int count) => Box.RemoveItems(itemId, count);
            public void Add(int itemId, int count) => Box.AddItems(itemId, count);
        }

        static double ParseRange(string text) =>
            double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) && r >= 0 ? r : 0;

        /// <summary>A structure within transfer range of the player's structure (not in its docking group).</summary>
        sealed class InRange
        {
            public IStructure Structure;
            public double Distance;
            public int Containers;
        }

        /// <summary>
        /// Containers the player may use, with labels:
        ///  1. the docking group of the structure the player is in: if that structure is docked, its carrier is the
        ///     root (no chain docking in Empyrion), plus everything docked to the root;
        ///  2. with <paramref name="includeRangeContainers"/>: owned structures within <paramref name="rangeMeters"/>
        ///     (entity position to entity position, i.e. core to core) and their docking groups.
        /// Containers of the player's own structure have no suffix, all others "@ structure name".
        /// <paramref name="inRange"/> (optional) receives the structures in range, for listing.
        /// </summary>
        /// <summary>Optional profiling of a scope collection (find_item: is the range search a bottleneck?).</summary>
        sealed class ScopeTiming
        {
            public readonly System.Diagnostics.Stopwatch GroupContainers = new System.Diagnostics.Stopwatch();
            public readonly System.Diagnostics.Stopwatch RangeLookup = new System.Diagnostics.Stopwatch();
            public readonly System.Diagnostics.Stopwatch RangeContainers = new System.Diagnostics.Stopwatch();
            public int GroupStructures, RangeStructures, GroupContainerCount, RangeContainerCount, StructuresChecked;
            public readonly HashSet<int> RangeIds = new HashSet<int>();
        }

        /// <summary>One member of the player's docking group, for the "structures in reach" tree.</summary>
        sealed class GroupMember
        {
            public IStructure Structure;
            public string Name;
            public string Role; // carrier | docked | standalone
            public bool Aboard;
            public int Containers;
        }

        static List<ITransferContainer> CollectScope(IPlayfield pf, IStructure current, IPlayer player, bool requireOwnership,
            double rangeMeters, bool includeRangeContainers, List<InRange> inRange, List<GroupMember> group = null,
            ScopeTiming timing = null, bool playerOnCurrent = true, UnityEngine.Vector3? origin = null)
        {
            bool Allowed(IStructure x) => x != null && (!requireOwnership || player == null || Owns(player, x));

            var included = new HashSet<int>();
            var order = new List<IStructure>();
            void AddGroup(IStructure any)
            {
                var root = CarrierOf(pf, any) ?? any;
                foreach (var x in new[] { root }.Concat(DockedOf(root)))
                    if (Allowed(x) && included.Add(x.Id)) order.Add(x);
            }

            // on foot (no own structure under the player, e.g. inside a POI): no docking group, only own structures in
            // range of the player (the ship parked 80 m outside P3)
            if (current != null)
            {
                if (Allowed(current)) { included.Add(current.Id); order.Add(current); }
                AddGroup(current);
            }

            if (group != null && current != null)
            {
                var root = CarrierOf(pf, current) ?? current;
                var docked = DockedOf(root).Where(Allowed).ToList();
                var rootRole = docked.Count > 0 ? "carrier" : "standalone";
                foreach (var x in new[] { root }.Concat(docked))
                {
                    if (!Allowed(x)) continue;
                    group.Add(new GroupMember
                    {
                        Structure = x, Role = x.Id == root.Id ? rootRole : "docked",
                        Aboard = playerOnCurrent && x.Id == current.Id, Containers = CountContainers(x),
                    });
                }
            }

            var groupCount = order.Count;
            if (rangeMeters > 0 && (origin.HasValue || current?.Entity != null || player != null))
            {
                timing?.RangeLookup.Start();
                var from = origin ?? (current?.Entity != null ? current.Entity.Position : player.Position);
                foreach (var s in StructuresOf(pf))
                {
                    if (timing != null) timing.StructuresChecked++;
                    if (included.Contains(s.Id) || !Allowed(s) || s.Entity == null) continue;
                    var d = UnityEngine.Vector3.Distance(from, s.Entity.Position);
                    if (d > rangeMeters) continue;
                    inRange?.Add(new InRange { Structure = s, Distance = d, Containers = CountContainers(s) });
                    if (includeRangeContainers) AddGroup(s);
                }
                timing?.RangeLookup.Stop();
            }
            if (timing != null)
            {
                timing.GroupStructures = groupCount;
                timing.RangeStructures = order.Count - groupCount;
                foreach (var x in order.Skip(groupCount)) timing.RangeIds.Add(x.Id);
            }

            // Structure names for labels: always set; duplicates get "#id" so labels stay unique.
            var names = new Dictionary<int, string>();
            foreach (var g in order.GroupBy(x => (x.Entity?.Name ?? "").Trim().ToLowerInvariant()))
                foreach (var x in g)
                {
                    var n = string.IsNullOrWhiteSpace(x.Entity?.Name) ? $"structure {x.Id}" : x.Entity.Name.Trim();
                    names[x.Id] = g.Count() > 1 ? $"{n} #{x.Id}" : n;
                }
            if (group != null) foreach (var m in group) m.Name = names.TryGetValue(m.Structure.Id, out var gn) ? gn : m.Structure.Entity?.Name;

            var found = new List<GameContainer>();
            foreach (var s in order)
            {
                var isRange = timing != null && timing.RangeIds.Contains(s.Id);
                var sw = timing == null ? null : isRange ? timing.RangeContainers : timing.GroupContainers;
                sw?.Start();
                var before = found.Count;
                AddContainers(found, pf, s, names[s.Id]);
                for (int i = before; i < found.Count; i++) found[i].IsCurrent = current != null && s.Id == current.Id;
                sw?.Stop();
                if (timing != null)
                {
                    if (isRange) timing.RangeContainerCount += found.Count - before;
                    else timing.GroupContainerCount += found.Count - before;
                }
            }

            var labels = Transfers.MakeLabels(found.Select(c => (c.Name, c.Kind, c.Vessel)).ToList());
            for (int i = 0; i < found.Count; i++) found[i].Label = labels[i];
            return found.Cast<ITransferContainer>().ToList();
        }

        static IStructure CarrierOf(IPlayfield pf, IStructure s)
        {
            try
            {
                var parentId = s.Entity?.DockedTo ?? 0;
                if (parentId <= 0 || parentId == s.Id) return null;
                var entities = pf.Entities;
                return entities != null && entities.TryGetValue(parentId, out var e) ? e?.Structure : null;
            }
            catch { return null; }
        }

        static IEnumerable<IStructure> DockedOf(IStructure s)
        {
            List<IStructure> docked = null;
            try { docked = s.GetDockedVessels(); } catch (Exception e) { Log($"GetDockedVessels failed: {e.Message}"); }
            return docked?.Where(d => d != null) ?? Enumerable.Empty<IStructure>();
        }

        static int CountContainers(IStructure s)
        {
            int n = 0;
            foreach (var t in ContainerTypes)
                try { n += s.GetDevices(t)?.Count ?? 0; } catch { }
            return n;
        }

        /// <summary>PF: containers of one structure in the player's transfer scope (by structure name).</summary>
        static string ExecuteContainers(Dictionary<string, string> req)
        {
            var pfName = req.Get("playfield");
            var pf = playfields.FirstOrDefault(x => x.Name == pfName);
            if (pf == null) return Error($"The playfield {pfName} is not loaded on this server process.");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("The player is not on this playfield right now.");

            var requireOwnership = req.Get("ownership") != "0";
            var range = ParseRange(req.Get("range"));
            var active = ResolveActive(pf, player, requireOwnership, range);
            var current = active.Structure;
            var name = req.Get("structure");
            if (current == null && string.IsNullOrEmpty(name))
                return Error("No own ship or base of the player is aboard or within transfer range.");
            var all = ActiveScope(pf, active, player, requireOwnership, range, includeRangeContainers: true, null);
            var wanted = string.IsNullOrEmpty(name) ? all.Where(c => c.IsCurrent).ToList()
                : all.Where(c => string.Equals(c.Vessel, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (wanted.Count == 0)
            {
                var names = all.Select(c => c.Vessel).Distinct();
                return Error($"No structure '{name}' in transfer range. In range: {string.Join(", ", names)}.");
            }

            var j = new Json().Obj().Prop("ok", true).Prop("structure", name ?? current?.Entity?.Name);
            j.Key("containers").Arr();
            foreach (var tc in wanted) WriteContainer(j, (GameContainer)tc);
            return j.EndArr().EndObj().ToString();
        }

        static void WriteContainer(Json j, GameContainer c)
        {
            j.Obj().Prop("label", c.Label).Prop("type", c.Kind).Prop("name", c.Name);
            j.Prop("structure", c.Vessel);
            if (c.Locked) j.Prop("open", true); // someone has its inventory window open
            j.Vec("pos", c.Pos.x, c.Pos.y, c.Pos.z).Prop("volumeCapacity", c.Box.VolumeCapacity);
            j.Key("items").Arr();
            List<ItemStack> content = null;
            try { content = c.Box.GetContent(); } catch (Exception ex) { Log($"GetContent failed: {ex.Message}"); }
            if (content != null)
                foreach (var st in content)
                {
                    j.Obj();
                    ItemProps(j, st, withDescription: false);
                    j.EndObj();
                }
            j.EndArr().EndObj();
        }

        static void AddContainers(List<GameContainer> found, IPlayfield pf, IStructure s, string vessel)
        {
            foreach (var t in ContainerTypes)
            {
                IDevicePosList list;
                try { list = s.GetDevices(t); } catch (Exception ex) { Log($"GetDevices({t}) failed: {ex.Message}"); continue; }
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var pos = list.GetAt(i);
                    IContainer c = null;
                    try { c = s.GetDevice<IContainer>(pos); } catch (Exception ex) { Log($"GetDevice failed: {ex.Message}"); }
                    if (c == null) continue;
                    string customName = null;
                    int blockType = 0;
                    try
                    {
                        var block = s.GetBlock(pos);
                        customName = block?.CustomName;
                        if (block != null) block.Get(out blockType, out _, out _, out _);
                    }
                    catch { }
                    found.Add(new GameContainer
                    {
                        Playfield = pf, Structure = s, Pos = pos, Box = c,
                        Name = string.IsNullOrWhiteSpace(customName) ? null : customName,
                        Kind = t.ToString(), Vessel = vessel, BlockType = blockType,
                    });
                }
            }
        }
    }
}
