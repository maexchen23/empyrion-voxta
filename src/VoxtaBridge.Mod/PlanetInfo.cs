using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>
    /// What the game shows about one generated planet, moon or sector: the instanced template the save keeps per playfield
    /// (Saves/Games/&lt;game&gt;/Templates/&lt;playfield&gt;/playfield.yaml): description (difficulty, special effects, lore), gravity,
    /// atmosphere, temperatures, radiation and the resources it can have. Game-free.
    /// </summary>
    public sealed class PlanetInfo
    {
        public string Description, Difficulty, PlanetType;
        public List<string> Effects = new List<string>();
        /// <summary>Gravity in g (|Gravity| / 9.81), null if not set.</summary>
        public double? GravityG;
        public double? AtmosphereDensity, AtmosphereO2, Radiation;
        /// <summary>AtmosphereBreathable as rolled for this playfield; missing means not breathable (the game's default).</summary>
        public bool Breathable;
        public double? TemperatureMin, TemperatureMax, TemperatureNight;
        public List<string> Resources = new List<string>();

        static readonly Regex Markup = new Regex(@"\[(?:/?[a-z]{1,4}|-|[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)\]", RegexOptions.IgnoreCase);

        static double? Num(string s) =>
            double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;

        public static PlanetInfo Parse(string yaml)
        {
            var p = new PlanetInfo();
            if (string.IsNullOrEmpty(yaml)) return p;
            var lines = yaml.Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.Length == 0 || char.IsWhiteSpace(l[0]) || l[0] == '-' || l[0] == '#') continue;
                var colon = l.IndexOf(':');
                if (colon <= 0) continue;
                var key = l.Substring(0, colon).Trim();
                var value = l.Substring(colon + 1).Trim();
                switch (key)
                {
                    case "Gravity": if (Num(value) is double g) p.GravityG = Math.Abs(g) / 9.81; break;
                    case "AtmosphereDensity": p.AtmosphereDensity = Num(value); break;
                    case "AtmosphereO2": p.AtmosphereO2 = Num(value); break;
                    case "AtmosphereBreathable": p.Breathable = value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "Radiation": p.Radiation = Num(value); break;
                    case "TemperatureNight": p.TemperatureNight = Num(value); break;
                    case "PlanetType": p.PlanetType = value.Trim('"'); break;
                    case "TemperatureMinMax":
                        var vals = Items(lines, i).Select(Num).Where(x => x.HasValue).Select(x => x.Value).ToList();
                        if (vals.Count >= 2) { p.TemperatureMin = vals.Min(); p.TemperatureMax = vals.Max(); }
                        break;
                    case "Description":
                        p.SetDescription(value.StartsWith(">") || value.StartsWith("|") ? Block(lines, i) : value.Trim('"'));
                        break;
                    case "RandomResources":
                        for (var k = i + 1; k < lines.Length && (lines[k].Length == 0 || lines[k][0] == ' ' || lines[k][0] == '-'); k++)
                        {
                            var m = Regex.Match(lines[k], @"^-\s*Name:\s*(\S+)");
                            if (m.Success && !p.Resources.Contains(m.Groups[1].Value)) p.Resources.Add(m.Groups[1].Value);
                        }
                        break;
                }
            }
            return p;
        }

        /// <summary>"- 10" list items right under a key.</summary>
        static IEnumerable<string> Items(string[] lines, int at)
        {
            for (var k = at + 1; k < lines.Length && lines[k].StartsWith("-"); k++) yield return lines[k].Substring(1);
        }

        /// <summary>A folded/literal block: the indented lines after the key; blank lines separate paragraphs.</summary>
        static string Block(string[] lines, int at)
        {
            var parts = new List<string>();
            var para = new List<string>();
            for (var k = at + 1; k < lines.Length; k++)
            {
                var l = lines[k];
                if (l.Length > 0 && !char.IsWhiteSpace(l[0])) break;
                if (l.Trim().Length == 0) { if (para.Count > 0) { parts.Add(string.Join(" ", para)); para.Clear(); } continue; }
                para.Add(l.Trim());
            }
            if (para.Count > 0) parts.Add(string.Join(" ", para));
            return string.Join("\n", parts);
        }

        void SetDescription(string raw)
        {
            var text = Markup.Replace(raw ?? "", "").Replace("\\n", "\n");
            var lore = new List<string>();
            foreach (var line in text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                var m = Regex.Match(line, @"^Difficulty:\s*(.+)$", RegexOptions.IgnoreCase);
                if (m.Success) { Difficulty = m.Groups[1].Value.Trim(); continue; }
                // "Sensor Interference: POI detection range reduced." style lines are special effects
                if (Regex.IsMatch(line, @"^[A-Z][A-Za-z' -]{2,40}:\s+\S")) { Effects.Add(line); continue; }
                lore.Add(line);
            }
            Description = string.Join(" ", lore).Trim();
        }
    }
}
