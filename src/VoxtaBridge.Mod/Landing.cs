using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// Docked and landed state of a structure for the status data (the Voxta module's watch events). "Landed" = any
    /// landing gear block extended: the gear only extends when the ship rests on something (terrain, a base, another
    /// ship); tested on dev 2026-11-20. The gear is found in the game's device lists (game internals, verified builds only)
    /// and remembered per structure (<see cref="BlockFinder"/>), so the 2-second snapshot reads one block.
    /// </summary>
    public partial class BridgeMod
    {
        static readonly BlockFinder gearFinder = new BlockFinder();

        /// <summary>Name of the structure this one is docked to, or null.</summary>
        static string DockedToName(IStructure s)
        {
            try
            {
                var id = s.Entity?.DockedTo ?? 0;
                if (id <= 0 || id == s.Id) return null;
                foreach (var pf in playfields)
                    if (pf.Entities != null && pf.Entities.TryGetValue(id, out var e) && e != null) return e.Name;
                return id.ToString();
            }
            catch { return null; }
        }

        static bool IsLandingGear(int type) => (ItemName(type) ?? "").IndexOf("landinggear", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>True if the landing gear is extended, false if retracted, null if unknown (no landing gear, or the
        /// game internals are unavailable on this game version). All gears of a ship move together: one is read.</summary>
        static bool? LandingGearDown(IStructure s)
        {
            try
            {
                var pos = gearFinder.Find(s, IsLandingGear, () =>
                {
                    var lists = GameInternals.DeviceLists(s, Unverified, out _);
                    if (lists == null) return null;
                    foreach (var l in lists)
                        for (var i = 0; i < l.Count; i++)
                            if (IsLandingGear(BlockFinder.TypeAt(s, l.GetAt(i)))) return l.GetAt(i);
                    return null;
                }, out _);
                if (!pos.HasValue) return null;
                var b = s.GetBlock(pos.Value);
                return b != null && BlockActive(b);
            }
            catch { return null; }
        }
    }
}
