using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>The structure the item features work from, and why.</summary>
    public sealed class ActiveInfo
    {
        public IStructure Structure;
        /// <summary>"pinned", "pilot", "aboard", "standing", "last aboard", "nearest".</summary>
        public string How;
        /// <summary>The player is on/in it (pilot, aboard, standing); else they are on foot elsewhere.</summary>
        public bool PlayerOn;
        public float Distance;
        /// <summary>Why an earlier set active structure was dropped, or null.</summary>
        public string Note;
        /// <summary>The own structure the player is on when it isn't the active one (a set structure elsewhere), else null.</summary>
        public IStructure On;
    }

    /// <summary>
    /// One place that decides which own structure the container, find, transfer, inventory and crafting features work
    /// from, in this order:
    /// 1. set by the player (empyrion_set_active_structure), while it exists, is theirs and is within transfer range;
    /// 2. the own structure the player pilots, is inside or stands on;
    /// 3. the own structure they were last aboard, if within transfer range (park the carrier, walk into a POI);
    /// 4. the nearest own root structure (not docked) within transfer range.
    /// On foot, other structures in range are measured from the player. POIs and other players' structures never count.
    /// Per playfield server process; forgotten whenever the player is on another playfield than the one stored (also on a
    /// reused playfield server), so a playfield change always starts fresh.
    /// </summary>
    public partial class BridgeMod
    {
        static readonly Dictionary<int, (int id, string pf)> lastAboard = new Dictionary<int, (int, string)>();
        static readonly Dictionary<int, (int id, string pf)> pinnedActive = new Dictionary<int, (int, string)>();
        /// <summary>The resolution of the current playfield request (main thread), added to its answer as "workingFrom".</summary>
        static ActiveInfo lastResolved;

        /// <summary>
        /// Every 2 s: forget the set and last-aboard structure of every player who is not on the playfield they were stored
        /// for: gone from this process, or on another playfield of it (a reused playfield server). Changing playfields
        /// always starts fresh, whatever the server reuse.
        /// </summary>
        static void ForgetAbsentPlayers(IEnumerable<IPlayfield> loaded)
        {
            var where = new Dictionary<int, string>();
            foreach (var pf in loaded)
                try { foreach (var p in pf.Players.Values) where[p.Id] = pf.Name; } catch { }
            bool Stale(int id, string pf) => !where.TryGetValue(id, out var now) || now != pf;
            lock (pinnedActive)
            {
                foreach (var kv in pinnedActive.Where(kv => Stale(kv.Key, kv.Value.pf)).ToList())
                {
                    pinnedActive.Remove(kv.Key);
                    Log($"active structure: forgot the set structure of player {kv.Key} (left {kv.Value.pf})");
                }
                foreach (var kv in lastAboard.Where(kv => Stale(kv.Key, kv.Value.pf)).ToList()) lastAboard.Remove(kv.Key);
            }
        }

        static IStructure StructureById(IPlayfield pf, int id)
        {
            try { if (pf.Entities.TryGetValue(id, out var e) && e != null) return e.Structure; } catch { }
            return null;
        }

        static float DistanceTo(IPlayer player, IStructure s)
        {
            try { return s.Entity != null ? UnityEngine.Vector3.Distance(player.Position, s.Entity.Position) : float.MaxValue; }
            catch { return float.MaxValue; }
        }

        static ActiveInfo ResolveActive(IPlayfield pf, IPlayer player, bool requireOwnership, double range)
        {
            var info = new ActiveInfo();
            var on = FindStructureFor(pf, player, requireOwnership, out var onHow);
            bool Usable(IStructure s) => s != null && (!requireOwnership || Owns(player, s));
            bool InRange(IStructure s, out float d) { d = DistanceTo(player, s); return range > 0 && d <= range; }

            lock (pinnedActive)
            {
                if (on != null) lastAboard[player.Id] = (on.Id, pf.Name);

                if (pinnedActive.TryGetValue(player.Id, out var pin))
                {
                    var s = pin.pf == pf.Name ? StructureById(pf, pin.id) : null;
                    if (pin.pf != pf.Name) { /* changed playfield: always start fresh, no note */ }
                    else if (!Usable(s))
                        info.Note = "the active structure set earlier no longer exists or isn't the player's; back to automatic";
                    else if (on != null && on.Id == s.Id)
                    {
                        info.Structure = s; info.How = "pinned"; info.PlayerOn = true; info.Distance = 0;
                        return lastResolved = info;
                    }
                    else if (InRange(s, out var d))
                    {
                        info.Structure = s; info.How = "pinned"; info.Distance = d; info.On = on;
                        return lastResolved = info;
                    }
                    else
                        info.Note = $"'{s.Entity?.Name}', set as active structure, is out of transfer range ({Math.Round(DistanceTo(player, s))} m); back to automatic";
                    pinnedActive.Remove(player.Id);
                }

                if (on != null)
                {
                    info.Structure = on; info.How = onHow; info.PlayerOn = true;
                    return lastResolved = info;
                }

                if (lastAboard.TryGetValue(player.Id, out var last) && last.pf == pf.Name)
                {
                    var s = StructureById(pf, last.id);
                    if (Usable(s) && InRange(s, out var d))
                    {
                        info.Structure = s; info.How = "last aboard"; info.Distance = d;
                        return lastResolved = info;
                    }
                }
            }

            IStructure best = null;
            var bestD = float.MaxValue;
            foreach (var s in StructuresOf(pf))
            {
                if (!Usable(s) || s.Entity == null || s.Entity.DockedTo > 0) continue;
                if (InRange(s, out var d) && d < bestD) { best = s; bestD = d; }
            }
            if (best != null) { info.Structure = best; info.How = "nearest"; info.Distance = bestD; }
            return lastResolved = info;
        }

        /// <summary>CollectScope from the active structure: on foot, range is measured from the player.</summary>
        static List<ITransferContainer> ActiveScope(IPlayfield pf, ActiveInfo a, IPlayer player, bool requireOwnership, double range,
            bool includeRangeContainers, List<InRange> inRange, List<GroupMember> group = null, ScopeTiming timing = null) =>
            CollectScope(pf, a.Structure, player, requireOwnership, range, includeRangeContainers, inRange, group, timing,
                playerOnCurrent: a.PlayerOn, origin: a.PlayerOn ? (UnityEngine.Vector3?)null : player.Position);

        static void WriteActive(Json j, ActiveInfo a)
        {
            j.Obj();
            if (a.Structure != null)
                j.Prop("name", a.Structure.Entity?.Name).Prop("type", a.Structure.Entity?.Type.ToString()).Prop("how", a.How)
                    .Prop("playerOn", a.PlayerOn).Prop("distance", Math.Round(a.Distance));
            if (a.Note != null) j.Prop("note", a.Note);
            if (a.On != null) j.Key("on").Obj().Prop("name", a.On.Entity?.Name).Prop("type", a.On.Entity?.Type.ToString()).EndObj();
            j.EndObj();
        }

        /// <summary>Adds "workingFrom" (the request's active structure) to a playfield answer object.</summary>
        static string WithWorkingFrom(string json, ActiveInfo a)
        {
            if (a == null || string.IsNullOrEmpty(json) || json[0] != '{') return json;
            var j = new Json();
            WriteActive(j, a);
            return "{\"workingFrom\":" + j + (json.Length > 2 ? "," : "") + json.Substring(1);
        }

        /// <summary>HTTP thread: POST /active (name=structure name, or auto).</summary>
        static (int, string) HandleSetActive(string body)
        {
            var f = Form.Parse(body);
            var (code, json, _) = ForwardToPlayfield("set-active", f.Get("player"), ("name", (f.Get("name") ?? "auto").Trim()));
            return (code, json);
        }

        /// <summary>Playfield server: set (name) or clear (auto) the player's active structure.</summary>
        static string ExecuteSetActive(Dictionary<string, string> req)
        {
            var pf = playfields.FirstOrDefault(x => x.Name == req.Get("playfield"));
            if (pf == null) return Error("The playfield is not loaded on this server process.");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("The player is not on this playfield right now.");
            var requireOwnership = req.Get("ownership") != "0";
            var range = ParseRange(req.Get("range"));
            var wanted = (req.Get("name") ?? "").Trim();

            if (wanted.Length == 0 || wanted.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                lock (pinnedActive) pinnedActive.Remove(player.Id);
                var auto = ResolveActive(pf, player, requireOwnership, range);
                var ja = new Json().Obj().Prop("ok", true).Prop("mode", "auto").Key("active");
                WriteActive(ja, auto);
                return ja.EndObj().ToString();
            }

            var own = StructuresOf(pf).Where(s => s.Entity != null && (!requireOwnership || Owns(player, s)))
                .Select(s => (s, d: DistanceTo(player, s))).ToList();
            var hit = own.Where(x => string.Equals(x.s.Entity.Name, wanted, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.d).FirstOrDefault();
            if (hit.s == null) hit = own.Where(x => (x.s.Entity.Name ?? "").StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.d).FirstOrDefault();
            if (hit.s == null) hit = own.Where(x => (x.s.Entity.Name ?? "").IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(x => x.d).FirstOrDefault();
            if (hit.s == null)
                return Error($"No own structure called '{wanted}' on {pf.Name}. Own structures here: " +
                             string.Join(", ", own.OrderBy(x => x.d).Take(8).Select(x => $"'{x.s.Entity.Name}' ({x.s.Entity.Type}, {Math.Round(x.d)} m)")) + ".");
            var on = FindStructureFor(pf, player, requireOwnership, out _);
            if (!(on != null && on.Id == hit.s.Id) && !(range > 0 && hit.d <= range))
                return Error($"'{hit.s.Entity.Name}' is {Math.Round(hit.d)} m away, outside the transfer range of {Math.Round(range)} m.");
            lock (pinnedActive) pinnedActive[player.Id] = (hit.s.Id, pf.Name);
            var a = ResolveActive(pf, player, requireOwnership, range);
            var j = new Json().Obj().Prop("ok", true).Prop("mode", "set").Key("active");
            WriteActive(j, a);
            return j.EndObj().ToString();
        }
    }
}
