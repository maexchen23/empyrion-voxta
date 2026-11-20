using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// The ship-control actions of POST /devices for the Voxta tools: never the full list of a structure's signals and
    /// devices (big carriers have hundreds), only what is asked for.
    /// <list type="bullet">
    /// <item><c>find</c> <c>query</c> [<c>structure</c>]: signals and named devices / device groups whose name or block type
    /// contains the query, plus power and shield, with their state; at most <see cref="MaxFindControls"/>. Without
    /// <c>structure</c>: the active structure (as for the item tools) and its docking group.</item>
    /// <item><c>control</c> <c>changes</c> [<c>structure</c>]: "target=state" items separated by ';' or new lines (state
    /// on, off or toggle) on one structure (default: the active structure; a named one must be in reach). Target: <c>power</c>, <c>shield</c>, a
    /// signal or a named device / group; a <c>signal:</c> / <c>device:</c> prefix settles a name clash. Unknown targets
    /// answer with close matches (at most <see cref="MaxSuggestions"/>), nothing else is touched for them.</item>
    /// </list>
    /// </summary>
    public partial class BridgeMod
    {
        const int MaxFindControls = 15;
        const int MaxSuggestions = 8;

        sealed class Control
        {
            public IStructure Structure;
            public string Kind;      // power | shield | signal | device
            public string Name;
            public string Type;      // block type(s) of a device
            public int Blocks;
        }

        static string StructName(IStructure s) => s.Entity?.Name ?? s.Id.ToString();

        /// <summary>All controls of one structure (signals, named devices, power, shield).</summary>
        static List<Control> ControlsOf(IStructure s)
        {
            var list = new List<Control> { new Control { Structure = s, Kind = "power", Name = "power" } };
            if (ShieldGenerators(s, out _)?.Count > 0) list.Add(new Control { Structure = s, Kind = "shield", Name = "shield" });
            var seen = new HashSet<string>();
            foreach (var x in AllSignals(s))
                if (seen.Add(x.Name)) list.Add(new Control { Structure = s, Kind = "signal", Name = x.Name });
            foreach (var n in s.GetAllCustomDeviceNames() ?? new string[0])
            {
                var blocks = (s.GetDevicePositions(n) ?? new List<VectorInt3>()).Select(p => s.GetBlock(p)).Where(b => b != null).ToList();
                if (blocks.Count == 0) continue;
                list.Add(new Control
                {
                    Structure = s, Kind = "device", Name = n, Blocks = blocks.Count,
                    Type = string.Join(", ", blocks.Select(BlockName).Where(x => x != null).Distinct().Take(3)),
                });
            }
            return list;
        }

        static string ControlState(Control c)
        {
            var s = c.Structure;
            switch (c.Kind)
            {
                case "power": return s.IsPowered ? "on" : "off";
                case "shield": return s.IsShieldActive ? "on" : "off";
                case "signal":
                    try { return s.GetSignalState(c.Name) ? "on" : "off"; } catch { return "?"; }
                default:
                    var blocks = (s.GetDevicePositions(c.Name) ?? new List<VectorInt3>()).Select(p => s.GetBlock(p)).Where(b => b != null).ToList();
                    var on = blocks.Count(BlockActive);
                    return on == blocks.Count ? "on" : on == 0 ? "off" : $"{on} of {blocks.Count} on";
            }
        }

        static void ControlJson(Json j, Control c) =>
            j.Obj().Prop("structure", StructName(c.Structure)).Prop("kind", c.Kind).Prop("name", c.Name).Prop("type", c.Type)
                .Prop("state", ControlState(c)).EndObj();

        /// <summary>The player's structure and its docking group (carrier first, then docked ships).</summary>
        static List<IStructure> DockingGroupOf(IPlayfield pf, IStructure s)
        {
            var root = CarrierOf(pf, s) ?? s;
            return new[] { root }.Concat(DockedOf(root)).Where(x => x != null).GroupBy(x => x.Id).Select(g => g.First()).ToList();
        }

        static string DevicesFind(IPlayfield pf, IPlayer player, List<IStructure> scope, string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return Error("'query' is required, e.g. 'turret', 'light', 'production' or a signal name.");
            scope = scope.Where(x => Owns(player, x)).ToList();
            var all = scope.SelectMany(ControlsOf).ToList();
            bool Hit(Control c) =>
                c.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (c.Type ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (c.Type != null && c.Type.Split(',').Any(t => (gameText?.DisplayName(t.Trim()) ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
            var hits = all.Where(Hit).ToList();
            var j = new Json().Obj().Prop("ok", true).Prop("query", query).Prop("matches", hits.Count);
            j.Key("structures").Arr();
            foreach (var x in scope) j.Val(StructName(x));
            j.EndArr();
            j.Key("controls").Arr();
            foreach (var c in hits.Take(MaxFindControls)) ControlJson(j, c);
            j.EndArr();
            if (hits.Count > MaxFindControls) j.Prop("note", $"{hits.Count - MaxFindControls} more; search more precisely");
            if (hits.Count == 0) j.Prop("note", "nothing matches; devices are only found by their custom name or device group, so unnamed ones can't be switched");
            return j.EndObj().ToString();
        }

        /// <summary>Close names for an unknown target: containing either way, then by edit distance.</summary>
        static List<Control> Suggest(List<Control> all, string wanted)
        {
            var w = wanted.ToLowerInvariant();
            int Score(Control c)
            {
                var n = c.Name.ToLowerInvariant();
                if (n.Contains(w) || w.Contains(n)) return 0;
                return Levenshtein(n, w);
            }
            return all.Select(c => (c, d: Score(c))).Where(x => x.d <= Math.Max(2, w.Length / 3)).OrderBy(x => x.d).Select(x => x.c).Take(MaxSuggestions).ToList();
        }

        static int Levenshtein(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var k = 0; k <= b.Length; k++) d[0, k] = k;
            for (var i = 1; i <= a.Length; i++)
                for (var k = 1; k <= b.Length; k++)
                    d[i, k] = Math.Min(Math.Min(d[i - 1, k] + 1, d[i, k - 1] + 1), d[i - 1, k - 1] + (a[i - 1] == b[k - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        static string DevicesControl(IStructure s, IPlayer player, string changes)
        {
            var items = (changes ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (items.Count == 0) return Error("'changes' is required: target=on|off|toggle, several separated by ';'.");
            if (items.Count > 20) return Error("At most 20 changes at once.");
            var all = ControlsOf(s);
            var j = new Json().Obj().Prop("structure", StructName(s));
            var okAll = true;
            j.Key("results").Arr();
            foreach (var item in items)
            {
                var eq = item.LastIndexOf('=');
                var target = (eq > 0 ? item.Substring(0, eq) : item).Trim();
                var state = (eq > 0 ? item.Substring(eq + 1) : "").Trim().ToLowerInvariant();
                j.Obj().Prop("target", target);
                var (result, ok) = ControlOne(s, player, all, target, state, j);
                okAll &= ok;
                j.Prop("ok", ok).Prop("result", result).EndObj();
            }
            j.EndArr();
            return j.Prop("ok", okAll).EndObj().ToString();
        }

        static (string text, bool ok) ControlOne(IStructure s, IPlayer player, List<Control> all, string target, string state, Json j)
        {
            string kind = null;
            var name = target;
            foreach (var pre in new[] { "signal", "device" })
                if (target.StartsWith(pre + ":", StringComparison.OrdinalIgnoreCase)) { kind = pre; name = target.Substring(pre.Length + 1).Trim(); }
            var lower = name.ToLowerInvariant();
            if (kind == null && (lower == "power" || lower == "main power")) kind = "power";
            if (kind == null && (lower == "shield" || lower == "shields")) kind = "shield";

            List<Control> hits;
            if (kind == "power" || kind == "shield") hits = all.Where(c => c.Kind == kind).ToList();
            else
            {
                var pool = all.Where(c => (c.Kind == "signal" || c.Kind == "device") && (kind == null || c.Kind == kind)).ToList();
                hits = pool.Where(c => c.Name == name).ToList();
                if (hits.Count == 0) hits = pool.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (hits.Count == 0)
            {
                if (kind == "shield") return ("this structure has no shield generator", false);
                var close = Suggest(all.Where(c => c.Kind == "signal" || c.Kind == "device").ToList(), name);
                j.Key("closeMatches").Arr();
                foreach (var c in close) ControlJson(j, c);
                j.EndArr();
                return (close.Count > 0 ? "no signal or named device by that name; close matches listed" : "no signal or named device by that name (devices need a custom name or a device group)", false);
            }
            if (hits.Select(c => c.Kind).Distinct().Count() > 1)
                return ($"'{name}' is both a signal and a device; write signal:{name} or device:{name}", false);
            var ctl = hits[0];
            j.Prop("kind", ctl.Kind).Prop("name", ctl.Name);
            var before = ControlState(ctl);
            var on = ParseState(state, before != "off");
            if (on == null) return ("state must be on, off or toggle", false);
            j.Prop("before", before);
            string err = null;
            switch (ctl.Kind)
            {
                case "power":
                    if (s.IsPowered != on.Value) err = GameInternals.SetPower(s, player, on.Value, Unverified);
                    break;
                case "shield":
                    var gens = ShieldGenerators(s, out err);
                    if (gens != null) foreach (var b in gens) if (BlockActive(b) != on.Value) SetBlockActive(b, on.Value);
                    break;
                case "signal":
                    if (s.GetSignalState(ctl.Name) != on.Value) err = GameInternals.SetSignal(s, player, ctl.Name, on.Value, Unverified);
                    break;
                default:
                    foreach (var b in (s.GetDevicePositions(ctl.Name) ?? new List<VectorInt3>()).Select(p => s.GetBlock(p)).Where(b => b != null))
                        if (BlockActive(b) != on.Value) SetBlockActive(b, on.Value);
                    break;
            }
            if (err != null) return (err, false);
            var now = ControlState(ctl);
            j.Prop("now", now);
            if (ctl.Kind == "shield" && on.Value && before == "off") return ("shield generator on; the shield needs a short warm-up", true);
            if (ctl.Kind == "shield") return ("shield generator " + (on.Value ? "on" : "off") + " (ship logic may switch it back)", true);
            var wanted = on.Value ? "on" : "off";
            return (now == wanted ? "done" : $"set, but reads {now} now", now == wanted);
        }
    }
}
