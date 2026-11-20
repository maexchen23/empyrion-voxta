using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// POST /devices (dedi → playfield server): read and control one of the player's own structures. Form body:
    /// <c>action</c> (default <c>list</c>), optional <c>structure</c> (an own structure in reach, see
    /// <see cref="DevicesReach"/>; default the active structure, like the item endpoints), optional <c>player</c>. Actions:
    /// <list type="bullet">
    /// <item><c>list</c>: power, shield (level, capacity, state), tanks, signals with state, named devices, LCDs, teleporters;</item>
    /// <item><c>signal</c> <c>name</c> <c>state</c>=on|off|toggle: set a control panel or block signal like the control panel;</item>
    /// <item><c>device</c> <c>name</c> <c>state</c>: switch every block of a named device or device group on/off;</item>
    /// <item><c>power</c> <c>state</c>: the structure's main power (control panel toggle);</item>
    /// <item><c>shield</c> <c>state</c>: the shield generator(s) on/off (the shield follows its generator);</item>
    /// <item><c>lcd</c> <c>name</c> [<c>text</c>]: read, or with <c>text</c> replace, the text of the named LCD(s);</item>
    /// <item><c>teleporter</c> <c>name</c> [<c>target</c>, <c>targetPlayfield</c>, <c>targetSystem</c>]: read, or set the destination
    /// (only with DevWriteTools: it ignores the game's range and reputation rules);</item>
    /// <item><c>tank</c> <c>tank</c>=fuel|oxygen|pentaxid, <c>direction</c>=fill|drain, optional <c>item</c>, <c>amount</c>
    /// (items; default as many as fit), optional <c>container</c> (take from / put into only this container): fill a tank from items in the docking group's containers, or drain it into the item
    /// the game withdraws (first item of the tank's accept list).</item>
    /// </list>
    /// Writes need the structure to be the player's or their faction's, whatever RequireOwnership says.
    /// </summary>
    public partial class BridgeMod
    {
        static bool Unverified => config?.AllowUnverifiedGameInternals == true;

        static readonly HashSet<string> DeviceWriteActions = new HashSet<string> { "signal", "device", "power", "shield", "lcd", "teleporter", "tank", "control" };

        static (int, string) HandleDevices(string body)
        {
            var f = Form.Parse(body);
            var action = (f.Get("action") ?? "list").Trim().ToLowerInvariant();
            if (action != "list" && action != "find" && !DeviceWriteActions.Contains(action))
                return (400, Error("'action' must be list, find, control, signal, device, power, shield, lcd, teleporter or tank."));
            var writes = action != "list" && action != "find" && !(action == "lcd" && f.Get("text") == null)
                         && !(action == "teleporter" && f.Get("target") == null && f.Get("targetPlayfield") == null && f.Get("targetSystem") == null);
            if (writes && !config.DeviceControlEnabled) return (403, Error("Device control is switched off on this server (DeviceControlEnabled=false)."));
            if (action == "teleporter" && writes && !config.DevWriteTools)
                return (403, Error("Setting teleporter targets is a development tool (it ignores the game's range and reputation rules) and is switched off (DevWriteTools=false)."));
            var keys = new[] { "structure", "name", "state", "text", "target", "targetPlayfield", "targetSystem", "tank", "direction", "item", "amount", "container", "query", "changes" };
            var fields = keys.Where(k => f.Get(k) != null).Select(k => (k, f.Get(k))).ToList();
            fields.Add(("action", action));
            fields.Add(("max", config.MaxItemsPerTransfer.ToString(CultureInfo.InvariantCulture)));
            var (code, json, player) = ForwardToPlayfield("devices", f.Get("player"), fields.ToArray());
            if (writes && player != null) Log($"devices {action} by {player.Name}: {string.Join(" ", fields.Where(x => x.Item1 != "text").Select(x => x.Item1 + "=" + x.Item2))} -> {(json.Length > 300 ? json.Substring(0, 300) : json)}");
            return (code, json);
        }

        /// <summary>Playfield server.</summary>
        static string ExecuteDevices(Dictionary<string, string> req)
        {
            var pf = playfields.FirstOrDefault(x => x.Name == req.Get("playfield"));
            if (pf == null) return Error("The playfield is not loaded on this server process.");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("The player is not on this playfield right now.");

            var action = req.Get("action") ?? "list";
            var wantedStructure = (req.Get("structure") ?? "").Trim();
            var range = ParseRange(req.Get("range"));
            var s = DevicesTarget(pf, player, wantedStructure, range, out var err);
            if (action == "find")
            {
                if (s == null) return Error(err);
                // without a name: the active structure's docking group (carrier and docked ships)
                return DevicesFind(pf, player, wantedStructure.Length > 0 ? new List<IStructure> { s } : DockingGroupOf(pf, s), req.Get("query"));
            }
            if (s == null) return Error(err);
            var name = (req.Get("name") ?? "").Trim();
            var state = (req.Get("state") ?? "").Trim().ToLowerInvariant();

            switch (action)
            {
                case "list": return DevicesList(s);
                case "control": return DevicesControl(s, player, req.Get("changes"));
                case "signal": return DevicesSignal(s, player, name, state);
                case "device": return DevicesSwitch(s, name, state);
                case "power": return DevicesPower(s, player, state);
                case "shield": return DevicesShield(s, state);
                case "lcd": return DevicesLcd(s, name, req.Get("text"));
                case "teleporter": return DevicesTeleporter(s, name, req.Get("target"), req.Get("targetPlayfield"), req.Get("targetSystem"));
                case "tank": return DevicesTank(pf, player, s, req);
            }
            return Error("unknown action");
        }

        /// <summary>
        /// The structures the player can reach, like the item tools: the active structure (aboard → last aboard in range →
        /// nearest; or the one set with POST /active) with its docking group, plus own structures within transfer range and
        /// their docking groups. Always own or faction structures only, whatever RequireOwnership says.
        /// </summary>
        static (IStructure active, List<IStructure> reach) DevicesReach(IPlayfield pf, IPlayer player, double range)
        {
            var info = ResolveActive(pf, player, true, range);
            var reach = new List<IStructure>();
            void AddGroup(IStructure x)
            {
                if (x == null) return;
                foreach (var m in DockingGroupOf(pf, x))
                    if (Owns(player, m) && reach.All(r => r.Id != m.Id)) reach.Add(m);
            }
            AddGroup(info.Structure);
            if (range > 0)
                foreach (var x in StructuresOf(pf).Where(x => Owns(player, x)).OrderBy(x => DistanceTo(player, x)))
                    if (DistanceTo(player, x) <= range) AddGroup(x);
            return (info.Structure, reach);
        }

        /// <summary>The named structure within reach (exact, prefix, contains; closest first), else the active structure.</summary>
        static IStructure DevicesTarget(IPlayfield pf, IPlayer player, string wanted, double range, out string error)
        {
            error = null;
            var (active, reach) = DevicesReach(pf, player, range);
            if (wanted.Length == 0)
            {
                if (active == null) error = "No own ship or base of the player is aboard or within transfer range; name one with 'structure'.";
                return active;
            }
            var own = reach.Select(x => (s: x, n: x.Entity?.Name ?? "", d: DistanceTo(player, x))).ToList();
            var hit = own.Where(x => string.Equals(x.n, wanted, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.d).FirstOrDefault();
            if (hit.s == null) hit = own.Where(x => x.n.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.d).FirstOrDefault();
            if (hit.s == null) hit = own.Where(x => x.n.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(x => x.d).FirstOrDefault();
            if (hit.s == null)
                error = own.Count == 0 ? "No own ship or base is aboard or within transfer range."
                    : $"No own structure called '{wanted}' in reach (docking group and transfer range). In reach: {string.Join(", ", own.OrderBy(x => x.d).Take(8).Select(x => $"'{x.n}'"))}.";
            return hit.s;
        }

        static bool? ParseState(string state, bool current)
        {
            switch (state)
            {
                case "on": case "1": case "true": return true;
                case "off": case "0": case "false": return false;
                case "toggle": return !current;
            }
            return null;
        }

        static string BlockName(IBlock b)
        {
            if (b == null) return null;
            b.Get(out var type, out _, out _, out _);
            return ItemName(type) ?? type.ToString(CultureInfo.InvariantCulture);
        }

        static bool BlockActive(IBlock b)
        {
            b.Get(out _, out _, out _, out var active);
            return active;
        }

        /// <summary>IBlock.Set with every argument explicit: 'active' defaults to false in the game's signature.</summary>
        static void SetBlockActive(IBlock b, bool on) => b.Set(null, null, null, on);

        static void ShieldJson(Json j, IStructure s)
        {
            j.Key("shield").Obj().Prop("active", s.IsShieldActive).Prop("level", s.ShieldLevel);
            var info = GameInternals.Shield(s, Unverified, out var err);
            if (info != null) j.Prop("capacity", info.Capacity).Prop("rechargePerSecond", info.RechargeRate).Prop("state", info.State);
            else j.Prop("detailError", err);
            j.EndObj();
        }

        static string DevicesList(IStructure s)
        {
            var j = new Json().Obj().Prop("ok", true).Key("structure").Obj();
            StructureCore(j, s);
            j.EndObj();
            ShieldJson(j, s);
            GameInternals.Verified(Unverified, out var gameInfo);
            j.Key("gameInternals").Obj().Prop("available", GameInternals.Gate(Unverified) == null).Prop("game", gameInfo).EndObj();
            j.Prop("powerUse", s.PowerConsumption).Prop("powerCapacity", s.PowerOutCapacity);

            j.Key("signals").Arr();
            void Sig(SenderSignal x, string source)
            {
                bool? st = null;
                try { st = s.GetSignalState(x.Name); } catch { }
                j.Obj().Prop("name", x.Name).Prop("source", source).Prop("on", st).EndObj();
            }
            var seen = new HashSet<string>();
            foreach (var x in s.GetControlPanelSignals() ?? new List<SenderSignal>()) if (seen.Add(x.Name)) Sig(x, "panel");
            foreach (var x in s.GetBlockSignals(null) ?? new List<SenderSignal>()) if (seen.Add(x.Name)) Sig(x, "block");
            j.EndArr();

            j.Key("devices").Arr();
            foreach (var n in (s.GetAllCustomDeviceNames() ?? new string[0]).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var pos = s.GetDevicePositions(n) ?? new List<VectorInt3>();
                var blocks = pos.Select(p => s.GetBlock(p)).Where(b => b != null).ToList();
                var on = blocks.Count(BlockActive);
                j.Obj().Prop("name", n).Prop("blocks", blocks.Count)
                    .Prop("type", string.Join(", ", blocks.Select(BlockName).Distinct().Take(3)))
                    .Prop("on", on == blocks.Count ? "on" : on == 0 ? "off" : $"{on} of {blocks.Count} on").EndObj();
            }
            j.EndArr();

            j.Key("lcds").Arr();
            var lcds = s.GetDevices(DeviceTypeName.LCD);
            for (var i = 0; lcds != null && i < lcds.Count; i++)
            {
                var p = lcds.GetAt(i);
                var text = s.GetDevice<ILcd>(p)?.GetText() ?? "";
                j.Obj().Prop("name", s.GetBlock(p)?.CustomName).Prop("text", text.Length > 300 ? text.Substring(0, 300) + "…" : text).EndObj();
            }
            j.EndArr();

            j.Key("teleporters").Arr();
            foreach (var kind in new[] { DeviceTypeName.Teleporter, DeviceTypeName.Portal })
            {
                var list = s.GetDevices(kind);
                for (var i = 0; list != null && i < list.Count; i++)
                {
                    var p = list.GetAt(i);
                    var tp = s.GetDevice<ITeleporter>(p);
                    if (tp == null) continue;
                    var d = tp.TargetData;
                    j.Obj().Prop("name", s.GetBlock(p)?.CustomName).Prop("kind", kind.ToString())
                        .Prop("target", d.TargetEntityNameOrGroup).Prop("targetPlayfield", d.TargetPlayfield).Prop("targetSystem", d.TargetSolarSystemName).EndObj();
                }
            }
            j.EndArr();
            var lights = s.GetDevices(DeviceTypeName.Light);
            j.Prop("lights", lights?.Count ?? 0);
            return j.EndObj().ToString();
        }

        static List<SenderSignal> AllSignals(IStructure s) =>
            (s.GetControlPanelSignals() ?? new List<SenderSignal>()).Concat(s.GetBlockSignals(null) ?? new List<SenderSignal>()).ToList();

        static string DevicesSignal(IStructure s, IPlayer player, string name, string state)
        {
            var all = AllSignals(s);
            var hit = all.Select(x => x.Name).FirstOrDefault(n => n == name)
                      ?? all.Select(x => x.Name).FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
                return Error($"No signal called '{name}' on '{s.Entity?.Name}'. Signals: {string.Join(", ", all.Select(x => x.Name).Distinct())}.");
            var before = s.GetSignalState(hit);
            var on = ParseState(state, before);
            if (on == null) return Error("'state' must be on, off or toggle.");
            if (on == before) return SignalResult(s, hit, before, "already in that state");
            var err = GameInternals.SetSignal(s, player, hit, on.Value, Unverified);
            if (err != null) return Error($"Setting the signal failed: {err}.");
            return SignalResult(s, hit, before, null);
        }

        static string SignalResult(IStructure s, string name, bool before, string note) =>
            new Json().Obj().Prop("ok", true).Prop("structure", s.Entity?.Name).Prop("signal", name)
                .Prop("before", before).Prop("now", s.GetSignalState(name)).Prop("note", note).EndObj().ToString();

        static string DevicesSwitch(IStructure s, string name, string state)
        {
            var names = s.GetAllCustomDeviceNames() ?? new string[0];
            var hit = names.FirstOrDefault(n => n == name) ?? names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return Error($"No device or group called '{name}' on '{s.Entity?.Name}'. Named devices: {string.Join(", ", names.Take(40))}.");
            var blocks = (s.GetDevicePositions(hit) ?? new List<VectorInt3>()).Select(p => s.GetBlock(p)).Where(b => b != null).ToList();
            if (blocks.Count == 0) return Error($"'{hit}' has no blocks.");
            var wasOn = blocks.Count(BlockActive);
            var on = ParseState(state, wasOn > 0);
            if (on == null) return Error("'state' must be on, off or toggle.");
            foreach (var b in blocks) if (BlockActive(b) != on.Value) SetBlockActive(b, on.Value);
            var nowOn = blocks.Count(BlockActive);
            return new Json().Obj().Prop("ok", nowOn == (on.Value ? blocks.Count : 0)).Prop("structure", s.Entity?.Name).Prop("device", hit)
                .Prop("blocks", blocks.Count).Prop("onBefore", wasOn).Prop("onNow", nowOn).EndObj().ToString();
        }

        static string DevicesPower(IStructure s, IPlayer player, string state)
        {
            var before = s.IsPowered;
            var on = ParseState(state, before);
            if (on == null) return Error("'state' must be on, off or toggle.");
            if (on == before)
                return new Json().Obj().Prop("ok", true).Prop("structure", s.Entity?.Name).Prop("powered", before).Prop("note", "already in that state").EndObj().ToString();
            var err = GameInternals.SetPower(s, player, on.Value, Unverified);
            if (err != null) return Error($"Switching the power failed: {err}.");
            return new Json().Obj().Prop("ok", true).Prop("structure", s.Entity?.Name).Prop("before", before).Prop("powered", s.IsPowered).EndObj().ToString();
        }

        /// <summary>Positions of the shield generator(s): the structure's internal device list whose blocks are shield generators.</summary>
        static List<IBlock> ShieldGenerators(IStructure s, out string error)
        {
            var lists = GameInternals.DeviceLists(s, Unverified, out error);
            if (lists == null) return null;
            foreach (var l in lists)
            {
                var blocks = Enumerable.Range(0, l.Count).Select(i => s.GetBlock(l.GetAt(i))).Where(b => b != null).ToList();
                if (blocks.Count > 0 && blocks.All(b => (BlockName(b) ?? "").IndexOf("ShieldGenerator", StringComparison.OrdinalIgnoreCase) >= 0))
                    return blocks;
            }
            error = "no shield generator on this structure";
            return null;
        }

        static string DevicesShield(IStructure s, string state)
        {
            var gens = ShieldGenerators(s, out var err);
            if (gens == null) return Error($"Can't switch the shield: {err}.");
            var wasOn = gens.Any(BlockActive);
            var on = ParseState(state, wasOn);
            if (on == null) return Error("'state' must be on, off or toggle.");
            foreach (var b in gens) if (BlockActive(b) != on.Value) SetBlockActive(b, on.Value);
            var j = new Json().Obj().Prop("ok", gens.All(b => BlockActive(b) == on.Value)).Prop("structure", s.Entity?.Name)
                .Prop("generators", gens.Count).Prop("generatorOnBefore", wasOn).Prop("generatorOn", gens.Any(BlockActive));
            ShieldJson(j, s);
            if (on.Value && !wasOn) j.Prop("note", "the shield needs a short warm-up after switching on");
            return j.EndObj().ToString();
        }

        static List<VectorInt3> NamedOfKind(IStructure s, string name, DeviceTypeName[] kinds, out string error)
        {
            error = null;
            var positions = new List<(VectorInt3 p, string n)>();
            foreach (var k in kinds)
            {
                var l = s.GetDevices(k);
                for (var i = 0; l != null && i < l.Count; i++) positions.Add((l.GetAt(i), s.GetBlock(l.GetAt(i))?.CustomName ?? ""));
            }
            var hits = positions.Where(x => x.n == name).ToList();
            if (hits.Count == 0) hits = positions.Where(x => string.Equals(x.n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0)
                error = positions.Count == 0 ? $"'{s.Entity?.Name}' has none."
                    : $"None called '{name}' on '{s.Entity?.Name}'. Names: {string.Join(", ", positions.Select(x => x.n.Length > 0 ? $"'{x.n}'" : "(unnamed)").Distinct())}.";
            return hits.Select(x => x.p).ToList();
        }

        static string DevicesLcd(IStructure s, string name, string text)
        {
            var pos = NamedOfKind(s, name, new[] { DeviceTypeName.LCD }, out var err);
            if (pos.Count == 0) return Error($"No LCD: {err}");
            var j = new Json().Obj().Prop("ok", true).Prop("structure", s.Entity?.Name).Prop("lcd", name).Prop("count", pos.Count);
            if (text != null)
            {
                text = text.Replace("\r\n", "\n");
                if (text.Length > 2000) return Error("The text is too long (max 2000 characters).");
                foreach (var p in pos) s.GetDevice<ILcd>(p)?.SetText(text);
                j.Prop("written", true);
            }
            j.Prop("text", s.GetDevice<ILcd>(pos[0])?.GetText());
            return j.EndObj().ToString();
        }

        static string DevicesTeleporter(IStructure s, string name, string target, string targetPlayfield, string targetSystem)
        {
            var pos = NamedOfKind(s, name, new[] { DeviceTypeName.Teleporter, DeviceTypeName.Portal }, out var err);
            if (pos.Count == 0) return Error($"No teleporter: {err}");
            if (pos.Count > 1) return Error($"{pos.Count} teleporters are called '{name}'; give them unique names.");
            var tp = s.GetDevice<ITeleporter>(pos[0]);
            if (tp == null) return Error("The teleporter can't be read.");
            var before = tp.TargetData;
            var write = target != null || targetPlayfield != null || targetSystem != null;
            if (write)
            {
                var d = before;
                if (target != null) d.TargetEntityNameOrGroup = target;
                if (targetPlayfield != null) d.TargetPlayfield = targetPlayfield;
                if (targetSystem != null) d.TargetSolarSystemName = targetSystem;
                tp.TargetData = d;
            }
            var now = tp.TargetData;
            return new Json().Obj().Prop("ok", true).Prop("structure", s.Entity?.Name).Prop("teleporter", name).Prop("written", write)
                .Prop("target", now.TargetEntityNameOrGroup).Prop("targetPlayfield", now.TargetPlayfield).Prop("targetSystem", now.TargetSolarSystemName)
                .EndObj().ToString();
        }

        static string DevicesTank(IPlayfield pf, IPlayer player, IStructure s, Dictionary<string, string> req)
        {
            if (gameText == null) return Error("The game's item config is not loaded (yet).");
            var kind = (req.Get("tank") ?? "").Trim().ToLowerInvariant();
            var tank = TankOf(s, kind);
            if (kind != "fuel" && kind != "oxygen" && kind != "pentaxid") return Error("'tank' must be fuel, oxygen or pentaxid.");
            if (tank == null || tank.Capacity <= 0) return Error($"'{s.Entity?.Name}' has no {kind} tank.");
            var drain = (req.Get("direction") ?? "fill").Trim().ToLowerInvariant() == "drain";
            var amountText = (req.Get("amount") ?? "").Trim().ToLowerInvariant();
            int? amount = null;
            if (amountText.Length > 0 && amountText != "all" && amountText != "max")
            {
                if (!int.TryParse(amountText, out var a) || a <= 0) return Error("'amount' must be a positive number of items, or all.");
                amount = a;
            }
            int.TryParse(req.Get("max"), out var maxPer);
            if (maxPer <= 0) maxPer = 10000;
            // containers of the structure's docking group (no range: tanks are filled from on board)
            var sources = CollectScope(pf, s, player, true, 0, false, null).Where(c => !c.Locked).ToList();
            var containerSpec = (req.Get("container") ?? "").Trim();
            if (containerSpec.Length > 0)
            {
                var picked = Transfers.Match(sources, containerSpec);
                if (picked.Count == 0)
                    return Error($"No container '{containerSpec}' in the docking group of '{s.Entity?.Name}' (or it is open in the game).");
                sources = picked;
            }
            var contentBefore = tank.Content;
            var accepts = gameText.TankAccepts(kind);

            int itemId;
            int value;
            if (drain)
            {
                var drainItem = gameText.TankDrainItem(kind);
                itemId = ItemIdOf(drainItem);
                value = gameText.TankValue(kind, drainItem);
                if (itemId == 0 || value <= 0) return Error($"The game's item for withdrawing {kind} is unknown.");
                var n = (int)Math.Floor(tank.Content / value + 1e-6);
                if (amount != null) n = Math.Min(n, amount.Value);
                n = Math.Min(n, maxPer);
                if (n <= 0) return Error($"The {kind} tank holds {Math.Round(tank.Content, 1)}, less than one {Show(drainItem)} ({value}).");
                if (kind == "pentaxid") return DrainPentaxid(s, tank, sources, itemId, drainItem, n, contentBefore);
                tank.AddContent(-n * value);
                var taken = contentBefore - tank.Content;
                var units = (int)Math.Floor(taken / value + 1e-6);
                var placed = units > 0 ? Transfers.PutInto(itemId, units, sources) : 0;
                if (placed < units) tank.AddContent((units - placed) * value); // no room: back into the tank
                if (taken - units * value > 0.001) tank.AddContent(taken - units * value);
                return TankResult(s, kind, tank, contentBefore, drainItem, placed, value, "drained into", placed < n ? (placed < units ? "the containers are full" : "the tank gave less than asked") : null);
            }

            // fill: the given item, else the accepted item with the smallest value present on board (finest steps, cheap first)
            var present = sources.SelectMany(c => c.ItemIds()).Distinct()
                .Select(id => (id, n: ItemName(id))).Where(x => x.n != null && accepts.Contains(x.n) && gameText.TankValue(kind, x.n) > 0).ToList();
            var itemWanted = (req.Get("item") ?? "").Trim();
            string chosen;
            if (itemWanted.Length > 0)
            {
                var m = present.FirstOrDefault(x => string.Equals(x.n, itemWanted, StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(gameText.DisplayName(x.n), itemWanted, StringComparison.OrdinalIgnoreCase));
                if (m.n == null)
                    return Error(accepts.Any(a => string.Equals(a, itemWanted, StringComparison.OrdinalIgnoreCase) || string.Equals(gameText.DisplayName(a), itemWanted, StringComparison.OrdinalIgnoreCase))
                        ? $"No {itemWanted} in the containers of '{s.Entity?.Name}' (docking group)."
                        : $"A {kind} tank doesn't take '{itemWanted}'. It takes: {string.Join(", ", accepts.Select(Show))}.");
                itemId = m.id; chosen = m.n;
            }
            else
            {
                if (present.Count == 0) return Error($"No items for the {kind} tank in the containers of '{s.Entity?.Name}' (docking group). It takes: {string.Join(", ", accepts.Select(Show))}.");
                var pick = present.OrderBy(x => gameText.TankValue(kind, x.n)).First();
                itemId = pick.id; chosen = pick.n;
            }
            value = gameText.TankValue(kind, chosen);
            var free = tank.Capacity - tank.Content;
            var fit = (int)Math.Floor(free / value + 1e-6);
            if (fit <= 0) return Error($"The {kind} tank is full ({Math.Round(tank.Content, 1)} of {Math.Round(tank.Capacity, 1)}), or one {Show(chosen)} ({value}) doesn't fit any more.");
            var want = amount == null ? fit : Math.Min(fit, amount.Value);
            var r = Transfers.RemoveOnly(itemId, want, sources.Cast<ITransferContainer>().ToList(), maxPer);
            if (!r.Ok) return Error(r.Error);
            tank.AddContent(r.Moved * value);
            var added = tank.Content - contentBefore;
            var usedItems = (int)Math.Floor(added / value + 1e-6);
            if (usedItems < r.Moved) Transfers.PutInto(itemId, r.Moved - usedItems, sources); // the tank took less: give the rest back
            return TankResult(s, kind, tank, contentBefore, chosen, usedItems, value, "filled from",
                usedItems < r.Moved ? "the tank took less than expected; the rest went back" : r.Note);
        }

        /// <summary>
        /// The pentaxid tank is a container of crystals (not a level like fuel and oxygen), and the game's
        /// IStructureTank.AddContent ignores negative amounts for it: crystals come out of the tank block (see
        /// <see cref="RemovePentaxid"/>) and go into the docking group's containers.
        /// </summary>
        static string DrainPentaxid(IStructure s, IStructureTank tank, List<ITransferContainer> targets, int itemId, string item, int n, float before)
        {
            if (PentaxidTankBlocks(s, out var err).Count == 0) return Error($"Can't open the pentaxid tank: {err}.");
            var taken = RemovePentaxid(s, itemId, n);
            var placed = taken > 0 ? Transfers.PutInto(itemId, taken, targets) : 0;
            if (placed < taken) tank.AddContent(taken - placed); // no room: back into the tank
            return TankResult(s, "pentaxid", tank, before, item, placed, 1, "drained into",
                placed < taken ? "the containers are full" : placed < n ? "the tank gave less than asked" : null);
        }

        static string TankResult(IStructure s, string kind, IStructureTank tank, float before, string item, int items, int value, string verb, string note) =>
            new Json().Obj().Prop("ok", items > 0).Prop("structure", s.Entity?.Name).Prop("tank", kind)
                .Prop("before", Math.Round(before, 1)).Prop("content", Math.Round(tank.Content, 1)).Prop("capacity", Math.Round(tank.Capacity, 1))
                .Prop("item", Show(item)).Prop("items", items).Prop("unitsPerItem", value).Prop("summary", $"{verb} {items} {Show(item)}")
                .Prop("note", note).EndObj().ToString();
    }
}
