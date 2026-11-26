using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoxtaBridge
{
    /// <summary>
    /// Mining contracts (POST /mining-contracts; dedi + playfield): the scenario's contract table (who wants which ore, how
    /// much, for what), and with <c>mine=1</c> the player's own contract tokens and how much of the wanted ore is in reach.
    /// The table lives on the dedi; the playfield server only reports token ids and ore counts, the module joins them.
    /// </summary>
    public partial class BridgeMod
    {
        static volatile MiningContracts miningContracts;
        /// <summary>Dedi: the quest hand-in container names from Dialogues.ecf, sent along with every playfield request.</summary>
        static volatile string[] questDropNames;
        /// <summary>Playfield server: the hand-in container names last sent by the dedi.</summary>
        static volatile HashSet<string> questDrops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int MaxContracts = 12;

        /// <summary>HTTP thread: POST /mining-contracts (ore=…, faction=…, mine=1, player=…).</summary>
        static (int, string) HandleMiningContracts(string body)
        {
            var f = Form.Parse(body);
            var mc = miningContracts;
            if (mc == null || mc.Count == 0) return (200, Error("This scenario has no mining contracts (none found in Dialogues.ecf)."));

            var j = new Json().Obj().Prop("ok", true);
            j.Key("rules").Obj()
                .Prop("cooldownSeconds", mc.CooldownSeconds)
                .Prop("offerFaction", mc.OfferFaction)
                .Prop("choiceFromTier", mc.ChoiceFromTier)
                .EndObj();

            var mine = f.Get("mine") is string m && (m == "1" || m.Equals("true", StringComparison.OrdinalIgnoreCase) || m.Equals("yes", StringComparison.OrdinalIgnoreCase));
            var list = mine ? mc.All.ToList() : mc.Find(f.Get("ore"), f.Get("faction"), DisplayName).OrderByDescending(c => c.Credits).ToList();
            if (!mine)
            {
                if (f.Get("ore") is string ore && ore.Trim().Length > 0) j.Prop("ore", ore.Trim());
                if (f.Get("faction") is string fac && fac.Trim().Length > 0) j.Prop("faction", fac.Trim());
                j.Prop("count", list.Count);
            }
            j.Key("contracts").Arr();
            foreach (var c in mine ? list : list.Take(MaxContracts)) WriteContract(j, c);
            j.EndArr();
            if (!mine && list.Count > MaxContracts) j.Prop("more", list.Count - MaxContracts);

            if (mine)
            {
                DediPlayer player;
                string why;
                lock (sync) player = PickPlayer(f.Get("player"), out why);
                if (player == null) return (200, Error(why));
                string bag = "", toolbar = "";
                if (legacy != null)
                {
                    var inv = GetInventory(player.EntityId, out _);
                    if (inv != null) { bag = Stacks.Encode(ToStacks(inv.bag)); toolbar = Stacks.Encode(ToStacks(inv.toolbelt)); }
                }
                var tokens = string.Join(",", mc.All.Select(c => c.TokenId.ToString(CultureInfo.InvariantCulture)));
                var ores = string.Join(",", mc.All.SelectMany(c => c.Ores.Select(o => o.Item)).Distinct());
                var (code, found, _) = ForwardToPlayfield("mining-scan", player.SteamId, ("tokens", tokens), ("ores", ores), ("bag", bag), ("toolbar", toolbar));
                if (code != 200) return (code, found);
                j.Key("found").Raw(found);
            }
            return (200, j.EndObj().ToString());
        }

        static string DisplayName(string item) => gameText?.DisplayName(item);

        static void WriteContract(Json j, MiningContract c)
        {
            j.Obj().Prop("token", c.TokenId);
            if (TokenName(c.TokenId) is string name) j.Prop("name", name);
            j.Prop("faction", c.Faction).Prop("credits", c.Credits).Prop("reputation", c.Reputation);
            j.Key("ores").Arr();
            foreach (var (item, count) in c.Ores)
                j.Obj().Prop("item", item).Prop("name", DisplayName(item) ?? item).Prop("count", count).EndObj();
            j.EndArr().EndObj();
        }

        /// <summary>
        /// Playfield server: where the contract tokens are (inventory or containers in reach) and how much of each contract ore
        /// is there. Tokens are <c>Token</c> stacks whose ammo field is the token id.
        /// </summary>
        static string ExecuteMiningScan(Dictionary<string, string> req)
        {
            var pfName = req.Get("playfield");
            var pf = playfields.FirstOrDefault(x => x.Name == pfName);
            if (pf == null) return Error($"The playfield {pfName} is not loaded on this server process.");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("The player is not on this playfield right now.");
            var requireOwnership = req.Get("ownership") != "0";
            var active = ResolveActive(pf, player, requireOwnership, ParseRange(req.Get("range")));
            var all = ActiveScope(pf, active, player, requireOwnership, ParseRange(req.Get("range")), includeRangeContainers: true,
                new List<InRange>(), null, new ScopeTiming());

            var tokenIds = new HashSet<int>((req.Get("tokens") ?? "").Split(',').Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0));
            var oreNames = new HashSet<string>((req.Get("ores") ?? "").Split(',').Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);

            var places = new List<(string where, bool inventory, bool open, List<InvStack> stacks)>
            {
                ("player bag", true, false, Stacks.Decode(req.Get("bag"))),
                ("player toolbar", true, false, Stacks.Decode(req.Get("toolbar"))),
            };
            places.AddRange(all.Select(c => (c.Label, false, c.Locked, c.GetStacks())));

            var j = new Json().Obj().Prop("ok", true);
            j.Key("tokens").Arr();
            foreach (var p in places)
                foreach (var g in p.stacks.Where(st => tokenIds.Contains(st.Ammo) && gameText?.IsToken(ItemName(st.Id)) == true).GroupBy(st => st.Ammo))
                {
                    j.Obj().Prop("token", g.Key).Prop("where", p.where).Prop("count", g.Sum(st => st.Count));
                    if (p.inventory) j.Prop("inventory", true);
                    if (p.open) j.Prop("open", true);
                    j.EndObj();
                }
            j.EndArr();
            j.Key("ores").Arr();
            foreach (var p in places)
                foreach (var g in p.stacks.Where(st => oreNames.Contains(ItemName(st.Id) ?? "")).GroupBy(st => ItemName(st.Id)))
                {
                    j.Obj().Prop("item", g.Key).Prop("where", p.where).Prop("count", g.Sum(st => st.Count));
                    if (p.open) j.Prop("open", true);
                    j.EndObj();
                }
            j.EndArr();
            return j.EndObj().ToString();
        }
    }
}
