using System;
using System.Collections.Generic;
using System.Linq;

namespace VoxtaBridge
{
    /// <summary>
    /// Names and star lookup for Voxta's galaxy map markers (game-free, unit-tested). Markers are private star bookmarks
    /// named "&lt;star&gt; (Voxta)" or, for a route, "&lt;n&gt; &lt;star&gt; (Voxta)"; the suffix marks them as ours so
    /// the player's own markers are never touched.
    /// </summary>
    public static class StarMarks
    {
        public const string Suffix = " (Voxta)";
        public const int MaxMarks = 12;
        public const string DonePrefix = "done: ";
        public static readonly string UnusedName = "unused" + Suffix;

        public const int MaxNameLength = 40;

        /// <summary>A name for a marker the player keeps: not empty, not too long, and never one of Voxta's own names.</summary>
        public static bool ValidPermanentName(string raw, out string name, out string error)
        {
            name = raw?.Trim() ?? "";
            error = null;
            if (name.Length == 0) error = "'name' is required.";
            else if (name.Length > MaxNameLength) error = $"The name is too long (at most {MaxNameLength} characters).";
            else if (name.EndsWith(Suffix.Trim(), StringComparison.OrdinalIgnoreCase) || name.StartsWith(DonePrefix, StringComparison.OrdinalIgnoreCase))
                error = "Names ending in '(Voxta)' or starting with 'done:' are reserved for Voxta's temporary markers.";
            return error == null;
        }

        public static string MarkerName(string star, int number) => (number > 0 ? number + " " : "") + star + Suffix;

        public static bool IsOurs(string markerName) => markerName != null && markerName.EndsWith(Suffix, StringComparison.Ordinal);

        /// <summary>"Elo Alpha, Rubathra; Isomnir" → names, trimmed, empty ones dropped, duplicates removed (order kept).</summary>
        public static List<string> SplitNames(string list)
        {
            var r = new List<string>();
            if (string.IsNullOrWhiteSpace(list)) return r;
            foreach (var part in list.Split(new[] { ',', ';', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var n = part.Trim().Trim('"', '\'');
                if (n.Length > 0 && !r.Contains(n, StringComparer.OrdinalIgnoreCase)) r.Add(n);
            }
            return r;
        }

        /// <summary>
        /// A star by name: exact (case-insensitive), else the only one starting with the name; otherwise null with up to
        /// five suggestions (names starting with or containing it, closest to <paramref name="near"/> first).
        /// </summary>
        public static T Find<T>(IEnumerable<T> stars, Func<T, string> name, Func<T, double> distance, string wanted, out List<string> suggestions)
            where T : struct
        {
            suggestions = new List<string>();
            var w = wanted.Trim();
            var list = stars.Where(s => !string.IsNullOrEmpty(name(s))).ToList();
            var exact = list.Where(s => string.Equals(name(s), w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) return exact.OrderBy(distance).First();
            var prefix = list.Where(s => name(s).StartsWith(w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (prefix.Count == 1) return prefix[0];
            var similar = prefix.Concat(list.Where(s => name(s).IndexOf(w, StringComparison.OrdinalIgnoreCase) > 0)).OrderBy(distance)
                .Select(name).Distinct(StringComparer.OrdinalIgnoreCase).Take(5);
            suggestions.AddRange(similar);
            return default;
        }
    }
}
