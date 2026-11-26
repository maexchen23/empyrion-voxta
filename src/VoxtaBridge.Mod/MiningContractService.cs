using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        /// <summary>Prefabs whose NPCs hand out mining contracts (they use a dialogue that leads to the offer).</summary>
        static volatile HashSet<string> contractPrefabs;
        /// <summary>Prefabs where contracts can be handed in → the per-POI variable that must be 1 first (null = always).</summary>
        static volatile Dictionary<string, string> handInPrefabs;

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
                // refinery cooldowns: the next time each refinery offers this player a contract, in game time
                if (GameTimeSeconds() is long now) j.Prop("gameTimeS", now);
                if (mc.CooldownVar != null && SaveGameDir() is string save)
                    try { j.Key("offers").Raw(ReadContractOffers(Path.Combine(save, "global.db"), player.EntityId, mc.CooldownVar, player.Playfield, withDiscovered: true)); }
                    catch (Exception e) { j.Prop("offersNote", e.Message); }
                if (handInPrefabs is Dictionary<string, string> hp && hp.Count > 0 && SaveGameDir() is string save2)
                    try { j.Key("handIn").Raw(ReadHandInPoints(Path.Combine(save2, "global.db"), player.EntityId, player.Playfield, hp)); }
                    catch (Exception e) { j.Prop("handInNote", e.Message); }
                if (f.Get("advise") is string a && (a == "1" || a.Equals("yes", StringComparison.OrdinalIgnoreCase)))
                {
                    j.Prop("advise", true);
                    WriteContractStandings(j, mc, player);
                }
            }
            return (200, j.EndObj().ToString());
        }

        /// <summary>
        /// The player's standing with each contract faction and with the refinery's offer faction (whose tier unlocks the
        /// faction choice): "standings": [{faction, standing, tier}], "offer": {faction, standing, tier, canChoose}.
        /// </summary>
        static void WriteContractStandings(Json j, MiningContracts mc, DediPlayer player)
        {
            var idx = traderIndex;
            var save = SaveGameDir();
            if (idx == null || save == null) { j.Prop("standingsNote", "faction standings unknown (no trader index or save)"); return; }
            Dictionary<int, Dictionary<int, int>> standings = null;
            int group = -1, facId = 0;
            try
            {
                ReadPlayerTradeData(Path.Combine(save, "global.db"), player.EntityId, player.Playfield, out group, out facId, out _, out _, out _, new List<KnownPoi>());
                var dat = Path.Combine(save, "game.dat");
                if (File.Exists(dat)) standings = TraderIndex.ReadStandings(File.ReadAllBytes(dat), idx.IsNpcFaction);
            }
            catch (Exception e) { j.Prop("standingsNote", e.Message); }
            var key = group == 0 ? facId : -1;

            int? StandingOf(IEnumerable<string> names)
            {
                foreach (var n in names)
                    if (idx.Faction(n) is FactionDef def) return idx.Standing(standings, key, def);
                return null;
            }

            j.Key("standings").Arr();
            foreach (var g in mc.All.GroupBy(c => c.Faction))
            {
                var s = StandingOf(MiningContracts.FactionKeys(g.First()));
                j.Obj().Prop("faction", g.Key);
                if (s is int v) j.Prop("standing", v).Prop("tier", TraderIndex.Tier(v));
                j.EndObj();
            }
            j.EndArr();
            if (mc.OfferFaction != null)
            {
                var s = StandingOf(new[] { mc.OfferFaction });
                j.Key("offer").Obj().Prop("faction", mc.OfferFaction);
                if (s is int v)
                {
                    j.Prop("standing", v).Prop("tier", TraderIndex.Tier(v));
                    // the gate is a tier's minimum ("HonoredMin"): the faction choice opens at that tier
                    if (mc.ChoiceFromTier is string t && t.EndsWith("Min", StringComparison.Ordinal))
                        j.Prop("choiceTier", t.Substring(0, t.Length - 3)).Prop("canChoose", TraderIndex.Tier(v) == t.Substring(0, t.Length - 3));
                }
                j.EndObj();
            }
        }

        /// <summary>
        /// The scenario's game time in seconds, as dialogues see it (<c>GetGameTimeInSec()</c>): the server's game ticks at
        /// 20 per second (checked on a dedicated server: a refinery's cooldown write matched GameTicks / 20 within the
        /// quest log's poll delay). Game time stands still while the server is down. Null when unknown.
        /// </summary>
        internal static long? GameTimeSeconds()
        {
            try { return api?.Application != null ? (long)(api.Application.GameTicks / 20) : (long?)null; }
            catch { return null; }
        }

        /// <summary>
        /// The player's discovered POIs where contracts can be handed in, nearest first: [{where, ly}]. A gated prefab (e.g. a
        /// mining-site vessel) only counts where the player's per-POI variable is 1 (its own quest done there).
        /// </summary>
        static string ReadHandInPoints(string db, int playerId, string playerPlayfield, Dictionary<string, string> prefabs, int max = 8)
        {
            var found = new List<(string where, double? ly)>();
            using (var c = new Mono.Data.Sqlite.SqliteConnection($"Data Source={db};Read Only=True;"))
            {
                c.Open();
                (double x, double y, double z)? here = null;
                using (var q = c.CreateCommand())
                {
                    q.CommandText = "SELECT sectorx, sectory, sectorz FROM Playfields WHERE name = @pf LIMIT 1";
                    q.Parameters.AddWithValue("@pf", playerPlayfield ?? "");
                    using (var r = q.ExecuteReader())
                        if (r.Read() && !(r[0] is DBNull)) here = (Convert.ToDouble(r[0]), Convert.ToDouble(r[1]), Convert.ToDouble(r[2]));
                }
                // gate variables met: "<POI id>-…" rows with value 1
                var met = new HashSet<string>(StringComparer.Ordinal);
                foreach (var gate in prefabs.Values.Where(g => g != null).Distinct())
                    using (var q = c.CreateCommand())
                    {
                        q.CommandText = "SELECT state FROM DialogueVars WHERE entityid = @p AND name = @n AND value = 1";
                        q.Parameters.AddWithValue("@p", playerId);
                        q.Parameters.AddWithValue("@n", gate);
                        using (var r = q.ExecuteReader())
                            while (r.Read())
                                if (r[0] is string st && st.IndexOf('-') is int d && d > 0) met.Add(gate + "@" + st.Substring(0, d));
                    }
                using (var q = c.CreateCommand())
                {
                    q.CommandText =
                        "SELECT DISTINCT d.poiid, e.name, p.name, p.sectorx, p.sectory, p.sectorz, s.bpname FROM DiscoveredPOIs d " +
                        "JOIN Structures s ON s.entityid = d.poiid JOIN Entities e ON e.entityid = d.poiid JOIN Playfields p ON p.pfid = e.pfid " +
                        "WHERE d.entityid = @p AND (e.isremoved IS NULL OR e.isremoved = 0)";
                    q.Parameters.AddWithValue("@p", playerId);
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                        {
                            if (!(r[6] is string bp) || !prefabs.TryGetValue(bp, out var gate)) continue;
                            var poi = Convert.ToInt64(r[0]).ToString(CultureInfo.InvariantCulture);
                            if (gate != null && !met.Contains(gate + "@" + poi)) continue;
                            double? ly = null;
                            if (here is var (hx, hy, hz) && !(r[3] is DBNull))
                            {
                                double dx = Convert.ToDouble(r[3]) - hx, dy = Convert.ToDouble(r[4]) - hy, dz = Convert.ToDouble(r[5]) - hz;
                                ly = Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz) / 100000.0, 1);
                            }
                            found.Add(($"{r[1] as string} ({r[2] as string})", ly));
                        }
                }
            }
            var j = new Json().Arr();
            foreach (var (where, ly) in found.OrderBy(x => x.ly ?? double.MaxValue).Take(max))
            {
                j.Obj().Prop("where", where);
                if (ly.HasValue) j.Prop("ly", ly.Value);
                j.EndObj();
            }
            return j.EndArr().ToString();
        }

        static string DisplayName(string item) => gameText?.DisplayName(item);

        static void WriteContract(Json j, MiningContract c)
        {
            j.Obj().Prop("token", c.TokenId);
            if (TokenName(c.TokenId) is string name) j.Prop("name", name);
            j.Prop("faction", c.Faction).Prop("credits", c.Credits).Prop("reputation", c.Reputation);
            if (c.Weight > 0) j.Prop("weight", c.Weight);
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
