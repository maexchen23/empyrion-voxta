using System;
using System.Collections.Generic;

namespace VoxtaBridge
{
    /// <summary>Text helpers for the game chat (game-free, unit-tested).</summary>
    public static class ChatText
    {
        /// <summary>Splits a long text at sentence or word boundaries into chat-sized parts (game-free).</summary>
        public static List<string> Split(string text, int max)
        {
            var parts = new List<string>();
            text = text.Replace("\r", "").Trim();
            while (text.Length > max)
            {
                var cut = -1;
                foreach (var sep in new[] { "\n", ". ", "! ", "? ", "; ", ", ", " " })
                {
                    var i = text.LastIndexOf(sep, max - 1, StringComparison.Ordinal);
                    if (i > max / 3) { cut = i + sep.Length; break; }
                }
                if (cut <= 0) cut = max;
                parts.Add(text.Substring(0, cut).Trim());
                text = text.Substring(cut).Trim();
            }
            if (text.Length > 0) parts.Add(text);
            return parts;
        }
    }
}
