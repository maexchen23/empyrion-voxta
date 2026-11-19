using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoxtaBridge
{
    /// <summary>
    /// POST /system (dedi): bodies of a solar system the player knows, as the galaxy map shows them: the current system by
    /// default, or one named by system or body name (system=), or system=known for a list of all known systems. A system is
    /// known once the player or their faction discovered any playfield in it; unknown systems stay
    /// unexplored (only star class and territory on the galaxy map).
    /// </summary>
    public partial class BridgeMod
    {
        const int MaxKnownSystems = 25;

        static (int, string) HandleSystem(string body)
        {
            var f = Form.Parse(body);
            DediPlayer player;
            string why;
            lock (sync) player = PickPlayer(f.Get("player"), out why);
            if (player == null) return (409, Error(why));
            var save = SaveGameDir();
            if (save == null) return (500, Error("The save game folder was not found."));
            var db = Path.Combine(save, "global.db");
            int facGroup = -1, facId = -1;
            try { ReadPlayerTradeData(db, player.EntityId, player.Playfield, out facGroup, out facId, out _, out _, out _, new List<KnownPoi>()); } catch { }
            var bodies = ReadKnownBodies(db, player.EntityId, facGroup, facId);
            var current = SystemOf(db, player.Playfield);
            var here = bodies.FirstOrDefault(b => b.System == current);
            double Ly(KnownBody b) => here == null ? -1 : Math.Sqrt(Sq(b.X - here.X) + Sq(b.Y - here.Y) + Sq(b.Z - here.Z));

            var wanted = (f.Get("system") ?? "").Trim();
            var j = new Json().Obj().Prop("ok", true).Prop("playfield", player.Playfield).Prop("currentSystem", current);
            if (wanted.Equals("known", StringComparison.OrdinalIgnoreCase))
            {
                var systems = bodies.GroupBy(b => b.System).Select(g => g.First()).OrderBy(Ly).ToList();
                j.Prop("knownSystems", systems.Count).Key("systems").Arr();
                foreach (var s in systems.Take(MaxKnownSystems))
                {
                    var g = bodies.Where(b => b.System == s.System).ToList();
                    j.Obj().Prop("system", s.System).Prop("starClass", s.StarClass).Prop("ly", Math.Round(Ly(s), 1))
                        .Prop("planets", g.Count(b => b.PfType == 1)).Prop("moons", g.Count(b => b.PfType == 4))
                        .Prop("byPlayer", s.ByPlayer).EndObj();
                }
                return (200, j.EndArr().EndObj().ToString());
            }

            string system = current;
            if (wanted.Length > 0)
            {
                system = bodies.Select(b => b.System).Distinct().FirstOrDefault(s => s.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? bodies.FirstOrDefault(b => b.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))?.System
                    ?? bodies.Select(b => b.System).Distinct().FirstOrDefault(s => s.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? bodies.FirstOrDefault(b => b.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))?.System;
                if (system == null)
                    return (200, Error($"No system or planet called '{wanted}' is known to the player or their faction. Unexplored systems only show their star class and territory on the galaxy map."));
            }
            var list = bodies.Where(b => b.System == system).ToList();
            if (list.Count == 0) return (200, Error($"No bodies are known for the system {system}."));
            var first = list[0];
            j.Prop("system", system).Prop("starClass", first.StarClass).Prop("ly", Math.Round(Ly(first), 1)).Prop("byPlayer", first.ByPlayer);
            j.Key("bodies").Arr();
            foreach (var b in list.Where(b => b.PfType == 1 || b.PfType == 3 || b.PfType == 4 || b.PfType == 5)
                         .OrderBy(b => b.PfType == 3 ? 0 : 1).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
                j.Obj().Prop("name", b.Name).Prop("kind", b.Kind).Prop("type", PoiScan.PlanetTypeLabel(b.PlanetType))
                    .Prop("entered", b.Entered).Prop("here", b.Name == player.Playfield).EndObj();
            return (200, j.EndArr().EndObj().ToString());
        }
    }
}
