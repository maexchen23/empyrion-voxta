using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// mark_stars (POST /marks, dedi): Voxta's star markers on the player's galaxy map, as private star bookmarks in
    /// global.db (format verified 2026-11-18 against markers set in-game: type 0, no pfid, sector = LY × 100000,
    /// waypoint + HUD, maxdistance -1, owner facgroup 1 / facid = player). The galaxy map and marker list read them at
    /// once; the HUD only loads markers at login (client-side copy). A marker is removed when the player arrives in its
    /// system (playfield change into a sector within 1 LY of the star) or on request, but only once the player is offline:
    /// deleting a marker while the galaxy map's marker dialog is editing it leaves the client's dialog stuck (NRE in its
    /// save handler for every later marker until the game is restarted; seen 2026-11-18). Pending deletions are in memory
    /// and lost on a server restart (the markers then just stay).
    /// Existing Voxta marker rows are reused (moved and renamed) rather than deleted; reached ones are renamed "done: …".
    /// action=add (stars=a, b, c; numbered=1 for a route; replace=0 keeps earlier ones), action=clear, action=list.
    /// </summary>
    public partial class BridgeMod
    {
        /// <summary>Player entity id → marker names to delete when the player is offline ("*" = all Voxta markers).</summary>
        static readonly Dictionary<int, HashSet<string>> pendingMarkDeletes = new Dictionary<int, HashSet<string>>();

        static void QueueMarkDelete(int playerId, string name)
        {
            lock (pendingMarkDeletes)
            {
                if (!pendingMarkDeletes.TryGetValue(playerId, out var set)) pendingMarkDeletes[playerId] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(name ?? "*");
            }
        }

        /// <summary>A marker is (re)added: it must survive pending deletions; a pending "clear all" turns into the other names.</summary>
        static void KeepMark(int playerId, string name, IEnumerable<string> otherMarks)
        {
            lock (pendingMarkDeletes)
            {
                if (!pendingMarkDeletes.TryGetValue(playerId, out var set)) return;
                if (set.Remove("*")) foreach (var o in otherMarks) set.Add(o);
                set.Remove(name);
            }
        }

        static List<string> PendingMarkDeletes(int playerId)
        {
            lock (pendingMarkDeletes) return pendingMarkDeletes.TryGetValue(playerId, out var set) ? set.ToList() : new List<string>();
        }

        /// <summary>Dedi: the player went offline; run the queued marker deletions (off the main thread).</summary>
        static void OnPlayerOffline(int playerId, string playerName)
        {
            HashSet<string> names;
            lock (pendingMarkDeletes)
            {
                if (!pendingMarkDeletes.TryGetValue(playerId, out names)) return;
                pendingMarkDeletes.Remove(playerId);
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var save = SaveGameDir();
                    if (save == null) return;
                    using (var c = OpenSaveDb(Path.Combine(save, "global.db")))
                    {
                        var n = names.Contains("*") ? DeleteMarks(c, playerId, null) : names.Sum(x => DeleteMarks(c, playerId, x));
                        Log($"marks: {playerName} offline, removed {n} marker(s)");
                    }
                }
                catch (Exception e) { Log($"marks: deleting after logoff failed: {e.Message}"); }
            });
        }

        static (int, string) HandleMarks(string body)
        {
            var f = Form.Parse(body);
            var action = (f.Get("action") ?? "add").Trim().ToLowerInvariant();
            DediPlayer player;
            string why;
            lock (sync) player = PickPlayer(f.Get("player"), out why);
            if (player == null) return (409, Error(why));
            var save = SaveGameDir();
            if (save == null) return (500, Error("The save game folder was not found."));
            var db = Path.Combine(save, "global.db");
            var j = new Json().Obj().Prop("ok", true).Prop("action", action);

            using (var c = OpenSaveDb(db))
            {
                if (action == "clear")
                {
                    QueueMarkDelete(player.EntityId, null);
                    Log($"marks: clear queued for {player.Name} (runs at logoff)");
                    j.Prop("pendingUntilLogoff", true);
                    return (200, ListMarks(c, j, player.EntityId).EndObj().ToString());
                }
                if (action == "list")
                {
                    j.Key("pendingDeletes").Arr();
                    foreach (var n in PendingMarkDeletes(player.EntityId)) j.Val(n);
                    j.EndArr();
                    return (200, ListMarks(c, j, player.EntityId).EndObj().ToString());
                }
                if (action == "permanent") return HandlePermanentMark(c, j, f, player, db);
                if (action != "add") return (400, Error("action must be add, permanent, clear or list"));

                var names = StarMarks.SplitNames(f.Get("stars"));
                if (names.Count == 0) return (400, Error("'stars' is required: one or more star names, comma separated."));
                if (names.Count > StarMarks.MaxMarks) return (400, Error($"At most {StarMarks.MaxMarks} stars at once."));
                var stars = GalaxyStars();
                if (stars == null) return (503, Error("The galaxy is not available on this server: " + galaxyStarsError));

                double? px = null, py = null, pz = null;
                try { ReadSector(db, player.Playfield, out px, out py, out pz); } catch { }
                double Dist(Star s) => px.HasValue ? Math.Sqrt(Sq(s.X - px.Value / 100000) + Sq(s.Y - py.Value / 100000) + Sq(s.Z - pz.Value / 100000)) : 0;

                var found = new List<Star>();
                var missing = new List<(string name, List<string> suggestions)>();
                var visible = stars.Where(s => !s.Hidden).ToList();
                foreach (var n in names)
                {
                    var s = StarMarks.Find(visible, x => x.Name, Dist, n, out var sugg);
                    if (s.Name == null) missing.Add((n, sugg));
                    else if (!found.Any(x => x.Name == s.Name)) found.Add(s);
                }
                if (found.Count == 0)
                {
                    j = new Json().Obj().Prop("ok", false).Prop("error", "None of these stars exists.");
                    MissingJson(j, missing);
                    return (404, j.EndObj().ToString());
                }

                var numbered = f.Get("numbered") == "1" || (f.Get("numbered") == null && found.Count > 1 && f.Get("route") == "1");
                // Reuse the player's Voxta marker rows (move + rename, in id order): an update keeps the row, so the client's
                // marker dialog never loses a marker it may be editing (tested 2026-11-18: an open edit is just discarded).
                // Rows left over are renamed "unused (Voxta)" and deleted at logoff; new rows only when more are needed.
                var keep = f.Get("replace") == "0";
                var slots = keep ? new List<(long bid, string name)>() : MarkRows(c, player.EntityId);
                ulong ticks = 0;
                try { ticks = api.Application.GameTicks; } catch { }
                var all = AllMarkNames(c, player.EntityId);
                j.Key("marked").Arr();
                for (var i = 0; i < found.Count; i++)
                {
                    var s = found[i];
                    var name = StarMarks.MarkerName(s.Name, numbered ? i + 1 : 0);
                    KeepMark(player.EntityId, name, all);
                    if (i < slots.Count) UpdateStarMark(c, slots[i].bid, name, s, ticks);
                    else if (!MarkExists(c, player.EntityId, name)) InsertStarMark(c, player.EntityId, name, s, ticks);
                    j.Obj().Prop("marker", name).Prop("star", s.Name).Prop("class", galaxyOdds?.Class(s.TypeIndex)?.StarClass);
                    if (px.HasValue) j.Prop("ly", Math.Round(Dist(s), 1));
                    j.EndObj();
                }
                j.EndArr().Prop("numbered", numbered);
                var unused = slots.Skip(found.Count).ToList();
                foreach (var (bid, _) in unused) RenameMark(c, bid, StarMarks.UnusedName);
                if (unused.Count > 0)
                {
                    QueueMarkDelete(player.EntityId, StarMarks.UnusedName);
                    j.Prop("unusedUntilLogoff", unused.Count);
                }
                MissingJson(j, missing);
                Log($"marks: {player.Name}: {string.Join(", ", found.Select(s => s.Name))}{(missing.Count > 0 ? $"; unknown: {string.Join(", ", missing.Select(m => m.name))}" : "")}");
                return (200, j.EndObj().ToString());
            }
        }

        static List<(long bid, string name)> MarkRows(SqliteConnection c, int playerId)
        {
            var r = new List<(long, string)>();
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT bid, name FROM Bookmarks WHERE type = 0 AND facgroup = 1 AND facid = @p AND entityid = @p AND name LIKE @n ORDER BY bid";
                q.Parameters.AddWithValue("@p", playerId);
                q.Parameters.AddWithValue("@n", "%" + StarMarks.Suffix);
                using (var rd = q.ExecuteReader()) while (rd.Read()) r.Add((Convert.ToInt64(rd[0]), rd[1] as string));
            }
            return r;
        }

        static void UpdateStarMark(SqliteConnection c, long bid, string name, Star s, ulong ticks)
        {
            using (var u = c.CreateCommand())
            {
                u.CommandText = "UPDATE Bookmarks SET name = @name, sectorx = @sx, sectory = @sy, sectorz = @sz, posx = @x, posy = @y, posz = @z, " +
                                "iswaypoint = 1, isshowhud = 1, isremove = 0, createdticks = @t WHERE bid = @bid";
                u.Parameters.AddWithValue("@name", name);
                u.Parameters.AddWithValue("@sx", s.X * 100000L);
                u.Parameters.AddWithValue("@sy", s.Y * 100000L);
                u.Parameters.AddWithValue("@sz", s.Z * 100000L);
                u.Parameters.AddWithValue("@x", (double)s.X);
                u.Parameters.AddWithValue("@y", (double)s.Y);
                u.Parameters.AddWithValue("@z", (double)s.Z);
                u.Parameters.AddWithValue("@t", (long)ticks);
                u.Parameters.AddWithValue("@bid", bid);
                u.ExecuteNonQuery();
            }
        }

        static void RenameMark(SqliteConnection c, long bid, string name)
        {
            using (var u = c.CreateCommand())
            {
                u.CommandText = "UPDATE Bookmarks SET name = @name WHERE bid = @bid";
                u.Parameters.AddWithValue("@name", name);
                u.Parameters.AddWithValue("@bid", bid);
                u.ExecuteNonQuery();
            }
        }

        static List<string> AllMarkNames(SqliteConnection c, int playerId)
        {
            var r = new List<string>();
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT name FROM Bookmarks WHERE type = 0 AND facgroup = 1 AND facid = @p AND entityid = @p AND name LIKE @n";
                q.Parameters.AddWithValue("@p", playerId);
                q.Parameters.AddWithValue("@n", "%" + StarMarks.Suffix);
                using (var rd = q.ExecuteReader()) while (rd.Read()) r.Add(rd[0] as string);
            }
            return r;
        }

        /// <summary>
        /// A marker the player keeps: an ordinary private star marker with a free name, written once and
        /// never touched by the mod again (no suffix, so the reuse/cleanup logic ignores it). hud=0 clears isshowhud and
        /// iswaypoint (galaxy map only).
        /// </summary>
        static (int, string) HandlePermanentMark(SqliteConnection c, Json j, Dictionary<string, string> f, DediPlayer player, string db)
        {
            if (!StarMarks.ValidPermanentName(f.Get("name"), out var name, out var why)) return (400, Error(why));
            var wanted = f.Get("star")?.Trim();
            if (string.IsNullOrEmpty(wanted)) return (400, Error("'star' is required."));
            var stars = GalaxyStars();
            if (stars == null) return (503, Error("The galaxy is not available on this server: " + galaxyStarsError));
            double? px = null, py = null, pz = null;
            try { ReadSector(db, player.Playfield, out px, out py, out pz); } catch { }
            double Dist(Star s) => px.HasValue ? Math.Sqrt(Sq(s.X - px.Value / 100000) + Sq(s.Y - py.Value / 100000) + Sq(s.Z - pz.Value / 100000)) : 0;
            var star = StarMarks.Find(stars.Where(s => !s.Hidden), x => x.Name, Dist, wanted, out var sugg);
            if (star.Name == null)
            {
                var e = new Json().Obj().Prop("ok", false).Prop("error", $"There is no star called '{wanted}'.");
                MissingJson(e, new List<(string, List<string>)> { (wanted, sugg) });
                return (404, e.EndObj().ToString());
            }
            if (MarkExists(c, player.EntityId, name)) return (409, Error($"The player already has a marker called '{name}'."));
            var hud = f.Get("hud") != "0";
            ulong ticks = 0;
            try { ticks = api.Application.GameTicks; } catch { }
            InsertStarMark(c, player.EntityId, name, star, ticks, hud);
            j.Prop("permanent", true).Prop("marker", name).Prop("star", star.Name).Prop("class", galaxyOdds?.Class(star.TypeIndex)?.StarClass).Prop("hud", hud);
            if (px.HasValue) j.Prop("ly", Math.Round(Dist(star), 1));
            Log($"marks: {player.Name}: permanent marker '{name}' on {star.Name} (hud {(hud ? "on" : "off")})");
            return (200, j.EndObj().ToString());
        }

        static bool MarkExists(SqliteConnection c, int playerId, string name)
        {
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT 1 FROM Bookmarks WHERE type = 0 AND facgroup = 1 AND facid = @p AND entityid = @p AND name = @n LIMIT 1";
                q.Parameters.AddWithValue("@p", playerId);
                q.Parameters.AddWithValue("@n", name);
                return q.ExecuteScalar() != null;
            }
        }

        static void MissingJson(Json j, List<(string name, List<string> suggestions)> missing)
        {
            if (missing.Count == 0) return;
            j.Key("unknown").Arr();
            foreach (var (name, sugg) in missing)
            {
                j.Obj().Prop("name", name).Key("didYouMean").Arr();
                foreach (var s in sugg) j.Val(s);
                j.EndArr().EndObj();
            }
            j.EndArr();
        }

        static SqliteConnection OpenSaveDb(string db)
        {
            var c = new SqliteConnection($"Data Source={db};");
            c.Open();
            using (var p = c.CreateCommand()) { p.CommandText = "PRAGMA busy_timeout = 3000"; p.ExecuteNonQuery(); }
            return c;
        }

        static void InsertStarMark(SqliteConnection c, int playerId, string name, Star s, ulong ticks, bool hud = true)
        {
            using (var ins = c.CreateCommand())
            {
                ins.CommandText =
                    "INSERT INTO Bookmarks (type, refid, facgroup, facid, entityid, pfid, name, sectorx, sectory, sectorz, posx, posy, posz, icon, " +
                    "isshared, iswaypoint, isremove, isshowhud, iscallback, createdticks, expireafterticks, mindistance, maxdistance) " +
                    "VALUES (0, 0, 1, @p, @p, NULL, @name, @sx, @sy, @sz, @x, @y, @z, 1, 0, @hud, 0, @hud, 0, @t, 0, 0, -1)";
                ins.Parameters.AddWithValue("@hud", hud ? 1 : 0);
                ins.Parameters.AddWithValue("@p", playerId);
                ins.Parameters.AddWithValue("@name", name);
                ins.Parameters.AddWithValue("@sx", s.X * 100000L);
                ins.Parameters.AddWithValue("@sy", s.Y * 100000L);
                ins.Parameters.AddWithValue("@sz", s.Z * 100000L);
                ins.Parameters.AddWithValue("@x", (double)s.X);
                ins.Parameters.AddWithValue("@y", (double)s.Y);
                ins.Parameters.AddWithValue("@z", (double)s.Z);
                ins.Parameters.AddWithValue("@t", (long)ticks);
                ins.ExecuteNonQuery();
            }
        }

        /// <summary>Deletes the player's Voxta star markers: all of them, or the one with this exact name.</summary>
        static int DeleteMarks(SqliteConnection c, int playerId, string exactName)
        {
            using (var del = c.CreateCommand())
            {
                del.CommandText = "DELETE FROM Bookmarks WHERE type = 0 AND facgroup = 1 AND facid = @p AND entityid = @p AND " +
                                  (exactName == null ? "name LIKE @n" : "name = @n");
                del.Parameters.AddWithValue("@p", playerId);
                del.Parameters.AddWithValue("@n", exactName ?? "%" + StarMarks.Suffix);
                return del.ExecuteNonQuery();
            }
        }

        static Json ListMarks(SqliteConnection c, Json j, int playerId)
        {
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT name, posx, posy, posz FROM Bookmarks WHERE type = 0 AND facgroup = 1 AND facid = @p AND entityid = @p AND name LIKE @n ORDER BY name";
                q.Parameters.AddWithValue("@p", playerId);
                q.Parameters.AddWithValue("@n", "%" + StarMarks.Suffix);
                j.Key("marks").Arr();
                using (var r = q.ExecuteReader())
                    while (r.Read()) j.Obj().Prop("marker", r[0] as string).Prop("pos", $"{r[1]},{r[2]},{r[3]}").EndObj();
                j.EndArr();
            }
            return j;
        }

        /// <summary>Dedi: the player changed playfield; remove Voxta markers within 1 LY of the new sector (off the main thread).</summary>
        static void OnPlayfieldChanged(int playerId, string playerName, string playfield)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var save = SaveGameDir();
                    if (save == null || string.IsNullOrEmpty(playfield)) return;
                    var db = Path.Combine(save, "global.db");
                    // a first visit creates the playfield's row a moment after the warp: retry a few times
                    double? sx = null, sy = null, sz = null;
                    for (var attempt = 0; attempt < 6 && !sx.HasValue; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(10000);
                        ReadSector(db, playfield, out sx, out sy, out sz);
                    }
                    if (!sx.HasValue) { Log($"marks: no sector for {playfield}, markers kept"); return; }
                    double x = sx.Value / 100000, y = sy.Value / 100000, z = sz.Value / 100000;
                    using (var c = OpenSaveDb(db))
                    {
                        var reached = new List<string>();
                        using (var q = c.CreateCommand())
                        {
                            q.CommandText = "SELECT name, posx, posy, posz FROM Bookmarks WHERE type = 0 AND facgroup = 1 AND facid = @p AND entityid = @p AND name LIKE @n";
                            q.Parameters.AddWithValue("@p", playerId);
                            q.Parameters.AddWithValue("@n", "%" + StarMarks.Suffix);
                            using (var r = q.ExecuteReader())
                                while (r.Read())
                                {
                                    var d = Math.Sqrt(Sq(Convert.ToDouble(r[1]) - x) + Sq(Convert.ToDouble(r[2]) - y) + Sq(Convert.ToDouble(r[3]) - z));
                                    if (d <= 1.0) reached.Add(r[0] as string);
                                }
                        }
                        foreach (var n in reached)
                        {
                            if (n.StartsWith(StarMarks.DonePrefix, StringComparison.Ordinal)) continue;
                            var done = StarMarks.DonePrefix + n;
                            using (var u = c.CreateCommand())
                            {
                                u.CommandText = "UPDATE Bookmarks SET name = @d WHERE type = 0 AND facgroup = 1 AND facid = @p AND entityid = @p AND name = @n";
                                u.Parameters.AddWithValue("@d", done);
                                u.Parameters.AddWithValue("@p", playerId);
                                u.Parameters.AddWithValue("@n", n);
                                u.ExecuteNonQuery();
                            }
                            QueueMarkDelete(playerId, done);
                        }
                        if (reached.Count > 0) Log($"marks: {playerName} reached {playfield}, marked done (removed at logoff): {string.Join(", ", reached)}");
                    }
                }
                catch (Exception e) { Log($"marks: cleanup after playfield change failed: {e.Message}"); }
            });
        }
    }
}
