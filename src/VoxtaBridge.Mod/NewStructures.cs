using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eleon.Modding;
using Mono.Data.Sqlite;

namespace VoxtaBridge
{
    /// <summary>
    /// Ships and bases that just came into being: spawned from a blueprint, or a starter block placed (playfield server).
    /// The game sends no server event for either, so every snapshot diffs the playfield's player- and faction-owned
    /// structures against the previous one; a new one only counts if global.db says it was created between the two
    /// snapshots (structures that merely loaded with the playfield or flew in are older). Tested on dev 2026-11-20: blueprint
    /// spawns have frombplibrary=1; starters have frombplibrary=0 and bpname BA_Player/SV_Player/CV_Player/HV_Player; the
    /// row is in the save at once or one snapshot later. Each find is reported in the owner's snapshot for
    /// <see cref="NewStructureReportFor"/> (the module counts every id once).
    /// </summary>
    public partial class BridgeMod
    {
        static readonly TimeSpan NewStructureReportFor = TimeSpan.FromSeconds(60);
        static readonly TimeSpan NewStructureLookupFor = TimeSpan.FromSeconds(30);

        public sealed class NewStructure
        {
            public int Id;
            public string Name, Type, Kind, Blueprint;
            public FactionGroup Group;
            public int FactionId;
            public DateTime Found;
        }

        static readonly Dictionary<string, (HashSet<int> ids, ulong ticks)> ownByPf = new Dictionary<string, (HashSet<int>, ulong)>();
        static readonly Dictionary<int, (ulong prevTicks, ulong ticks, DateTime seen, IEntity entity)> newPending =
            new Dictionary<int, (ulong, ulong, DateTime, IEntity)>();
        static readonly List<NewStructure> newFound = new List<NewStructure>();

        static bool IsShipOrBase(IEntity e) =>
            e.Type == EntityType.BA || e.Type == EntityType.CV || e.Type == EntityType.SV || e.Type == EntityType.HV;

        /// <summary>Playfield server, every snapshot (main thread).</summary>
        static void TrackNewStructures(IPlayfield pf)
        {
            ulong ticks = 0;
            try { ticks = api.Application.GameTicks; } catch { }
            var own = new HashSet<int>();
            foreach (var e in pf.Entities.Values)
                if (e != null && IsShipOrBase(e) && PlayerOwned(e))
                {
                    own.Add(e.Id);
                    if (ownByPf.TryGetValue(pf.Name, out var prev) && !prev.ids.Contains(e.Id) && !newPending.ContainsKey(e.Id))
                        newPending[e.Id] = (prev.ticks, ticks, DateTime.UtcNow, e);
                }
            ownByPf[pf.Name] = (own, ticks);
            ResolveNewStructures();
            newFound.RemoveAll(n => DateTime.UtcNow - n.Found > NewStructureReportFor);
        }

        /// <summary>The save's row of each pending new structure: created between its two snapshots → found.</summary>
        static void ResolveNewStructures()
        {
            if (newPending.Count == 0) return;
            var save = SaveGameDir();
            if (save == null) return;
            try
            {
                using (var c = new SqliteConnection($"Data Source={Path.Combine(save, "global.db")};Read Only=True;"))
                {
                    c.Open();
                    foreach (var id in newPending.Keys.ToList())
                    {
                        var p = newPending[id];
                        bool resolved = false;
                        using (var q = c.CreateCommand())
                        {
                            q.CommandText = "SELECT e.createdticks, s.frombplibrary, s.bpname FROM Entities e " +
                                            "LEFT JOIN Structures s ON s.entityid = e.entityid WHERE e.entityid = @id";
                            q.Parameters.AddWithValue("@id", id);
                            using (var r = q.ExecuteReader())
                                if (r.Read())
                                {
                                    resolved = true;
                                    var created = Convert.ToUInt64(r[0]);
                                    if (created > p.prevTicks && created <= p.ticks)
                                    {
                                        var fromBlueprint = r[1] != DBNull.Value && Convert.ToBoolean(r[1]);
                                        var e = p.entity;
                                        var n = new NewStructure
                                        {
                                            Id = id, Name = e.Name, Type = e.Type.ToString(), Kind = fromBlueprint ? "blueprint" : "starter",
                                            Blueprint = r[2] as string, Group = e.Faction.Group, FactionId = e.Faction.Id, Found = DateTime.UtcNow,
                                        };
                                        newFound.Add(n);
                                        Log($"new structure: #{id} '{n.Name}' {n.Type} ({n.Kind}, bp '{n.Blueprint}')");
                                    }
                                }
                        }
                        if (resolved || DateTime.UtcNow - p.seen > NewStructureLookupFor) newPending.Remove(id);
                    }
                }
            }
            catch (Exception ex) { Log($"new structures: {ex.Message}"); }
        }

        /// <summary>The new structures this player owns (own or their faction's), for their snapshot.</summary>
        static void WriteNewStructures(Json j, IPlayer p)
        {
            var mine = newFound.Where(n =>
                (n.Group == FactionGroup.Player && n.FactionId == p.Id) ||
                (n.Group == FactionGroup.Faction && p.FactionData.Group == FactionGroup.Faction && p.FactionData.Id == n.FactionId)).ToList();
            if (mine.Count == 0) return;
            j.Key("newStructures").Arr();
            foreach (var n in mine)
                j.Obj().Prop("id", n.Id).Prop("name", n.Name).Prop("type", n.Type).Prop("kind", n.Kind).Prop("blueprint", n.Blueprint).EndObj();
            j.EndArr();
        }
    }
}
