using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// POST /structure (dedi → playfield server): details of one of the player's (or their faction's) own structures on
    /// the current playfield by name, like the current-ship data: type, size, power, shield, mass, pilot, tanks, distance,
    /// docked vessels. Never someone else's structure or a POI.
    /// </summary>
    public partial class BridgeMod
    {
        static (int, string) HandleStructureInfo(string body)
        {
            var f = Form.Parse(body);
            var name = (f.Get("name") ?? "").Trim();
            if (name.Length == 0) return (400, Error("'name' is required (one of the player's ships or bases)."));
            var (code, json, _) = ForwardToPlayfield("structure-info", f.Get("player"), ("name", name));
            return (code, json);
        }

        /// <summary>Playfield server: find an own structure by name (exact, prefix, then contains; closest first).</summary>
        static string ExecuteStructureInfo(Dictionary<string, string> req)
        {
            var pf = playfields.FirstOrDefault(x => x.Name == req.Get("playfield"));
            if (pf == null) return Error("The playfield is not loaded on this server process.");
            int.TryParse(req.Get("player"), out var playerId);
            var player = pf.Players?.Values.FirstOrDefault(x => x.Id == playerId);
            if (player == null) return Error("The player is not on this playfield right now.");
            var wanted = (req.Get("name") ?? "").Trim();

            var all = pf.Entities.Values.Where(e => e != null).ToList();
            var own = new List<(IEntity e, IStructure s, float d)>();
            foreach (var e in all)
            {
                if (e.Type != EntityType.BA && e.Type != EntityType.CV && e.Type != EntityType.SV && e.Type != EntityType.HV) continue;
                IStructure s = null;
                try { s = e.Structure; } catch { }
                if (s == null || !Owns(player, s)) continue;
                own.Add((e, s, UnityEngine.Vector3.Distance(e.Position, player.Position)));
            }
            var hit = own.Where(x => string.Equals(x.e.Name, wanted, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.d).FirstOrDefault();
            if (hit.e == null) hit = own.Where(x => (x.e.Name ?? "").StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.d).FirstOrDefault();
            if (hit.e == null) hit = own.Where(x => (x.e.Name ?? "").IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(x => x.d).FirstOrDefault();
            if (hit.e == null)
            {
                var names = own.OrderBy(x => x.d).Take(8).Select(x => $"'{x.e.Name}' ({x.e.Type})");
                return Error(own.Count == 0
                    ? $"The player has no ships or bases on {pf.Name}."
                    : $"No own structure called '{wanted}' on {pf.Name}. Own structures here: {string.Join(", ", names)}.");
            }

            var j = new Json().Obj().Prop("ok", true).Key("structure").Obj();
            StructureCore(j, hit.s);
            j.Prop("distance", Math.Round(hit.d));
            int containers = 0;
            foreach (var t in ContainerTypes)
                try { containers += hit.s.GetDevices(t)?.Count ?? 0; } catch { }
            j.Prop("containerCount", containers);
            if (hit.e.DockedTo > 0)
            {
                var carrier = all.FirstOrDefault(x => x.Id == hit.e.DockedTo);
                j.Prop("dockedTo", carrier?.Name ?? hit.e.DockedTo.ToString());
            }
            j.Key("docked").Arr();
            foreach (var d in all.Where(x => x.DockedTo == hit.e.Id)) j.Obj().Prop("name", d.Name).Prop("type", d.Type.ToString()).EndObj();
            j.EndArr();
            return j.EndObj().EndObj().ToString();
        }
    }
}
