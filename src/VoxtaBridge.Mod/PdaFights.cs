using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>One wave of a scripted fight: the ships that spawn, then the cores (or other blocks) to destroy.</summary>
    public sealed class FightWave
    {
        /// <summary>Blocks the player has to destroy (PDA <c>BlockDestroyed</c> Amount).</summary>
        public int Targets;
        /// <summary>Block types that count (PDA <c>Types</c>; RE2 almost always <c>CoreNPC</c>).</summary>
        public List<string> Types = new List<string>();
        /// <summary>Ships spawned for this wave (<c>SpawnOPV</c> counts), 0 = the targets were there already.</summary>
        public int Ships;
        /// <summary>What spawns, tidied ("Bandit", "Bandit Bomber").</summary>
        public List<string> What = new List<string>();
        /// <summary>Friendly ships spawned to help (a faction named like "…Defender"), e.g. "2 Polaris".</summary>
        public int Allies;
        public List<string> AlliesWhat = new List<string>();
        /// <summary>Seconds the PDA waits before this wave (<c>WaitAction</c> amounts since the previous wave).</summary>
        public int DelayS;
        /// <summary>The PDA's message when the wave starts ("A fleet of enemy ships have warped in! Defend the transport!").</summary>
        public string Message;
    }

    /// <summary>A PDA chapter that is a scripted fight: waves of targets to destroy, and what the player does afterwards.</summary>
    public sealed class FightChapter
    {
        public string Id, Title, StartMessage;
        public List<FightWave> Waves = new List<FightWave>();
        /// <summary>The first task after the last wave ("Deactivate the emergency beacon."), or null.</summary>
        public string After;
        public int TotalTargets => Waves.Sum(w => w.Targets);
    }

    /// <summary>
    /// The scenario's scripted fights from PDA.yaml (game-free, unit-tested): every chapter with <c>BlockDestroyed</c> tasks
    /// (hidden ones too: RE2's combat-site defenses, the bandit beacon ambush, smuggler jobs). The game reports chapter
    /// starts and ends to the server but not task progress, so the bridge counts the destroyed blocks itself.
    /// </summary>
    public sealed class PdaFights
    {
        readonly Dictionary<string, FightChapter> byId = new Dictionary<string, FightChapter>(StringComparer.Ordinal);

        public int Count => byId.Count;
        public IEnumerable<FightChapter> All => byId.Values;
        public FightChapter ById(string id) => id != null && byId.TryGetValue(id, out var f) ? f : null;

        static readonly Regex ChapterSplit = new Regex(@"\n\s{2}- ChapterTitle:\s*", RegexOptions.Compiled);
        static readonly Regex TaskSplit = new Regex(@"\n\s{6}- TaskTitle:\s*", RegexOptions.Compiled);
        static readonly Regex Action = new Regex(@"Check:\s*(\w+)", RegexOptions.Compiled);
        static readonly Regex Amount = new Regex(@"\n\s*Amount:\s*(\d+)", RegexOptions.Compiled);
        static readonly Regex TypesList = new Regex(@"\n\s*Types:\s*\n((?:\s*-\s*[^\n]+\n?)+)", RegexOptions.Compiled);
        static readonly Regex ListItem = new Regex(@"-\s*([^\n]+)", RegexOptions.Compiled);
        static readonly Regex Spawn = new Regex(@"SpawnOPV:\s*([^;\s]+);([^;\s]+);(\d+);", RegexOptions.Compiled);
        static readonly Regex StartMsg = new Regex(@"\n\s{8}StartMessage:\s*'?\w*;\d+\|((?:eden_)?pda_\w+)", RegexOptions.Compiled);
        static readonly Regex ChapterStartMsg = new Regex(@"\n\s{4}StartMessage:\s*'?\w*;\d+\|((?:eden_)?pda_\w+)", RegexOptions.Compiled);
        static readonly Regex Markup = new Regex(@"\[[^\]]*\]", RegexOptions.Compiled);

        public void Load(TextReader yaml, TextReader csv)
        {
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            if (csv != null)
                foreach (var row in GameText.ReadCsv(csv))
                    if (row.Count >= 2 && row[0].Length > 0 && !texts.ContainsKey(row[0])) texts[row[0]] = row[1];
            string Text(string key)
            {
                if (key == null) return null;
                key = key.Trim().Trim('\'', '"');
                if (texts.TryGetValue(key, out var t) || (key.StartsWith("eden_", StringComparison.Ordinal) && texts.TryGetValue(key.Substring(5), out t)))
                    return Markup.Replace(t, "").Replace("\\n", " ").Replace("  ", " ").Trim();
                return null;
            }

            foreach (var chapter in ChapterSplit.Split("\n" + yaml.ReadToEnd().Replace("\r\n", "\n")).Skip(1))
            {
                if (chapter.IndexOf("BlockDestroyed", StringComparison.Ordinal) < 0) continue;
                var id = chapter.Split('\n')[0].Trim().Trim('\'', '"');
                var tasksAt = chapter.IndexOf("\n    Tasks:", StringComparison.Ordinal);
                if (tasksAt < 0) continue;
                var head = chapter.Substring(0, tasksAt);
                var f = new FightChapter { Id = id, Title = Text(id) ?? id, StartMessage = ChapterStartMsg.Match(head) is Match sm && sm.Success ? Text(sm.Groups[1].Value) : null };

                int ships = 0, delay = 0, allies = 0;
                var what = new List<string>();
                var alliesWhat = new List<string>();
                var afterLast = false;
                foreach (var task in TaskSplit.Split(chapter.Substring(tasksAt)).Skip(1))
                {
                    var title = Text(task.Split('\n')[0]);
                    var checks = Action.Matches(task).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                    if (checks.Contains("BlockDestroyed"))
                    {
                        var amount = Amount.Match(task.Substring(task.IndexOf("BlockDestroyed", StringComparison.Ordinal)));
                        var wave = new FightWave
                        {
                            Targets = amount.Success ? int.Parse(amount.Groups[1].Value, CultureInfo.InvariantCulture) : 1,
                            Ships = ships, What = what.Distinct().ToList(), DelayS = delay, Allies = allies, AlliesWhat = alliesWhat.Distinct().ToList(),
                            Message = StartMsg.Match(task) is Match m && m.Success ? Text(m.Groups[1].Value) : null,
                        };
                        if (TypesList.Match(task) is Match ty && ty.Success)
                            wave.Types.AddRange(ListItem.Matches(ty.Groups[1].Value).Cast<Match>().Select(x => x.Groups[1].Value.Trim().Trim('\'', '"')));
                        if (wave.Types.Count == 0) wave.Types.Add("CoreNPC");
                        f.Waves.Add(wave);
                        ships = 0; delay = 0; allies = 0; what = new List<string>(); alliesWhat = new List<string>(); afterLast = true;
                        f.After = null;
                    }
                    else
                    {
                        if (checks.Contains("WaitAction") && Amount.Match(task) is Match w && w.Success) delay += int.Parse(w.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (afterLast && f.After == null && title != null && !checks.Contains("WaitAction")) f.After = title;
                    }
                    // ships spawned when this task completes (or starts) belong to the next wave
                    foreach (Match s in Spawn.Matches(task))
                    {
                        var n = int.Parse(s.Groups[3].Value, CultureInfo.InvariantCulture);
                        if (s.Groups[1].Value.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            allies += n;
                            alliesWhat.Add(ShipLabel(s.Groups[2].Value));
                        }
                        else
                        {
                            ships += n;
                            what.Add(ShipLabel(s.Groups[2].Value));
                        }
                    }
                }
                if (f.Waves.Count == 0) continue;
                byId[f.Id] = f;
            }
        }

        /// <summary>"RE2_OPVBanditBomberT1" → "Bandit Bomber".</summary>
        public static string ShipLabel(string prefab)
        {
            var s = Regex.Replace(prefab, @"^(RE2_|R2|Eden_)?(OPV|MPV|BAO|BA)?_?", "");
            s = Regex.Replace(s, @"T\d+\w?$", "");
            s = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])", " ").Trim();
            return s.Length > 0 ? s : prefab;
        }
    }
}
