using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoxtaBridge
{
    /// <summary>One mining contract: the token the refinery hands out and what turning it in asks for and pays.</summary>
    public sealed class MiningContract
    {
        public int TokenId;
        /// <summary>The faction the contract is for, as the dialogue names it ("Zirax Empire").</summary>
        public string Faction;
        /// <summary>The ores to deliver (internal item names), in order.</summary>
        public List<(string Item, int Count)> Ores = new List<(string, int)>();
        public int Credits;
        /// <summary>Reputation with <see cref="Faction"/> on hand-in.</summary>
        public int Reputation;
        /// <summary>The dialogue branch suffix ("Colonist", "Trader"), close to the faction's key in Factions.ecf.</summary>
        public string Branch;
        /// <summary>How likely the refinery picks this one from its faction's loot group (LootGroups.ecf), 0 = unknown.</summary>
        public double Weight;
    }

    /// <summary>
    /// The scenario's mining contracts (RE2 Orbital Refinery, game-free, unit-tested): read from Dialogues.ecf.
    /// The hand-in dialogue has one branch per faction (chosen in <c>Eden_ContractMining_Greet</c>, which also sets the
    /// faction name), and each branch one option per contract token that sets the ore, amount and reward.
    /// </summary>
    public sealed class MiningContracts
    {
        readonly Dictionary<int, MiningContract> byToken = new Dictionary<int, MiningContract>();

        public int Count => byToken.Count;
        public IEnumerable<MiningContract> All => byToken.Values.OrderBy(c => c.TokenId);

        /// <summary>Seconds before the same refinery gives the player another contract (game time), 0 = unknown.</summary>
        public int CooldownSeconds { get; private set; }

        /// <summary>The refinery lets the player choose the contract's faction from this reputation tier ("HonoredMin"), or null.</summary>
        public string ChoiceFromTier { get; private set; }

        /// <summary>Whose reputation the refinery checks for the contract offers ("Polaris"), or null.</summary>
        public string OfferFaction { get; private set; }

        const string Prefix = "Eden_ContractMining";
        static readonly Regex RepFaction = new Regex(@"RewardRepFaction\s*=\s*'([^']+)'", RegexOptions.Compiled);
        static readonly Regex Assign = new Regex(@"(\w+)\s*=\s*'?([\w ]+?)'?\s*(?:;|$)", RegexOptions.Compiled);
        static readonly Regex Cooldown = new Regex(@"(\w+)\s*=\s*\(?\s*GetGameTimeInSec\(\)\s*\+\s*(\d+)", RegexOptions.Compiled);

        /// <summary>
        /// The dialogues that lead to the contract offer (<c>…Offer_Init</c> and every dialogue whose Next/OptionNext reaches it
        /// within a few steps, e.g. a refinery NPC's greeting): a prefab using one of them hands out contracts.
        /// </summary>
        public HashSet<string> OfferEntryDialogues { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// The dialogues that lead to the contract hand-in (<c>Eden_ContractMining_Init</c>), each with the per-POI variable
        /// that must be 1 on the way there (e.g. a mining-site vessel takes contracts once its own quest is done), or null
        /// when the way is open. A prefab using one of them is a hand-in point.
        /// </summary>
        public Dictionary<string, string> HandInEntryDialogues { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        static readonly Regex PerPoiDecl = new Regex(@"Variable_\d+:\s*""(\w+)""\s*,\s*param1:\s*dbplayerpoi\w*", RegexOptions.Compiled);
        static readonly Regex GateEq = new Regex(@"^(\w+)\s*==\s*1$", RegexOptions.Compiled);

        /// <summary>Reverse walk from a dialogue (3 steps): name → gate (null = reachable without a per-POI condition).</summary>
        static Dictionary<string, string> Walk(Dictionary<string, List<(string parent, string gate)>> parents, string start, bool exists)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!exists) return seen;
            seen[start] = null;
            var frontier = new List<string> { start };
            for (var depth = 0; depth < 3 && frontier.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var child in frontier)
                {
                    if (!parents.TryGetValue(child, out var ps)) continue;
                    foreach (var (parent, gate) in ps)
                    {
                        var g = gate ?? seen[child];
                        if (!seen.TryGetValue(parent, out var had)) { seen[parent] = g; next.Add(parent); }
                        else if (had != null && g == null) { seen[parent] = null; next.Add(parent); }
                    }
                }
                frontier = next;
            }
            return seen;
        }

        /// <summary>The per-player, per-refinery dialogue variable that holds the next time a contract is offered; it changes whenever one is given.</summary>
        public string CooldownVar { get; private set; }
        static readonly Regex TierGate = new Regex(@"GetReputation\(Faction\.(\w+)\)\s*>=\s*Reputation\.(\w+)", RegexOptions.Compiled);

        public void LoadDialogues(TextReader reader)
        {
            var allBlocks = TradeMissions.ReadBlocks(reader);
            var blocks = allBlocks.Where(b => b.Name.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
            var byName = blocks.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // the greeting's options: faction branch → faction name
            if (byName.TryGetValue(Prefix + "_Greet", out var greet))
                foreach (var (key, value) in greet.Lines.Where(l => l.key.StartsWith("OptionExecute_", StringComparison.Ordinal)))
                {
                    var faction = RepFaction.Match(value);
                    var next = greet.Same("OptionNext", key)?.Split(',')[0].Trim();
                    if (faction.Success && next != null && byName.TryGetValue(next, out var branch))
                        ReadBranch(branch, faction.Groups[1].Value, next.Substring(Prefix.Length).TrimStart('_'));
                }

            // which dialogues lead to the offer and to the hand-in: reverse Next/OptionNext edges, a few steps deep
            var perPoi = new HashSet<string>(allBlocks.SelectMany(x => x.Lines).Select(l => PerPoiDecl.Match(l.key + ": " + l.value))
                .Where(m => m.Success).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
            var parents = new Dictionary<string, List<(string parent, string gate)>>(StringComparer.Ordinal);
            foreach (var b in allBlocks)
                foreach (var (key, value) in b.Lines.Where(l => l.key.StartsWith("Next_", StringComparison.Ordinal) || l.key.StartsWith("OptionNext_", StringComparison.Ordinal)))
                {
                    var target = value.Split(',')[0].Trim();
                    // a per-POI "Var == 1" condition on this edge gates the way (e.g. "quest done here")
                    var cond = b.Same(key.StartsWith("Next_", StringComparison.Ordinal) ? "NextIf" : "OptionIf", key);
                    string gate = null;
                    if (cond != null && GateEq.Match(cond.Trim().Trim('"')) is Match g && g.Success && perPoi.Contains(g.Groups[1].Value)) gate = g.Groups[1].Value;
                    if (!parents.TryGetValue(target, out var list)) parents[target] = list = new List<(string, string)>();
                    list.Add((b.Name, gate));
                }
            foreach (var n in Walk(parents, Prefix + "Offer_Init", byName.ContainsKey(Prefix + "Offer_Init")).Keys) OfferEntryDialogues.Add(n);
            foreach (var kv in Walk(parents, Prefix + "_Init", allBlocks.Any(x => x.Name == Prefix + "_Init"))) HandInEntryDialogues[kv.Key] = kv.Value;

            // how the refinery hands contracts out: cooldown after one is given, faction choice from a reputation tier
            foreach (var b in blocks.Where(b => b.Name.StartsWith(Prefix + "Offer_", StringComparison.Ordinal)))
                foreach (var (_, value) in b.Lines)
                {
                    if (CooldownSeconds == 0 && Cooldown.Match(value) is Match cd && cd.Success)
                    {
                        CooldownVar = cd.Groups[1].Value;
                        CooldownSeconds = int.Parse(cd.Groups[2].Value);
                    }
                    var tier = TierGate.Match(value);
                    if (tier.Success && b.Name == Prefix + "Offer_Init")
                    {
                        OfferFaction = tier.Groups[1].Value;
                        // the gated greeting offers several contract tables = the faction choice
                        var next = b.Lines.FirstOrDefault(l => l.key.StartsWith("Next_", StringComparison.Ordinal)
                                                               && b.Same("NextIf", l.key) == value).value;
                        if (next != null && byName.TryGetValue(next.Split(',')[0].Trim(), out var gated)
                            && gated.All("OptionExecute").Count(e => e.Contains("AddItemsFromContainer")) > 1)
                            ChoiceFromTier = tier.Groups[2].Value;
                    }
                }
        }

        void ReadBranch(TradeMissions.Block branch, string faction, string branchName)
        {
            foreach (var ex in branch.All("OptionExecute"))
            {
                var v = Assign.Matches(ex.Trim().Trim('"')).Cast<Match>().GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value.Trim());
                if (!v.TryGetValue("TokenID", out var tok) || !int.TryParse(tok, out var tokenId)) continue;
                var c = new MiningContract { TokenId = tokenId, Faction = faction, Branch = branchName };
                foreach (var n in new[] { "Ore", "Ore2", "Ore3" })
                    if (v.TryGetValue(n + "Type", out var item) && v.TryGetValue(n + "Count", out var cnt) && int.TryParse(cnt, out var count))
                        c.Ores.Add((item, count));
                if (c.Ores.Count == 0) continue;
                if (v.TryGetValue("RewardCredit", out var cr)) int.TryParse(cr, out c.Credits);
                if (v.TryGetValue("RewardRepCount", out var rp)) int.TryParse(rp, out c.Reputation);
                byToken[tokenId] = c;
            }
        }

        static readonly Regex LootHead = new Regex(@"^\{\s*\+?LootGroup\s+Name:\s*(\S+)", RegexOptions.Compiled);
        static readonly Regex LootToken = new Regex(@"^\s*Item_\d+:\s*Token\s*,\s*param1:\s*""?meta=(\d+)""?\s*,\s*param2:\s*([\d.]+)", RegexOptions.Compiled);

        /// <summary>
        /// The pick weights of the contract tokens from LootGroups.ecf (<c>Item_0: Token, param1: "meta=6800", param2: 0.5</c>):
        /// a refinery draws one token from a faction's group, rare contracts have a lower weight. Returns how many were set.
        /// </summary>
        public int LoadLootGroups(TextReader reader)
        {
            var set = 0;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (LootHead.IsMatch(line)) continue;
                var m = LootToken.Match(line);
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out var id) || !byToken.TryGetValue(id, out var c)) continue;
                if (double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w))
                {
                    c.Weight = w;
                    set++;
                }
            }
            return set;
        }

        /// <summary>Names to look the contract's faction up by in Factions.ecf: "Polaris Megacorp" → Polaris, branch "Colonist" → Colonists.</summary>
        public static IEnumerable<string> FactionKeys(MiningContract c)
        {
            if (c.Faction != null)
            {
                yield return c.Faction;
                yield return c.Faction.Split(' ')[0];
            }
            if (!string.IsNullOrEmpty(c.Branch))
            {
                yield return c.Branch;
                yield return c.Branch + "s";
            }
        }

        static readonly Regex QuestDrop = new Regex(@"HandleQuestItemDrop\(\s*'([^']+)'", RegexOptions.Compiled);

        /// <summary>
        /// The names of the hand-in containers quest dialogues check (<c>HandleQuestItemDrop('CargoDrop', …)</c>: the
        /// player puts the items into the NPC structure's container with this name, then talks to the NPC).
        /// </summary>
        public static List<string> DropContainerNames(TextReader reader)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while ((line = reader.ReadLine()) != null)
                foreach (Match m in QuestDrop.Matches(line)) names.Add(m.Groups[1].Value.Trim());
            return names.Where(n => n.Length > 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public MiningContract ByToken(int tokenId) => byToken.TryGetValue(tokenId, out var c) ? c : null;

        /// <summary>
        /// Contracts asking for an ore and/or for a faction (both optional; parts of names, case-insensitive). The ore is
        /// matched on the internal name and on the display name from <paramref name="displayName"/>.
        /// </summary>
        public List<MiningContract> Find(string ore, string faction, Func<string, string> displayName)
        {
            bool Has(string text, string part) => text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
            ore = string.IsNullOrWhiteSpace(ore) ? null : ore.Trim();
            faction = string.IsNullOrWhiteSpace(faction) ? null : faction.Trim();
            return All.Where(c => (faction == null || Has(c.Faction, faction))
                                  && (ore == null || c.Ores.Any(o => Has(o.Item, ore) || Has(displayName?.Invoke(o.Item), ore))))
                .ToList();
        }
    }
}
