using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>GET /debug/pois/&lt;playfield&gt;: the playfield's POIs from global.db with their class (dedi, full prefab index incl. traders).</summary>
    public partial class BridgeMod
    {
        static (PoiKind kind, string why) ClassifyWithIndex(string blueprint, string name, int faction)
        {
            var facts = traderIndex?.Facts(blueprint);
            if (facts == null) return PoiClassifier.ClassifyByName(blueprint ?? name, name, faction);
            return PoiClassifier.Classify(facts.WithFaction(faction));
        }

        static string DebugPois(string query)
        {
            var rest = (query ?? "").Length > "/debug/pois/".Length ? query.Substring("/debug/pois/".Length) : "";
            if (rest.Length == 0) return Error("use /debug/pois/<playfield name>");
            var pf = Uri.UnescapeDataString(rest);
            var save = SaveGameDir();
            if (save == null) return Error("no save folder");
            var j = new Json().Obj().Prop("playfield", pf).Key("pois").Arr();
            var counts = new Dictionary<PoiKind, int>();
            using (var c = new SqliteConnection($"Data Source={Path.Combine(save, "global.db")};Read Only=True;"))
            {
                c.Open();
                using (var q = c.CreateCommand())
                {
                    q.CommandText = "SELECT e.entityid, e.name, s.bpname, e.facgroup, (SELECT COUNT(*) FROM DiscoveredPOIs d WHERE d.poiid = e.entityid) " +
                                    "FROM Entities e JOIN Structures s ON s.entityid = e.entityid JOIN Playfields p ON p.pfid = e.pfid " +
                                    "WHERE p.name = @pf AND e.ispoi = 1 AND e.isremoved = 0 ORDER BY e.entityid";
                    q.Parameters.AddWithValue("@pf", pf);
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                        {
                            var bp = r[2] as string;
                            var fac = r[3] is DBNull ? -1 : Convert.ToInt32(r[3]);
                            var (kind, why) = ClassifyWithIndex(bp, r[1] as string, fac);
                            var facts = traderIndex?.Facts(bp);
                            counts[kind] = counts.TryGetValue(kind, out var n) ? n + 1 : 1;
                            j.Obj().Prop("id", Convert.ToInt64(r[0])).Prop("name", r[1] as string).Prop("prefab", bp).Prop("faction", fac)
                                .Prop("class", PoiClassifier.Label(kind)).Prop("why", why)
                                .Prop("turrets", facts?.Turrets ?? -1).Prop("sentries", facts?.Sentries ?? 0).Prop("turretTypes", facts?.TurretTypes ?? 0).Prop("discovered", Convert.ToInt32(r[4]) > 0).EndObj();
                        }
                }
            }
            j.EndArr().Key("counts").Obj();
            foreach (var kv in counts.OrderByDescending(k => k.Value)) j.Prop(PoiClassifier.Label(kv.Key), kv.Value);
            return j.EndObj().EndObj().ToString();
        }
    }
}
