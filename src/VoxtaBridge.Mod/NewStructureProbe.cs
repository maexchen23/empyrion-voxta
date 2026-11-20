using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eleon.Modding;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// New-structure probe (part of the combat probe, CombatProbe=true; read-only, playfield server). Finds out how a ship
    /// spawned from a blueprint or a placed starter block (base/vessel) can be detected: every snapshot it diffs the
    /// playfield's player- and faction-owned structures against the previous snapshot and logs new ones with the game
    /// ticks of both snapshots, then looks up their row in global.db (created ticks, from blueprint library, player
    /// created, block count), retrying for up to a minute because the save may lag.
    /// </summary>
    public partial class BridgeMod
    {
        static readonly Dictionary<string, (HashSet<int> ids, ulong ticks, DateTime at)> probeOwnByPf =
            new Dictionary<string, (HashSet<int>, ulong, DateTime)>();
        static readonly Dictionary<int, (string pf, ulong prevTicks, ulong ticks, DateTime seen, int tries)> probeNewPending =
            new Dictionary<int, (string, ulong, ulong, DateTime, int)>();

        static bool PlayerOwned(IEntity e)
        {
            try { return e.Faction.Group == FactionGroup.Player || e.Faction.Group == FactionGroup.Faction; }
            catch { return false; }
        }

        static void ProbeNewStructures(IPlayfield pf)
        {
            if (!ProbeOn) return;
            ulong ticks = 0;
            try { ticks = api.Application.GameTicks; } catch { }
            var own = new HashSet<int>();
            var byId = new Dictionary<int, IEntity>();
            foreach (var e in pf.Entities.Values)
            {
                if (e == null) continue;
                if (e.Type != EntityType.BA && e.Type != EntityType.CV && e.Type != EntityType.SV && e.Type != EntityType.HV) continue;
                if (!PlayerOwned(e)) continue;
                own.Add(e.Id);
                byId[e.Id] = e;
            }
            if (probeOwnByPf.TryGetValue(pf.Name, out var prev))
            {
                foreach (var id in own.Where(i => !prev.ids.Contains(i)))
                {
                    var e = byId[id];
                    ProbeLine($"new own structure on {pf.Name}: #{id} '{e.Name}' {e.Type} fac {e.Faction.Group}/{e.Faction.Id}, " +
                              $"ticks prev snapshot {prev.ticks} ({(DateTime.UtcNow - prev.at).TotalSeconds:F1} s ago), now {ticks}");
                    probeNewPending[id] = (pf.Name, prev.ticks, ticks, DateTime.UtcNow, 0);
                }
                foreach (var id in prev.ids.Where(i => !own.Contains(i)))
                    ProbeLine($"own structure gone from {pf.Name}: #{id} (ticks now {ticks})");
            }
            else ProbeLine($"own structures baseline on {pf.Name}: {own.Count} (ticks {ticks})");
            probeOwnByPf[pf.Name] = (own, ticks, DateTime.UtcNow);
            ProbeNewStructureRows(ticks);
        }

        /// <summary>The global.db rows of the pending new structures; each tried once per snapshot for up to a minute.</summary>
        static void ProbeNewStructureRows(ulong ticksNow)
        {
            if (probeNewPending.Count == 0) return;
            var save = SaveGameDir();
            if (save == null) return;
            try
            {
                using (var c = new SqliteConnection($"Data Source={Path.Combine(save, "global.db")};Read Only=True;"))
                {
                    c.Open();
                    foreach (var id in probeNewPending.Keys.ToList())
                    {
                        var p = probeNewPending[id];
                        using (var q = c.CreateCommand())
                        {
                            q.CommandText = "SELECT e.createdticks, s.frombplibrary, s.playercreated, s.cntblocks, s.bpname, e.etype FROM Entities e " +
                                            "LEFT JOIN Structures s ON s.entityid = e.entityid WHERE e.entityid = @id";
                            q.Parameters.AddWithValue("@id", id);
                            using (var r = q.ExecuteReader())
                                if (r.Read())
                                {
                                    var created = Convert.ToUInt64(r[0]);
                                    var between = created > p.prevTicks && created <= p.ticks ? "between the snapshots" :
                                        created <= p.prevTicks ? "before the previous snapshot" : "after this snapshot";
                                    ProbeLine($"new structure #{id} row after {(DateTime.UtcNow - p.seen).TotalSeconds:F1} s: createdticks {created} " +
                                              $"({between}; snapshots {p.prevTicks}..{p.ticks}, now {ticksNow}), frombplibrary {r[1]}, playercreated {r[2]}, " +
                                              $"blocks {r[3]}, bpname '{r[4]}', etype {r[5]}");
                                    probeNewPending.Remove(id);
                                    continue;
                                }
                        }
                        if (DateTime.UtcNow - p.seen > TimeSpan.FromSeconds(60))
                        {
                            ProbeLine($"new structure #{id}: no global.db row after 60 s");
                            probeNewPending.Remove(id);
                        }
                    }
                }
            }
            catch (Exception e) { ProbeLine($"new structure rows: {e.Message}"); }
        }
    }
}
