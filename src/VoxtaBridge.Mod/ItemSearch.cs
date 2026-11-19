using System;
using System.Collections.Generic;
using System.Linq;

namespace VoxtaBridge
{
    /// <summary>
    /// Fuzzy item search by name (game-free, unit-tested). The character guesses names ("12.7mm Ammo") that the game
    /// spells differently ("12.7mm Rounds"), so besides exact and substring matches the query is split into words,
    /// with a few synonyms (ammo = rounds = bullet …) and plural forms.
    /// </summary>
    public static class ItemSearch
    {
        public struct Hit
        {
            public string Internal;
            public string Display;
            public int Score;
            public override string ToString() => $"{Display} [{Internal}] {Score}";
        }

        static readonly string[][] Synonyms =
        {
            new[] { "ammo", "ammunition", "round", "rounds", "bullet", "bullets", "shell", "shells", "cartridge", "cartridges", "charge", "charges" },
            new[] { "gun", "guns", "weapon", "weapons" },
            new[] { "plate", "plates" },
            new[] { "ingot", "ingots", "bar", "bars" },
        };

        static string Squash(string s) => s?.Replace(" ", "").Replace("-", "").Replace("_", "").ToLowerInvariant();

        static IEnumerable<string> Variants(string word)
        {
            yield return word;
            if (word.Length > 3 && word.EndsWith("s")) yield return word.Substring(0, word.Length - 1);
            foreach (var group in Synonyms)
                if (group.Contains(word))
                    foreach (var w in group) if (w != word) yield return w;
        }

        /// <summary>0 = no match, 100 = exact.</summary>
        public static int Score(string query, string display, string internalName)
        {
            if (string.IsNullOrWhiteSpace(query)) return 0;
            var q = Squash(query);
            var d = Squash(display) ?? "";
            var i = Squash(internalName) ?? "";
            if (q == d || q == i) return 100;
            if (d.Length > 0 && d.Contains(q)) return 80;
            if (i.Contains(q)) return 70;

            var words = query.ToLowerInvariant().Split(new[] { ' ', '-', '_', '/', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 && !Synonyms.Any(g => g.Contains(words.FirstOrDefault()))) return 0;
            var text = (display ?? "").ToLowerInvariant() + " " + (internalName ?? "").ToLowerInvariant();
            var matched = words.Count(w => Variants(w).Any(v => text.Contains(v)));
            if (matched == 0 || matched * 2 < words.Length) return 0;
            return 60 * matched / words.Length;
        }

        /// <summary>
        /// Matches, best first, keeping only those reasonably close to the best one (a "12.7mm Rounds" full word match
        /// hides all the "… Ammo Box" half matches). Duplicate display names keep the best entry.
        /// </summary>
        public static List<Hit> Search(string query, IEnumerable<(string internalName, string display)> candidates)
        {
            var hits = candidates
                .Select(c => new Hit { Internal = c.internalName, Display = c.display ?? c.internalName, Score = Score(query, c.display, c.internalName) })
                .Where(h => h.Score > 0)
                .ToList();
            if (hits.Count == 0) return hits;
            var best = hits.Max(h => h.Score);
            var floor = best >= 60 ? best * 3 / 4 : best;
            return hits.Where(h => h.Score >= floor)
                .OrderByDescending(h => h.Score).ThenBy(h => h.Display, StringComparer.OrdinalIgnoreCase)
                .GroupBy(h => h.Display, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .ToList();
        }
    }
}
