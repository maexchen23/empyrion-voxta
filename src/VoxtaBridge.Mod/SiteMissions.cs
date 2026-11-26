using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>A one-time delivery job of a POI's NPC (RE2 mining sites: "6,000 Ilmenite Ore into the CargoDrop").</summary>
    public sealed class SiteMission
    {
        /// <summary>The NPC's start dialogue (what the prefab references), e.g. "Eden_PolMinerMissionT2_Init".</summary>
        public string Start;
        public List<(string Item, int Count)> Needs = new List<(string, int)>();
        /// <summary>The container on the POI the items go into (the quest dialogue checks it), e.g. "CargoDrop".</summary>
        public string Container;
        /// <summary>Per-player (and POI) variable that is 1 once the job is done.</summary>
        public string DoneVar;
        /// <summary>Credits paid on hand-in (the largest fixed amount among the rewards), 0 = none or not fixed.</summary>
        public int Credits;
        /// <summary>The player picks the reward (several reward steps), e.g. credits or reputation or ore.</summary>
        public bool RewardChoice;
        /// <summary>Faction whose standing the NPC checks first, and the minimum tier ("Neutral"), or null.</summary>
        public string RepFaction, RepMinTier;
    }

    /// <summary>
    /// One-time delivery jobs from Dialogues.ecf (game-free, unit-tested): a <c>…_Check</c> step that calls
    /// <c>HandleQuestItemDrop('&lt;container&gt;', requiredItems)</c> with a fixed item list (amounts may be variables the
    /// family sets to a number), a <c>…_Init</c> that skips the job once a per-player-and-POI variable is 1, and the reward
    /// step that sets it. RE2: the mining-site NPCs (T1 2,000 Hematite … T4 30,000 Chromite).
    /// </summary>
    public sealed class SiteMissions
    {
        readonly Dictionary<string, SiteMission> byStart = new Dictionary<string, SiteMission>(StringComparer.Ordinal);

        public int Count => byStart.Count;
        public IEnumerable<SiteMission> All => byStart.Values.OrderBy(m => m.Start, StringComparer.Ordinal);
        public SiteMission ByStart(string start) => start != null && byStart.TryGetValue(start, out var m) ? m : null;

        static readonly Regex Drop = new Regex(@"HandleQuestItemDrop\(\s*'([^']+)'\s*,\s*requiredItems\s*\)", RegexOptions.Compiled);
        static readonly Regex Need = new Regex(@"\(\s*('?)(\w+)\1\s*,\s*(\w+)\s*\)", RegexOptions.Compiled);
        static readonly Regex Assign = new Regex(@"\b(\w+)\s*=\s*(\d+)\s*(?:;|$)", RegexOptions.Compiled);
        static readonly Regex DoneGate = new Regex(@"^""?\s*(\w+)\s*==\s*1\s*""?$", RegexOptions.Compiled);
        static readonly Regex RepGate = new Regex(@"GetReputation\(Faction\.(\w+)\)\s*<\s*Reputation\.(\w+)Min", RegexOptions.Compiled);
        static readonly Regex Credit = new Regex(@"Player\.Credit\s*\+=\s*(\d+)", RegexOptions.Compiled);
        static readonly Regex PerPoiDecl = new Regex(@"^""(\w+)""\s*,\s*param1:\s*dbplayerpoi\w*", RegexOptions.Compiled);

        public void LoadDialogues(TextReader reader)
        {
            var blocks = TradeMissions.ReadBlocks(reader);
            var byName = blocks.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var check in blocks.Where(b => b.Name.EndsWith("_Check", StringComparison.Ordinal)))
            {
                var exec = check.All("Execute").FirstOrDefault(e => Drop.IsMatch(e));
                if (exec == null) continue;
                var prefix = check.Name.Substring(0, check.Name.Length - "_Check".Length);
                if (!byName.TryGetValue(prefix + "_Init", out var init)) continue;
                var family = blocks.Where(b => b.Name.StartsWith(prefix + "_", StringComparison.Ordinal)).ToList();

                // numbers the family assigns to plain variables (e.g. "Quantity = 100" in the Init)
                var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var e in family.SelectMany(b => b.All("Execute")))
                    foreach (Match a in Assign.Matches(e.Trim().Trim('"').Replace("<nl>", ";")))
                        numbers[a.Groups[1].Value] = int.Parse(a.Groups[2].Value, CultureInfo.InvariantCulture);

                var m = new SiteMission { Start = init.Name, Container = Drop.Match(exec).Groups[1].Value };
                var list = exec.Substring(0, exec.IndexOf("HandleQuestItemDrop", StringComparison.Ordinal));
                var brace = list.IndexOf('{');
                if (brace < 0) continue;
                list = list.Substring(brace); // after "new List<(string, int)> {"
                var ok = true;
                foreach (Match n in Need.Matches(list))
                {
                    if (n.Groups[1].Value != "'") { ok = false; break; } // item from a variable: computed list, not a fixed job
                    var amount = n.Groups[3].Value;
                    if (int.TryParse(amount, out var c) || numbers.TryGetValue(amount, out c)) m.Needs.Add((n.Groups[2].Value, c));
                    else { ok = false; break; }
                }
                if (!ok || m.Needs.Count == 0) continue;

                var perPoi = new HashSet<string>(init.All("Variable").Select(v => PerPoiDecl.Match(v)).Where(x => x.Success).Select(x => x.Groups[1].Value), StringComparer.Ordinal);
                foreach (var (key, cond) in init.Lines.Where(l => l.key.StartsWith("NextIf_", StringComparison.Ordinal)))
                {
                    if (m.DoneVar == null && DoneGate.Match(cond.Trim()) is Match g && g.Success && perPoi.Contains(g.Groups[1].Value)) m.DoneVar = g.Groups[1].Value;
                    if (m.RepFaction == null && RepGate.Match(cond) is Match r && r.Success) { m.RepFaction = r.Groups[1].Value; m.RepMinTier = r.Groups[2].Value; }
                }
                if (m.DoneVar == null) continue;

                // rewards: the steps that set the done variable to 1
                var setDone = new Regex(@"\b" + Regex.Escape(m.DoneVar) + @"\s*=\s*1\b");
                var rewards = family.Where(b => b.All("Execute").Concat(b.All("OptionExecute")).Any(e => setDone.IsMatch(e))).ToList();
                m.RewardChoice = rewards.Count > 1;
                m.Credits = rewards.SelectMany(b => b.All("Execute").Concat(b.All("OptionExecute")))
                    .SelectMany(e => Credit.Matches(e).Cast<Match>()).Select(c => int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture))
                    .DefaultIfEmpty(0).Max();
                byStart[m.Start] = m;
            }
        }
    }
}
