using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Eleon.Modding;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// Combat probe (CombatProbe=true in VoxtaBridge.cfg; read-only, for finding out what the game reports about fights):
    /// - both processes: every IModApi.GameEvent with its arguments (kills, attacks, deaths …);
    /// - playfield server: the NPC/creature/drone entities near each player (type, name, faction, distance) when they
    ///   change, and NPC entities unloading near a player (a kill?);
    /// - dedi: the players' kill counter (API1) and the save's PlayerStatistics row, diffed.
    /// Lines go to VoxtaBridge.probe.&lt;process&gt;.txt next to the DLL (one file per process).
    /// </summary>
    public partial class BridgeMod
    {
        static readonly HashSet<EntityType> NpcTypes = new HashSet<EntityType>
        {
            EntityType.Animal, EntityType.EnemyDrone, EntityType.Turret, EntityType.Cyborg, EntityType.NPCFighter,
            EntityType.TroopTransport, EntityType.Civilian, EntityType.Trader, EntityType.Unknown,
        };
        const float ProbeRange = 600;
        static readonly object probeLock = new object();
        static readonly Dictionary<int, string> probeNear = new Dictionary<int, string>();
        static readonly Dictionary<int, (string what, int player, float d)> probeNpcSeen = new Dictionary<int, (string, int, float)>();
        static readonly Dictionary<int, int> probeKills = new Dictionary<int, int>();
        static readonly Dictionary<int, string> probeStats = new Dictionary<int, string>();
        static DateTime nextProbeStats = DateTime.MinValue;
        static bool probeHooked;

        static bool ProbeOn => config?.CombatProbe == true;

        static void ProbeLine(string what)
        {
            try
            {
                var file = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                    $"VoxtaBridge.probe.{(mode == ApplicationMode.DedicatedServer ? "dedi" : "pf" + System.Diagnostics.Process.GetCurrentProcess().Id)}.txt");
                lock (probeLock) File.AppendAllText(file, $"{DateTime.UtcNow:HH:mm:ss.fff}Z | {what}\n", new UTF8Encoding(false));
            }
            catch (Exception e) { Log($"probe: {e.Message}"); }
        }

        /// <summary>Both processes, after the config is read: hook the game events.</summary>
        static void ProbeHook()
        {
            if (!ProbeOn || probeHooked) return;
            probeHooked = true;
            api.GameEvent += (type, a1, a2, a3, a4, a5) =>
            {
                try
                {
                    if (type == GameEventType.WaitAction || type == GameEventType.DayNightChange) return;
                    string A(object o) => o == null ? "-" : $"{o.GetType().Name}:{Short(o)}";
                    ProbeLine($"GameEvent {type} {A(a1)} {A(a2)} {A(a3)} {A(a4)} {A(a5)}");
                }
                catch { }
            };
            ProbeLine($"probe on ({mode})");
        }

        static string Short(object o)
        {
            string s;
            try
            {
                if (o is IEntity e) s = $"entity#{e.Id} {e.Type} '{e.Name}' fac {e.Faction.Group}/{e.Faction.Id}";
                else if (o is IPlayer p) s = $"player#{p.Id} {p.Name}";
                else s = o.ToString();
            }
            catch (Exception ex) { s = "(" + ex.GetType().Name + ")"; }
            return s.Length > 160 ? s.Substring(0, 160) : s;
        }

        /// <summary>Playfield server, every snapshot: NPCs near each player, logged when the picture changes.</summary>
        static void ProbeNpcs(IPlayfield pf)
        {
            if (!ProbeOn) return;
            var players = pf.Players?.Values.ToList();
            if (players == null || players.Count == 0) return;
            var npcs = pf.Entities.Values.Where(e => e != null && NpcTypes.Contains(e.Type)).ToList();
            var seenNow = new HashSet<int>();
            foreach (var p in players)
            {
                var near = npcs.Select(e => (e, d: UnityEngine.Vector3.Distance(e.Position, p.Position))).Where(x => x.d <= ProbeRange).OrderBy(x => x.d).ToList();
                foreach (var (e, d) in near)
                {
                    seenNow.Add(e.Id);
                    probeNpcSeen[e.Id] = ($"{e.Type} '{e.Name}' fac {e.Faction.Group}/{e.Faction.Id}", p.Id, d);
                }
                var summary = near.Count == 0 ? "none" :
                    string.Join(", ", near.GroupBy(x => $"{x.e.Type} '{x.e.Name}' fac {x.e.Faction.Group}/{x.e.Faction.Id}")
                        .Select(g => $"{g.Count()}x {g.Key} (nearest {Math.Round(g.Min(x => x.d))} m)")) +
                    $"; total on playfield: {npcs.Count}";
                if (!probeNear.TryGetValue(p.Id, out var before) || before != summary)
                {
                    probeNear[p.Id] = summary;
                    ProbeLine($"{p.Name} @ {pf.Name} ({p.Position.x:0},{p.Position.y:0},{p.Position.z:0}) within {ProbeRange} m: {summary}");
                }
            }
            // NPCs seen near a player that are gone from the playfield now (killed? despawned?)
            var all = new HashSet<int>(npcs.Select(e => e.Id));
            foreach (var id in probeNpcSeen.Keys.Where(id => !all.Contains(id)).ToList())
            {
                var (what, player, d) = probeNpcSeen[id];
                ProbeLine($"gone: entity#{id} {what}, last seen {Math.Round(d)} m from player {player}");
                probeNpcSeen.Remove(id);
            }
        }

        /// <summary>Dedi, on every API1 player info: the kill counter.</summary>
        static void ProbeKills(PlayerInfo pi)
        {
            if (!ProbeOn) return;
            if (probeKills.TryGetValue(pi.entityId, out var k) && k != pi.kills) ProbeLine($"{pi.playerName}: API1 kills {k} -> {pi.kills} (died {pi.died})");
            probeKills[pi.entityId] = pi.kills;
        }

        /// <summary>Dedi main thread: every 10 s the save's PlayerStatistics row of the online players, diffed.</summary>
        static void ProbeStatsTick()
        {
            if (!ProbeOn || DateTime.UtcNow < nextProbeStats) return;
            nextProbeStats = DateTime.UtcNow.AddSeconds(10);
            List<DediPlayer> online;
            lock (sync) online = dediPlayers.Values.Where(x => x.Online).ToList();
            if (online.Count == 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var save = SaveGameDir();
                    if (save == null) return;
                    using (var c = new SqliteConnection($"Data Source={Path.Combine(save, "global.db")};Read Only=True;"))
                    {
                        c.Open();
                        foreach (var p in online)
                            using (var q = c.CreateCommand())
                            {
                                q.CommandText = "SELECT killedenemies, killedallied, killedanimals, killeddrones, killedplayers, died FROM PlayerStatistics WHERE entityid = @p";
                                q.Parameters.AddWithValue("@p", p.EntityId);
                                using (var r = q.ExecuteReader())
                                    if (r.Read())
                                    {
                                        var s = $"enemies {r[0]}, allied {r[1]}, animals {r[2]}, drones {r[3]}, players {r[4]}, died {r[5]}";
                                        lock (probeLock)
                                        {
                                            if (!probeStats.TryGetValue(p.EntityId, out var b) || b != s)
                                            {
                                                probeStats[p.EntityId] = s;
                                                ProbeLine($"{p.Name}: PlayerStatistics {(b == null ? "start" : b + " ->")} {s}");
                                            }
                                        }
                                    }
                            }
                    }
                }
                catch (Exception e) { ProbeLine($"stats: {e.Message}"); }
            });
        }
    }
}
