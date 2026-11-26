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

        /// <summary>The per-player, per-refinery dialogue variable that holds the next time a contract is offered; it changes whenever one is given.</summary>
        public string CooldownVar { get; private set; }
        static readonly Regex TierGate = new Regex(@"GetReputation\(Faction\.(\w+)\)\s*>=\s*Reputation\.(\w+)", RegexOptions.Compiled);

        public void LoadDialogues(TextReader reader)
        {
            var blocks = TradeMissions.ReadBlocks(reader).Where(b => b.Name.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
            var byName = blocks.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // the greeting's options: faction branch → faction name
            if (byName.TryGetValue(Prefix + "_Greet", out var greet))
                foreach (var (key, value) in greet.Lines.Where(l => l.key.StartsWith("OptionExecute_", StringComparison.Ordinal)))
                {
                    var faction = RepFaction.Match(value);
                    var next = greet.Same("OptionNext", key)?.Split(',')[0].Trim();
                    if (faction.Success && next != null && byName.TryGetValue(next, out var branch))
                        ReadBranch(branch, faction.Groups[1].Value);
                }

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

        void ReadBranch(TradeMissions.Block branch, string faction)
        {
            foreach (var ex in branch.All("OptionExecute"))
            {
                var v = Assign.Matches(ex.Trim().Trim('"')).Cast<Match>().GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value.Trim());
                if (!v.TryGetValue("TokenID", out var tok) || !int.TryParse(tok, out var tokenId)) continue;
                var c = new MiningContract { TokenId = tokenId, Faction = faction };
                foreach (var n in new[] { "Ore", "Ore2", "Ore3" })
                    if (v.TryGetValue(n + "Type", out var item) && v.TryGetValue(n + "Count", out var cnt) && int.TryParse(cnt, out var count))
                        c.Ores.Add((item, count));
                if (c.Ores.Count == 0) continue;
                if (v.TryGetValue("RewardCredit", out var cr)) int.TryParse(cr, out c.Credits);
                if (v.TryGetValue("RewardRepCount", out var rp)) int.TryParse(rp, out c.Reputation);
                byToken[tokenId] = c;
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
