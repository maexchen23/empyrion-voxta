using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>One body (planet, moon, sector) of a solar system the player's side knows.</summary>
    public sealed class KnownBody
    {
        public string System, StarClass, Name, PlanetType;
        public int PfType;
        /// <summary>Galaxy position in light years (sector units / 100,000).</summary>
        public double X, Y, Z;
        /// <summary>The playfield itself was entered by the player or their faction (DiscoveredPlayfields).</summary>
        public bool Entered;
        /// <summary>The system was discovered by the player personally (else only by the faction).</summary>
        public bool ByPlayer;
        public string Kind => PfType == 3 ? "star" : PfType == 4 ? "moon" : PfType == 5 ? "gas giant" : PfType == 2 ? "space" : "planet";
    }

    /// <summary>
    /// Systems the player knows: a system counts as discovered once the player or their faction has
    /// any discovered playfield in it (warping to the star is enough); then the galaxy map shows all its bodies with
    /// type and description. Bodies themselves count as entered once someone entered their playfield.
    /// </summary>
    public partial class BridgeMod
    {
        static List<KnownBody> ReadKnownBodies(string db, int playerId, int facGroup, int facId, string onlySystem = null)
        {
            var list = new List<KnownBody>();
            using (var c = new SqliteConnection($"Data Source={db};Read Only=True;"))
            {
                c.Open();
                using (var q = c.CreateCommand())
                {
                    const string mine = "(d.facgroup = 1 AND d.facid = @p)";
                    const string ours = "(" + mine + " OR (d.facgroup = @fg AND d.facid = @fi))";
                    q.CommandText =
                        "SELECT s.name, s.startype, p.name, p.pftype, p.planettype, s.sectorx, s.sectory, s.sectorz, " +
                        $"EXISTS (SELECT 1 FROM DiscoveredPlayfields d WHERE d.pfid = p.pfid AND {ours}), " +
                        $"EXISTS (SELECT 1 FROM DiscoveredPlayfields d JOIN Playfields p2 ON p2.pfid = d.pfid WHERE p2.ssid = s.ssid AND {mine}) " +
                        "FROM Playfields p JOIN SolarSystems s ON s.ssid = p.ssid WHERE p.isinstance = 0 AND p.ssid IN " +
                        $"(SELECT p3.ssid FROM DiscoveredPlayfields d JOIN Playfields p3 ON p3.pfid = d.pfid WHERE {ours})" +
                        (onlySystem != null ? " AND s.name = @sys" : "");
                    q.Parameters.AddWithValue("@p", playerId);
                    q.Parameters.AddWithValue("@fg", facGroup);
                    q.Parameters.AddWithValue("@fi", facId);
                    if (onlySystem != null) q.Parameters.AddWithValue("@sys", onlySystem);
                    using (var r = q.ExecuteReader())
                        while (r.Read())
                            list.Add(new KnownBody
                            {
                                System = r[0] as string, StarClass = r[1] as string, Name = r[2] as string, PfType = Convert.ToInt32(r[3]),
                                PlanetType = r[4] as string, X = Convert.ToDouble(r[5]) / 100000.0, Y = Convert.ToDouble(r[6]) / 100000.0,
                                Z = Convert.ToDouble(r[7]) / 100000.0, Entered = Convert.ToInt32(r[8]) > 0, ByPlayer = Convert.ToInt32(r[9]) > 0,
                            });
                }
            }
            return list;
        }

        /// <summary>The solar system of a playfield (by playfield name), or null.</summary>
        static string SystemOf(string db, string playfield)
        {
            using (var c = new SqliteConnection($"Data Source={db};Read Only=True;"))
            {
                c.Open();
                using (var q = c.CreateCommand())
                {
                    q.CommandText = "SELECT s.name FROM Playfields p JOIN SolarSystems s ON s.ssid = p.ssid WHERE p.name = @pf";
                    q.Parameters.AddWithValue("@pf", playfield ?? "");
                    return q.ExecuteScalar() as string;
                }
            }
        }
    }
}
