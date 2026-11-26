using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// Delivery jobs of the POIs on the player's playfield (RE2 mining sites): which POI wants what, into which container,
    /// for what, and whether the player did it already. In /status (<c>siteJobs.&lt;steamId&gt;</c>) so the module can speak
    /// up on arrival; POST /site-cargo checks the free volume of the ship's harvest controllers against a job.
    /// </summary>
    public partial class BridgeMod
    {
        static volatile SiteMissions siteMissions;
        /// <summary>Prefab → the delivery job its NPC gives (the prefab uses the job's start dialogue).</summary>
        static volatile Dictionary<string, SiteMission> sitePrefabs;

        /// <summary>The site jobs on the player's playfield: [{key, where, poi, needs: [{item, name, count}], container, credits?, choice?, rep?, done?}].</summary>
        static string ReadSiteJobs(string db, int playerId, string playfield)
        {
            var prefabs = sitePrefabs;
            var j = new Json().Arr();
            if (prefabs == null || prefabs.Count == 0 || string.IsNullOrEmpty(playfield)) return j.EndArr().ToString();
            using (var c = new SqliteConnection($"Data Source={db};Read Only=True;"))
            {
                c.Open();
                var pois = new List<(long id, string name, SiteMission m)>();
                using (var q = c.CreateCommand())
                {
                    q.CommandText = "SELECT e.entityid, e.name, s.bpname FROM Entities e JOIN Structures s ON s.entityid = e.entityid " +
                                    "JOIN Playfields p ON p.pfid = e.pfid WHERE p.name = @pf AND (e.isremoved IS NULL OR e.isremoved = 0)";
                    q.Parameters.AddWithValue("@pf", playfield);
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                            if (r[2] is string bp && prefabs.TryGetValue(bp, out var m)) pois.Add((Convert.ToInt64(r[0]), r[1] as string, m));
                }
                foreach (var (id, name, m) in pois)
                {
                    var done = false;
                    using (var q = c.CreateCommand())
                    {
                        q.CommandText = "SELECT 1 FROM DialogueVars WHERE entityid = @p AND name = @n AND value = 1 AND state LIKE @s LIMIT 1";
                        q.Parameters.AddWithValue("@p", playerId);
                        q.Parameters.AddWithValue("@n", m.DoneVar);
                        q.Parameters.AddWithValue("@s", id.ToString(CultureInfo.InvariantCulture) + "-%");
                        using (var r = q.ExecuteReader()) done = r.Read();
                    }
                    j.Obj().Prop("key", id.ToString(CultureInfo.InvariantCulture)).Prop("poi", name).Prop("where", $"{name} ({playfield})");
                    j.Key("needs").Arr();
                    foreach (var (item, count) in m.Needs)
                        j.Obj().Prop("item", item).Prop("name", gameText?.DisplayName(item) ?? item).Prop("count", count).EndObj();
                    j.EndArr().Prop("container", m.Container);
                    if (m.Credits > 0) j.Prop("credits", m.Credits);
                    if (m.RewardChoice) j.Prop("choice", true);
                    if (m.RepFaction != null)
                        j.Prop("rep", $"{traderIndex?.Faction(m.RepFaction)?.Label ?? m.RepFaction} {m.RepMinTier}");
                    if (done) j.Prop("done", true);
                    j.EndObj();
                }
            }
            return j.EndArr().ToString();
        }

        /// <summary>HTTP thread: POST /site-cargo (player, items = "Item:count,…"): harvest controller space vs. the load's volume.</summary>
        static (int, string) HandleSiteCargo(string body)
        {
            var f = Form.Parse(body);
            DediPlayer player;
            string why;
            lock (sync) player = PickPlayer(f.Get("player"), out why);
            if (player == null) return (200, Error(why));
            var needL = 0.0;
            foreach (var part in (f.Get("items") ?? "").Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && int.TryParse(kv[1], out var n)) needL += n * (gameText?.Volume(kv[0].Trim()) ?? 0);
            }
            var (code, found, _) = ForwardToPlayfield("harvest-space", player.SteamId);
            if (code != 200) return (code, found);
            return (200, new Json().Obj().Prop("ok", true).Prop("needL", Math.Round(needL)).Key("space").Raw(found).EndObj().ToString());
        }

        /// <summary>
        /// Playfield server: the harvest controllers on the player's ship and its docking group (not structures merely in
        /// range): capacity, used and free volume in litres, the largest free one by name.
        /// </summary>
        static string ExecuteHarvestSpace(Dictionary<string, string> req)
        {
            var pfName = req.Get("playfield");
            var pf = playfields.FirstOrDefault(x => x.Name == pfName);
            if (pf == null) return Error($"The playfield {pfName} is not loaded on this server process.");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("The player is not on this playfield right now.");
            var requireOwnership = req.Get("ownership") != "0";
            var active = ResolveActive(pf, player, requireOwnership, ParseRange(req.Get("range")));
            if (active.Structure == null) return Error("The player is not on or near an own ship.");
            var all = ActiveScope(pf, active, player, requireOwnership, ParseRange(req.Get("range")), includeRangeContainers: false,
                new List<InRange>(), null, new ScopeTiming());
            double cap = 0, used = 0;
            var count = 0;
            foreach (var c in all.OfType<GameContainer>().Where(c => c.Kind == "HarvestCntr"))
            {
                count++;
                try
                {
                    cap += c.Box.VolumeCapacity;
                    foreach (var st in c.Box.GetContent() ?? new List<Eleon.Modding.ItemStack>()) used += st.count * (gameText?.Volume(ItemName(st.id)) ?? 0);
                }
                catch (Exception e) { Log($"harvest-space: {c.Label}: {e.Message}"); }
            }
            return new Json().Obj().Prop("ok", true).Prop("structure", active.Structure.Entity?.Name).Prop("harvestControllers", count)
                .Prop("capacityL", Math.Round(cap)).Prop("usedL", Math.Round(used)).Prop("freeL", Math.Round(Math.Max(0, cap - used))).EndObj().ToString();
        }
    }
}
