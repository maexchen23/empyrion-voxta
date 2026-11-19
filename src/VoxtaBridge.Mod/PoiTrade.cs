using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// POST /poi-trade (dedi): what the traders at a POI sell and buy, from the scenario's trader config (what traders of
    /// that kind can have, not live stock). Fair play:
    /// - the POI must be one the player or their faction discovered (current playfield first); item names and price tiers
    ///   are general knowledge about that kind of trader;
    /// - credits (detail) only for POIs the player has visited (VisitedStructures); no stock: the config only has a range
    ///   per trader kind, the real stock and restocking aren't readable ("cheap item X here" is enough);
    /// - otherwise, by prefab display name, a general answer ("a Solaris-AMP Factory usually sells …") without detail.
    /// Everyday goods (food, cheap components, building blocks) are only counted unless all=1.
    /// </summary>
    public partial class BridgeMod
    {
        const int MaxTradeItems = 60;

        static (int, string) HandlePoiTrade(string body)
        {
            var f = Form.Parse(body);
            var idx = traderIndex;
            if (idx == null) return (503, Error("The trader data is not loaded yet."));
            var wanted = (f.Get("poi") ?? "").Trim();
            if (wanted.Length == 0) return (400, Error("'poi' is required (a POI name, e.g. 'Solaris-AMP Factory')."));
            var wantDetail = f.Get("detail") == "1";
            var all = f.Get("all") == "1";
            DediPlayer player;
            lock (sync) player = PickPlayer(f.Get("player"), out _);

            string prefab = null, name = null, playfield = null;
            var visited = false;
            var save = SaveGameDir();
            if (player != null && save != null)
            {
                var db = Path.Combine(save, "global.db");
                int facGroup = -1, facId = -1;
                try { ReadPlayerTradeData(db, player.EntityId, player.Playfield, out facGroup, out facId, out _, out _, out _, new List<KnownPoi>()); } catch { }
                using (var c = new SqliteConnection($"Data Source={db};Read Only=True;"))
                {
                    c.Open();
                    using (var q = c.CreateCommand())
                    {
                        q.CommandText =
                            "SELECT e.name, s.bpname, pf.name, EXISTS (SELECT 1 FROM VisitedStructures v WHERE v.poiid = e.entityid AND v.entityid = @p) " +
                            "FROM Entities e JOIN Structures s ON s.entityid = e.entityid JOIN Playfields pf ON pf.pfid = e.pfid " +
                            "WHERE e.ispoi = 1 AND e.isremoved = 0 AND e.name LIKE @n AND EXISTS (SELECT 1 FROM DiscoveredPOIs d " +
                            "WHERE d.poiid = e.entityid AND (d.entityid = @p OR (d.facgroup = @fg AND d.facid = @fi))) " +
                            "ORDER BY pf.name = @pf DESC, e.name = @exact DESC, 4 DESC LIMIT 1";
                        q.Parameters.AddWithValue("@p", player.EntityId);
                        q.Parameters.AddWithValue("@fg", facGroup);
                        q.Parameters.AddWithValue("@fi", facId);
                        q.Parameters.AddWithValue("@n", "%" + wanted.Replace("%", "") + "%");
                        q.Parameters.AddWithValue("@pf", player.Playfield);
                        q.Parameters.AddWithValue("@exact", wanted);
                        using (var r = q.ExecuteReader())
                            if (r.Read())
                            {
                                name = r[0] as string; prefab = r[1] as string; playfield = r[2] as string;
                                visited = Convert.ToInt32(r[3]) > 0;
                            }
                    }
                }
            }
            var pt = prefab != null ? idx.Prefab(prefab) : null;
            var general = false;
            if (pt == null)
            {
                pt = idx.PrefabsByDisplay(wanted);
                if (pt != null) { name = pt.Display; playfield = null; visited = false; general = true; }
            }
            if (pt == null || pt.Traders.Count == 0)
                return (200, Error(name != null && !general ? $"{name} has no traders." : $"No trading POI called '{wanted}' is known."));
            var detail = wantDetail && visited;

            var t = gameText;
            var traders = pt.Traders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var offers = traders.SelectMany(tr => idx.Offers(tr)).ToList();
            var j = new Json().Obj().Prop("ok", true).Prop("poi", name).Prop("playfield", playfield)
                .Prop("general", general).Prop("visited", visited);
            if (wantDetail && !detail) j.Prop("detailWithheld", true);
            j.Key("traders").Arr();
            foreach (var tr in traders) j.Val(TraderIndex.SplitWords(tr));
            j.EndArr();
            void List(string key, IEnumerable<TraderOffer> list, bool selling)
            {
                var items = list.GroupBy(o => o.Item, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (o: g.First(), name: t?.DisplayName(g.Key) ?? g.Key, market: t?.MarketPrice(g.Key), category: t?.Category(g.Key)))
                    .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList();
                var everyday = all ? 0 : items.RemoveAll(x => TraderIndex.IsEveryday(x.o.Item, x.category, x.market));
                j.Prop(key + "Count", items.Count).Prop(key + "Everyday", everyday).Key(key).Arr();
                foreach (var x in items.Take(MaxTradeItems))
                {
                    var price = selling ? x.o.SellPrice : x.o.BuyPrice;
                    j.Obj().Prop("item", x.name).Prop("tier", TraderIndex.PriceTier(price, x.market));
                    if (detail)
                    {
                        if (TraderIndex.Credits(price, x.market) is { } cr)
                            j.Prop("credits", Math.Round(cr.lo) == Math.Round(cr.hi) ? $"{Math.Round(cr.lo)}" : $"{Math.Round(cr.lo)}-{Math.Round(cr.hi)}");
                    }
                    j.EndObj();
                }
                j.EndArr();
            }
            List("sells", offers.Where(o => o.Sells), true);
            List("buys", offers.Where(o => o.Buys), false);
            return (200, j.EndObj().ToString());
        }
    }
}
