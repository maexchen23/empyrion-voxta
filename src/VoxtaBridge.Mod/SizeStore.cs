using System;
using System.Collections.Generic;
using System.Globalization;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// SQLite persistence for <see cref="SizeMemory"/>: <c>VoxtaBridge.db</c> next to the bridge config, table
    /// <c>StructureSizes</c> keyed by the persistent entity id. Several playfield server processes use it at once;
    /// SQLite's locking (WAL + busy timeout) handles that. Playfield servers only (Mono.Data.Sqlite is in their Managed folder).
    /// </summary>
    public static class SizeStore
    {
        static bool initialised;

        static SqliteConnection Open(string path)
        {
            var c = new SqliteConnection($"Data Source={path};Version=3;");
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout = 3000;";
                cmd.ExecuteNonQuery();
                if (!initialised)
                {
                    cmd.CommandText =
                        "PRAGMA journal_mode = WAL;" +
                        "CREATE TABLE IF NOT EXISTS StructureSizes (" +
                        " entityid INTEGER PRIMARY KEY, name TEXT, dx REAL NOT NULL, dy REAL NOT NULL, dz REAL NOT NULL," +
                        " seenutc TEXT NOT NULL, playfield TEXT);";
                    cmd.ExecuteNonQuery();
                    initialised = true;
                }
            }
            return c;
        }

        public static SizeMemory.Entry Read(string path, int id)
        {
            using (var c = Open(path))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT name, dx, dy, dz, seenutc, playfield FROM StructureSizes WHERE entityid = @id";
                cmd.Parameters.AddWithValue("@id", id);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return new SizeMemory.Entry
                    {
                        Id = id, Name = r[0] as string,
                        Dx = Convert.ToSingle(r[1]), Dy = Convert.ToSingle(r[2]), Dz = Convert.ToSingle(r[3]),
                        SeenUtc = DateTime.Parse((string)r[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        Playfield = r[5] as string,
                    };
                }
            }
        }

        public static void Write(string path, List<SizeMemory.Entry> entries)
        {
            if (entries.Count == 0) return;
            using (var c = Open(path))
            using (var tx = c.BeginTransaction())
            {
                foreach (var e in entries)
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText =
                            // INSERT OR REPLACE, not ON CONFLICT … DO UPDATE: the game's bundled sqlite3.dll may predate 3.24
                            "INSERT OR REPLACE INTO StructureSizes (entityid, name, dx, dy, dz, seenutc, playfield) VALUES (@id, @n, @x, @y, @z, @s, @p)";
                        cmd.Parameters.AddWithValue("@id", e.Id);
                        cmd.Parameters.AddWithValue("@n", e.Name ?? "");
                        cmd.Parameters.AddWithValue("@x", (double)e.Dx);
                        cmd.Parameters.AddWithValue("@y", (double)e.Dy);
                        cmd.Parameters.AddWithValue("@z", (double)e.Dz);
                        cmd.Parameters.AddWithValue("@s", e.SeenUtc.ToString("o", CultureInfo.InvariantCulture));
                        cmd.Parameters.AddWithValue("@p", e.Playfield ?? "");
                        cmd.ExecuteNonQuery();
                    }
                tx.Commit();
            }
        }
    }
}
