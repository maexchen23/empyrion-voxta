using System;
using System.Collections.Generic;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// Finds one block of a kind on a structure and remembers where: the cached position is re-checked with a single
    /// GetBlock and only searched again when the block is gone; "none found" is kept until the structure's block count
    /// changes. Used for the micro jump drive and the landing gear (all gears of a ship move together, so one is enough).
    /// </summary>
    public sealed class BlockFinder
    {
        readonly Dictionary<int, (VectorInt3? pos, int blockCount)> cache = new Dictionary<int, (VectorInt3?, int)>();

        /// <param name="isWanted">Block type id → is it the kind looked for.</param>
        /// <param name="search">The expensive search (device lists, named devices, block scan); null = none.</param>
        /// <param name="how">"cached position", "cached: none (…)" or "searched".</param>
        public VectorInt3? Find(IStructure s, Func<int, bool> isWanted, Func<VectorInt3?> search, out string how)
        {
            how = null;
            if (s == null) return null;
            int sid = s.Entity?.Id ?? s.Id;
            int blocks = 0;
            try { blocks = s.BlockCount; } catch { }
            lock (cache)
                if (cache.TryGetValue(sid, out var cached))
                {
                    if (cached.pos.HasValue && isWanted(TypeAt(s, cached.pos.Value)))
                    { how = "cached position"; return cached.pos; }
                    if (!cached.pos.HasValue && cached.blockCount == blocks)
                    { how = $"cached: none (block count {blocks} unchanged)"; return null; }
                }
            var found = search();
            lock (cache) cache[sid] = (found, blocks);
            how = "searched";
            return found;
        }

        public static int TypeAt(IStructure s, VectorInt3 p)
        {
            try
            {
                var b = s.GetBlock(p.x, p.y, p.z);
                if (b == null) return 0;
                b.Get(out var t, out _, out _, out _);
                return t;
            }
            catch { return 0; }
        }
    }
}
