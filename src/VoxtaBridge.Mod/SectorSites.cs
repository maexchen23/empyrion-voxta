using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>The scenario's warp-in report for a sector type (the PDA shows it to the player anyway).</summary>
    public sealed class SectorBriefing
    {
        /// <summary>"science" or "security" (from the report's title), else "sector".</summary>
        public string Kind;
        public string Text;
    }

    /// <summary>A probe-scan site: the player's scanning table resolves anomalies here with probes.</summary>
    public sealed class ScanSite
    {
        public string Dialogue, Label, ProbeItem;
        /// <summary>Per player and playfield: 1 once scanned; and the stored probe count once the player looked (may be null).</summary>
        public string DoneVar, CountVar;
        /// <summary>
        /// The probe count the table rolls on first use: difficulty = <see cref="ScoreVar"/> + Random(RollMin, RollMax),
        /// count = clamp(difficulty / Divisor, ClampMin, ClampMax). Null ScoreVar = formula not found.
        /// </summary>
        public string ScoreVar;
        public int RollMin, RollMax, Divisor = 1, ClampMin, ClampMax;
        /// <summary>
        /// Per-playfield variable the scan needs at 1 or more before the table rolls (RE2: <c>Eden_AnomalyBonus</c>, set by
        /// a mission such as the nebula's escape pods); null = the site can always be scanned.
        /// </summary>
        public string GateVar;

        /// <summary>The probes the table will ask for with this score (null when the formula is unknown).</summary>
        public (int Min, int Max)? ProbeRange(long score)
        {
            if (ScoreVar == null || Divisor <= 0) return null;
            int Count(long roll) => (int)Math.Max(ClampMin, Math.Min(ClampMax, (score + roll) / Divisor));
            return (Count(RollMin), Count(RollMax));
        }
    }

    /// <summary>
    /// A mission against the clock, per player and playfield (RE2: the nebula's escape pods: 10 minutes of oxygen for 5
    /// crew): accepting it sets <see cref="TimerVar"/> to game time + <see cref="Seconds"/> and <see cref="StateVar"/> to
    /// <see cref="ActiveValue"/>; other dialogues raise <see cref="CounterVar"/> (1 … <see cref="Total"/>); the reward sets
    /// the state to <see cref="DoneValue"/>.
    /// </summary>
    public sealed class TimedMission
    {
        public string Start, Npc, TimerVar, StateVar, CounterVar;
        public int Seconds, ActiveValue, DoneValue = 1, Total;
    }

    /// <summary>An NPC that calls for help (distress, escort, besieged station): its plea, per player once.</summary>
    public sealed class HelpCall
    {
        public string Start;
        /// <summary>The plea in the NPC's own words; for rolled variants the part they share ("Please help us! Our ship's …").</summary>
        public string Plea;
        /// <summary>Per-player variable that reaches <see cref="DoneValue"/> once helped.</summary>
        public string DoneVar;
        public int DoneValue = 1;
        /// <summary>When the details are rolled on first contact: the rolled variable and the plea per value.</summary>
        public string SelectorVar;
        public Dictionary<int, string> PleaByValue = new Dictionary<int, string>();
    }

    /// <summary>
    /// What a sector broadcasts (game-free, unit-tested): warp-in reports from PDA.yaml (chapters activated by
    /// <c>PlayfieldTypeEntered</c>), probe-scan sites (chapters for a playfield type that open a scan dialogue), and NPCs
    /// calling for help from Dialogues.ecf (a plea in their greeting, a per-player done gate, a paid reward).
    /// </summary>
    public sealed class SectorSites
    {
        readonly Dictionary<string, SectorBriefing> briefings = new Dictionary<string, SectorBriefing>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> scanDialogueByType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, ScanSite> scanByDialogue = new Dictionary<string, ScanSite>(StringComparer.Ordinal);
        readonly Dictionary<string, HelpCall> helpByStart = new Dictionary<string, HelpCall>(StringComparer.Ordinal);

        public int BriefingCount => briefings.Count;
        public int ScanTypeCount => scanDialogueByType.Count;
        /// <summary>The per-playfield probe count variables of the scan sites (set once the player used the table there).</summary>
        public IEnumerable<string> ScanCountVars => scanByDialogue.Values.Select(s => s.CountVar).Where(v => v != null).Distinct();
        public IEnumerable<HelpCall> HelpCalls => helpByStart.Values.OrderBy(h => h.Start, StringComparer.Ordinal);
        public HelpCall Help(string start) => start != null && helpByStart.TryGetValue(start, out var h) ? h : null;
        readonly List<TimedMission> timed = new List<TimedMission>();
        public IReadOnlyList<TimedMission> Timed => timed;

        /// <summary>The report for the first of these sector type names that has one (folder name, then PlanetType).</summary>
        public SectorBriefing Briefing(params string[] types) =>
            types.Where(t => !string.IsNullOrEmpty(t)).Select(t => briefings.TryGetValue(t, out var b) ? b : null).FirstOrDefault(b => b != null);

        public ScanSite Scan(params string[] types) =>
            types.Where(t => !string.IsNullOrEmpty(t)).Select(t => scanDialogueByType.TryGetValue(t, out var d) && scanByDialogue.TryGetValue(d, out var s) ? s : null)
                .FirstOrDefault(s => s != null);

        // ----- PDA -------------------------------------------------------------------------------------------------

        static readonly Regex ChapterSplit = new Regex(@"\n\s{2}- ChapterTitle:\s*", RegexOptions.Compiled);
        static readonly Regex Entered = new Regex(@"Check:\s*PlayfieldTypeEntered[ \t]*\n\s*Names:[ \t]*\n((?:[ \t]*-[ \t]*[^\n]+\n?)+)", RegexOptions.Compiled);
        static readonly Regex TypesList = new Regex(@"\n\s{4}PlayfieldTypes:[ \t]*\n((?:[ \t]*-[ \t]*[^\n]+\n?)+)", RegexOptions.Compiled);
        static readonly Regex ListItem = new Regex(@"-[ \t]*([^\n]+)", RegexOptions.Compiled);
        static readonly Regex Hidden = new Regex(@"^\s{4}Visibility:\s*Never\b", RegexOptions.Compiled | RegexOptions.Multiline);
        static readonly Regex TextKey = new Regex(@"(?:^|[|;:\s])((?:eden_)?pda_\w+)", RegexOptions.Compiled);
        static readonly Regex DlgMessage = new Regex(@"(?:StartMessage|CompletedMessage):\s*'?dlg;(\w+)\|", RegexOptions.Compiled);
        static readonly Regex Markup = new Regex(@"\[[^\]]*\]", RegexOptions.Compiled);
        static readonly Regex Boilerplate = new Regex(@"briefing can be reviewed|Displaying a message|^Downloading|^Message$|^Complete$|Assessment$|^Wait", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public void LoadPda(TextReader yaml, TextReader csv)
        {
            var texts = ReadCsv(csv);
            string Text(string key)
            {
                if (texts.TryGetValue(key, out var t) || (key.StartsWith("eden_", StringComparison.Ordinal) && texts.TryGetValue(key.Substring(5), out t)))
                    return Markup.Replace(t, "").Replace("\\n", " ").Replace("  ", " ").Trim();
                return null;
            }
            foreach (var chapter in ChapterSplit.Split("\n" + yaml.ReadToEnd().Replace("\r\n", "\n")).Skip(1))
            {
                var title = Text(chapter.Split('\n')[0].Trim().Trim('\'', '"')) ?? "";

                // scan chapters may be hidden (RE2: the nebula's pod scans, orbit scans for missions)
                var dlg = DlgMessage.Match(chapter);
                var types = TypesList.Match(chapter);
                if (dlg.Success && types.Success)
                    foreach (Match t in ListItem.Matches(types.Groups[1].Value))
                    {
                        var type = t.Groups[1].Value.Trim().Trim('\'', '"');
                        if (!scanDialogueByType.ContainsKey(type)) scanDialogueByType[type] = dlg.Groups[1].Value;
                    }

                if (Hidden.IsMatch(chapter)) continue;
                var entered = Entered.Match(chapter);
                if (!entered.Success) continue;
                var names = ListItem.Matches(entered.Groups[1].Value).Cast<Match>().Select(m => m.Groups[1].Value.Trim().Trim('\'', '"')).ToList();
                var all = TextKey.Matches(chapter).Cast<Match>().Select(m => Text(m.Groups[1].Value)).Where(s => !string.IsNullOrEmpty(s)).ToList();
                var kind = all.Any(s => s.Equals("Security Assessment", StringComparison.OrdinalIgnoreCase)) || title.StartsWith("Security", StringComparison.OrdinalIgnoreCase)
                    ? "security"
                    : all.Any(s => s.Equals("Science Assessment", StringComparison.OrdinalIgnoreCase)) || title.Contains("Assessment") ? "science" : "sector";
                // the report: the longest text that isn't boilerplate; the log copy starts with the type name, drop that
                var report = all.Where(s => s.Length > 40 && !Boilerplate.IsMatch(s))
                    .Select(s => names.Aggregate(s, (acc, n) => acc.StartsWith(n, StringComparison.OrdinalIgnoreCase) ? acc.Substring(n.Length).Trim() : acc))
                    .OrderByDescending(s => s.Length).FirstOrDefault();
                if (report == null) continue;
                foreach (var n in names)
                    if (!briefings.ContainsKey(n)) briefings[n] = new SectorBriefing { Kind = kind, Text = report };
            }
        }

        static Dictionary<string, string> ReadCsv(TextReader csv)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (csv == null) return d;
            foreach (var row in GameText.ReadCsv(csv))
                if (row.Count >= 2 && row[0].Length > 0 && !d.ContainsKey(row[0])) d[row[0]] = row[1];
            return d;
        }

        // ----- dialogues ---------------------------------------------------------------------------------------------

        static readonly Regex VarDecl = new Regex(@"^""(\w+)""\s*,\s*param1:\s*(\w+)", RegexOptions.Compiled);
        static readonly Regex ProbeType = new Regex(@"\b\w*ProbeType\s*=\s*'(\w+)'", RegexOptions.Compiled);
        static readonly Regex SiteType = new Regex(@"\bSiteType\s*=\s*'([^']+)'", RegexOptions.Compiled);
        static readonly Regex Gate = new Regex(@"^""?\s*(\w+)\s*(==|>=)\s*(\d+)\s*""?$", RegexOptions.Compiled);
        static readonly Regex Plea = new Regex(@"\b(please help|please,? you have to help|you have to help|help (me|us)\b|help defend|need (your )?(help|assistance)|could use your assistance)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex Thanks = new Regex(@"\bthank", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex GatePart = new Regex(@"\b(\w+)\s*(==|>=)\s*(\d+)\b", RegexOptions.Compiled);
        static readonly Regex Paid = new Regex(@"Player\.Credit\s*\+=|AddReputation\(", RegexOptions.Compiled);
        static readonly Regex Roll = new Regex(@"\b(\w+)\s*=\s*Random\(\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.Compiled);
        static readonly Regex Cmp = new Regex(@"^""?\s*(\w+)\s*(==|>=)\s*(\d+)\s*""?$", RegexOptions.Compiled);
        static readonly Regex Placeholder = new Regex(@"\{[^}]*\}|@\w\d", RegexOptions.Compiled);

        /// <summary>
        /// Scan dialogues (for the scan sites from the PDA) and NPCs calling for help. <paramref name="exclude"/>: start
        /// dialogues handled elsewhere (mining jobs, trader missions, contracts).
        /// </summary>
        public void LoadDialogues(TextReader ecf, TextReader csv, ISet<string> exclude = null)
        {
            var texts = ReadCsv(csv);
            string Text(string key) => key != null && texts.TryGetValue(key.Trim(), out var t)
                ? Placeholder.Replace(Markup.Replace(Regex.Replace(t, "<[^>]+>", ""), ""), "").Replace("\\n", " ").Replace("  ", " ").Trim()
                : null;
            var blocks = TradeMissions.ReadBlocks(ecf);
            var byName = blocks.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            LoadTimed(blocks, byName, Text);

            // scan sites: probe type, label, done and count variables from the dialogue the PDA opens
            foreach (var dlg in scanDialogueByType.Values.Distinct())
            {
                if (!byName.TryGetValue(dlg, out var b)) continue;
                var exec = string.Join(";", b.All("Execute"));
                var probe = ProbeType.Match(exec);
                if (!probe.Success) continue;
                var vars = b.All("Variable").Select(v => VarDecl.Match(v)).Where(m => m.Success).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
                var done = b.Lines.Where(l => l.key.StartsWith("NextIf_", StringComparison.Ordinal)).Select(l => Gate.Match(l.value.Trim()))
                    .Where(m => m.Success && m.Groups[3].Value == "1" && vars.TryGetValue(m.Groups[1].Value, out var t) && t.StartsWith("dbplayerpf", StringComparison.Ordinal))
                    .Select(m => m.Groups[1].Value).FirstOrDefault();
                var site = scanByDialogue[dlg] = new ScanSite
                {
                    Dialogue = dlg, ProbeItem = probe.Groups[1].Value, Label = SiteType.Match(exec) is Match st && st.Success ? st.Groups[1].Value : null,
                    DoneVar = done, CountVar = vars.Keys.FirstOrDefault(v => v.EndsWith("ProbeCount", StringComparison.Ordinal) && vars[v].StartsWith("dbplayerpf", StringComparison.Ordinal)),
                };
                // the roll on first use, in a step the scan dialogue leads to: "D = Score + Random(a,b); … Count = Math.Clamp((D / n), lo, hi)"
                if (site.CountVar != null)
                    foreach (var (nextKey, nextValue) in b.Lines.Where(l => l.key.StartsWith("Next_", StringComparison.Ordinal)))
                    {
                        var target = nextValue.Split(',')[0].Trim();
                        if (!byName.TryGetValue(target, out var t)) continue;
                        var e = string.Join(";", t.All("Execute"));
                        var count = new Regex(Regex.Escape(site.CountVar) + @"\s*=\s*Math\.Clamp\(\s*\(?\s*(\w+)\s*/\s*(\d+)\s*\)?\s*,\s*(\d+)\s*,\s*(\d+)\s*\)").Match(e);
                        if (!count.Success) continue;
                        var roll = new Regex(@"\b" + Regex.Escape(count.Groups[1].Value) + @"\s*=\s*(\w+)\s*\+\s*Random\(\s*(\d+)\s*,\s*(\d+)\s*\)").Match(e);
                        if (!roll.Success) continue;
                        int N(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture);
                        site.ScoreVar = roll.Groups[1].Value;
                        site.RollMin = N(roll.Groups[2]); site.RollMax = N(roll.Groups[3]);
                        site.Divisor = N(count.Groups[2]); site.ClampMin = N(count.Groups[3]); site.ClampMax = N(count.Groups[4]);
                        // "Difficulty == 0 && Eden_AnomalyBonus >= 1": the roll waits for a mission
                        var cond = b.Same("NextIf", nextKey) ?? "";
                        site.GateVar = Regex.Matches(cond, @"\b(\w+)\s*>=\s*1\b").Cast<Match>().Select(m => m.Groups[1].Value)
                            .FirstOrDefault(v => v != count.Groups[1].Value && vars.TryGetValue(v, out var vt) && vt.StartsWith("dbplayerpf", StringComparison.Ordinal));
                        break;
                    }
            }

            // calls for help: families <prefix>_Init / _Start with a per-player done gate, a paid reward that sets it, a plea
            foreach (var start in blocks.Where(b => b.Name.EndsWith("_Init", StringComparison.Ordinal) || b.Name.EndsWith("_Start", StringComparison.Ordinal)))
            {
                if (exclude != null && exclude.Contains(start.Name)) continue;
                var prefix = start.Name.Substring(0, start.Name.LastIndexOf('_'));
                var family = blocks.Where(b => b.Name.StartsWith(prefix + "_", StringComparison.Ordinal)).ToList();
                var vars = family.SelectMany(b => b.All("Variable")).Select(v => VarDecl.Match(v)).Where(m => m.Success)
                    .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
                bool PerPlayer(string v) => vars.TryGetValue(v, out var t) && t.StartsWith("dbplayer", StringComparison.Ordinal) && t != "dbplayer_int";

                string doneVar = null;
                var doneValue = 1;
                // the done gate may be part of a longer condition ("Eden_Complete == 1 && IsBlockActive('Generator')")
                foreach (var g in start.Lines.Where(l => l.key.StartsWith("NextIf_", StringComparison.Ordinal))
                             .SelectMany(l => GatePart.Matches(l.value).Cast<Match>()))
                {
                    if (!PerPlayer(g.Groups[1].Value)) continue;
                    var v = int.Parse(g.Groups[3].Value, CultureInfo.InvariantCulture);
                    var sets = new Regex(@"\b" + Regex.Escape(g.Groups[1].Value) + @"\s*=\s*" + v + @"\b(?!\s*[=.])");
                    // the reward step sets the gate's value and pays
                    if (v >= 1 && family.Any(b => b.All("Execute").Concat(b.All("OptionExecute")).Any(e => sets.IsMatch(e) && Paid.IsMatch(e))))
                    {
                        doneVar = g.Groups[1].Value;
                        doneValue = v;
                        break;
                    }
                }
                if (doneVar == null) continue;

                // pleas: greetings that ask for help, not the thank-you after it
                var pleaByBlock = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var b in family)
                {
                    var t = Text(b.First("Output"));
                    if (t != null && Plea.IsMatch(t) && !Thanks.IsMatch(t)) pleaByBlock[b.Name] = t;
                }
                if (pleaByBlock.Count == 0) continue;
                var call = new HelpCall { Start = start.Name, DoneVar = doneVar, DoneValue = doneValue };
                var pleas = pleaByBlock.Values.Distinct().ToList();
                call.Plea = pleas[0];

                // details rolled on first contact: Var = Random(a,b), then a block choosing by "Var >= n" / "Var == n"
                var roll = family.SelectMany(b => b.All("Execute").Concat(b.All("OptionExecute"))).Select(e => Roll.Match(e)).FirstOrDefault(m => m.Success && PerPlayer(m.Groups[1].Value));
                if (pleas.Count > 1 && roll != null)
                {
                    call.Plea = CommonStart(pleas);
                    var sel = roll.Groups[1].Value;
                    var chooser = family.FirstOrDefault(b =>
                    {
                        var conds = b.Lines.Where(l => l.key.StartsWith("NextIf_", StringComparison.Ordinal)).ToList();
                        return conds.Count > 1 && conds.All(l => Cmp.Match(l.value.Trim()) is Match c && c.Success && c.Groups[1].Value == sel);
                    });
                    if (chooser != null)
                    {
                        call.SelectorVar = sel;
                        int lo = int.Parse(roll.Groups[2].Value, CultureInfo.InvariantCulture), hi = int.Parse(roll.Groups[3].Value, CultureInfo.InvariantCulture);
                        for (var v = lo; v <= hi; v++)
                            foreach (var (key, cond) in chooser.Lines.Where(l => l.key.StartsWith("NextIf_", StringComparison.Ordinal)))
                            {
                                var c = Cmp.Match(cond.Trim());
                                var n = int.Parse(c.Groups[3].Value, CultureInfo.InvariantCulture);
                                if (!(c.Groups[2].Value == "==" ? v == n : v >= n)) continue;
                                var target = chooser.Same("Next", key)?.Split(',')[0].Trim();
                                if (target != null && pleaByBlock.TryGetValue(target, out var p)) call.PleaByValue[v] = p;
                                break;
                            }
                    }
                }
                helpByStart[call.Start] = call;
            }
        }

        static readonly Regex TimerSet = new Regex(@"\b(\w+)\s*=\s*\(?\s*GetGameTimeInSec\(\)\s*\+\s*(\d+)\s*\)?", RegexOptions.Compiled);
        static readonly Regex ConstSet = new Regex(@"\b(\w+)\s*=\s*(\d+)\b(?!\s*[=.])", RegexOptions.Compiled);

        /// <summary>Timed missions: an option that sets a per-playfield variable to "game time + N" (see <see cref="TimedMission"/>).</summary>
        void LoadTimed(List<TradeMissions.Block> blocks, Dictionary<string, TradeMissions.Block> byName, Func<string, string> text)
        {
            var allSets = blocks.SelectMany(b => b.All("Execute").Concat(b.All("OptionExecute"))).SelectMany(e => ConstSet.Matches(e).Cast<Match>()).ToList();
            foreach (var b in blocks)
                foreach (var e in b.All("OptionExecute").Concat(b.All("Execute")))
                {
                    var tm = TimerSet.Match(e);
                    if (!tm.Success) continue;
                    var prefix = b.Name.Substring(0, b.Name.LastIndexOf('_'));
                    if (!byName.TryGetValue(prefix + "_Init", out var start)) continue;
                    var vars = start.All("Variable").Select(v => VarDecl.Match(v)).Where(m => m.Success)
                        .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
                    bool PerPf(string v) => vars.TryGetValue(v, out var t) && t.StartsWith("dbplayerpf", StringComparison.Ordinal);
                    if (!PerPf(tm.Groups[1].Value)) continue;
                    // the state: set in the same option, and the start checks it for the done value
                    var doneChecks = start.Lines.Where(l => l.key.StartsWith("NextIf_", StringComparison.Ordinal))
                        .SelectMany(l => Regex.Matches(l.value, @"\b(\w+)\s*==\s*(\d+)").Cast<Match>()).ToList();
                    var state = ConstSet.Matches(e).Cast<Match>().FirstOrDefault(m => PerPf(m.Groups[1].Value) && doneChecks.Any(d => d.Groups[1].Value == m.Groups[1].Value));
                    if (state == null) continue;
                    var m0 = new TimedMission
                    {
                        Start = start.Name, Npc = text(start.First("NPCName")) ?? start.First("NPCName"), TimerVar = tm.Groups[1].Value,
                        Seconds = int.Parse(tm.Groups[2].Value, CultureInfo.InvariantCulture), StateVar = state.Groups[1].Value,
                        ActiveValue = int.Parse(state.Groups[2].Value, CultureInfo.InvariantCulture),
                    };
                    // the counter: a per-playfield variable of the start that dialogues set to 1, 2, … N (N >= 2)
                    foreach (var v in vars.Keys.Where(v => PerPf(v) && v != m0.TimerVar && v != m0.StateVar))
                    {
                        var values = new HashSet<int>(allSets.Where(m => m.Groups[1].Value == v).Select(m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
                        var n = values.Count > 0 ? values.Max() : 0;
                        if (n >= 2 && Enumerable.Range(1, n).All(values.Contains)) { m0.CounterVar = v; m0.Total = n; break; }
                    }
                    if (timed.All(t => t.Start != m0.Start)) timed.Add(m0);
                }
        }

        /// <summary>The words the pleas start with in common ("Please help us! Our ship's"), else the first sentence of the first.</summary>
        static string CommonStart(List<string> texts)
        {
            var words = texts.Select(t => t.Split(' ')).ToList();
            var n = 0;
            while (words.All(w => w.Length > n && w[n] == words[0][n])) n++;
            if (n >= 3) return string.Join(" ", words[0].Take(n)) + " …";
            var first = texts[0];
            var dot = first.IndexOfAny(new[] { '.', '!' });
            return dot > 0 ? first.Substring(0, dot + 1) : first;
        }
    }
}
