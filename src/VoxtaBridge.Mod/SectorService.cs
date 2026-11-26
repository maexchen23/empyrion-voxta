using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// What the player's ship picks up in the current sector (in /status as <c>sector.&lt;steamId&gt;</c>): the scenario's
    /// warp-in report (and whether this is the first visit), a probe-scan site, and NPCs calling for help. Only what a
    /// ship would hear: the report the PDA shows anyway, pleas on the local channel (space sectors only), its own scanner.
    /// </summary>
    public partial class BridgeMod
    {
        static volatile SectorSites sectorSites;
        /// <summary>Prefab → the call for help its NPC makes.</summary>
        static volatile Dictionary<string, HelpCall> helpPrefabs;

        /// <summary>A visit counts as the first while the playfield was discovered less than this long ago (game time).</summary>
        const long FirstVisitSeconds = 600;
        /// <summary>Playfields.pftype of a space playfield (1 planet, 2 space, 4 moon, …).</summary>
        const int SpacePlayfieldType = 2;

        /// <summary>SteamID → the last logged sector summary (quest log), so only changes are written.</summary>
        static readonly Dictionary<string, string> loggedSector = new Dictionary<string, string>();
        static readonly System.Text.RegularExpressions.Regex Ticking = new System.Text.RegularExpressions.Regex(@"""(nowS|endsAtS)"":\d+,?", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Quest log: the sector summary when it changes (arrival, scan done, a call answered, a rescue …), not the ticking clock.</summary>
        static void LogSectorChange(DediPlayer p, string sector)
        {
            if (config?.QuestLog != true || p.SteamId == null || sector == null) return;
            var key = Ticking.Replace(sector, "");
            lock (loggedSector)
            {
                if (loggedSector.TryGetValue(p.SteamId, out var last) && last == key) return;
                loggedSector[p.SteamId] = key;
            }
            // the briefing text is long and fixed: its kind is enough here
            var line = System.Text.RegularExpressions.Regex.Replace(sector, @"""briefing"":""(?:[^""\\]|\\.)*""", "\"briefing\":\"…\"");
            QuestLogLine(RecWhere(p), "sector " + line);
        }

        /// <summary>{playfield, type, planetType, briefing?, briefingKind?, firstVisit, scan?, calls: […]}, or null when nothing is known.</summary>
        static string ReadSector(string db, int playerId, string playfield)
        {
            var sites = sectorSites;
            if (sites == null || string.IsNullOrEmpty(playfield)) return null;
            using (var c = new SqliteConnection($"Data Source={db};Read Only=True;"))
            {
                c.Open();
                string folder = null;
                long pfid = -1;
                var pftype = -1;
                using (var q = c.CreateCommand())
                {
                    q.CommandText = "SELECT pfid, pftype, planettype FROM Playfields WHERE name = @pf LIMIT 1";
                    q.Parameters.AddWithValue("@pf", playfield);
                    using (var r = q.ExecuteReader())
                        if (r.Read())
                        {
                            pfid = Convert.ToInt64(r[0]);
                            pftype = r[1] is DBNull ? -1 : Convert.ToInt32(r[1]);
                            folder = r[2] as string;
                        }
                }
                var planetType = folder == null ? null : traderIndex?.PlanetTypeOf(folder);
                var j = new Json().Obj().Prop("playfield", playfield).Prop("type", folder).Prop("planetType", planetType);

                if (sites.Briefing(folder, planetType) is SectorBriefing b)
                {
                    j.Prop("briefingKind", b.Kind).Prop("briefing", b.Text);
                    // first visit: discovered just now (or not yet written); later visits stay quiet
                    long? discovered = null;
                    using (var q = c.CreateCommand())
                    {
                        q.CommandText = "SELECT MAX(gametime) FROM DiscoveredPlayfields WHERE entityid = @p AND pfid = @f";
                        q.Parameters.AddWithValue("@p", playerId);
                        q.Parameters.AddWithValue("@f", pfid);
                        var v = q.ExecuteScalar();
                        if (v != null && !(v is DBNull)) discovered = Convert.ToInt64(v);
                    }
                    var nowTicks = GameTimeSeconds() * 20;
                    j.Prop("firstVisit", discovered == null || discovered == 0 || (nowTicks.HasValue && nowTicks.Value - discovered.Value < FirstVisitSeconds * 20));
                }

                if (sites.Scan(planetType, folder) is ScanSite s)
                {
                    long PfVar(string name)
                    {
                        if (name == null) return 0;
                        using (var q = c.CreateCommand())
                        {
                            q.CommandText = "SELECT value FROM DialogueVars WHERE entityid = @p AND name = @n AND state = @s LIMIT 1";
                            q.Parameters.AddWithValue("@p", playerId);
                            q.Parameters.AddWithValue("@n", name);
                            q.Parameters.AddWithValue("@s", playfield);
                            var v = q.ExecuteScalar();
                            return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
                        }
                    }
                    j.Key("scan").Obj().Prop("label", s.Label).Prop("probe", s.ProbeItem).Prop("probeName", gameText?.DisplayName(s.ProbeItem) ?? s.ProbeItem);
                    if (s.GateVar != null) j.Prop("gated", PfVar(s.GateVar) < 1 ? "waiting" : "open");
                    // the table's own count once rolled; before that the count its formula gives (exact when the roll can't change it)
                    var count = PfVar(s.CountVar);
                    if (count > 0) j.Prop("probes", count);
                    else if (s.ProbeRange(PfVar(s.ScoreVar)) is (int lo, int hi))
                    {
                        if (lo == hi) j.Prop("probes", lo);
                        else j.Prop("probesMin", lo).Prop("probesMax", hi);
                    }
                    if (PfVar(s.DoneVar) >= 1) j.Prop("done", true);
                    j.EndObj();
                }

                // missions against the clock on this playfield (RE2: the nebula's escape pods)
                j.Key("timed").Arr();
                foreach (var t in sites.Timed)
                {
                    long TVar(string name)
                    {
                        if (name == null) return 0;
                        using (var q = c.CreateCommand())
                        {
                            q.CommandText = "SELECT value FROM DialogueVars WHERE entityid = @p AND name = @n AND state = @s LIMIT 1";
                            q.Parameters.AddWithValue("@p", playerId);
                            q.Parameters.AddWithValue("@n", name);
                            q.Parameters.AddWithValue("@s", playfield);
                            var v = q.ExecuteScalar();
                            return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
                        }
                    }
                    var endsAt = TVar(t.TimerVar);
                    var st = TVar(t.StateVar);
                    if (endsAt <= 0 && st != t.ActiveValue) continue;
                    var state = st == t.DoneValue ? "done" : st == t.ActiveValue ? "active" : "none";
                    if (state == "none") continue;
                    j.Obj().Prop("key", t.Start).Prop("npc", t.Npc).Prop("state", state).Prop("endsAtS", endsAt);
                    if (GameTimeSeconds() is long now) j.Prop("nowS", now);
                    if (t.CounterVar != null) j.Prop("count", TVar(t.CounterVar)).Prop("total", t.Total);
                    j.EndObj();
                }
                j.EndArr();

                j.Key("calls").Arr();
                var prefabs = helpPrefabs;
                if (prefabs != null && prefabs.Count > 0 && pftype == SpacePlayfieldType)
                {
                    var pois = new List<(long id, string name, HelpCall call)>();
                    using (var q = c.CreateCommand())
                    {
                        q.CommandText = "SELECT e.entityid, e.name, s.bpname FROM Entities e JOIN Structures s ON s.entityid = e.entityid " +
                                        "WHERE e.pfid = @f AND (e.isremoved IS NULL OR e.isremoved = 0)";
                        q.Parameters.AddWithValue("@f", pfid);
                        using (var r = q.ExecuteReader())
                            while (r.Read())
                                if (r[2] is string bp && prefabs.TryGetValue(bp, out var call)) pois.Add((Convert.ToInt64(r[0]), r[1] as string, call));
                    }
                    foreach (var (id, name, call) in pois)
                    {
                        var idText = id.ToString(CultureInfo.InvariantCulture);
                        long Var(string varName)
                        {
                            if (varName == null) return 0;
                            using (var q = c.CreateCommand())
                            {
                                // per POI ("<id>-…") or per playfield
                                q.CommandText = "SELECT MAX(value) FROM DialogueVars WHERE entityid = @p AND name = @n AND (state LIKE @poi OR state = @pf)";
                                q.Parameters.AddWithValue("@p", playerId);
                                q.Parameters.AddWithValue("@n", varName);
                                q.Parameters.AddWithValue("@poi", idText + "-%");
                                q.Parameters.AddWithValue("@pf", playfield);
                                var v = q.ExecuteScalar();
                                return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
                            }
                        }
                        var plea = call.Plea;
                        var rolled = false;
                        if (call.SelectorVar != null && Var(call.SelectorVar) is long sel && sel > 0 && call.PleaByValue.TryGetValue((int)sel, out var p))
                        {
                            plea = p;
                            rolled = true;
                        }
                        j.Obj().Prop("key", idText).Prop("poi", name).Prop("where", $"{name} ({playfield})").Prop("plea", plea);
                        if (rolled) j.Prop("specific", true);
                        if (Var(call.DoneVar) >= call.DoneValue) j.Prop("done", true);
                        j.EndObj();
                    }
                }
                j.EndArr();
                return j.EndObj().ToString();
            }
        }
    }
}
