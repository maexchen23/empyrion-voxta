using System;
using System.Collections.Generic;
using System.Linq;

namespace VoxtaBridge
{
    /// <summary>application/x-www-form-urlencoded, used for request bodies and dedi ↔ playfield packets (no JSON parser needed).</summary>
    public static class Form
    {
        public static Dictionary<string, string> Parse(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return d;
            foreach (var pair in text.Split('&'))
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                var k = eq < 0 ? pair : pair.Substring(0, eq);
                var v = eq < 0 ? "" : pair.Substring(eq + 1);
                d[Decode(k)] = Decode(v);
            }
            return d;
        }

        public static string Encode(IEnumerable<KeyValuePair<string, string>> values) =>
            string.Join("&", values.Where(kv => kv.Value != null)
                .Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

        public static string Encode(params (string key, string value)[] values) =>
            Encode(values.Select(v => new KeyValuePair<string, string>(v.key, v.value)));

        static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        public static string Get(this Dictionary<string, string> d, string key) =>
            d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    }
}
