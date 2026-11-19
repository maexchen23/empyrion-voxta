using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eleon.Modding;
using Mono.Data.Sqlite;
using UnityEngine;

namespace VoxtaBridge
{
    /// <summary>
    /// Experiment (2026-11-18): a galaxy-wide HUD waypoint on a star, like the ones set on the galaxy map. Those
    /// are global.db Bookmarks rows with type 0 (BookmarkType.Star), no pfid, sector = star LY × 100000, pos = star LY,
    /// iswaypoint = isshowhud = 1, maxdistance = -1, owned by the player (facgroup 1, facid = entity id). No API creates
    /// them, so the dedi writes the row; the dedi reads bookmarks from the DB per faction, but the client may only ask at
    /// login. refresh: add and remove a temporary playfield marker (IPda.SetMapMarker) to make the client reload.
    /// POST /debug/starmarker: action=add|refresh|remove|list, star=&lt;name&gt;, refresh=1, player=SteamID.
    /// </summary>
    public partial class BridgeMod
    {
        const string MarkerSuffix = " (Voxta)";
        const string RefreshMarkerName = "Voxta refresh";
        static readonly List<(int playerId, Vector3 pos, ulong at)> markerRemovals = new List<(int, Vector3, ulong)>();

        /// <summary>HTTP thread (dedi).</summary>
        static (int, string) HandleStarMarker(string body)
        {
            var f = Form.Parse(body);
            var action = (f.Get("action") ?? "add").Trim().ToLowerInvariant();
            DediPlayer player;
            string why;
            lock (sync) player = PickPlayer(f.Get("player"), out why);
            if (player == null) return (409, Error(why));
            var save = SaveGameDir();
            if (save == null) return (500, Error("save folder not found"));
            var db = Path.Combine(save, "global.db");
            var j = new Json().Obj().Prop("ok", true).Prop("action", action).Prop("player", player.Name);

            if (action == "refresh") return (200, Refresh(j, player).EndObj().ToString());
            if (action == "push")
            {
                // faction to announce: default = the player's own faction from global.db
                int group = 0, id = -1;
                if (int.TryParse(f.Get("group"), out var g)) group = g;
                if (int.TryParse(f.Get("id"), out var i)) id = i;
                if (id < 0)
                    try { ReadPlayerTradeData(db, player.EntityId, player.Playfield, out group, out id, out _, out _, out _, new List<KnownPoi>()); }
                    catch (Exception e) { return (500, Error("faction lookup: " + e.Message)); }
                lock (bookmarkPushes) bookmarkPushes.Enqueue((group, id));
                return (200, j.Prop("queued", $"BookmarkUpdate for faction ({group}, {id})").EndObj().ToString());
            }

            using (var c = new SqliteConnection($"Data Source={db};"))
            {
                c.Open();
                using (var pragma = c.CreateCommand()) { pragma.CommandText = "PRAGMA busy_timeout = 3000"; pragma.ExecuteNonQuery(); }
                if (action == "list" || action == "remove")
                {
                    if (action == "remove")
                        using (var del = c.CreateCommand())
                        {
                            del.CommandText = "DELETE FROM Bookmarks WHERE type = 0 AND entityid = @p AND name LIKE @n";
                            del.Parameters.AddWithValue("@p", player.EntityId);
                            del.Parameters.AddWithValue("@n", "%" + MarkerSuffix);
                            j.Prop("removed", del.ExecuteNonQuery());
                        }
                    using (var q = c.CreateCommand())
                    {
                        q.CommandText = "SELECT bid, name, posx, posy, posz, isshowhud, iswaypoint, maxdistance, createdticks FROM Bookmarks WHERE type = 0 AND entityid = @p ORDER BY bid";
                        q.Parameters.AddWithValue("@p", player.EntityId);
                        j.Key("starMarkers").Arr();
                        using (var r = q.ExecuteReader())
                            while (r.Read())
                                j.Obj().Prop("bid", Convert.ToInt64(r[0])).Prop("name", r[1] as string)
                                    .Prop("pos", $"{r[2]},{r[3]},{r[4]}").Prop("hud", Convert.ToInt32(r[5])).Prop("waypoint", Convert.ToInt32(r[6]))
                                    .Prop("maxdistance", Convert.ToInt32(r[7])).Prop("created", Convert.ToInt64(r[8])).EndObj();
                        j.EndArr();
                    }
                    if (action == "remove" && f.Get("refresh") == "1") Refresh(j, player);
                    return (200, j.EndObj().ToString());
                }
                if (action != "add") return (400, Error("action must be add, refresh, remove or list"));

                var name = f.Get("star")?.Trim();
                if (string.IsNullOrEmpty(name)) return (400, Error("'star' is required"));
                var stars = GalaxyStars();
                if (stars == null) return (503, Error("galaxy not available: " + galaxyStarsError));
                var star = stars.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (star.Name == null) return (404, Error($"no star named '{name}'"));

                ulong ticks = 0;
                try { ticks = api.Application.GameTicks; } catch { }
                using (var ins = c.CreateCommand())
                {
                    ins.CommandText =
                        "INSERT INTO Bookmarks (type, refid, facgroup, facid, entityid, pfid, name, sectorx, sectory, sectorz, posx, posy, posz, icon, " +
                        "isshared, iswaypoint, isremove, isshowhud, iscallback, createdticks, expireafterticks, mindistance, maxdistance) " +
                        "VALUES (0, 0, 1, @p, @p, NULL, @name, @sx, @sy, @sz, @x, @y, @z, 1, 0, 1, 0, 1, 0, @t, 0, 0, -1)";
                    ins.Parameters.AddWithValue("@p", player.EntityId);
                    ins.Parameters.AddWithValue("@name", star.Name + MarkerSuffix);
                    ins.Parameters.AddWithValue("@sx", star.X * 100000L);
                    ins.Parameters.AddWithValue("@sy", star.Y * 100000L);
                    ins.Parameters.AddWithValue("@sz", star.Z * 100000L);
                    ins.Parameters.AddWithValue("@x", (double)star.X);
                    ins.Parameters.AddWithValue("@y", (double)star.Y);
                    ins.Parameters.AddWithValue("@z", (double)star.Z);
                    ins.Parameters.AddWithValue("@t", (long)ticks);
                    ins.ExecuteNonQuery();
                }
                using (var id = c.CreateCommand())
                {
                    id.CommandText = "SELECT last_insert_rowid()";
                    j.Prop("bid", Convert.ToInt64(id.ExecuteScalar()));
                }
                j.Prop("star", star.Name).Prop("pos", $"{star.X},{star.Y},{star.Z}").Prop("ticks", (long)ticks);
                Log($"star marker: added '{star.Name}{MarkerSuffix}' for {player.Name} at {star.X},{star.Y},{star.Z} LY");
            }
            if (f.Get("refresh") == "1") Refresh(j, player);
            return (200, j.EndObj().ToString());
        }

        static Json Refresh(Json j, DediPlayer player)
        {
            var (code, json, _) = ForwardToPlayfield("marker-refresh", player.SteamId);
            j.Prop("refreshCode", code).Key("refresh").Raw(json ?? "null");
            return j;
        }

        static readonly Queue<(int group, int id)> bookmarkPushes = new Queue<(int, int)>();

        /// <summary>
        /// Dedi main thread: run the game's own BookmarkUpdate package handler for a faction, as if a client had reported a
        /// change (decompiled 2026-11-18: group 0 → sent to every online member of that faction; group 1 with id 0 → sent to
        /// all clients). Clients then reload their markers. Found by type, not name (obfuscated): the MemoryOptions-style
        /// package whose EnumNetPackageId is BookmarkUpdate, built with its DomainTable constructor; its only declared
        /// parameterless void method is the handler.
        /// </summary>
        static void FlushBookmarkPushes()
        {
            (int group, int id) next;
            lock (bookmarkPushes)
            {
                if (bookmarkPushes.Count == 0) return;
                next = bookmarkPushes.Dequeue();
            }
            try { Log("bookmark push: " + PushBookmarkUpdate(next.group, next.id)); }
            catch (Exception e) { Log($"bookmark push failed: {e}"); }
        }

        static System.Reflection.ConstructorInfo bookmarkPkgCtor;
        static System.Reflection.MethodInfo bookmarkPkgHandler;
        static Type domainTableType;

        static string PushBookmarkUpdate(int group, int id)
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            if (bookmarkPkgCtor == null)
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Assembly-CSharp");
                var enumType = asm.GetType("EnumNetPackageId") ?? throw new Exception("EnumNetPackageId not found");
                var wanted = Enum.Parse(enumType, "BookmarkUpdate");
                Type[] types;
                try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types)
                {
                    if (t.IsAbstract || t.IsInterface) continue;
                    var idMethod = t.GetMethods(all).FirstOrDefault(m => m.ReturnType == enumType && m.GetParameters().Length == 0);
                    if (idMethod == null) continue;
                    var ctor = t.GetConstructors(all).FirstOrDefault(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.IsValueType);
                    var empty = t.GetConstructor(all, null, Type.EmptyTypes, null);
                    if (ctor == null || empty == null) continue;
                    object probe;
                    try { probe = empty.Invoke(null); } catch { continue; }
                    if (!Equals(idMethod.Invoke(probe, null), wanted)) continue;
                    var handlers = t.GetMethods(all).Where(m => m.ReturnType == typeof(void) && m.GetParameters().Length == 0).ToList();
                    if (handlers.Count != 1) throw new Exception($"{t.FullName}: {handlers.Count} handler candidates");
                    bookmarkPkgCtor = ctor;
                    bookmarkPkgHandler = handlers[0];
                    domainTableType = ctor.GetParameters()[0].ParameterType;
                    break;
                }
                if (bookmarkPkgCtor == null) throw new Exception("BookmarkUpdate package type not found");
            }
            var facCtor = domainTableType.GetConstructors().FirstOrDefault(c =>
            {
                var ps = c.GetParameters();
                return ps.Length == 2 && ps[0].ParameterType == typeof(FactionGroup) && ps[1].ParameterType == typeof(int);
            }) ?? throw new Exception("DomainTable(FactionGroup, int) not found");
            var faction = facCtor.Invoke(new object[] { (FactionGroup)group, id });
            var pkg = bookmarkPkgCtor.Invoke(new[] { faction });
            bookmarkPkgHandler.Invoke(pkg, null);
            return $"{bookmarkPkgCtor.DeclaringType.FullName}.{bookmarkPkgHandler.Name}() run for faction ({group}, {id})";
        }

        /// <summary>Playfield server: add a temporary map marker next to the player; it is removed again a few seconds later.</summary>
        static string ExecuteMarkerRefresh(Dictionary<string, string> req)
        {
            var pf = playfields.FirstOrDefault(x => x.Name == req.Get("playfield"));
            if (pf == null) return Error("playfield not loaded here");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("player not on this playfield");
            var pda = api.PDA;
            if (pda == null) return Error("IPda not available on this process");
            var pos = player.Position + new Vector3(0, 50, 0);
            pda.SetMapMarker(true, pos, RefreshMarkerName, 0, player.Id);
            lock (markerRemovals) markerRemovals.Add((player.Id, pos, api.Application.GameTicks + 60));
            Log($"marker refresh: added '{RefreshMarkerName}' for player {player.Id} on {pf.Name}, removal in 60 ticks");
            return new Json().Obj().Prop("ok", true).Prop("added", RefreshMarkerName).Prop("removeAfterTicks", 60).EndObj().ToString();
        }

        /// <summary>Playfield server update: remove the temporary markers that are due.</summary>
        static void ProcessMarkerRemovals()
        {
            List<(int playerId, Vector3 pos, ulong at)> due;
            lock (markerRemovals)
            {
                if (markerRemovals.Count == 0) return;
                var now = api.Application.GameTicks;
                due = markerRemovals.Where(m => m.at <= now).ToList();
                markerRemovals.RemoveAll(m => m.at <= now);
            }
            foreach (var (pid, pos, _) in due)
            {
                try
                {
                    api.PDA?.SetMapMarker(false, pos, RefreshMarkerName, 0, pid);
                    Log($"marker refresh: removed '{RefreshMarkerName}' for player {pid}");
                }
                catch (Exception e) { Log($"marker refresh: remove failed: {e.Message}"); }
            }
        }
    }
}
