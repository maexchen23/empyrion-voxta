using System;
using System.Collections.Generic;
using System.Linq;

namespace VoxtaBridge
{
    /// <summary>
    /// Structure sizes (bounding box in metres) by persistent entity id: the same in every playfield/sector. Far
    /// structures become Proxy entities without a structure after a few minutes (seen 2026-11-18: a carrier known at 28 km,
    /// unknown 10 min later), so a jump to them uses the last size seen while loaded. This class is the in-memory part
    /// (game-free, unit-tested): what is known, and what changed since the last save. <see cref="SizeStore"/> persists it.
    /// </summary>
    public sealed class SizeMemory
    {
        public sealed class Entry
        {
            public int Id;
            public string Name;
            public float Dx, Dy, Dz;
            public DateTime SeenUtc;
            public string Playfield;
            public float Radius => 0.5f * (float)Math.Sqrt(Dx * Dx + Dy * Dy + Dz * Dz);
        }

        /// <summary>Re-save an unchanged size at most this often, so "last seen" stays roughly current without a write every scan.</summary>
        public static readonly TimeSpan SeenRefresh = TimeSpan.FromMinutes(10);

        readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        readonly Dictionary<int, DateTime> savedSeen = new Dictionary<int, DateTime>();
        readonly HashSet<int> changed = new HashSet<int>();

        public int Count => entries.Count;
        public bool Dirty => changed.Count > 0;

        public Entry Get(int id) => entries.TryGetValue(id, out var e) ? e : null;

        /// <summary>A size seen just now in a loaded structure. Marks it for saving if new, changed, or last saved long ago.</summary>
        public void Record(int id, string name, string playfield, float dx, float dy, float dz, DateTime nowUtc)
        {
            var same = entries.TryGetValue(id, out var e) && e.Dx == dx && e.Dy == dy && e.Dz == dz && e.Name == name && e.Playfield == playfield;
            entries[id] = new Entry { Id = id, Name = name, Playfield = playfield, Dx = dx, Dy = dy, Dz = dz, SeenUtc = nowUtc };
            if (!same || !savedSeen.TryGetValue(id, out var saved) || nowUtc - saved >= SeenRefresh) changed.Add(id);
        }

        /// <summary>An entry read from the store (no save needed).</summary>
        public void Loaded(Entry e)
        {
            entries[e.Id] = e;
            savedSeen[e.Id] = e.SeenUtc;
        }

        /// <summary>Entries to write; call <see cref="Saved"/> after a successful write.</summary>
        public List<Entry> Pending() => changed.Select(id => entries[id]).ToList();

        public void Saved(IEnumerable<Entry> written)
        {
            foreach (var e in written)
            {
                savedSeen[e.Id] = e.SeenUtc;
                changed.Remove(e.Id);
            }
        }
    }
}
