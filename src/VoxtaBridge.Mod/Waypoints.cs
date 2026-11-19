using System;
using System.Collections.Generic;
using System.IO;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// The player's HUD waypoints/bookmarks on a playfield, read-only from the save's global.db (table Bookmarks;
    /// the game writes it live). isremove = 1 is a temporary marker (e.g. "[Hum] Test Hauler", type 2), still shown. Detector highlights are client-only and not in the DB.
    /// Kept in its own class so the SQLite assembly is only loaded when used (playfield server has Mono.Data.Sqlite).
    /// </summary>
    public static class Waypoints
    {
        public struct Mark
        {
            public long Id;
            public int Type;
            public string Name;
            public float X, Y, Z;
            public bool Hud, Waypoint, Shared, Temporary;
        }

        public static string FindDb(string saveGamePath)
        {
            if (string.IsNullOrEmpty(saveGamePath)) return null;
            var p = Path.Combine(saveGamePath, "global.db");
            return File.Exists(p) ? p : null;
        }

        public static List<Mark> For(string dbPath, int playerId, int factionId, string playfield)
        {
            var list = new List<Mark>();
            using (var c = new SqliteConnection($"Data Source={dbPath};Read Only=True;"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT b.bid, b.type, b.name, b.posx, b.posy, b.posz, b.isshowhud, b.iswaypoint, b.isshared, b.isremove " +
                        "FROM Bookmarks b JOIN Playfields p ON p.pfid = b.pfid " +
                        "WHERE p.name = @pf AND (b.isshowhud = 1 OR b.iswaypoint = 1) " +
                        "AND (b.entityid = @pid OR (b.isshared = 1 AND b.facid = @fac))";
                    cmd.Parameters.AddWithValue("@pf", playfield);
                    cmd.Parameters.AddWithValue("@pid", playerId);
                    cmd.Parameters.AddWithValue("@fac", factionId);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            list.Add(new Mark
                            {
                                Id = Convert.ToInt64(r[0]), Type = Convert.ToInt32(r[1]), Name = r[2] as string,
                                X = Convert.ToSingle(r[3]), Y = Convert.ToSingle(r[4]), Z = Convert.ToSingle(r[5]),
                                Hud = Convert.ToBoolean(r[6]), Waypoint = Convert.ToBoolean(r[7]), Shared = Convert.ToBoolean(r[8]), Temporary = Convert.ToBoolean(r[9]),
                            });
                }
            }
            return list;
        }
        public struct Body
        {
            public string Name;
            public int Kind; // Playfields.pftype: 1 planet, 4 moon
            public int Size;
            public float X, Y, Z;
        }

        /// <summary>Planets and moons in the same sector as <paramref name="playfield"/>, with their position in it.</summary>
        public static List<Body> Bodies(string dbPath, string playfield)
        {
            var list = new List<Body>();
            using (var c = new SqliteConnection($"Data Source={dbPath};Read Only=True;"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT b.name, b.pftype, b.planetsize, b.posx, b.posy, b.posz FROM Playfields b JOIN Playfields p " +
                        "ON p.sectorx = b.sectorx AND p.sectory = b.sectory AND p.sectorz = b.sectorz " +
                        "WHERE p.name = @pf AND b.pftype IN (1, 4)";
                    cmd.Parameters.AddWithValue("@pf", playfield);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            list.Add(new Body
                            {
                                Name = r[0] as string, Kind = Convert.ToInt32(r[1]), Size = Convert.ToInt32(r[2]),
                                X = Convert.ToSingle(r[3]), Y = Convert.ToSingle(r[4]), Z = Convert.ToSingle(r[5]),
                            });
                }
            }
            return list;
        }

        /// <summary>Blueprint name per structure entity id (Structures.bpname), e.g. "AsteroidVoxel02Iron".</summary>
        public static Dictionary<long, string> Blueprints(string dbPath, IEnumerable<long> ids)
        {
            var map = new Dictionary<long, string>();
            var idList = string.Join(",", ids);
            if (idList.Length == 0) return map;
            using (var c = new SqliteConnection($"Data Source={dbPath};Read Only=True;"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = $"SELECT entityid, bpname FROM Structures WHERE entityid IN ({idList})";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) map[Convert.ToInt64(r[0])] = r[1] as string;
                }
            }
            return map;
        }
        /// <summary>
        /// A dialogue variable (RE2 settings like Eden_Admin_MicroJump / Eden_MicroJumpBonus), best effort: the
        /// player's own row if there is one, else any row with that name. null if the game never stored it.
        /// </summary>
        public static int? DialogueInt(string dbPath, string name, int entityId)
        {
            using (var c = new SqliteConnection($"Data Source={dbPath};Read Only=True;"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT value FROM DialogueVars WHERE name = @n ORDER BY (entityid = @e) DESC LIMIT 1";
                    cmd.Parameters.AddWithValue("@n", name);
                    cmd.Parameters.AddWithValue("@e", entityId);
                    var v = cmd.ExecuteScalar();
                    return v == null || v is DBNull ? (int?)null : Convert.ToInt32(v);
                }
            }
        }
    }
}
