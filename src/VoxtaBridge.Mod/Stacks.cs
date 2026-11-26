using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoxtaBridge
{
    /// <summary>
    /// An item stack with its state (mirror of the game's ItemStack, so this file stays game-free).
    /// <c>Ammo</c>: rounds loaded in a weapon, or the token id of a token. <c>Decay</c>: wear (weapons/tools) or
    /// spoilage (food). Stacks with state must be moved whole, never via the count-only game calls, or the state is
    /// lost (an emptied magazine, a "repaired" weapon, a token without identity).
    /// </summary>
    public struct InvStack
    {
        public int Id, Count, Slot, Ammo, Decay;
        public InvStack(int id, int count, int slot = 0, int ammo = 0, int decay = 0) { Id = id; Count = count; Slot = slot; Ammo = ammo; Decay = decay; }
        public bool HasState => Ammo != 0 || Decay != 0;
        public bool SameKind(InvStack o) => Id == o.Id && Ammo == o.Ammo && Decay == o.Decay;
        public override string ToString() => $"{Id}x{Count}@{Slot}(a{Ammo},d{Decay})";
    }

    /// <summary>Game-independent stack handling (unit-tested).</summary>
    public static class Stacks
    {
        public const int BagSlots = 40;      // 5 x 8 (RE2)
        public const int ToolbarSlots = 9;   // keys 1-9; key 0 is the empty hand
        public const int DefaultStackSize = 999;

        /// <summary>
        /// Takes up to <paramref name="amount"/> of an item out of <paramref name="list"/> (smaller stacks first,
        /// so full stacks stay full), splitting a stack when needed. Returns the taken stacks with their state;
        /// <paramref name="list"/> is modified in place (emptied stacks removed).
        /// </summary>
        public static List<InvStack> Take(List<InvStack> list, int itemId, int amount, int? token = null)
        {
            var taken = new List<InvStack>();
            var got = 0;
            foreach (var i in Enumerable.Range(0, list.Count).Where(i => list[i].Id == itemId && (token == null || list[i].Ammo == token))
                         .OrderBy(i => list[i].Count).ToList())
            {
                if (got >= amount) break;
                var st = list[i];
                var n = Math.Min(st.Count, amount - got);
                taken.Add(new InvStack(st.Id, n, st.Slot, st.Ammo, st.Decay));
                st.Count -= n;
                list[i] = st;
                got += n;
            }
            list.RemoveAll(st => st.Count <= 0);
            return taken;
        }

        /// <summary>
        /// Places stacks into a slot list: first tops up existing stacks of the same kind (only stacks without state,
        /// up to the stack size), then new stacks in the lowest free slots below <paramref name="slotCount"/>.
        /// Returns what did not fit. <paramref name="slots"/> is modified in place.
        /// </summary>
        public static List<InvStack> Place(List<InvStack> slots, IEnumerable<InvStack> incoming, int slotCount,
            Func<int, int> stackSize)
        {
            var leftover = new List<InvStack>();
            foreach (var original in incoming)
            {
                var item = original;
                var max = Math.Max(1, stackSize(item.Id));

                // 1. merge (never for stacks with state: two loaded pistols stay two stacks)
                if (!item.HasState)
                {
                    for (int i = 0; i < slots.Count && item.Count > 0; i++)
                    {
                        var s = slots[i];
                        if (!s.SameKind(item) || s.Count >= max) continue;
                        var n = Math.Min(max - s.Count, item.Count);
                        s.Count += n;
                        slots[i] = s;
                        item.Count -= n;
                    }
                }

                // 2. new stacks in free slots
                while (item.Count > 0)
                {
                    var used = new HashSet<int>(slots.Select(s => s.Slot));
                    var slot = Enumerable.Range(0, slotCount).Where(x => !used.Contains(x)).DefaultIfEmpty(-1).First();
                    if (slot < 0) break;
                    var n = Math.Min(max, item.Count);
                    slots.Add(new InvStack(item.Id, n, slot, item.Ammo, item.Decay));
                    item.Count -= n;
                }
                if (item.Count > 0) leftover.Add(item);
            }
            return leftover;
        }

        /// <summary>Litres of a set of stacks (items with unknown volume count as 0).</summary>
        public static double Volume(IEnumerable<InvStack> stacks, Func<int, double> volumeOf) =>
            stacks.Sum(s => s.Count * volumeOf(s.Id));

        /// <summary>
        /// Splits <paramref name="incoming"/> into what fits under <paramref name="limit"/> litres given the current
        /// volume, and the rest. Items with unknown volume (0) always fit. Server-wide limit: the real capacity
        /// depends on armor and boosts, which the mod can't see.
        /// </summary>
        public static (List<InvStack> fits, List<InvStack> rest) CapByVolume(IEnumerable<InvStack> incoming, double currentLiters,
            double limit, Func<int, double> volumeOf)
        {
            var fits = new List<InvStack>();
            var rest = new List<InvStack>();
            var free = limit - currentLiters;
            foreach (var st in incoming)
            {
                var per = volumeOf(st.Id);
                if (per <= 0) { fits.Add(st); continue; }
                var n = free <= 0 ? 0 : Math.Min(st.Count, (int)Math.Floor(free / per + 1e-9));
                if (n > 0) { fits.Add(new InvStack(st.Id, n, st.Slot, st.Ammo, st.Decay)); free -= n * per; }
                if (n < st.Count) rest.Add(new InvStack(st.Id, st.Count - n, st.Slot, st.Ammo, st.Decay));
            }
            return (fits, rest);
        }

        public enum WriteOutcome { Done, NotApplied, Partial }

        /// <summary>
        /// Did an inventory write take effect? Judged only by the item count re-read from the game (never by the write
        /// call's own reply, which was stale on 2026-11-17 and duplicated 189 logs). Works for adding (expected &gt; before)
        /// and taking (expected &lt; before). Returns the outcome and how many verifiably moved (never negative).
        /// </summary>
        public static (WriteOutcome outcome, int applied) Classify(int before, int expected, int observed)
        {
            var want = Math.Abs(expected - before);
            if (want == 0) return (WriteOutcome.Done, 0);
            var adding = expected > before;
            var moved = adding ? observed - before : before - observed;
            if (moved >= want) return (WriteOutcome.Done, want);
            if (moved <= 0) return (WriteOutcome.NotApplied, 0);
            return (WriteOutcome.Partial, moved);
        }

        public static int Total(IEnumerable<InvStack> stacks, int itemId = 0) =>
            stacks.Where(s => itemId == 0 || s.Id == itemId).Sum(s => s.Count);

        /// <summary>"id:count:ammo:decay;…" for packets between dedi and playfield servers.</summary>
        public static string Encode(IEnumerable<InvStack> stacks) =>
            string.Join(";", stacks.Select(s => string.Join(":", s.Id, s.Count, s.Ammo, s.Decay)));

        public static List<InvStack> Decode(string text)
        {
            var list = new List<InvStack>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var part in text.Split(';'))
            {
                var f = part.Split(':');
                if (f.Length != 4) continue;
                int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id);
                int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count);
                int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ammo);
                int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var decay);
                if (id > 0 && count > 0) list.Add(new InvStack(id, count, 0, ammo, decay));
            }
            return list;
        }
    }
}
