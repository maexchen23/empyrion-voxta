using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>One screen of a POI's info terminal: its text and the options it offers.</summary>
    public sealed class TerminalPage
    {
        public string Name, Text;
        public List<string> Options = new List<string>();
    }

    /// <summary>
    /// The info terminals of POIs (abandoned bunkers, reactors, launch controllers): the dialogue tree that starts at
    /// <c>POI_&lt;X&gt;_Init</c> in the scenario's Dialogues.ecf, with the English texts from Dialogues.csv. Game-free;
    /// the game only shows these when the player stands at the terminal, so the bridge hands them out only for POIs the
    /// player is at or has visited.
    /// </summary>
    public sealed class PoiTerminals
    {
        sealed class Node
        {
            public string Output, NpcName;
            public List<(string label, string next)> Options = new List<(string, string)>();
            public List<string> Next = new List<string>();
        }

        readonly Dictionary<string, Node> nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int DialogueCount => nodes.Count;
        public int InitCount => nodes.Keys.Count(k => k.StartsWith("POI_", StringComparison.OrdinalIgnoreCase) && k.EndsWith("_Init", StringComparison.OrdinalIgnoreCase));

        static readonly Regex Header = new Regex(@"^\{\s*\+?\s*Dialogue\s+Name:\s*(?<name>[^\s,#]+)", RegexOptions.IgnoreCase);
        static readonly Regex KeyValue = new Regex(@"^(?<key>[A-Za-z_]+?)(?:_(?<n>\d+))?\s*:\s*(?<value>.*)$");

        public void LoadDialogues(TextReader reader)
        {
            string line;
            Node cur = null;
            var labels = new Dictionary<int, string>();
            var targets = new Dictionary<int, string>();
            void Flush()
            {
                if (cur == null) return;
                foreach (var n in labels.Keys.OrderBy(x => x))
                    cur.Options.Add((labels[n], targets.TryGetValue(n, out var t) ? t : null));
                labels.Clear(); targets.Clear();
            }
            while ((line = reader.ReadLine()) != null)
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                var h = Header.Match(t);
                if (h.Success)
                {
                    Flush();
                    cur = new Node();
                    nodes[h.Groups["name"].Value] = cur;
                    continue;
                }
                if (t.StartsWith("}")) { Flush(); cur = null; continue; }
                if (cur == null) continue;
                var m = KeyValue.Match(t);
                if (!m.Success) continue;
                var value = m.Groups["value"].Value.Trim();
                var comma = value.IndexOf(',');
                var first = (comma >= 0 ? value.Substring(0, comma) : value).Trim().Trim('"');
                int.TryParse(m.Groups["n"].Value, out var num);
                switch (m.Groups["key"].Value)
                {
                    case "Output": cur.Output = first; break;
                    case "NPCName": cur.NpcName = first; break;
                    case "Option": labels[num] = first; break;
                    case "OptionNext": targets[num] = first; break;
                    case "Next": cur.Next.Add(first); break;
                }
            }
            Flush();
        }

        /// <summary>Dialogues.csv: KEY, English, … (texts referenced by Output/Option/NPCName).</summary>
        public void LoadTexts(TextReader reader)
        {
            foreach (var row in GameText.ReadCsv(reader))
            {
                if (row.Count < 2) continue;
                var key = row[0].Trim();
                if (key.Length == 0 || key == "KEY") continue;
                var text = CleanMarkup(row[1]);
                if (text.Length > 0) texts[key] = text;
            }
        }

        static readonly Regex Tags = new Regex(@"</?(?:b|i|u|size|color|sup|sub|mark|align)(?:=[^>]*)?>", RegexOptions.IgnoreCase);
        static readonly Regex Codes = new Regex(@"@[a-z]\d+", RegexOptions.IgnoreCase);
        static readonly Regex Spaces = new Regex(@"[ \t]{2,}");

        /// <summary>Unity rich text and the game's typing codes (@d0 delay, @w2 wait, @q0 …) removed; \n kept as line breaks.</summary>
        public static string CleanMarkup(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\\n", "\n");
            s = Tags.Replace(s, "");
            s = Codes.Replace(s, "");
            s = Spaces.Replace(s, " ");
            var lines = s.Split('\n').Select(l => l.Trim()).ToList();
            return string.Join("\n", lines).Trim();
        }

        string Text(string key) => key != null && texts.TryGetValue(key, out var t) ? t : key == null ? null : CleanMarkup(key);

        /// <summary>The terminal's name (NPCName of the Init dialogue), or null.</summary>
        public string TerminalName(string init) =>
            nodes.TryGetValue(init ?? "", out var n) && n.NpcName != null ? Text(n.NpcName) : null;

        /// <summary>
        /// The pages reachable from <paramref name="init"/> (breadth first, only dialogues of the same POI_X_ family; End,
        /// Return and other families stop the walk), with their text and option labels, until about
        /// <paramref name="maxChars"/> characters.
        /// </summary>
        public List<TerminalPage> Pages(string init, int maxChars = 3000)
        {
            var result = new List<TerminalPage>();
            if (init == null || !nodes.ContainsKey(init)) return result;
            var family = init.EndsWith("_Init", StringComparison.OrdinalIgnoreCase) ? init.Substring(0, init.Length - "Init".Length) : init;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { init };
            var queue = new Queue<string>();
            queue.Enqueue(init);
            var total = 0;
            while (queue.Count > 0 && total < maxChars)
            {
                var name = queue.Dequeue();
                if (!nodes.TryGetValue(name, out var node)) continue;
                var text = Text(node.Output);
                if (!string.IsNullOrEmpty(text))
                {
                    var page = new TerminalPage { Name = name, Text = text };
                    page.Options.AddRange(node.Options.Select(o => Text(o.label)).Where(o => !string.IsNullOrEmpty(o)));
                    result.Add(page);
                    total += text.Length;
                }
                foreach (var next in node.Next.Concat(node.Options.Select(o => o.next)))
                {
                    if (string.IsNullOrEmpty(next) || !next.StartsWith(family, StringComparison.OrdinalIgnoreCase)) continue;
                    if (seen.Add(next)) queue.Enqueue(next);
                }
            }
            return result;
        }
    }
}
