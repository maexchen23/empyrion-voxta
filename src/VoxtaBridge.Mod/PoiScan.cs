using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>One POI as reported to the character.</summary>
    public sealed class ScanPoi
    {
        public long Id;
        public string Name, Faction, Tier, Why;
        public int FactionId = -1;
        public PoiKind Kind;
        public int Turrets = -1, Sentries, Traders;
        public double X, Y, Z, Distance;
        public bool Discovered, Near;
        public string TerminalDialogue;
    }

    /// <summary>A POI or a complex of neighbouring POIs of the same faction (one Ghyst main base plus ~45 aux buildings).</summary>
    public sealed class ScanGroup
    {
        public List<ScanPoi> Members = new List<ScanPoi>();
        public ScanPoi Lead => Members[0];
        public double Distance => Members.Min(m => m.Distance);
        public int Turrets => Members.Sum(m => Math.Max(0, m.Turrets));
        public bool IsComplex => Members.Count > 1;
        /// <summary>Strongest kind in the group: major base before trading before outpost.</summary>
        public PoiKind Kind => Lead.Kind;
    }

    /// <summary>Grouping and ordering of POIs for the scan (game-free, unit-tested).</summary>
    public static class PoiScan
    {
        public const double ComplexRadius = 600;

        static int Rank(PoiKind k)
        {
            switch (k)
            {
                case PoiKind.MajorFaction: return 0;
                case PoiKind.Trading: return 1;
                case PoiKind.MinorFaction: return 2;
                case PoiKind.Abandoned: return 3;
                case PoiKind.Salvage: return 4;
                default: return 5;
            }
        }

        static bool Joins(ScanPoi p) =>
            p.FactionId >= 0 && p.FactionId != 255 && (p.Kind == PoiKind.MajorFaction || p.Kind == PoiKind.MinorFaction || p.Kind == PoiKind.Trading || p.Kind == PoiKind.Civilian);

        /// <summary>
        /// Faction POIs (major, minor, trading) of the same faction within <paramref name="radius"/> of each other form one
        /// group (single linkage); everything else stays on its own. Groups are ordered by distance, members by strength.
        /// </summary>
        public static List<ScanGroup> Group(IList<ScanPoi> pois, double radius = ComplexRadius)
        {
            var parent = Enumerable.Range(0, pois.Count).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (var i = 0; i < pois.Count; i++)
            {
                if (!Joins(pois[i])) continue;
                for (var k = i + 1; k < pois.Count; k++)
                {
                    if (!Joins(pois[k]) || pois[k].FactionId != pois[i].FactionId) continue;
                    var dx = pois[i].X - pois[k].X; var dy = pois[i].Y - pois[k].Y; var dz = pois[i].Z - pois[k].Z;
                    if (dx * dx + dy * dy + dz * dz <= radius * radius) parent[Find(k)] = Find(i);
                }
            }
            return Enumerable.Range(0, pois.Count).GroupBy(Find)
                .Select(g => new ScanGroup
                {
                    Members = g.Select(i => pois[i]).OrderBy(p => Rank(p.Kind)).ThenByDescending(p => p.Turrets).ThenBy(p => p.Distance).ToList(),
                })
                .OrderBy(g => g.Distance).ToList();
        }

        /// <summary>
        /// The groups to report: notable ones (everything but civilian) first, civilian only in the slots left; the result is
        /// ordered by distance again. Civilian condensers and modules otherwise crowd out the places that matter.
        /// </summary>
        public static List<ScanGroup> Pick(List<ScanGroup> groups, int max) =>
            groups.Where(g => g.Kind != PoiKind.Civilian).Take(max)
                .Concat(groups.Where(g => g.Kind == PoiKind.Civilian).Take(Math.Max(0, max - groups.Count(g => g.Kind != PoiKind.Civilian))))
                .OrderBy(g => g.Distance).ToList();

        /// <summary>"3 trading, 41 small faction outpost" for a complex.</summary>
        public static string MemberSummary(ScanGroup g) =>
            string.Join(", ", g.Members.GroupBy(m => m.Kind).OrderBy(x => Rank(x.Key)).Select(x => $"{x.Count()} {PoiClassifier.Label(x.Key)}"));

        /// <summary>"MoonLava" → "Lava Moon", "JupiterPurple" → "Purple gas giant", "Stormy Desert" stays.</summary>
        public static string PlanetTypeLabel(string t)
        {
            if (string.IsNullOrEmpty(t)) return null;
            var words = Regex.Replace(t, "(?<=[a-z])(?=[A-Z])", " ").Split(' ').Where(w => w.Length > 0).ToList();
            if (words.Count > 1 && words[0] == "Moon") { words.RemoveAt(0); words.Add("Moon"); }
            if (words[0] == "Jupiter") { words.RemoveAt(0); words.Add("gas giant"); }
            return string.Join(" ", words);
        }
    }
}
