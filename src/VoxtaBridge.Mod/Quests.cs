using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// Trader missions (POST /quests, /quest-pursue; dedi): the scenario's trader mission dialogues (TD_X_*) combined with
    /// the player's DialogueVars rows in global.db (type 110 = per player and POI). Read on demand, nothing is cached but
    /// the pursued quest's context line. The pursued quest (one per player) is kept by the bridge in a small file next to
    /// the DLL so it survives restarts.
    /// </summary>
    public partial class BridgeMod
    {
        static volatile TradeMissions tradeMissions;
        const int MaxQuests = 5;
        const double LockedPenaltyLy = 10;

        /// <summary>The prefab's trader mission family that gates its trades, or null.</summary>
        static MissionFamily GatedFamily(string prefab)
        {
            var tm = tradeMissions;
            var facts = traderIndex?.Facts(prefab);
            if (tm == null || facts == null) return null;
            return MissionStarts(facts).Select(tm.ByStart).FirstOrDefault(f => f != null && f.TradeGated);
        }

        /// <summary>
        /// Null when this POI trades for the player; else why not yet: the open unlock mission (and work order), or "talk to
        /// the trader" when the player has no row for it yet.
        /// </summary>
        static string TradeLock(string prefab, long poiId, ILookup<long, QuestRow> questsByPoi)
        {
            var fam = GatedFamily(prefab);
            if (fam == null || questsByPoi == null) return null;
            var q = questsByPoi[poiId].FirstOrDefault(x => x.Family == fam);
            if (q != null && q.Mission.CanTrade > 0) return null;
            if (q?.Mission.Mission is TradeMission m)
                return $"unlock mission: {NeedsText(m)}" + (WorkOrderText(fam) is string wo ? $"; or a work order: {wo}" : "");
            return "the trader gives an unlock mission first" + (WorkOrderText(fam) is string w ? $" (or a work order: {w})" : "");
        }

        /// <summary>The mission start dialogues a prefab uses: the TD_…_Start ones and the watched others (e.g. a foreman).</summary>
        static IEnumerable<string> MissionStarts(PoiFacts facts) =>
            facts == null ? null : facts.TradeDialogues.Concat(facts.WatchedDialogues);

        sealed class QuestRow
        {
            public string State;
            public long PoiId;
            public string Name, Prefab, Playfield, System;
            public int FacGroup = -1;
            public double Sx, Sy, Sz;
            public double? Ly;
            public Dictionary<string, int> Vars = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public MissionFamily Family;
            public MissionState Mission;
        }

        /// <summary>The player's trader mission rows, one per trader (POI + trader position), with the family resolved.</summary>
        static List<QuestRow> ReadQuests(string db, int playerId)
        {
            var tm = tradeMissions;
            var idx = traderIndex;
            var rows = new Dictionary<string, QuestRow>();
            using (var c = new SqliteConnection($"Data Source={db};Read Only=True;"))
            {
                c.Open();
                using (var q = c.CreateCommand())
                {
                    q.CommandText = "SELECT name, value, state FROM DialogueVars WHERE type = 110 AND entityid = @p";
                    q.Parameters.AddWithValue("@p", playerId);
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                        {
                            var state = r[2] as string ?? "";
                            var dash = state.IndexOf('-');
                            if (dash <= 0 || !long.TryParse(state.Substring(0, dash), out var poi)) continue;
                            if (!rows.TryGetValue(state, out var row)) rows[state] = row = new QuestRow { State = state, PoiId = poi };
                            row.Vars[r[0] as string ?? ""] = r[1] is DBNull ? 0 : Convert.ToInt32(r[1]);
                        }
                }
                foreach (var row in rows.Values)
                    using (var q = c.CreateCommand())
                    {
                        q.CommandText =
                            "SELECT e.name, s.bpname, e.facgroup, p.name, p.sectorx, p.sectory, p.sectorz, ss.name FROM Entities e " +
                            "LEFT JOIN Structures s ON s.entityid = e.entityid JOIN Playfields p ON p.pfid = e.pfid " +
                            "LEFT JOIN SolarSystems ss ON ss.ssid = p.ssid WHERE e.entityid = @e";
                        q.Parameters.AddWithValue("@e", row.PoiId);
                        using (var r = q.ExecuteReader())
                            if (r.Read())
                            {
                                row.Name = r[0] as string; row.Prefab = r[1] as string; row.FacGroup = r[2] is DBNull ? -1 : Convert.ToInt32(r[2]);
                                row.Playfield = r[3] as string; row.Sx = Convert.ToDouble(r[4]); row.Sy = Convert.ToDouble(r[5]); row.Sz = Convert.ToDouble(r[6]);
                                row.System = r[7] as string;
                            }
                    }
            }
            var list = new List<QuestRow>();
            foreach (var row in rows.Values)
            {
                if (tm == null || row.Name == null) continue;
                row.Family = tm.Resolve(MissionStarts(idx?.Facts(row.Prefab)), row.Vars);
                if (row.Family == null) continue;
                row.Mission = TradeMissions.State(row.Family, row.Vars);
                list.Add(row);
            }
            return list;
        }

        static string ItemLabel(MissionNeed n)
        {
            if (n.TokenId.HasValue) return gameText?.GetToken(n.TokenId.Value)?.Name ?? $"token {n.TokenId}";
            return gameText?.DisplayName(n.Item) ?? n.Item;
        }

        /// <summary>"needs": [{item (internal), name, count, tokenId?}]: for inventory progress and the in-reach search.</summary>
        static void WriteNeeds(Json j, TradeMission m)
        {
            j.Key("needs").Arr();
            foreach (var n in m.Needs)
            {
                j.Obj().Prop("item", n.Item).Prop("name", ItemLabel(n)).Prop("count", n.Count);
                if (n.TokenId.HasValue) j.Prop("tokenId", n.TokenId.Value);
                j.EndObj();
            }
            j.EndArr();
        }

        static string NeedsText(TradeMission m) =>
            string.Join(" + ", m.Needs.Select(n => n.Count == 1 && n.TokenId.HasValue ? ItemLabel(n) : $"{n.Count.ToString("N0", CultureInfo.InvariantCulture)} {ItemLabel(n)}"));

        static string RewardText(TradeMission m, string ownFaction)
        {
            var parts = new List<string>();
            if (m.MoneyAs == "PlatinumCard") parts.Add($"{m.Money} platinum card{(m.Money == 1 ? "" : "s")}");
            else if (m.MoneyAs == "MoneyCard") parts.Add($"{m.Money.ToString("N0", CultureInfo.InvariantCulture)} credits as a money card");
            else if (m.MoneyAs == "Credits") parts.Add($"{m.Money.ToString("N0", CultureInfo.InvariantCulture)} credits");
            foreach (var kv in m.Reputation.Where(kv => kv.Value != 0))
                parts.Add($"+{kv.Value} reputation with {(kv.Key == "*" ? ownFaction ?? "the trader's faction" : traderIndex?.Faction(kv.Key)?.Label ?? kv.Key)}");
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }

        static string TraderName(MissionFamily f) => tradeMissions?.Text(f.NpcNameKey);

        static string WorkOrderText(MissionFamily f)
        {
            if (!f.WorkOrderToken.HasValue) return null;
            var name = gameText?.GetToken(f.WorkOrderToken.Value)?.Name ?? "a work order";
            return f.WorkOrderPrice.HasValue
                ? $"{name}, {f.WorkOrderPrice.Value.ToString("N0", CultureInfo.InvariantCulture)} credits at a GIN console (no credit or reputation reward then)"
                : $"{name} from a GIN console (no credit or reputation reward then)";
        }

        /// <summary>One line for the context or a list: "Solaris-AMP Factory (Viunean) wants 50 Xenon Gas Bottle (optional follow-up)".</summary>
        static string QuestLine(QuestRow q)
        {
            var where = $"{q.Name} ({q.Playfield})";
            switch (q.Mission.Status)
            {
                case "open":
                    var s = $"{where} wants {NeedsText(q.Mission.Mission)}";
                    if (q.Mission.FollowUp) s += " (optional follow-up; it trades already)";
                    else if (q.Family.TradeGated) s += " to unlock its trades" + (q.Family.WorkOrderToken.HasValue ? ", or a work order" : "");
                    return s;
                case "locked": return $"{where}: trades locked; talking to the trader gives the mission";
                case "unlocked": return $"{where}: trades unlocked" + (q.Mission.CanTrade < q.Family.FollowUpsBelow ? ", more missions on offer (\"Offer assistance\")" : "");
                default: return $"{where}: no mission running";
            }
        }

        static void WriteQuest(Json j, QuestRow q, bool pursued)
        {
            var idx = traderIndex;
            var fac = idx?.Faction(q.FacGroup);
            j.Obj().Prop("factory", q.Name).Prop("playfield", q.Playfield);
            if (q.System != null) j.Prop("system", q.System);
            if (q.Ly.HasValue) j.Prop("ly", Math.Round(q.Ly.Value, 1));
            if (fac != null) j.Prop("faction", fac.Label);
            var trader = TraderName(q.Family);
            if (trader != null) j.Prop("trader", trader);
            j.Prop("status", q.Mission.Status);
            if (q.Family.TradeGated) j.Prop("tradesUnlocked", q.Mission.CanTrade > 0);
            if (q.Mission.Mission is TradeMission m)
            {
                j.Prop("wants", NeedsText(m));
                if (q.Mission.FollowUp) j.Prop("followUp", true);
                WriteNeeds(j, m);
                var reward = RewardText(m, fac?.Label);
                if (reward != null) j.Prop("reward", reward);
                if (!q.Mission.FollowUp && WorkOrderText(q.Family) is string wo) j.Prop("workOrder", wo);
            }
            else if (q.Mission.Status == "unlocked" && q.Mission.CanTrade < q.Family.FollowUpsBelow) j.Prop("moreMissions", true);
            if (pursued) j.Prop("pursued", true);
            j.EndObj();
        }

        /// <summary>HTTP thread: POST /quests (player, status=open|unlocked|all, near=playfield|system|&lt;ly&gt;, item=…, max).</summary>
        static (int, string) HandleQuests(string body)
        {
            var f = Form.Parse(body);
            if (tradeMissions == null) return (503, Error("The trader mission data is not loaded (no Dialogues.ecf in this scenario)."));
            DediPlayer player;
            string why;
            lock (sync) player = PickPlayer(f.Get("player"), out why);
            if (player == null) return (200, Error(why));
            var save = SaveGameDir();
            if (save == null) return (500, Error("The save game folder was not found."));
            var db = Path.Combine(save, "global.db");

            List<QuestRow> quests;
            double? px = null, py = null, pz = null;
            string here = null;
            try
            {
                quests = ReadQuests(db, player.EntityId);
                ReadSector(db, player.Playfield, out px, out py, out pz);
                here = SystemOf(db, player.Playfield);
            }
            catch (Exception e) { return (500, Error("global.db: " + e.Message)); }
            foreach (var q in quests)
                if (px.HasValue) q.Ly = Math.Sqrt(Sq(q.Sx - px.Value) + Sq(q.Sy - py.Value) + Sq(q.Sz - pz.Value)) / 100000.0;

            var status = (f.Get("status") ?? "open").Trim().ToLowerInvariant();
            IEnumerable<QuestRow> sel = quests;
            if (status == "open") sel = sel.Where(q => q.Mission.Status == "open" || q.Mission.Status == "locked");
            else if (status == "unlocked") sel = sel.Where(q => q.Mission.Status == "unlocked" || q.Mission.FollowUp);
            else status = "all";

            var near = (f.Get("near") ?? "").Trim().ToLowerInvariant();
            if (near == "playfield" || near == "planet" || near == "here") sel = sel.Where(q => q.Playfield == player.Playfield);
            else if (near == "system") sel = sel.Where(q => here != null && q.System == here);
            else if (double.TryParse(near, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxLy)) sel = sel.Where(q => q.Ly.HasValue && q.Ly.Value <= maxLy + 0.05);
            else near = null;

            var item = (f.Get("item") ?? "").Trim();
            if (item.Length > 0)
                sel = sel.Where(q => q.Mission.Mission != null && q.Mission.Mission.Needs.Any(n =>
                    ItemLabel(n).IndexOf(item, StringComparison.OrdinalIgnoreCase) >= 0 || n.Item.IndexOf(item, StringComparison.OrdinalIgnoreCase) >= 0));

            var max = int.TryParse(f.Get("max"), out var mx) && mx > 0 ? Math.Min(mx, 20) : MaxQuests;
            var list = sel.OrderBy(q => q.Ly ?? double.MaxValue).ThenBy(q => q.Name).ToList();
            var pursued = PursuedPoi(player.SteamId);

            var j = new Json().Obj().Prop("ok", true).Prop("status", status);
            if (near != null) j.Prop("near", near);
            if (item.Length > 0) j.Prop("item", item);
            j.Prop("count", list.Count).Key("quests").Arr();
            foreach (var q in list.Take(max)) WriteQuest(j, q, pursued == q.State);
            j.EndArr();
            if (list.Count > max) j.Prop("more", list.Count - max);
            j.Prop("tracked", quests.Count);
            return (200, j.EndObj().ToString());
        }

        // ----- the pursued quest --------------------------------------------------

        /// <summary>SteamID → the pursued trader (DialogueVars state key: POI id, trader position, playfield).</summary>
        static readonly Dictionary<string, string> pursuedQuests = new Dictionary<string, string>();
        static readonly Dictionary<string, (DateTime at, string json)> pursuedCache = new Dictionary<string, (DateTime, string)>();
        static bool pursuedLoaded;

        static string PursuedFile => Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "VoxtaBridge.pursued.txt");

        static string PursuedPoi(string steamId)
        {
            lock (pursuedQuests)
            {
                if (!pursuedLoaded)
                {
                    pursuedLoaded = true;
                    try
                    {
                        if (File.Exists(PursuedFile))
                            foreach (var line in File.ReadAllLines(PursuedFile))
                            {
                                var p = line.Split('\t');
                                if (p.Length == 2 && p[0].Length > 0) pursuedQuests[p[0]] = p[1];
                            }
                    }
                    catch (Exception e) { Log($"quests: reading {PursuedFile}: {e.Message}"); }
                }
                return steamId != null && pursuedQuests.TryGetValue(steamId, out var s) ? s : null;
            }
        }

        static void SetPursued(string steamId, string state)
        {
            PursuedPoi(null); // loads the file first
            lock (pursuedQuests)
            {
                if (state == null) pursuedQuests.Remove(steamId); else pursuedQuests[steamId] = state;
                pursuedCache.Remove(steamId);
                try { File.WriteAllLines(PursuedFile, pursuedQuests.Select(kv => kv.Key + "\t" + kv.Value)); }
                catch (Exception e) { Log($"quests: writing {PursuedFile}: {e.Message}"); }
            }
        }

        /// <summary>HTTP thread: POST /quest-pursue (player, name=factory name or playfield, or none).</summary>
        static (int, string) HandlePursueQuest(string body)
        {
            var f = Form.Parse(body);
            if (tradeMissions == null) return (503, Error("The trader mission data is not loaded (no Dialogues.ecf in this scenario)."));
            DediPlayer player;
            string why;
            lock (sync) player = PickPlayer(f.Get("player"), out why);
            if (player == null) return (200, Error(why));
            var wanted = (f.Get("name") ?? "").Trim();
            if (wanted.Length == 0 || wanted.Equals("none", StringComparison.OrdinalIgnoreCase) || wanted.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                SetPursued(player.SteamId, null);
                return (200, new Json().Obj().Prop("ok", true).Prop("pursuing", (string)null).EndObj().ToString());
            }
            var save = SaveGameDir();
            if (save == null) return (500, Error("The save game folder was not found."));
            List<QuestRow> quests;
            try { quests = ReadQuests(Path.Combine(save, "global.db"), player.EntityId); }
            catch (Exception e) { return (500, Error("global.db: " + e.Message)); }
            bool Match(QuestRow q, Func<string, bool> test) => test(q.Name ?? "") || test(q.Playfield ?? "") || test($"{q.Name} ({q.Playfield})");
            var open = quests.Where(q => q.Mission.Status == "open" || q.Mission.Status == "locked").ToList();
            var hits = open.Where(q => Match(q, s => s.Equals(wanted, StringComparison.OrdinalIgnoreCase))).ToList();
            if (hits.Count == 0) hits = open.Where(q => Match(q, s => s.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (hits.Count == 0)
                return (200, Error($"No open trader mission matches '{wanted}'." + (open.Count > 0
                    ? " Open ones: " + string.Join("; ", open.Take(8).Select(q => $"{q.Name} ({q.Playfield})")) + "."
                    : " The player has no open trader missions; they start by talking to a factory's trader.")));
            if (hits.Count > 1 && hits.Select(q => q.Name).Distinct().Count() > 1)
                return (200, Error($"'{wanted}' matches several: " + string.Join("; ", hits.Take(8).Select(q => $"{q.Name} ({q.Playfield})")) + ". Name one."));
            var hit = hits[0];
            SetPursued(player.SteamId, hit.State);
            var j = new Json().Obj().Prop("ok", true).Prop("line", QuestLine(hit)).Key("quest");
            WriteQuest(j, hit, true);
            return (200, j.EndObj().ToString());
        }

        /// <summary>"factory" when the trader's trades wait for the mission (also its follow-ups), else "trader".</summary>
        static string MissionKind(QuestRow q) => q.Family.TradeGated ? "factory" : "trader";

        /// <summary>
        /// /status, per online player, from global.db at most every 10 s:
        /// <c>pursuing.&lt;steamId&gt;</c> = {status, factory, line, items} for the pursued quest (a finished one stays "done"
        /// until another one is pursued or it is cleared), and <c>missions.&lt;steamId&gt;</c> = the open missions
        /// [{key, kind, line, where, followUp?, pursued?}] (key = trader + mission number, so a new follow-up at the same trader is a new key).
        /// </summary>
        /// <summary>
        /// The player's mining contract offers: one row per refinery that gave them a contract (the cooldown variable, its
        /// value changes with every contract given): [{key, value, where, ly}].
        /// </summary>
        static string ReadContractOffers(string db, int playerId, string cooldownVar, string playerPlayfield = null, bool withDiscovered = false) =>
            ReadPoiVarRows(db, playerId, cooldownVar, playerPlayfield, withDiscovered ? contractPrefabs : null);

        /// <summary>
        /// One per-player-and-POI dialogue variable (state key "&lt;POI id&gt;-…-&lt;playfield&gt;"): [{key, value, where, ly}],
        /// where = "POI name (playfield)", ly = distance from the player's playfield (galaxy sectors, 100,000 per LY).
        /// </summary>
        /// <param name="discoveredPrefabs">
        /// If set, also the POIs the player discovered that are built from one of these prefabs and have no row yet, with
        /// value 0 (= ready) and "discovered": true (e.g. refineries that never gave the player a contract).
        /// </param>
        static string ReadPoiVarRows(string db, int playerId, string varName, string playerPlayfield, HashSet<string> discoveredPrefabs = null)
        {
            var j = new Json().Arr();
            using (var c = new SqliteConnection($"Data Source={db};Read Only=True;"))
            {
                c.Open();
                (double x, double y, double z)? here = null;
                if (!string.IsNullOrEmpty(playerPlayfield))
                    using (var q = c.CreateCommand())
                    {
                        q.CommandText = "SELECT sectorx, sectory, sectorz FROM Playfields WHERE name = @pf LIMIT 1";
                        q.Parameters.AddWithValue("@pf", playerPlayfield);
                        using (var r = q.ExecuteReader())
                            if (r.Read() && !(r[0] is DBNull)) here = (Convert.ToDouble(r[0]), Convert.ToDouble(r[1]), Convert.ToDouble(r[2]));
                    }
                var rows = new List<(string state, long value)>();
                using (var q = c.CreateCommand())
                {
                    q.CommandText = "SELECT state, value FROM DialogueVars WHERE entityid = @p AND name = @n";
                    q.Parameters.AddWithValue("@p", playerId);
                    q.Parameters.AddWithValue("@n", varName);
                    using (var r = q.ExecuteReader())
                        while (r.Read()) rows.Add((r[0] as string ?? "", r[1] is DBNull ? 0 : Convert.ToInt64(r[1])));
                }
                foreach (var (state, value) in rows)
                {
                    string where = null;
                    double? ly = null;
                    var dash = state.IndexOf('-');
                    if (dash > 0 && long.TryParse(state.Substring(0, dash), out var poi))
                        using (var q = c.CreateCommand())
                        {
                            q.CommandText = "SELECT e.name, p.name, p.sectorx, p.sectory, p.sectorz FROM Entities e JOIN Playfields p ON p.pfid = e.pfid WHERE e.entityid = @e";
                            q.Parameters.AddWithValue("@e", poi);
                            using (var r = q.ExecuteReader())
                                if (r.Read())
                                {
                                    where = $"{r[0] as string} ({r[1] as string})";
                                    if (here is var (hx, hy, hz) && !(r[2] is DBNull))
                                    {
                                        double dx = Convert.ToDouble(r[2]) - hx, dy = Convert.ToDouble(r[3]) - hy, dz = Convert.ToDouble(r[4]) - hz;
                                        ly = Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz) / 100000.0, 1);
                                    }
                                }
                        }
                    j.Obj().Prop("key", state).Prop("value", value).Prop("where", where);
                    if (ly.HasValue) j.Prop("ly", ly.Value);
                    j.EndObj();
                }
                if (discoveredPrefabs != null && discoveredPrefabs.Count > 0)
                {
                    var known = new HashSet<long>(rows.Select(r => { var d = r.state.IndexOf('-'); return d > 0 && long.TryParse(r.state.Substring(0, d), out var id) ? id : -1; }));
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
                                var poi = Convert.ToInt64(r[0]);
                                if (known.Contains(poi) || !(r[6] is string bp) || !discoveredPrefabs.Contains(bp)) continue;
                                known.Add(poi);
                                j.Obj().Prop("key", "poi:" + poi.ToString(CultureInfo.InvariantCulture)).Prop("value", 0L)
                                    .Prop("where", $"{r[1] as string} ({r[2] as string})").Prop("discovered", true);
                                if (here is var (hx, hy, hz) && !(r[3] is DBNull))
                                {
                                    double dx = Convert.ToDouble(r[3]) - hx, dy = Convert.ToDouble(r[4]) - hy, dz = Convert.ToDouble(r[5]) - hz;
                                    j.Prop("ly", Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz) / 100000.0, 1));
                                }
                                j.EndObj();
                            }
                    }
                }
            }
            return j.EndArr().ToString();
        }

        static void WriteQuestStatus(Json j, IEnumerable<DediPlayer> online)
        {
            var save = SaveGameDir();
            var rows = new List<(string steamId, string pursuing, string missions, string offers, string sites)>();
            foreach (var p in online)
            {
                if (p.SteamId == null) continue;
                (DateTime at, string json) cached;
                bool fresh;
                lock (pursuedQuests) fresh = pursuedCache.TryGetValue(p.SteamId, out cached) && DateTime.UtcNow - cached.at < TimeSpan.FromSeconds(10);
                if (!fresh && save != null)
                {
                    string pursuing = null, missions, offers = null, sites = null;
                    try
                    {
                        var quests = ReadQuests(Path.Combine(save, "global.db"), p.EntityId);
                        var state = PursuedPoi(p.SteamId);
                        if (state != null)
                        {
                            var q = quests.FirstOrDefault(x => x.State == state);
                            var o = new Json().Obj();
                            if (q == null) o.Prop("status", "gone").Prop("line", "the pursued trader mission is no longer in the save");
                            else
                            {
                                var done = q.Mission.Status != "open" && q.Mission.Status != "locked";
                                o.Prop("status", done ? "done" : q.Mission.Status).Prop("factory", q.Name).Prop("line", done ? $"{q.Name} ({q.Playfield}): mission done" + (q.Mission.Status == "unlocked" ? ", trades unlocked" : "") : QuestLine(q));
                                if (q.Mission.Mission is TradeMission m && !done) WriteNeeds(o, m);
                            }
                            pursuing = o.EndObj().ToString();
                        }
                        var mj = new Json().Arr();
                        foreach (var q in quests.Where(x => x.Mission.Status == "open"))
                            {
                            mj.Obj().Prop("key", $"{q.State}#{q.Mission.Mission.Number}").Prop("kind", MissionKind(q)).Prop("line", QuestLine(q))
                                .Prop("where", $"{q.Name} ({q.Playfield})");
                            if (q.Mission.FollowUp) mj.Prop("followUp", true);
                            if (q.State == state) mj.Prop("pursued", true);
                            WriteNeeds(mj, q.Mission.Mission);
                            mj.EndObj();
                        }
                        missions = mj.EndArr().ToString();
                        if (miningContracts?.CooldownVar is string cv) offers = ReadContractOffers(Path.Combine(save, "global.db"), p.EntityId, cv, p.Playfield);
                        sites = ReadSiteJobs(Path.Combine(save, "global.db"), p.EntityId, p.Playfield);
                    }
                    catch (Exception e)
                    {
                        pursuing = PursuedPoi(p.SteamId) == null ? null : new Json().Obj().Prop("status", "error").Prop("line", "global.db: " + e.Message).EndObj().ToString();
                        missions = null; // unknown: the module keeps its last list, so nothing reads as new
                    }
                    cached = (DateTime.UtcNow, (pursuing ?? "") + "\u0001" + (missions ?? "") + "\u0001" + (offers ?? "") + "\u0001" + (sites ?? ""));
                    lock (pursuedQuests) pursuedCache[p.SteamId] = cached;
                }
                if (cached.json == null) continue;
                var parts = cached.json.Split('\u0001');
                rows.Add((p.SteamId, parts[0].Length > 0 ? parts[0] : null, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null,
                    parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null, parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null));
            }
            j.Key("pursuing").Obj();
            foreach (var r in rows.Where(r => r.pursuing != null)) j.Key(r.steamId).Raw(r.pursuing);
            j.EndObj();
            j.Key("missions").Obj();
            foreach (var r in rows.Where(r => r.missions != null)) j.Key(r.steamId).Raw(r.missions);
            j.EndObj();
            if (GameTimeSeconds() is long gameNow) j.Prop("gameTimeS", gameNow);
            j.Key("siteJobs").Obj();
            foreach (var r in rows.Where(r => r.sites != null)) j.Key(r.steamId).Raw(r.sites);
            j.EndObj();
            j.Key("contractOffers").Obj();
            foreach (var r in rows.Where(r => r.offers != null)) j.Key(r.steamId).Raw(r.offers);
            j.EndObj();
        }
    }
}
