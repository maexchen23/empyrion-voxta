using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// Fresh location for players on foot. The playfield server's IPlayer.Position of a walking player is updated only now
    /// and then (seen 2026-11-19: frozen 30–40 s while walking; seats change at once), but the dedi's API1 Player_Info
    /// position is fresh. So the playfield server sends the boxes of the structures near each player (VBL packets, every
    /// snapshot), and the dedi answers "inside a POI / at an own structure / distances" on each /status with the newest
    /// API1 position. Seats and "aboard inside an own ship" stay with the playfield server (CurrentStructure is instant).
    /// </summary>
    public partial class BridgeMod
    {
        const string BoxesPrefix = "VBL:";
        const float BoxRangeMeters = 2500;
        const int MaxBoxesPerPlayer = 150;

        // ----- playfield server ---------------------------------------------------------------

        /// <summary>Transform per structure, kept while the structure stays where it is.</summary>
        static readonly Dictionary<int, (UnityEngine.Vector3 pos, UnityEngine.Quaternion rot, LiveBox box)> boxCache =
            new Dictionary<int, (UnityEngine.Vector3, UnityEngine.Quaternion, LiveBox)>();

        static LiveBox Measure(IStructure s, IEntity e)
        {
            if (boxCache.TryGetValue(e.Id, out var c) && c.pos == e.Position && c.rot == e.Rotation) return c.box;
            var p = e.Position;
            const float d = 1000f;
            var o = s.GlobalToStructPos(p);
            var ex = s.GlobalToStructPos(p + new UnityEngine.Vector3(d, 0, 0));
            var ey = s.GlobalToStructPos(p + new UnityEngine.Vector3(0, d, 0));
            var ez = s.GlobalToStructPos(p + new UnityEngine.Vector3(0, 0, d));
            var box = new LiveBox
            {
                Id = e.Id, Name = e.Name, Type = e.Type.ToString(),
                Px = p.x, Py = p.y, Pz = p.z, Ox = o.x, Oy = o.y, Oz = o.z,
                // columns: how one world metre along x/y/z moves the block position
                M = new double[]
                {
                    (ex.x - o.x) / d, (ey.x - o.x) / d, (ez.x - o.x) / d,
                    (ex.y - o.y) / d, (ey.y - o.y) / d, (ez.y - o.y) / d,
                    (ex.z - o.z) / d, (ey.z - o.z) / d, (ez.z - o.z) / d,
                },
                MinX = s.MinPos.x, MinY = s.MinPos.y + 128, MinZ = s.MinPos.z,
                MaxX = s.MaxPos.x, MaxY = s.MaxPos.y + 128, MaxZ = s.MaxPos.z,
            };
            boxCache[e.Id] = (e.Position, e.Rotation, box);
            return box;
        }

        /// <summary>"Polaris", "another player", … for a structure that isn't the player's, or null.</summary>
        static string OwnerLabel(IEntity fe)
        {
            try
            {
                var g = fe.Faction.Group;
                var owner = g == FactionGroup.Player ? "another player" : g == FactionGroup.Faction ? "a player faction" : g.ToString();
                return owner.Length > 0 && char.IsDigit(owner[0]) ? null : owner;
            }
            catch { return null; }
        }

        /// <summary>Playfield server, with every snapshot: the structure boxes around each player (own ones flagged).</summary>
        static void SendBoxes(IPlayfield pf)
        {
            var players = pf.Players?.Values.ToList();
            if (players == null || players.Count == 0) return;
            var requireOwnership = config?.RequireOwnership ?? true;
            var sb = new StringBuilder(BoxesPrefix).Append(pf.Name).Append('\n');
            var structures = new List<(IEntity e, IStructure s)>();
            foreach (var e in pf.Entities.Values)
            {
                if (e == null || (e.Type != EntityType.BA && e.Type != EntityType.CV && e.Type != EntityType.SV && e.Type != EntityType.HV)) continue;
                IStructure s = null;
                try { s = e.Structure; } catch { }
                if (s != null) structures.Add((e, s));
            }
            foreach (var p in players)
            {
                // the player's own position here may be stale by a minute: the range is generous
                var near = structures.Select(x => (x.e, x.s, d: UnityEngine.Vector3.Distance(x.e.Position, p.Position)))
                    .Where(x => x.d <= BoxRangeMeters).OrderBy(x => x.d).Take(MaxBoxesPerPlayer);
                foreach (var (e, s, _) in near)
                {
                    LiveBox b;
                    try { b = Measure(s, e); } catch { continue; }
                    var own = !requireOwnership || Owns(p, s);
                    var line = new LiveBox
                    {
                        PlayerId = p.Id, Own = own, Id = b.Id, Name = b.Name, Type = b.Type, Owner = own ? null : OwnerLabel(e),
                        Px = b.Px, Py = b.Py, Pz = b.Pz, Ox = b.Ox, Oy = b.Oy, Oz = b.Oz, M = b.M,
                        MinX = b.MinX, MinY = b.MinY, MinZ = b.MinZ, MaxX = b.MaxX, MaxY = b.MaxY, MaxZ = b.MaxZ,
                    };
                    sb.Append(line.Encode()).Append('\n');
                }
            }
            if (!api.Network.SendToDedicatedServer(ModName, Encoding.UTF8.GetBytes(sb.ToString()), pf.Name))
                Log($"SendToDedicatedServer (boxes) returned false for {pf.Name}");
        }

        // ----- dedi ---------------------------------------------------------------------------

        /// <summary>Player entity id → boxes around them (playfield, received at).</summary>
        static readonly Dictionary<int, (string pf, DateTime at, List<LiveBox> boxes)> liveBoxes = new Dictionary<int, (string, DateTime, List<LiveBox>)>();
        /// <summary>Player entity id → API1 position (playfield, received at).</summary>
        static readonly Dictionary<int, (string pf, DateTime at, double x, double y, double z)> legacyPos = new Dictionary<int, (string, DateTime, double, double, double)>();

        static void ReceiveBoxes(string body)
        {
            var lines = body.Split('\n');
            if (lines.Length == 0) return;
            var pf = lines[0];
            var byPlayer = lines.Skip(1).Where(l => l.Length > 0).Select(LiveBox.Parse).Where(b => b != null).GroupBy(b => b.PlayerId).ToList();
            lock (sync)
            {
                // players of this playfield without boxes now: forget theirs from it
                foreach (var id in liveBoxes.Where(kv => kv.Value.pf == pf).Select(kv => kv.Key).ToList())
                    if (byPlayer.All(g => g.Key != id)) liveBoxes.Remove(id);
                foreach (var g in byPlayer) liveBoxes[g.Key] = (pf, DateTime.UtcNow, g.ToList());
            }
        }

        /// <summary>/status: per online player with a fresh API1 position and boxes of the same playfield: live.&lt;steamId&gt;.</summary>
        static void WriteLive(Json j, IEnumerable<DediPlayer> online)
        {
            j.Key("live").Obj();
            var now = DateTime.UtcNow;
            foreach (var p in online)
            {
                if (p.SteamId == null) continue;
                (string pf, DateTime at, double x, double y, double z) pos;
                (string pf, DateTime at, List<LiveBox> boxes) bx;
                lock (sync)
                {
                    if (!legacyPos.TryGetValue(p.EntityId, out pos) || !liveBoxes.TryGetValue(p.EntityId, out bx)) continue;
                }
                if (pos.pf != bx.pf || pos.pf != p.Playfield || now - pos.at > TimeSpan.FromSeconds(15) || now - bx.at > TimeSpan.FromSeconds(15)) continue;
                var (visiting, standing, own) = LiveBox.Locate(bx.boxes, pos.x, pos.y, pos.z, AboardMarginBlocks);
                j.Key(p.SteamId).Obj()
                    .Vec("pos", (float)pos.x, (float)pos.y, (float)pos.z)
                    .Prop("posAgeS", Math.Round((now - pos.at).TotalSeconds, 1)).Prop("boxesAgeS", Math.Round((now - bx.at).TotalSeconds, 1));
                if (visiting != null) j.Key("visiting").Obj().Prop("name", visiting.Name).Prop("type", visiting.Type).Prop("owner", visiting.Owner).EndObj();
                if (standing != null) j.Key("standing").Obj().Prop("name", standing.Name).Prop("type", standing.Type).EndObj();
                j.Key("own").Arr();
                foreach (var (b, d) in own.Take(8)) j.Obj().Prop("name", b.Name).Prop("type", b.Type).Prop("distance", Math.Round(d)).EndObj();
                j.EndArr().EndObj();
            }
            j.EndObj();
        }
    }
}
