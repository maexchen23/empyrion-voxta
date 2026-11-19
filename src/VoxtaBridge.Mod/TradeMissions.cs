using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>One item (or token) a mission asks for.</summary>
    public sealed class MissionNeed
    {
        public string Item;
        public int Count;
        /// <summary>Token id for <c>HasItem('Token', 1, id)</c>, else null.</summary>
        public int? TokenId;
    }

    /// <summary>One numbered mission of a trader dialogue (<c>TD_X_Mission03</c>).</summary>
    public sealed class TradeMission
    {
        public int Number;
        public string Dialogue, OutputKey;
        public List<MissionNeed> Needs = new List<MissionNeed>();
        /// <summary>Money reward (money card, platinum card value is not known, credits); 0 = none found.</summary>
        public int Money;
        /// <summary>"MoneyCard", "PlatinumCard" (count, not credits), "Credits" or null.</summary>
        public string MoneyAs;
        /// <summary>Reputation per faction name (from <c>AddReputation(Faction.X, n)</c>); "*" = the POI's own faction.</summary>
        public Dictionary<string, int> Reputation = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The hand-in raises CanTrade (an unlock or follow-up mission).</summary>
        public bool Unlocks;
    }

    /// <summary>
    /// A trader's mission dialogue family (<c>TD_PowerSolaris_*</c>): the variables it keeps per player and POI, its
    /// numbered missions, and the GIN work order that can replace a mission.
    /// </summary>
    public sealed class MissionFamily
    {
        public string Name, Start, NpcNameKey;
        /// <summary>Per player and POI (<c>dbplayerpoipos_int</c>): the active mission number, 0 = none.</summary>
        public string MissionVar;
        /// <summary>Per player (<c>dbplayer_int</c>): the GIN job counter, or null.</summary>
        public string JobVar;
        /// <summary>True when the trade window only opens after a mission (<c>CanTrade > 0</c> gates it).</summary>
        public bool TradeGated;
        /// <summary>Follow-up missions are offered while CanTrade is below this (RE2: 3); 0 = unknown.</summary>
        public int FollowUpsBelow;
        public int? WorkOrderToken;
        public int? WorkOrderPrice;
        public SortedDictionary<int, TradeMission> Missions = new SortedDictionary<int, TradeMission>();
    }

    /// <summary>A player's state at one trader, read from the save's DialogueVars rows (type 110) of that POI.</summary>
    public sealed class MissionState
    {
        /// <summary>"open" (a mission is waiting), "locked" (trade gated, no mission yet), "unlocked", "none" (nothing to do).</summary>
        public string Status;
        public TradeMission Mission;
        /// <summary>The open mission is an optional follow-up of an already unlocked trader.</summary>
        public bool FollowUp;
        public int CanTrade;
    }

    /// <summary>Index of the scenario's trader mission dialogues (game-free, unit-tested).</summary>
    public sealed class TradeMissions
    {
        readonly Dictionary<string, MissionFamily> families = new Dictionary<string, MissionFamily>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, MissionFamily> byStart = new Dictionary<string, MissionFamily>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int Count => families.Count;
        public IEnumerable<MissionFamily> Families => families.Values;

        static readonly Regex Header = new Regex(@"^\{\s*\+Dialogue\s+Name:\s*(\S+)", RegexOptions.Compiled);
        static readonly Regex KeyValue = new Regex(@"^\s*([A-Za-z]+(?:_\d+)?)\s*:\s*(.*)$", RegexOptions.Compiled);
        static readonly Regex VariableDecl = new Regex(@"^""(\w+)""\s*,\s*param1:\s*(\w+)", RegexOptions.Compiled);
        static readonly Regex HasItem = new Regex(@"HasItem\('([^']+)',\s*(\d+)(?:,\s*(\d+))?\)", RegexOptions.Compiled);
        static readonly Regex Rep = new Regex(@"AddReputation\(Faction\.(\w+),\s*(-?\d+)\)", RegexOptions.Compiled);
        static readonly Regex OwnFactionRep = new Regex(@"if\s*\(GetFaction\(LocalStructure", RegexOptions.Compiled);
        static readonly Regex MoneyItem = new Regex(@"AddItem\('(MoneyCard|PlatinumCard)',\s*(\d+)\)", RegexOptions.Compiled);
        static readonly Regex Credit = new Regex(@"Player\.Credit\s*\+=\s*(\d+)", RegexOptions.Compiled);
        static readonly Regex CheckIf = new Regex(@"^""\s*(\w+)\s*==\s*(\d+)\s*""$", RegexOptions.Compiled);
        static readonly Regex FollowUpIf = new Regex(@"CanTrade\s*<\s*(\d+)", RegexOptions.Compiled);
        static readonly Regex GinSale = new Regex(@"Player\.Credit\s*-=\s*(\d+)\s*;.*AddItem\('Token',\s*1,\s*(\d+)\)", RegexOptions.Compiled);

        sealed class Block
        {
            public string Name;
            public List<(string key, string value)> Lines = new List<(string, string)>();
            public IEnumerable<string> All(string prefix) => Lines.Where(l => l.key.StartsWith(prefix, StringComparison.Ordinal)).Select(l => l.value);
            public string First(string key) => Lines.FirstOrDefault(l => l.key == key).value;
            /// <summary>The value of key_N for the same N as the given line key (Option_2 → OptionIf_2).</summary>
            public string Same(string key, string ofKey)
            {
                var us = ofKey.LastIndexOf('_');
                return us < 0 ? null : First(key + ofKey.Substring(us));
            }
        }

        static List<Block> ReadBlocks(TextReader reader)
        {
            var list = new List<Block>();
            Block cur = null;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var t = line.Trim();
                var h = Header.Match(t);
                if (h.Success) { cur = new Block { Name = h.Groups[1].Value }; list.Add(cur); continue; }
                if (cur == null) continue;
                if (t.StartsWith("}")) { cur = null; continue; }
                if (t.StartsWith("#")) continue;
                var kv = KeyValue.Match(t);
                if (kv.Success) cur.Lines.Add((kv.Groups[1].Value, kv.Groups[2].Value.Trim()));
            }
            return list;
        }

        /// <summary>Dialogues.ecf: every <c>TD_X_Start</c> that declares a per-POI mission variable, with its missions.</summary>
        public void LoadDialogues(TextReader reader)
        {
            var blocks = ReadBlocks(reader);
            var byName = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in blocks) byName[b.Name] = b;
            var ginPrice = new Dictionary<int, int>();
            foreach (var b in blocks)
                foreach (var ex in b.All("OptionExecute"))
                {
                    var g = GinSale.Match(ex);
                    if (g.Success) ginPrice[int.Parse(g.Groups[2].Value)] = int.Parse(g.Groups[1].Value);
                }

            foreach (var start in blocks.Where(b => b.Name.StartsWith("TD_", StringComparison.Ordinal) && b.Name.EndsWith("_Start", StringComparison.Ordinal)))
            {
                var fam = new MissionFamily { Start = start.Name, Name = start.Name.Substring(0, start.Name.Length - "_Start".Length), NpcNameKey = start.First("NPCName") };
                var poiVars = new List<string>();
                foreach (var v in start.All("Variable"))
                {
                    var m = VariableDecl.Match(v);
                    if (!m.Success) continue;
                    if (m.Groups[2].Value == "dbplayerpoipos_int") poiVars.Add(m.Groups[1].Value);
                    else if (m.Groups[2].Value == "dbplayer_int" && m.Groups[1].Value.EndsWith("Job", StringComparison.Ordinal)) fam.JobVar = m.Groups[1].Value;
                }
                fam.MissionVar = poiVars.FirstOrDefault(v => v.EndsWith("Mission", StringComparison.Ordinal)) ?? poiVars.FirstOrDefault(v => v != "CanTrade");
                if (fam.MissionVar == null) continue;

                var startText = string.Join("\n", start.Lines.Select(l => l.value));
                fam.TradeGated = poiVars.Contains("CanTrade") && startText.IndexOf("OpenTraderWindow()", StringComparison.Ordinal) < 0
                                 && startText.IndexOf("CanTrade > 0", StringComparison.Ordinal) >= 0;
                var token = HasItem.Matches(startText).Cast<Match>().FirstOrDefault(m => m.Groups[1].Value == "Token" && m.Groups[3].Success);
                if (token != null && poiVars.Contains("CanTrade"))
                {
                    fam.WorkOrderToken = int.Parse(token.Groups[3].Value);
                    if (ginPrice.TryGetValue(fam.WorkOrderToken.Value, out var price)) fam.WorkOrderPrice = price;
                }

                // follow-ups: "Mission == 0 && CanTrade < 3" on the trade page
                foreach (var b in blocks.Where(b => b.Name.StartsWith(fam.Name + "_", StringComparison.Ordinal)))
                    foreach (var cond in b.All("OptionIf"))
                        if (cond.IndexOf(fam.MissionVar + " == 0", StringComparison.Ordinal) >= 0 && FollowUpIf.Match(cond) is Match fu && fu.Success)
                            fam.FollowUpsBelow = Math.Max(fam.FollowUpsBelow, int.Parse(fu.Groups[1].Value));

                // mission numbers: MissionCheck's "Var == N" → block, else the number in the name
                var numbered = new Dictionary<int, string>();
                if (byName.TryGetValue(fam.Name + "_MissionCheck", out var check))
                    foreach (var (key, value) in check.Lines.Where(l => l.key.StartsWith("NextIf_", StringComparison.Ordinal)))
                    {
                        var c = CheckIf.Match(value);
                        var next = check.Same("Next", key);
                        if (c.Success && c.Groups[1].Value == fam.MissionVar && next != null) numbered[int.Parse(c.Groups[2].Value)] = next.Split(',')[0].Trim();
                    }
                if (numbered.Count == 0)
                    foreach (var b in blocks)
                    {
                        var m = Regex.Match(b.Name, "^" + Regex.Escape(fam.Name) + @"_Mission(\d+)$");
                        if (m.Success) numbered[int.Parse(m.Groups[1].Value)] = b.Name;
                    }
                foreach (var kv in numbered)
                    if (byName.TryGetValue(kv.Value, out var mb) && ParseMission(mb, kv.Key) is TradeMission tm) fam.Missions[kv.Key] = tm;
                if (fam.Missions.Count == 0) continue;
                families[fam.Name] = fam;
                byStart[fam.Start] = fam;
            }
        }

        static TradeMission ParseMission(Block b, int number)
        {
            // the hand-in option: the one whose condition asks for items
            foreach (var (key, cond) in b.Lines.Where(l => l.key.StartsWith("OptionIf_", StringComparison.Ordinal)))
            {
                var needs = HasItem.Matches(cond).Cast<Match>().Select(m => new MissionNeed
                {
                    Item = m.Groups[1].Value, Count = int.Parse(m.Groups[2].Value), TokenId = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : (int?)null,
                }).ToList();
                if (needs.Count == 0) continue;
                var tm = new TradeMission { Number = number, Dialogue = b.Name, OutputKey = b.First("Output"), Needs = needs };
                var ex = b.Same("OptionExecute", key) ?? "";
                var mi = MoneyItem.Match(ex);
                if (mi.Success) { tm.Money = int.Parse(mi.Groups[2].Value); tm.MoneyAs = mi.Groups[1].Value; }
                else if (Credit.Match(ex) is Match cr && cr.Success) { tm.Money = int.Parse(cr.Groups[1].Value); tm.MoneyAs = "Credits"; }
                if (OwnFactionRep.IsMatch(ex))
                {
                    var r = Rep.Match(ex);
                    if (r.Success) tm.Reputation["*"] = int.Parse(r.Groups[2].Value);
                }
                else
                    foreach (Match r in Rep.Matches(ex)) tm.Reputation[r.Groups[1].Value] = int.Parse(r.Groups[2].Value);
                tm.Unlocks = Regex.IsMatch(ex, @"CanTrade\s*=\s*CanTrade\s*\+\s*1");
                return tm;
            }
            return null;
        }

        /// <summary>Dialogues.csv: KEY, English, … (for the NPC names and mission texts).</summary>
        public void LoadTexts(TextReader reader)
        {
            foreach (var row in GameText.ReadCsv(reader))
            {
                if (row.Count < 2) continue;
                var key = row[0].Trim();
                if (key.Length == 0 || key == "KEY") continue;
                var text = PoiTerminals.CleanMarkup(row[1]);
                if (text.Length > 0) texts[key] = text;
            }
        }

        public string Text(string key) => key != null && texts.TryGetValue(key, out var t) ? t : null;

        public MissionFamily ByStart(string start) => start != null && byStart.TryGetValue(start, out var f) ? f : null;
        public MissionFamily ByName(string name) => name != null && families.TryGetValue(name, out var f) ? f : null;

        /// <summary>
        /// The family of a POI's DialogueVars rows: among the families the POI's prefab uses, the one whose mission variable
        /// is in the rows (several factories share "IndustrialMission", so the prefab decides first).
        /// </summary>
        public MissionFamily Resolve(IEnumerable<string> prefabStarts, IDictionary<string, int> vars)
        {
            var cands = (prefabStarts ?? Enumerable.Empty<string>()).Select(ByStart).Where(f => f != null).ToList();
            var hit = cands.FirstOrDefault(f => vars.ContainsKey(f.MissionVar));
            if (hit != null) return hit;
            if (cands.Count > 0) return null; // the rows belong to another dialogue of this POI (none we index)
            // no prefab info: only when the variable name is unique
            var byVar = families.Values.Where(f => vars.ContainsKey(f.MissionVar)).ToList();
            return byVar.Count == 1 ? byVar[0] : null;
        }

        /// <summary>The player's state at one trader, from that POI's rows (missing rows = 0).</summary>
        public static MissionState State(MissionFamily fam, IDictionary<string, int> vars)
        {
            vars.TryGetValue(fam.MissionVar, out var n);
            vars.TryGetValue("CanTrade", out var canTrade);
            var st = new MissionState { CanTrade = canTrade };
            if (n > 0 && fam.Missions.TryGetValue(n, out var m))
            {
                st.Status = "open"; st.Mission = m; st.FollowUp = fam.TradeGated && canTrade > 0;
                return st;
            }
            if (fam.TradeGated) st.Status = canTrade > 0 ? "unlocked" : "locked";
            else st.Status = "none";
            return st;
        }
    }
}
