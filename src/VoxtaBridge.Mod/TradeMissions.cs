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
        /// <summary>
        /// Missions whose amount is rolled per player (RE2 Mining Foreman: "Eden_Quantity"): the needs' count comes from this
        /// per-POI variable; null = fixed amounts.
        /// </summary>
        public string QuantityVar;
        /// <summary>Per-POI variable that is 1 once the mission is done (then the NPC trades); null = the CanTrade scheme.</summary>
        public string DoneVar;
        /// <summary>
        /// Values of <see cref="DoneVar"/> the start dialogue still sends to the item check (Foreman: 0 and 2; beacon: 0).
        /// Empty = not read from the dialogue: then done is "1", or <see cref="DoneFrom"/>.
        /// </summary>
        public HashSet<int> OpenValues = new HashSet<int>();
        /// <summary>Values from which the start dialogue no longer asks (e.g. <c>Eden_Completed &gt;= 1</c>), or null.</summary>
        public int? DoneFrom;
        /// <summary>A one-off job with no trader behind it (an emergency beacon, a ship in distress), not a trade unlock.</summary>
        public bool Job;
        /// <summary>A probe scan with the ship's scanning table (made up by the bridge from the sector, no dialogue rows).</summary>
        public bool Scan;

        /// <summary>Whether this value of the done variable means the items were handed in.</summary>
        public bool IsDone(int value)
        {
            if (OpenValues.Count > 0) return !OpenValues.Contains(value);
            if (DoneFrom.HasValue) return value >= DoneFrom.Value;
            return value == 1;
        }
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

        internal sealed class Block
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

        internal static List<Block> ReadBlocks(TextReader reader)
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
            LoadQuantityMissions(blocks, byName);
            LoadRolledMissions(blocks, byName);
        }

        static readonly Regex TypeByMission = new Regex(@"if\s*\(\s*(\w+)\s*==\s*(\d+)\s*\)\s*(\w+)\s*=\s*'([^']+)'", RegexOptions.Compiled);
        static readonly Regex HasVarItem = new Regex(@"HasItem\(\s*(\w+)\s*,\s*(\w+)\s*\)", RegexOptions.Compiled);
        static readonly Regex SetsOne = new Regex(@"\b(\w+)\s*=\s*1\b(?!\s*[=.])", RegexOptions.Compiled);

        /// <summary>
        /// Missions shaped like RE2's Mining Foreman (<c>Eden_PolarisForeman_*</c>): the start dialogue maps a rolled mission
        /// number to an item (<c>if (Eden_Mission == 1) Type = 'EnergyCellLarge'</c>), the amount is a per-player-and-POI
        /// variable (<c>HasItem(Type, Eden_Quantity)</c>), and the hand-in sets a done variable to 1 and opens the trader.
        /// One mission per NPC; afterwards it trades, so these count as factory (trade-unlock) missions.
        /// </summary>
        void LoadQuantityMissions(List<Block> blocks, Dictionary<string, Block> byName)
        {
            foreach (var start in blocks.Where(b => b.Name.EndsWith("_Init", StringComparison.Ordinal)))
            {
                var poiVars = start.All("Variable").Select(v => VariableDecl.Match(v)).Where(m => m.Success && m.Groups[2].Value.StartsWith("dbplayerpoi", StringComparison.Ordinal))
                    .Select(m => m.Groups[1].Value).ToList();
                if (poiVars.Count < 2) continue;
                var types = start.All("Execute").SelectMany(e => TypeByMission.Matches(e).Cast<Match>())
                    .Where(m => poiVars.Contains(m.Groups[1].Value)).ToList();
                if (types.Count < 2) continue;
                var missionVar = types[0].Groups[1].Value;
                var typeVar = types[0].Groups[3].Value;
                var prefix = start.Name.Substring(0, start.Name.Length - "_Init".Length);
                var family = blocks.Where(b => b.Name.StartsWith(prefix + "_", StringComparison.Ordinal)).ToList();
                var text = string.Join("\n", family.SelectMany(b => b.Lines.Select(l => l.value)));
                var qty = HasVarItem.Matches(text).Cast<Match>().FirstOrDefault(m => m.Groups[1].Value == typeVar && poiVars.Contains(m.Groups[2].Value));
                if (qty == null) continue;
                // the hand-in: the block that removes the item; it sets the done variable to 1
                var handIn = family.FirstOrDefault(b => b.All("Execute").Concat(b.All("OptionExecute")).Any(e => e.Contains("RemoveItem(" + typeVar)));
                var done = handIn?.All("Execute").Concat(handIn.All("OptionExecute")).SelectMany(e => SetsOne.Matches(e).Cast<Match>())
                    .Select(m => m.Groups[1].Value).FirstOrDefault(poiVars.Contains);
                if (done == null) continue;
                var fam = new MissionFamily
                {
                    Start = start.Name, Name = prefix, NpcNameKey = start.First("NPCName"), MissionVar = missionVar,
                    QuantityVar = qty.Groups[2].Value, DoneVar = done, TradeGated = text.IndexOf("OpenTraderWindow()", StringComparison.Ordinal) >= 0,
                };
                fam.Job = !fam.TradeGated;
                ReadDoneGates(fam, start, byName, b => b.Lines.Any(l => l.value.Contains("HasItem(" + typeVar)));
                foreach (var m in types.Where(m => m.Groups[1].Value == missionVar && m.Groups[3].Value == typeVar))
                {
                    var n = int.Parse(m.Groups[2].Value);
                    fam.Missions[n] = new TradeMission { Number = n, Dialogue = handIn.Name, Needs = { new MissionNeed { Item = m.Groups[4].Value } } };
                }
                families[fam.Name] = fam;
                byStart[fam.Start] = fam;
            }
        }

        static readonly Regex GateCond = new Regex(@"^""\s*(\w+)\s*(==|>=)\s*(\d+)\s*""$", RegexOptions.Compiled);

        /// <summary>
        /// Which values of the done variable still lead to the item check: the start's <c>NextIf "Done == v"</c> branches
        /// whose target reaches a block that checks the items within 3 steps (not through the start again), and
        /// <c>"Done &gt;= v"</c> branches that don't (= done from v on).
        /// </summary>
        static void ReadDoneGates(MissionFamily fam, Block start, Dictionary<string, Block> byName, Func<Block, bool> asksItems)
        {
            bool Reaches(string target)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal) { start.Name };
                var frontier = new List<string> { target };
                for (var depth = 0; depth <= 3 && frontier.Count > 0; depth++)
                {
                    var next = new List<string>();
                    foreach (var name in frontier)
                    {
                        if (!seen.Add(name) || !byName.TryGetValue(name, out var b)) continue;
                        if (asksItems(b)) return true;
                        next.AddRange(b.Lines.Where(l => l.key.StartsWith("Next_", StringComparison.Ordinal) || l.key.StartsWith("OptionNext_", StringComparison.Ordinal))
                            .Select(l => l.value.Split(',')[0].Trim()));
                    }
                    frontier = next;
                }
                return false;
            }
            foreach (var (key, cond) in start.Lines.Where(l => l.key.StartsWith("NextIf_", StringComparison.Ordinal)))
            {
                var g = GateCond.Match(cond.Trim());
                if (!g.Success || g.Groups[1].Value != fam.DoneVar) continue;
                var v = int.Parse(g.Groups[3].Value);
                var target = start.Same("Next", key)?.Split(',')[0].Trim();
                if (target == null) continue;
                var open = Reaches(target);
                if (g.Groups[2].Value == "==") { if (open) fam.OpenValues.Add(v); }
                else if (!open && (fam.DoneFrom == null || v < fam.DoneFrom)) fam.DoneFrom = v;
            }
        }

        static readonly Regex RollVar = new Regex(@"\b(\w+)\s*=\s*Random\(\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.Compiled);

        /// <summary>
        /// Jobs whose request is rolled from a few fixed variants (RE2's ships in distress, <c>Eden_DistressCol_*</c>): the
        /// first talk rolls a per-POI variable (<c>Eden_Randomize = Random(1,5)</c>), a step picks a greeting by its value
        /// (<c>NextIf "Eden_Randomize &gt;= 3"</c>), each greeting asks for a fixed item (<c>OptionIf HasItem('Eden_RepairKitWarp', 1)</c>),
        /// and the hand-in sets a per-POI done variable that the start checks.
        /// </summary>
        void LoadRolledMissions(List<Block> blocks, Dictionary<string, Block> byName)
        {
            foreach (var start in blocks.Where(b => b.Name.EndsWith("_Init", StringComparison.Ordinal) || b.Name.EndsWith("_Start", StringComparison.Ordinal)))
            {
                if (byStart.ContainsKey(start.Name)) continue;
                var prefix = start.Name.Substring(0, start.Name.LastIndexOf('_'));
                var family = blocks.Where(b => b.Name.StartsWith(prefix + "_", StringComparison.Ordinal)).ToList();
                var poiVars = new HashSet<string>(family.SelectMany(b => b.All("Variable")).Select(v => VariableDecl.Match(v))
                    .Where(m => m.Success && m.Groups[2].Value.StartsWith("dbplayerpoi", StringComparison.Ordinal)).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
                if (poiVars.Count < 2) continue;
                var roll = family.SelectMany(b => b.All("Execute")).Select(e => RollVar.Match(e)).FirstOrDefault(m => m.Success && poiVars.Contains(m.Groups[1].Value));
                if (roll == null) continue;
                var sel = roll.Groups[1].Value;
                int lo = int.Parse(roll.Groups[2].Value), hi = int.Parse(roll.Groups[3].Value);
                var chooser = family.FirstOrDefault(b =>
                {
                    var conds = b.Lines.Where(l => l.key.StartsWith("NextIf_", StringComparison.Ordinal)).ToList();
                    return conds.Count > 1 && conds.All(l => GateCond.Match(l.value.Trim()) is Match c && c.Success && c.Groups[1].Value == sel);
                });
                if (chooser == null) continue;
                var fam = new MissionFamily { Start = start.Name, Name = prefix, NpcNameKey = start.First("NPCName"), MissionVar = sel, Job = true };
                for (var v = lo; v <= hi; v++)
                    foreach (var (key, cond) in chooser.Lines.Where(l => l.key.StartsWith("NextIf_", StringComparison.Ordinal)))
                    {
                        var c = GateCond.Match(cond.Trim());
                        var n = int.Parse(c.Groups[3].Value);
                        if (!(c.Groups[2].Value == "==" ? v == n : v >= n)) continue;
                        var target = chooser.Same("Next", key)?.Split(',')[0].Trim();
                        if (target != null && byName.TryGetValue(target, out var greet) && ParseMission(greet, v) is TradeMission tm) fam.Missions[v] = tm;
                        break;
                    }
                if (fam.Missions.Count == 0) continue;
                // done: the per-POI variable the hand-ins set and the start checks
                var handIns = fam.Missions.Values.SelectMany(m => byName.TryGetValue(m.Dialogue, out var g)
                    ? g.Lines.Where(l => l.key.StartsWith("OptionNext_", StringComparison.Ordinal)).Select(l => l.value.Trim()) : Enumerable.Empty<string>())
                    .Where(byName.ContainsKey).Select(n => byName[n]).ToList();
                foreach (var (key, cond) in start.Lines.Where(l => l.key.StartsWith("NextIf_", StringComparison.Ordinal)))
                {
                    var g = GateCond.Match(cond.Trim());
                    if (!g.Success || g.Groups[1].Value == sel || !poiVars.Contains(g.Groups[1].Value)) continue;
                    var sets = new Regex(@"\b" + Regex.Escape(g.Groups[1].Value) + @"\s*=\s*\d+");
                    if (!handIns.Any(h => h.All("Execute").Concat(h.All("OptionExecute")).Any(e => e.Contains("RemoveItem(") && sets.IsMatch(e)))) continue;
                    fam.DoneVar = g.Groups[1].Value;
                    break;
                }
                if (fam.DoneVar == null) continue;
                ReadDoneGates(fam, start, byName, b => b.All("OptionIf").Any(c => c.Contains("HasItem(")));
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
            if (fam.DoneVar != null)
            {
                // one mission per NPC: done → it trades; else the rolled item, the rolled amount
                vars.TryGetValue(fam.DoneVar, out var done);
                if (fam.IsDone(done)) return new MissionState { Status = fam.TradeGated ? "unlocked" : "none", CanTrade = 1 };
                if (n > 0 && fam.Missions.TryGetValue(n, out var qm))
                {
                    var count = 0;
                    if (fam.QuantityVar != null) vars.TryGetValue(fam.QuantityVar, out count);
                    var rolled = new TradeMission
                    {
                        Number = qm.Number, Dialogue = qm.Dialogue,
                        Needs = qm.Needs.Select(x => new MissionNeed { Item = x.Item, TokenId = x.TokenId, Count = fam.QuantityVar != null ? count : x.Count }).ToList(),
                    };
                    return new MissionState { Status = "open", Mission = rolled };
                }
                return new MissionState { Status = fam.TradeGated ? "locked" : "none" };
            }
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
