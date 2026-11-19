using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VoxtaBridge
{
    /// <summary>
    /// The generated galaxy as the galaxy map shows it: GalaxyGenerator.StarInfo entries held by a game singleton on the
    /// dedi (found 2026-11-18: 51,911 stars on dev; pos in LY = global.db sector / 100000; typeIndex = 0-based star class
    /// position in GalaxyConfig.ecf). The game assembly is obfuscated, so the list is found by type, not by name: the
    /// largest List&lt;StarInfo&gt; among static fields and singleton instance fields. Read once, copied into plain structs.
    /// </summary>
    public partial class BridgeMod
    {
        public struct Star
        {
            public string Name;
            public int X, Y, Z, TypeIndex;
            public bool Hidden, Manual;
            /// <summary>Owning factions the game assigned to the system (FactionGroup ids, -1 = none) and their strengths.</summary>
            public int Faction1, Faction2;
            public float Strength1, Strength2;
        }

        static List<Star> galaxyStars;
        static string galaxyStarsError;
        static readonly object galaxyLock = new object();

        static List<Star> GalaxyStars()
        {
            lock (galaxyLock)
            {
                if (galaxyStars != null || galaxyStarsError != null) return galaxyStars;
                try { galaxyStars = ReadGalaxyStars(out galaxyStarsError); }
                catch (Exception e) { galaxyStarsError = e.Message; }
                Log(galaxyStars != null ? $"galaxy: {galaxyStars.Count} stars read" : $"galaxy: not available ({galaxyStarsError})");
                return galaxyStars;
            }
        }

        /// <summary>GET /debug/stars?near=x,y,z: the 25 closest stars with class, territory and owning factions.</summary>
        static string DebugStars(string query)
        {
            var stars = GalaxyStars();
            if (stars == null) return Error(galaxyStarsError ?? "no stars");
            double x = 190, y = 15, z = 50;
            var m = System.Text.RegularExpressions.Regex.Match(query ?? "", @"near=(-?[\d.]+),(-?[\d.]+),(-?[\d.]+)");
            if (m.Success) { x = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture); y = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture); z = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture); }
            var odds = galaxyOdds;
            var j = new Json().Obj().Prop("count", stars.Count).Key("histFaction1").Obj();
            foreach (var g in stars.Where(s => !s.Hidden).GroupBy(s => s.Faction1).OrderByDescending(g => g.Count())) j.Prop(g.Key.ToString(), g.Count());
            j.EndObj().Key("stars").Arr();
            foreach (var s in stars.Where(s => !s.Hidden).OrderBy(s => (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y) + (s.Z - z) * (s.Z - z)).Take(25))
            {
                var at = odds?.TerritoriesAt(s.X, s.Y, s.Z);
                j.Obj().Prop("name", s.Name).Prop("class", odds?.Class(s.TypeIndex)?.StarClass).Prop("pos", $"{s.X},{s.Y},{s.Z}")
                    .Prop("f1", s.Faction1).Prop("s1", Math.Round(s.Strength1, 2)).Prop("f2", s.Faction2).Prop("s2", Math.Round(s.Strength2, 2))
                    .Prop("territory", at == null ? null : string.Join("+", at.Select(a => a.t.Faction ?? "neutral"))).Prop("manual", s.Manual).EndObj();
            }
            return j.EndArr().EndObj().ToString();
        }

        static List<Star> ReadGalaxyStars(out string error)
        {
            error = null;
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            var starType = asm?.GetType("GalaxyGenerator")?.GetNestedType("StarInfo", BindingFlags.Public | BindingFlags.NonPublic);
            if (starType == null) { error = "GalaxyGenerator.StarInfo not found"; return null; }
            var listType = typeof(List<>).MakeGenericType(starType);
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            IList best = null;
            foreach (var t in types)
            {
                FieldInfo[] fields;
                try { fields = t.GetFields(all); } catch { continue; }
                foreach (var f in fields.Where(f => f.IsStatic))
                {
                    object v;
                    try { v = f.GetValue(null); } catch { continue; }
                    if (v == null) continue;
                    if (f.FieldType == listType) { if (best == null || ((IList)v).Count > best.Count) best = (IList)v; continue; }
                    if (f.FieldType != t) continue;
                    foreach (var inner in fields.Where(x => !x.IsStatic && x.FieldType == listType))
                    {
                        object iv;
                        try { iv = inner.GetValue(v); } catch { continue; }
                        if (iv is IList l && (best == null || l.Count > best.Count)) best = l;
                    }
                }
            }
            if (best == null || best.Count == 0) { error = "no generated star list on this process"; return null; }

            var fPos = starType.GetField("pos"); var fType = starType.GetField("typeIndex"); var fName = starType.GetField("manualName");
            var fHide = starType.GetField("HideFromMap"); var fManual = starType.GetField("bManuallyPlaced");
            var px = fPos.FieldType.GetField("x"); var py = fPos.FieldType.GetField("y"); var pz = fPos.FieldType.GetField("z");
            if (px == null || py == null || pz == null) { error = "star position has no x/y/z fields"; return null; }
            var fF1 = starType.GetField("faction1"); var fF2 = starType.GetField("faction2");
            var fS1 = starType.GetField("faction1Strength"); var fS2 = starType.GetField("faction2Strength");
            // DomainTable (obfuscated) wraps a FactionData; FactionData.Group is the FactionGroup enum
            var facDataField = fF1?.FieldType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(x => x.FieldType.Name == "FactionData");
            var groupField = facDataField?.FieldType.GetField("Group");
            int Fac(object dt)
            {
                if (dt == null || facDataField == null || groupField == null) return -1;
                var fd = facDataField.GetValue(dt);
                return fd == null ? -1 : Convert.ToInt32(groupField.GetValue(fd));
            }
            var result = new List<Star>(best.Count);
            foreach (var s in best)
            {
                var pos = fPos.GetValue(s);
                result.Add(new Star
                {
                    Name = fName.GetValue(s) as string, TypeIndex = (int)fType.GetValue(s),
                    X = (int)px.GetValue(pos), Y = (int)py.GetValue(pos), Z = (int)pz.GetValue(pos),
                    Hidden = (bool)fHide.GetValue(s), Manual = (bool)fManual.GetValue(s),
                    Faction1 = Fac(fF1?.GetValue(s)), Faction2 = Fac(fF2?.GetValue(s)),
                    Strength1 = fS1 == null ? 0 : (float)fS1.GetValue(s), Strength2 = fS2 == null ? 0 : (float)fS2.GetValue(s),
                });
            }
            return result;
        }
    }
}
