using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// Transfers with the player's own inventory (option A, overwrite the inventory).
    /// The playfield API can only read Bag/Toolbar, so writes go through legacy API1 on the dedi:
    ///   container → player: PF removes (counted) → Request_Player_AddItem → verify by re-reading → put back what's missing
    ///   player → container: Request_Player_GetInventory → reduce stacks → Request_Player_SetInventory → verify →
    ///                       PF puts into the target → leftovers back to the player with AddItem
    /// KNOWN RISK: SetInventory overwrites bag + toolbar. If the player changes their inventory in the moment between
    /// our read and our write, that change is overwritten (an item can be lost or duplicated). Documented in Outline.
    /// </summary>
    public partial class BridgeMod
    {
        // ----- API1 calls from HTTP threads, executed on the dedi main thread ------------------

        sealed class Api1Pending
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public CmdId Event;
            public object Data;
        }

        const int Api1SeqFirst = 30000, Api1SeqLast = 60000;
        static int api1Seq = Api1SeqFirst;
        static readonly Dictionary<ushort, Api1Pending> api1Pending = new Dictionary<ushort, Api1Pending>();
        static readonly Queue<(CmdId cmd, ushort seq, object payload)> api1Outbox = new Queue<(CmdId, ushort, object)>();

        static ushort NextApi1Seq()
        {
            lock (api1Pending)
            {
                api1Seq = api1Seq >= Api1SeqLast ? Api1SeqFirst : api1Seq + 1;
                return (ushort)api1Seq;
            }
        }

        /// <summary>HTTP thread. With <paramref name="expectReply"/> waits up to 5 s for the event with our seq.</summary>
        static (bool ok, CmdId evt, object data) Api1(CmdId cmd, object payload, bool expectReply)
        {
            if (legacy == null) return (false, CmdId.Event_Error, "API1 not attached");
            var seq = NextApi1Seq();
            Api1Pending p = null;
            if (expectReply)
            {
                p = new Api1Pending();
                lock (api1Pending) api1Pending[seq] = p;
            }
            lock (api1Outbox) api1Outbox.Enqueue((cmd, seq, payload));
            if (!expectReply) return (true, CmdId.Event_Ok, null);

            var answered = p.Done.Wait(TimeSpan.FromSeconds(5));
            lock (api1Pending) api1Pending.Remove(seq);
            if (!answered) return (false, CmdId.Event_Error, "timeout");
            return (p.Event != CmdId.Event_Error, p.Event, p.Data);
        }

        /// <summary>Dedi main thread.</summary>
        static void FlushApi1Outbox()
        {
            while (true)
            {
                (CmdId cmd, ushort seq, object payload) next;
                lock (api1Outbox)
                {
                    if (api1Outbox.Count == 0) return;
                    next = api1Outbox.Dequeue();
                }
                try { legacy?.Game_Request(next.cmd, next.seq, next.payload); }
                catch (Exception e) { Log($"API1 {next.cmd} failed: {e.Message}"); }
            }
        }

        /// <summary>From Game_Event: true if the event answers one of our waiting calls.</summary>
        static bool CompleteApi1(CmdId evt, ushort seq, object data)
        {
            if (seq < Api1SeqFirst) return false;
            lock (api1Pending)
            {
                if (!api1Pending.TryGetValue(seq, out var p)) return false;
                p.Event = evt;
                p.Data = data;
                p.Done.Set();
                return true;
            }
        }

        static Inventory GetInventory(int playerId, out string error)
        {
            var (ok, _, data) = Api1(CmdId.Request_Player_GetInventory, new Id(playerId), true);
            error = ok ? null : $"Could not read the player's inventory ({Describe(data)}).";
            return ok ? data as Inventory : null;
        }

        static string Describe(object data) => data is ErrorInfo ei ? ei.errorType.ToString() : data?.ToString() ?? "no answer";

        static List<InvStack> ToStacks(ItemStack[] stacks) =>
            (stacks ?? new ItemStack[0]).Select(s => new InvStack(s.id, s.count, s.slotIdx, s.ammo, s.decay)).ToList();

        static ItemStack[] FromStacks(List<InvStack> stacks) =>
            stacks.Select(s => new ItemStack(s.Id, s.Count) { slotIdx = (byte)s.Slot, ammo = s.Ammo, decay = s.Decay }).ToArray();

        static int CountInInventory(Inventory inv, int itemId) =>
            Transfers.CountIn(ToStacks(inv?.bag), itemId) + Transfers.CountIn(ToStacks(inv?.toolbelt), itemId);

        // ----- writing the player's inventory -------------------------------------------------------

        /// <summary>
        /// Places stacks (with their state) into the player's inventory by rewriting it: merge into existing stacks of
        /// the same kind first, then the next free slot; bag unless the toolbar is asked for, or one fixed slot.
        /// Re-reads to verify. Returns the stacks that did not get in (inventory full, slot taken, write refused).
        /// Same accepted risk as taking (see class comment).
        /// </summary>
        static double VolumeOf(int itemId) => gameText?.Volume(ItemName(itemId)) ?? 0;

        /// <summary>
        /// Ask for fresh Player_Info right away, so /status (and the character's next look at the inventory) shows the
        /// result of a transfer instead of the up-to-5-s-old cache. The answer lands in the normal Event_Player_Info handler.
        /// </summary>
        static void RefreshPlayerInfo(int playerId) => Api1(CmdId.Request_Player_Info, new Id(playerId), false);

        /// <summary>
        /// Re-reads the inventory (up to ~5 s) until the item count reaches <paramref name="expected"/>; returns the
        /// last count seen. The SetInventory reply is NOT used for this: it can still show the old inventory.
        /// </summary>
        static int WaitForCount(int playerId, int itemId, int before, int expected)
        {
            var last = before;
            for (int i = 0; i < 17; i++)
            {
                Thread.Sleep(300);
                var inv = GetInventory(playerId, out _);
                if (inv == null) continue;
                last = CountInInventory(inv, itemId);
                var (outcome, _) = Stacks.Classify(before, expected, last);
                if (outcome == Stacks.WriteOutcome.Done) return last;
            }
            return last;
        }

        /// <param name="enforceVolumeLimit">false when handing back the player's own items (they must always get them back).</param>
        static List<InvStack> GiveToPlayer(int playerId, List<InvStack> stacks, bool toToolbar, out string error,
            bool enforceVolumeLimit = true)
        {
            error = null;
            var all = stacks.ToList(); // returned in full whenever nothing gets written
            var inv = GetInventory(playerId, out error);
            if (inv == null) return all;
            var bag = ToStacks(inv.bag);
            var toolbar = ToStacks(inv.toolbelt);
            var countsBefore = stacks.Select(x => x.Id).Distinct().ToDictionary(id => id, id => CountInInventory(inv, id));

            var overLimit = new List<InvStack>();
            var limit = config?.PlayerVolumeLimit ?? 0;
            if (enforceVolumeLimit && limit > 0)
            {
                var current = Stacks.Volume(bag.Concat(toolbar), VolumeOf);
                var (fits, rest) = Stacks.CapByVolume(stacks, current, limit, VolumeOf);
                if (fits.Count == 0)
                {
                    error = $"The player's inventory is at {current:0} L, the server's limit for giving is {limit:0} L. " +
                            "The player has to make room or move items by hand.";
                    return all;
                }
                if (rest.Count > 0)
                    error = $"capped by the inventory volume limit ({limit:0} L; the player is at {current:0} L)";
                stacks = fits;
                overLimit = rest;
            }

            var target = toToolbar ? toolbar : bag;
            // The game ignores slot numbers on SetInventory and packs the stacks in list order (seen 2026-11-17),
            // so "next free slot" just means appended; no fixed slots.
            var left = Stacks.Place(target, stacks, toToolbar ? Stacks.ToolbarSlots : Stacks.BagSlots, StackSizeOf);
            left.AddRange(overLimit);
            var placed = Stacks.Total(stacks) - Stacks.Total(left) + Stacks.Total(overLimit);
            if (placed == 0)
            {
                error = $"The player's {(toToolbar ? "toolbar" : "bag")} has no free slot.";
                return all;
            }

            var (ok, _, data) = Api1(CmdId.Request_Player_SetInventory, new Inventory(playerId, FromStacks(toolbar), FromStacks(bag)), true);
            if (!ok) { error = $"The game refused to change the inventory ({Describe(data)})."; return all; }

            // Verify by re-reading (one item per transfer). Only what verifiably arrived counts as given.
            var itemId = stacks.First().Id;
            var before = countsBefore[itemId];
            var observed = WaitForCount(playerId, itemId, before, before + placed);
            var (outcome, applied) = Stacks.Classify(before, before + placed, observed);
            if (outcome == Stacks.WriteOutcome.NotApplied)
            {
                // one last look before handing everything back, so a slow write can't cause a duplicate
                Thread.Sleep(1000);
                var last = GetInventory(playerId, out _);
                if (last != null) (outcome, applied) = Stacks.Classify(before, before + placed, CountInInventory(last, itemId));
            }
            if (outcome != Stacks.WriteOutcome.Done)
            {
                var notArrived = placed - applied;
                error = outcome == Stacks.WriteOutcome.NotApplied
                    ? "The inventory write did not take effect."
                    : $"Only {applied} of {placed} verifiably arrived (the inventory changed meanwhile).";
                api.LogError($"[{ModName}] give: {error} item {itemId} before {before} expected {before + placed} counted {observed}");
                var first = stacks.First();
                if (notArrived > 0) left.Add(new InvStack(first.Id, notArrived, 0, first.Ammo, first.Decay));
            }
            return left;
        }

        // ----- the two directions (HTTP thread) --------------------------------------------------

        /// <summary>Container(s) → player inventory (bag, or appended to the toolbar).</summary>
        static (int, string) TransferToPlayer(DediPlayer player, Dictionary<string, string> f, bool toToolbar)
        {
            var (code, json, _) = ForwardToPlayfield("remove", player.SteamId,
                ("item", f.Get("item")), ("amount", f.Get("amount") ?? "1"), ("from", f.Get("from")),
                ("max", config.MaxItemsPerTransfer.ToString()));
            if (code != 200) return (code, json);
            var removed = JsonInt(json, "moved");
            if (removed <= 0) return (200, json); // refusal from the playfield server, already explained
            var stacks = Stacks.Decode(JsonString(json, "stacks"));

            var left = GiveToPlayer(player.EntityId, stacks, toToolbar, out var giveError);
            var given = Math.Max(0, Stacks.Total(stacks) - Stacks.Total(left));
            var notes = new List<string>();
            if (giveError != null) notes.Add(giveError);
            var lost = 0;
            if (left.Count > 0)
            {
                var back = PutBack(player, left, json, out var stillLeft);
                lost = Stacks.Total(stillLeft);
                notes.Add($"{Stacks.Total(left)} did not fit the player's inventory, {back} were put back into the container(s)");
                if (lost > 0) api.LogError($"[{ModName}] to-player transfer LOST {Stacks.Encode(stillLeft)} (player {player.Name})");
            }
            return (200, Result(given > 0, given > 0 ? null : (giveError ?? "Nothing arrived in the player's inventory."), json, given, lost,
                "player inventory", notes.Count > 0 ? string.Join("; ", notes) : null));
        }

        /// <summary>Player inventory → one container.</summary>
        static (int, string) TransferFromPlayer(DediPlayer player, Dictionary<string, string> f, bool useBag, bool useToolbar)
        {
            // 1. Target must exist, be unique and closed before we touch the inventory.
            var (code, probe, _) = ForwardToPlayfield("probe", player.SteamId, ("to", f.Get("to")));
            if (code != 200) return (code, probe);
            if (!JsonBool(probe, "ok")) return (200, probe);
            var target = JsonString(probe, "to");

            // 2. Read the inventory and pick the item.
            var inv = GetInventory(player.EntityId, out var err);
            if (inv == null) return (200, Error(err));
            var bag = ToStacks(inv.bag);
            var toolbar = ToStacks(inv.toolbelt);
            var choice = Transfers.ResolveChoice(f.Get("item"),
                ChoicesIn((useBag ? bag : new List<InvStack>()).Concat(useToolbar ? toolbar : new List<InvStack>())), out var itemError);
            if (choice == null) return (200, Error(itemError.Replace("source container(s)", "player's inventory")));
            var itemId = choice.Id;
            var name = ItemName(itemId);
            var display = choice.Display ?? name;

            var amountText = f.Get("amount") ?? "1";
            int amount;
            if (amountText.Equals("all", StringComparison.OrdinalIgnoreCase)) amount = int.MaxValue;
            else if (!int.TryParse(amountText, out amount) || amount <= 0) return (200, Error($"Bad amount '{amountText}'."));
            var notes = new List<string>();
            if (amount != int.MaxValue && amount > config.MaxItemsPerTransfer) notes.Add($"capped at {config.MaxItemsPerTransfer} per transfer");
            amount = Math.Min(amount, config.MaxItemsPerTransfer);

            // 3. Take the stacks (with their state) and write the inventory back (the risky step, see class comment).
            var countBefore = CountInInventory(inv, itemId);
            var taken = Transfers.TakeFromInventory(bag, toolbar, itemId, amount, useBag, useToolbar, choice.Token);
            if (taken.Count == 0) return (200, Error($"No {display} in the player's {(useBag && useToolbar ? "inventory" : useBag ? "bag" : "toolbar")}."));
            var (setOk, _, setData) = Api1(CmdId.Request_Player_SetInventory, new Inventory(player.EntityId, FromStacks(toolbar), FromStacks(bag)), true);
            if (!setOk) return (200, Error($"The game refused to change the inventory ({Describe(setData)}); nothing was moved."));

            // Verify by re-reading (the SetInventory reply can be stale). Only what verifiably left the inventory goes on.
            var takenTotal = Stacks.Total(taken);
            var observed = WaitForCount(player.EntityId, itemId, countBefore, countBefore - takenTotal);
            var (outcome, removed) = Stacks.Classify(countBefore, countBefore - takenTotal, observed);
            if (outcome == Stacks.WriteOutcome.NotApplied)
            {
                Thread.Sleep(1000);
                var last = GetInventory(player.EntityId, out _);
                if (last != null) (outcome, removed) = Stacks.Classify(countBefore, countBefore - takenTotal, CountInInventory(last, itemId));
            }
            if (outcome == Stacks.WriteOutcome.NotApplied) return (200, Error("The inventory did not change; nothing was moved."));
            if (outcome == Stacks.WriteOutcome.Partial)
            {
                notes.Add($"only {removed} of {takenTotal} verifiably left the inventory (it changed meanwhile)");
                api.LogError($"[{ModName}] take: partial, item {itemId} before {countBefore} expected {countBefore - takenTotal} counted {observed}");
                taken = Stacks.Take(taken, itemId, removed);
            }

            // 4. Into the container; whatever does not fit goes back into the bag (with its state).
            var (putCode, putJson, _) = ForwardToPlayfield("put", player.SteamId, ("to", target), ("stacks", Stacks.Encode(taken)));
            var added = putCode == 200 ? JsonInt(putJson, "added") : 0;
            var left = putCode == 200 ? Stacks.Decode(JsonString(putJson, "left")) : taken;
            var lost = 0;
            if (left.Count > 0)
            {
                var stillLeft = GiveToPlayer(player.EntityId, left, toToolbar: false, out _, enforceVolumeLimit: false);
                lost = Stacks.Total(stillLeft);
                notes.Add($"'{target}' took only {added}, {Stacks.Total(left) - lost} went back to the player's bag");
                if (lost > 0) api.LogError($"[{ModName}] from-player transfer LOST {Stacks.Encode(stillLeft)} (player {player.Name})");
            }

            var j = new Json().Obj().Prop("ok", added > 0);
            if (added <= 0) j.Prop("error", "Nothing went into the container.");
            j.Prop("item", display).Prop("itemId", itemId).Prop("moved", added)
                .Prop("requested", amountText).Prop("to", target);
            j.Key("from").Arr().Obj().Prop("container", "player inventory").Prop("count", added).EndObj().EndArr();
            if (notes.Count > 0) j.Prop("note", string.Join("; ", notes));
            if (lost > 0) j.Prop("lost", lost);
            return (200, j.EndObj().ToString());
        }

        /// <summary>Puts stacks back into the containers they came from (PF op "put" with the source labels).</summary>
        static int PutBack(DediPlayer player, List<InvStack> stacks, string removeJson, out List<InvStack> stillLeft)
        {
            var sources = JsonSourceLabels(removeJson);
            var (code, json, _) = ForwardToPlayfield("put", player.SteamId,
                ("to", string.Join("\n", sources)), ("stacks", Stacks.Encode(stacks)), ("multi", "1"));
            stillLeft = code == 200 ? Stacks.Decode(JsonString(json, "left")) : stacks;
            return code == 200 ? JsonInt(json, "added") : 0;
        }

        static string Result(bool ok, string error, string removeJson, int moved, int lost, string to = null, string note = null)
        {
            var j = new Json().Obj().Prop("ok", ok);
            if (error != null) j.Prop("error", error);
            j.Prop("item", JsonString(removeJson, "item")).Prop("itemId", JsonInt(removeJson, "itemId"))
                .Prop("moved", moved).Prop("requested", JsonString(removeJson, "requested")).Prop("to", to ?? "player inventory");
            var from = System.Text.RegularExpressions.Regex.Match(removeJson ?? "", "\"from\":(\\[[^\\]]*\\])");
            j.Key("from").Raw(from.Success ? from.Groups[1].Value : "[]"); // per-source counts from the playfield server
            var notes = new[] { JsonString(removeJson, "note"), note }.Where(x => !string.IsNullOrEmpty(x)).ToList();
            if (notes.Count > 0) j.Prop("note", string.Join("; ", notes));
            if (lost > 0) j.Prop("lost", lost);
            return j.EndObj().ToString();
        }

        // Tiny readers for our own flat JSON (no parser in the mod).
        static int JsonInt(string json, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"" + key + "\":(-?\\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        static bool JsonBool(string json, string key) => (json ?? "").Contains("\"" + key + "\":true");

        static string JsonString(string json, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"" + key + "\":\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value) : null;
        }

        static List<string> JsonSourceLabels(string json) =>
            System.Text.RegularExpressions.Regex.Matches(json ?? "", "\"container\":\"((?:[^\"\\\\]|\\\\.)*)\"")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value)).ToList();

        // ----- playfield server ops: remove / put / probe -----------------------------------------

        /// <summary>Common start of PF ops: playfield, player, structure, containers in scope.</summary>
        static string ResolveScope(Dictionary<string, string> req, out List<ITransferContainer> all, out IStructure structure)
        {
            all = null;
            structure = null;
            var pfName = req.Get("playfield");
            var pf = playfields.FirstOrDefault(x => x.Name == pfName);
            if (pf == null) return Error($"The playfield {pfName} is not loaded on this server process.");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("The player is not on this playfield right now.");
            var requireOwnership = req.Get("ownership") != "0";
            var active = ResolveActive(pf, player, requireOwnership, ParseRange(req.Get("range")));
            structure = active.Structure;
            all = ActiveScope(pf, active, player, requireOwnership, ParseRange(req.Get("range")), includeRangeContainers: true, null);
            if (structure == null && all.Count == 0)
                return Error("No own ship or base of the player is aboard or within transfer range.");
            return null;
        }

        static string ExecuteRemove(Dictionary<string, string> req)
        {
            var error = ResolveScope(req, out var all, out var structure);
            if (error != null) return error;
            var from = req.Get("from");
            var sources = from == null ? all : Transfers.Match(all, from);
            if (sources.Count == 0) return Error($"No container '{from}'. Containers: {string.Join(", ", all.Select(c => c.Label))}.");

            var choice = Transfers.ResolveChoice(req.Get("item"), ChoicesIn(sources), out var itemError);
            if (choice == null) return Error(itemError);
            var itemId = choice.Id;
            var display = choice.Display ?? ItemName(itemId);
            var stackMode = choice.Token != null || Transfers.NeedsStackMode(itemId, sources, gameText?.IsToken(ItemName(itemId)) ?? false);

            var amountText = req.Get("amount") ?? "1";
            int amount;
            if (amountText.Equals("all", StringComparison.OrdinalIgnoreCase)) amount = Transfers.All;
            else if (!int.TryParse(amountText, out amount) || amount <= 0) return Error($"Bad amount '{amountText}'.");
            int.TryParse(req.Get("max"), out var max);
            if (max <= 0) max = 10000;

            var r = Transfers.RemoveOnly(itemId, amount, sources, max, stackMode, choice.Token);
            Log($"remove for player: {display} x{amountText}: " + (r.Ok ? $"took {r.Moved}" : $"failed: {r.Error}"));
            var j = new Json().Obj().Prop("ok", r.Ok);
            if (!r.Ok) j.Prop("error", r.Error);
            j.Prop("item", display).Prop("itemId", itemId).Prop("moved", r.Moved).Prop("requested", amountText).Prop("structure", structure?.Entity?.Name)
                .Prop("stacks", Stacks.Encode(r.Stacks));
            j.Key("from").Arr();
            foreach (var (label, count) in r.Sources) j.Obj().Prop("container", label).Prop("count", count).EndObj();
            j.EndArr();
            if (r.Note != null) j.Prop("note", r.Note);
            return j.EndObj().ToString();
        }

        /// <summary>Put a counted amount into one target (or, with multi=1, into several labels in order).</summary>
        static string ExecutePut(Dictionary<string, string> req)
        {
            var error = ResolveScope(req, out var all, out _);
            if (error != null) return error;
            var stacks = Stacks.Decode(req.Get("stacks"));
            if (stacks.Count == 0) return Error("Bad put request.");

            List<ITransferContainer> targets;
            if (req.Get("multi") == "1")
                targets = (req.Get("to") ?? "").Split('\n').SelectMany(l => all.Where(c => c.Label == l)).Concat(all).Distinct().ToList();
            else
            {
                targets = Transfers.Match(all, req.Get("to"));
                if (targets.Count != 1) return Error($"Target '{req.Get("to")}' is not exactly one container.");
                if (targets[0].Locked) return Error(Transfers.OpenMessage(new[] { targets[0].Label }, "target"));
            }
            var left = Transfers.PutStacksInto(stacks, targets, StackSizeOf);
            var added = Stacks.Total(stacks) - Stacks.Total(left);
            Log($"put: {Stacks.Encode(stacks)} -> {added} placed");
            return new Json().Obj().Prop("ok", added > 0).Prop("added", added).Prop("left", Stacks.Encode(left))
                .Prop("to", targets.FirstOrDefault()?.Label).EndObj().ToString();
        }

        /// <summary>Check a target before the player's inventory is touched.</summary>
        static string ExecuteProbe(Dictionary<string, string> req)
        {
            var error = ResolveScope(req, out var all, out _);
            if (error != null) return error;
            var targets = Transfers.Match(all, req.Get("to"));
            if (targets.Count == 0) return Error($"No container '{req.Get("to")}'. Containers: {string.Join(", ", all.Select(c => c.Label))}.");
            if (targets.Count > 1) return Error($"'{req.Get("to")}' matches several containers ({string.Join(", ", targets.Select(c => c.Label))}); name one.");
            if (targets[0].Locked) return Error(Transfers.OpenMessage(new[] { targets[0].Label }, "target"));
            return new Json().Obj().Prop("ok", true).Prop("to", targets[0].Label).EndObj().ToString();
        }
    }
}
