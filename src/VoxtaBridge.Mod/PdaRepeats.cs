using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>A repeatable PDA mission with a waiting time between runs.</summary>
    public sealed class PdaRepeat
    {
        /// <summary>The chapter id as PDA events name it ("eden_pda_UuuGC").</summary>
        public string Chapter;
        /// <summary>The chapter title from PDA.csv ("Research Unlock Points"), or null.</summary>
        public string Title;
        /// <summary>Waiting time after completing it, in seconds (game time).</summary>
        public int DelaySeconds;
        /// <summary>The dialogues whose option completes the chapter (its <c>DialogOption</c> tasks).</summary>
        public List<string> CompleteDialogues = new List<string>();
        /// <summary>
        /// A per-player-and-POI dialogue variable those dialogues set to 1 (e.g. "done at this vessel"), so each POI takes
        /// the mission once; null when there is none.
        /// </summary>
        public string OncePerPoiVar;
    }

    /// <summary>
    /// Repeatable PDA missions with a waiting time (game-free, unit-tested): from the scenario's PDA.yaml
    /// (<c>RepeatConditions: Delay</c> in minutes, <c>DelaySeconds</c> in seconds), titles from PDA.csv, and the dialogues
    /// that complete them from Dialogues.ecf. RE2's "Research Unlock Points": 180 minutes ("you may perform research at a
    /// different research vessel in 3 hours"), one research per vessel.
    /// </summary>
    public sealed class PdaRepeats
    {
        /// <summary>Shorter waits are pacing, not cooldowns worth telling the player about.</summary>
        public const int MinDelaySeconds = 600;

        readonly Dictionary<string, PdaRepeat> byChapter = new Dictionary<string, PdaRepeat>(StringComparer.Ordinal);

        public int Count => byChapter.Count;
        public IEnumerable<PdaRepeat> All => byChapter.Values.OrderBy(r => r.Chapter, StringComparer.Ordinal);
        public PdaRepeat Get(string chapter) => chapter != null && byChapter.TryGetValue(chapter, out var r) ? r : null;

        static readonly Regex ChapterSplit = new Regex(@"\n\s{2}- ChapterTitle:\s*", RegexOptions.Compiled);
        static readonly Regex Repeat = new Regex(@"\n(\s+)RepeatConditions:[ \t]*\r?\n((?:\1\s+\S.*\r?\n?)+)", RegexOptions.Compiled);
        static readonly Regex DelayMin = new Regex(@"^\s*Delay:\s*(\d+)", RegexOptions.Compiled | RegexOptions.Multiline);
        static readonly Regex DelaySec = new Regex(@"^\s*DelaySeconds:\s*(\d+)", RegexOptions.Compiled | RegexOptions.Multiline);
        static readonly Regex DialogOption = new Regex(@"Check:\s*DialogOption[ \t]*\r?\n\s*Names:[ \t]*\r?\n((?:\s*-\s*\S+[ \t]*\r?\n?)+)", RegexOptions.Compiled);
        static readonly Regex Hidden = new Regex(@"^\s{4}Visibility:\s*Never\b", RegexOptions.Compiled | RegexOptions.Multiline);
        static readonly Regex ListItem = new Regex(@"-\s*(\S+)", RegexOptions.Compiled);

        public void LoadPda(TextReader yaml)
        {
            var text = "\n" + yaml.ReadToEnd().Replace("\r\n", "\n");
            foreach (var chapterText in ChapterSplit.Split(text).Skip(1))
            {
                var chapter = chapterText.Split('\n')[0].Trim().Trim('\'', '"');
                var rep = Repeat.Match(chapterText);
                // hidden chapters are the scenario's own triggers (ambushes, penalties), not missions the player runs
                if (chapter.Length == 0 || !rep.Success || Hidden.IsMatch(chapterText)) continue;
                var block = rep.Groups[2].Value;
                var seconds = 0;
                if (DelayMin.Match(block) is Match m && m.Success) seconds += int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60;
                if (DelaySec.Match(block) is Match s && s.Success) seconds += int.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture);
                if (seconds < MinDelaySeconds) continue;
                var r = new PdaRepeat { Chapter = chapter, DelaySeconds = seconds };
                // completing dialogues: the DialogOption checks among the tasks (activation checks come before "Tasks:")
                var tasks = chapterText.IndexOf("Tasks:", StringComparison.Ordinal);
                if (tasks >= 0)
                    foreach (Match d in DialogOption.Matches(chapterText.Substring(tasks)))
                        foreach (Match n in ListItem.Matches(d.Groups[1].Value))
                            if (!r.CompleteDialogues.Contains(n.Groups[1].Value)) r.CompleteDialogues.Add(n.Groups[1].Value);
                byChapter[chapter] = r;
            }
        }

        /// <summary>Chapter titles from PDA.csv (key, English, …); keys may lack the "eden_" prefix the chapter ids have.</summary>
        public void LoadTitles(TextReader csv)
        {
            string line;
            while ((line = csv.ReadLine()) != null)
            {
                var comma = line.IndexOf(',');
                if (comma <= 0) continue;
                var key = line.Substring(0, comma);
                var r = Get(key) ?? Get("eden_" + key);
                if (r == null || r.Title != null) continue;
                var rest = line.Substring(comma + 1);
                string title;
                if (rest.StartsWith("\"", StringComparison.Ordinal))
                {
                    var end = rest.IndexOf("\",", 1, StringComparison.Ordinal);
                    title = end > 0 ? rest.Substring(1, end - 1) : rest.Trim('"');
                }
                else title = rest.Split(',')[0];
                title = Regex.Replace(title, @"\[[^\]]*\]", "").Replace("\\n", " ").Trim();
                if (title.Length > 0) r.Title = title;
            }
        }

        static readonly Regex VarDecl = new Regex(@"Variable_\d+:\s*""(\w+)""\s*,\s*param1:\s*dbplayerpoi\w*", RegexOptions.Compiled);
        static readonly Regex SetToOne = new Regex(@"\b(\w+)\s*=\s*1\b(?!\s*[=.])", RegexOptions.Compiled);

        /// <summary>From Dialogues.ecf: the per-player-and-POI variable each repeat's completing dialogue sets to 1.</summary>
        public void LinkDialogues(TextReader dialogues)
        {
            var blocks = TradeMissions.ReadBlocks(dialogues);
            var perPoi = new HashSet<string>(blocks.SelectMany(b => b.Lines).Select(l => VarDecl.Match(l.key + ": " + l.value))
                .Where(m => m.Success).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
            var byName = blocks.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var r in byChapter.Values)
                foreach (var name in r.CompleteDialogues)
                {
                    if (!byName.TryGetValue(name, out var b)) continue;
                    var set = b.Lines.Where(l => l.key.StartsWith("OptionExecute", StringComparison.Ordinal) || l.key == "Execute")
                        .SelectMany(l => SetToOne.Matches(l.value).Cast<Match>()).Select(m => m.Groups[1].Value)
                        .FirstOrDefault(perPoi.Contains);
                    if (set != null) { r.OncePerPoiVar = set; break; }
                }
        }
    }
}
