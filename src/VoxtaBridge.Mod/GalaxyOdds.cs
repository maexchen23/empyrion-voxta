using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>
    /// How likely a star class is to bring a given playfield template (game-free, unit-tested). Only uses what the
    /// galaxy map shows (the star class) plus the scenario's generation configs:
    /// GalaxyConfig.ecf (star class → SolarSystemConfig suffix), the SolarSystemConfig presets (PlanetCountProbs ×
    /// PlanetLists, POICountProbs × POIs) and the type sections (PlanetTypes/MoonTypes/OrbitTypes/POITypes/StationTypes,
    /// shared ones in SolarSystemConfig_Defines.yaml) whose Playfields lists are tried in order ("0.05 = rare, before the others").
    /// Result: expected number of each playfield template per system of that class.
    /// </summary>
    public sealed class GalaxyOdds
    {
        public sealed class StarClassDef
        {
            public int Index;
            public string StarClass, Suffix;
        }

        /// <summary>A faction territory: a sphere on the galaxy map (centre, radius in LY) plus the other factions allowed inside, with a chance at the centre and at the edge.</summary>
        public sealed class Territory
        {
            public string Name, Faction;
            public double X, Y, Z, Radius;
            public List<(string name, double center, double edge)> Others = new List<(string, double, double)>();
        }

        public sealed class TypeDef
        {
            public string Name;
            public string MoonProb;
            public List<string> MoonTypes = new List<string>();
            public List<string> OrbitTypes = new List<string>();
            public List<(string folder, double weight)> Playfields = new List<(string, double)>();
        }

        public sealed class Preset
        {
            public List<string> PlanetCountProbs = new List<string>();
            public List<string> PlanetLists = new List<string>();
            public string PoiCountProbs, Pois;
            public Dictionary<string, Dictionary<string, TypeDef>> Sections = new Dictionary<string, Dictionary<string, TypeDef>>(StringComparer.OrdinalIgnoreCase);
        }

        static readonly string[] SectionNames = { "PlanetTypes", "MoonTypes", "OrbitTypes", "POITypes", "StationTypes", "WarpTargetOrbitTypes" };

        readonly List<StarClassDef> classes = new List<StarClassDef>();
        public readonly List<Territory> Territories = new List<Territory>();
        /// <summary>Territory_None: the factions allowed in neutral space (outside every sphere).</summary>
        public Territory Neutral;
        readonly Dictionary<string, Preset> presets = new Dictionary<string, Preset>(StringComparer.OrdinalIgnoreCase);
        Preset defines;
        readonly Dictionary<string, Dictionary<string, double>> expectedCache = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);

        public int ClassCount => classes.Count;
        public int PresetCount => presets.Count;

        // ----- GalaxyConfig.ecf ----------------------------------------------------------------

        /// <summary>Star classes in file order: the game's StarInfo.typeIndex is the 0-based position (verified on dev, 2026-11-18).</summary>
        public void LoadGalaxyConfig(string text)
        {
            StarClassDef cur = null;
            Territory terr = null;
            foreach (var raw in text.Split('\n'))
            {
                var t = StripComment(raw).Trim();
                if (t.Length == 0) continue;
                if (Regex.IsMatch(t, @"^\{\s*\+?\s*GalaxyConfig\b")) { cur = new StarClassDef(); terr = null; continue; }
                var tm = Regex.Match(t, @"^\{\s*\+?\s*Child\s+(Territory_\w+)");
                if (tm.Success) { terr = new Territory { Name = tm.Groups[1].Value }; cur = null; continue; }
                if (t.StartsWith("{")) { cur = null; terr = null; continue; }
                if (t.StartsWith("}"))
                {
                    if (cur?.StarClass != null) { cur.Index = classes.Count; classes.Add(cur); }
                    if (terr != null && terr.Name.Equals("Territory_None", StringComparison.OrdinalIgnoreCase)) Neutral = terr;
                    else if (terr?.Faction != null && terr.Radius > 0) Territories.Add(terr);
                    cur = null;
                    terr = null;
                    continue;
                }
                if (terr != null) { ParseTerritoryLine(terr, t); continue; }
                if (cur == null) continue;
                var m = Regex.Match(t, @"^(StarClass|SolarSystemConfigSuffix):\s*(\S+)");
                if (!m.Success) continue;
                if (m.Groups[1].Value == "StarClass" && cur.StarClass == null) cur.StarClass = m.Groups[2].Value;
                else if (m.Groups[1].Value == "SolarSystemConfigSuffix" && cur.Suffix == null) cur.Suffix = m.Groups[2].Value;
            }
        }

        static void ParseTerritoryLine(Territory terr, string t)
        {
            var kv = KeyValue(t);
            if (kv.key == null) return;
            if (kv.key == "Faction") terr.Faction = kv.value;
            else if (kv.key == "Radius") double.TryParse(kv.value, NumberStyles.Float, CultureInfo.InvariantCulture, out terr.Radius);
            else if (kv.key == "Center")
            {
                var p = kv.value.Split(',').Select(x => double.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0).ToArray();
                if (p.Length == 3) { terr.X = p[0]; terr.Y = p[1]; terr.Z = p[2]; }
            }
            else if (kv.key.StartsWith("Other_"))
            {
                // Other_1: Talon, param1: "0.3, 0.7"
                var m = Regex.Match(t.Substring(t.IndexOf(':') + 1), @"^\s*(?<n>[^,\s]+)\s*(,\s*param1:\s*""(?<c>[\d.]+)\s*(,\s*(?<e>[\d.]+))?"")?");
                if (!m.Success) return;
                double c = 1, e = 1;
                if (m.Groups["c"].Success)
                {
                    double.TryParse(m.Groups["c"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out c);
                    e = c;
                    if (m.Groups["e"].Success) double.TryParse(m.Groups["e"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out e);
                }
                terr.Others.Add((m.Groups["n"].Value, c, e));
            }
        }

        /// <summary>Territories containing a point (LY), with how far out it is (0 = centre, 1 = edge).</summary>
        public List<(Territory t, double ratio)> TerritoriesAt(double x, double y, double z)
        {
            var r = new List<(Territory, double)>();
            foreach (var t in Territories)
            {
                var d = Math.Sqrt((x - t.X) * (x - t.X) + (y - t.Y) * (y - t.Y) + (z - t.Z) * (z - t.Z));
                if (d <= t.Radius) r.Add((t, d / t.Radius));
            }
            if (r.Count == 0 && Neutral != null) r.Add((Neutral, 0));
            return r;
        }

        static bool Eq(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Chance that a faction's POIs may be placed here (territory faction: 1; an Other_: interpolated centre → edge).</summary>
        static double FactionAllowed(List<(Territory t, double ratio)> at, string name)
        {
            double best = 0;
            foreach (var (t, ratio) in at)
            {
                if (Eq(t.Faction, name)) return 1;
                foreach (var (n, c, e) in t.Others)
                    if (Eq(n, name)) best = Math.Max(best, c + (e - c) * ratio);
            }
            return best;
        }

        /// <summary>
        /// Chance that a spawn entry may be placed at a star, from the territories there (GalaxyConfig.ecf: "In a given
        /// territory, only the POI of the Territory-Faction will be placed", plus its Other_ factions):
        /// FactionTerritory [All] → anywhere; True → only in a territory of its own faction; a list → only in territories
        /// that have one of the names as faction or Other_ (so never in neutral space); none → anywhere in neutral space,
        /// inside a territory only if its faction is allowed there. Neutral space is Territory_None with its own allowed
        /// factions. Factionless entries (None) are not restricted.
        /// </summary>
        public static double SpawnAllowed(string faction, List<string> factionTerritory, List<(Territory t, double ratio)> at)
        {
            var noFaction = string.IsNullOrWhiteSpace(faction) || Eq(faction, "None");
            if (factionTerritory != null && factionTerritory.Any(n => Eq(n, "All"))) return 1;
            List<string> names = factionTerritory;
            if (names != null && names.Count == 1 && (Eq(names[0], "True") || Eq(names[0], "False")))
                names = Eq(names[0], "False") || noFaction ? null : new List<string> { faction };
            if (names == null) return noFaction || at.Count == 0 ? 1 : FactionAllowed(at, faction);
            if (at.Count == 0) return 0;
            return names.Max(n => FactionAllowed(at, n));
        }

        public StarClassDef Class(int typeIndex) => typeIndex >= 0 && typeIndex < classes.Count ? classes[typeIndex] : null;

        /// <summary>Preset name for a star class: "SolarSystemConfig" + suffix (no suffix: the plain SolarSystemConfig).</summary>
        public static string PresetName(StarClassDef c) => "SolarSystemConfig" + (c?.Suffix ?? "");

        // ----- presets ----------------------------------------------------------------------

        public void AddPreset(string name, string yaml)
        {
            var p = ParsePreset(yaml);
            if (name.EndsWith("_Defines", StringComparison.OrdinalIgnoreCase)) defines = p;
            else presets[name] = p;
            expectedCache.Clear();
        }

        public bool HasPreset(string name) => presets.ContainsKey(name);

        public static Preset ParsePreset(string yaml)
        {
            var p = new Preset();
            string section = null;   // GenerationParams key at indent 2
            TypeDef cur = null;
            var inPlayfields = false;
            foreach (var raw in yaml.Split('\n'))
            {
                var line = StripComment(raw).TrimEnd('\r', ' ', '\t');
                var t = line.Trim();
                if (t.Length == 0) continue;
                var indent = line.Length - line.TrimStart(' ').Length;
                if (indent <= 2 && !t.StartsWith("-"))
                {
                    var kv = KeyValue(t);
                    section = kv.key;
                    cur = null;
                    inPlayfields = false;
                    if (section == "POICountProbs") p.PoiCountProbs = kv.value;
                    else if (section == "POIs") p.Pois = kv.value;
                    continue;
                }
                if (section == "PlanetCountProbs" || section == "PlanetLists")
                {
                    if (!t.StartsWith("-")) continue;
                    var v = t.Substring(1).Trim().Trim('\'', '"');
                    (section == "PlanetCountProbs" ? p.PlanetCountProbs : p.PlanetLists).Add(v);
                    continue;
                }
                if (section == null || !SectionNames.Contains(section, StringComparer.OrdinalIgnoreCase)) continue;
                if (!p.Sections.TryGetValue(section, out var defs)) p.Sections[section] = defs = new Dictionary<string, TypeDef>(StringComparer.OrdinalIgnoreCase);

                var nameMatch = Regex.Match(t, @"^-\s*Name:\s*(\S+)");
                if (nameMatch.Success)
                {
                    cur = new TypeDef { Name = nameMatch.Groups[1].Value.Trim('"', '\'') };
                    defs[cur.Name] = cur;
                    inPlayfields = false;
                    continue;
                }
                if (cur == null) continue;
                if (t.StartsWith("-"))
                {
                    if (!inPlayfields) continue; // Placement and other lists
                    var parts = t.Substring(1).Split(',').Select(s => s.Trim()).ToArray();
                    if (parts[0].Length == 0) continue;
                    var w = 1.0;
                    if (parts.Length > 1) double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out w);
                    cur.Playfields.Add((parts[0], w));
                    continue;
                }
                var kv2 = KeyValue(t);
                inPlayfields = kv2.key == "Playfields";
                if (kv2.key == "MoonProb") cur.MoonProb = kv2.value;
                else if (kv2.key == "MoonType") cur.MoonTypes = InlineList(kv2.value);
                else if (kv2.key == "OrbitType") cur.OrbitTypes = InlineList(kv2.value);
            }
            return p;
        }

        static List<string> InlineList(string v) =>
            v.Trim().TrimStart('[').TrimEnd(']').Split(',').Select(s => s.Trim().Trim('"', '\'')).Where(s => s.Length > 0).ToList();

        /// <summary>"0 : 0.7, 1 : 0.3" → expected count (0.3).</summary>
        public static double ExpectedCount(string probs)
        {
            if (string.IsNullOrWhiteSpace(probs)) return 0;
            double e = 0;
            foreach (var part in probs.Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length != 2) continue;
                if (double.TryParse(kv[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var k) &&
                    double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pr)) e += k * pr;
            }
            return e;
        }

        /// <summary>"Desert:0.5, Arid:0.3" → normalized weights.</summary>
        public static List<(string name, double weight)> Weights(string list)
        {
            var r = new List<(string, double)>();
            if (string.IsNullOrWhiteSpace(list)) return r;
            foreach (var part in list.Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length != 2) continue;
                if (double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0) r.Add((kv[0].Trim(), w));
            }
            var sum = r.Sum(x => x.Item2);
            return sum > 0 ? r.Select(x => (x.Item1, x.Item2 / sum)).ToList() : r;
        }

        /// <summary>Playfields tried in order: each takes its weight of what is left (the last is usually 1).</summary>
        public static List<(string folder, double p)> PickInOrder(List<(string folder, double weight)> list)
        {
            var r = new List<(string, double)>();
            var left = 1.0;
            foreach (var (f, w) in list)
            {
                var p = left * Math.Max(0, Math.Min(1, w));
                if (p > 0) r.Add((f, p));
                left -= p;
                if (left <= 1e-9) break;
            }
            return r;
        }

        TypeDef FindType(Preset p, string section, string name)
        {
            foreach (var src in new[] { p, defines })
            {
                if (src == null) continue;
                if (section != null)
                {
                    if (src.Sections.TryGetValue(section, out var d) && d.TryGetValue(name, out var td)) return td;
                    continue;
                }
                foreach (var s in SectionNames)
                    if (src.Sections.TryGetValue(s, out var d) && d.TryGetValue(name, out var td)) return td;
            }
            return null;
        }

        void AddType(Preset p, TypeDef td, double factor, Dictionary<string, double> into, int depth)
        {
            if (td == null || factor <= 0 || depth > 3) return;
            foreach (var (f, pr) in PickInOrder(td.Playfields))
                into[f] = (into.TryGetValue(f, out var v) ? v : 0) + factor * pr;
            if (td.MoonTypes.Count > 0 && td.MoonProb != null)
            {
                var moons = ExpectedCount(td.MoonProb);
                foreach (var m in td.MoonTypes) AddType(p, FindType(p, "MoonTypes", m), factor * moons / td.MoonTypes.Count, into, depth + 1);
            }
            foreach (var o in td.OrbitTypes) AddType(p, FindType(p, "OrbitTypes", o), factor / td.OrbitTypes.Count, into, depth + 1);
        }

        /// <summary>Expected number of each playfield template in one system generated from this preset.</summary>
        public Dictionary<string, double> Expected(string presetName)
        {
            if (expectedCache.TryGetValue(presetName, out var cached)) return cached;
            var into = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (presets.TryGetValue(presetName, out var p))
            {
                for (var i = 0; i < Math.Min(p.PlanetCountProbs.Count, p.PlanetLists.Count); i++)
                {
                    var count = ExpectedCount(p.PlanetCountProbs[i]);
                    if (count <= 0) continue;
                    foreach (var (label, w) in Weights(p.PlanetLists[i])) AddType(p, FindType(p, null, label), count * w, into, 0);
                }
                var pois = ExpectedCount(p.PoiCountProbs);
                foreach (var (label, w) in Weights(p.Pois)) AddType(p, FindType(p, "POITypes", label), pois * w, into, 0);
            }
            expectedCache[presetName] = into;
            return into;
        }

        /// <summary>Chance that a system has at least one hit, from expected hits (Poisson approximation).</summary>
        public static double AtLeastOne(double expectedHits) => 1 - Math.Exp(-Math.Max(0, expectedHits));

        static (string key, string value) KeyValue(string t)
        {
            var i = t.IndexOf(':');
            if (i <= 0) return (null, null);
            return (t.Substring(0, i).Trim(), t.Substring(i + 1).Trim().Trim('"', '\''));
        }

        static string StripComment(string line)
        {
            var q = '\0';
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (q != '\0') { if (c == q) q = '\0'; continue; }
                if (c == '"' || c == '\'') q = c;
                else if (c == '#') return line.Substring(0, i);
            }
            return line;
        }
    }
}
