using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;

namespace VoxtaBridge
{
    /// <summary>
    /// A structure's fuel, oxygen or pentaxid tank as a transfer container ("Fuel Tank @ Ship"), so the normal transfer
    /// moves items into and out of it. Only used when named as "to" or "from" (never an implicit source, never in
    /// find/containers lists): "move 10 fuel cells" must not drain the tank.
    ///
    /// Counting (Transfers.Move trusts counts only): for every item the tank accepts, Count = how many of that item the
    /// content is worth (content / units per item, rounded down). Adding n items adds n * units and raises that count by
    /// exactly n; anything that doesn't fit is not added, so the transfer puts it back. As a source the tank only offers
    /// the item the game withdraws (first item of the tank blocks' accept list), like the game's own withdraw.
    /// The pentaxid tank is a container of crystals; it is drained through the tank block's IContainer, because the
    /// game's AddContent ignores negative amounts for it.
    /// </summary>
    public partial class BridgeMod
    {
        sealed class TankContainer : ITransferContainer
        {
            public IStructure Structure;
            public IStructureTank Tank;
            public string TankKind;          // fuel | oxygen | pentaxid
            public int DrainItemId;
            public string Label { get; set; }
            public string Name { get; set; }
            public string Kind => "Tank";
            public string Vessel { get; set; }
            public bool IsCurrent { get; set; }
            public bool Locked => false;
            public int MaxSlots => 0;

            int Value(int itemId)
            {
                var n = ItemName(itemId);
                return n != null && gameText != null && gameText.TankAccepts(TankKind).Contains(n) ? gameText.TankValue(TankKind, n) : 0;
            }

            public int Count(int itemId)
            {
                var v = Value(itemId);
                return v <= 0 ? 0 : (int)Math.Floor(Tank.Content / v + 1e-6);
            }

            public IEnumerable<int> ItemIds()
            {
                if (DrainItemId != 0 && Count(DrainItemId) > 0) yield return DrainItemId;
            }

            public void Add(int itemId, int count)
            {
                var v = Value(itemId);
                if (v <= 0 || count <= 0) return;
                var fit = (int)Math.Floor((Tank.Capacity - Tank.Content) / v + 1e-6);
                var n = Math.Min(count, fit);
                if (n > 0) Tank.AddContent(n * v);
            }

            public void Remove(int itemId, int count)
            {
                if (itemId != DrainItemId || count <= 0) return;
                var n = Math.Min(count, Count(itemId));
                if (n <= 0) return;
                if (TankKind == "pentaxid") { RemovePentaxid(Structure, itemId, n); return; }
                Tank.AddContent(-n * Value(itemId));
            }

            public List<InvStack> GetStacks()
            {
                var n = DrainItemId != 0 ? Count(DrainItemId) : 0;
                return n > 0 ? new List<InvStack> { new InvStack(DrainItemId, n) } : new List<InvStack>();
            }

            public void SetStacks(List<InvStack> stacks) { } // tanks hold no stacks; callers verify by counting
        }

        static IStructureTank TankOf(IStructure s, string kind) =>
            kind == "fuel" ? s.FuelTank : kind == "oxygen" ? s.OxygenTank : kind == "pentaxid" ? s.PentaxidTank : null;

        static readonly (string kind, string name)[] TankNames = { ("fuel", "Fuel Tank"), ("oxygen", "Oxygen Tank"), ("pentaxid", "Pentaxid Tank") };

        /// <summary>The tanks of the given structures as transfer containers (only those with capacity).</summary>
        static List<ITransferContainer> TankContainers(IEnumerable<IStructure> structures, IStructure current)
        {
            var list = new List<ITransferContainer>();
            if (gameText == null) return list;
            foreach (var s in structures.Where(x => x != null).GroupBy(x => x.Id).Select(g => g.First()))
                foreach (var (kind, name) in TankNames)
                {
                    IStructureTank t = null;
                    try { t = TankOf(s, kind); } catch { }
                    if (t == null || t.Capacity <= 0) continue;
                    var vessel = s.Entity?.Name ?? s.Id.ToString();
                    list.Add(new TankContainer
                    {
                        Structure = s, Tank = t, TankKind = kind, DrainItemId = ItemIdOf(gameText.TankDrainItem(kind)),
                        Name = name, Vessel = vessel, Label = $"{name} @ {vessel}", IsCurrent = current != null && current.Id == s.Id,
                    });
                }
            return list;
        }

        /// <summary>The pentaxid tank block(s) of a structure as item containers (positions from the game's device lists).</summary>
        static List<IContainer> PentaxidTankBlocks(IStructure s, out string error)
        {
            var tanks = new List<IContainer>();
            var lists = GameInternals.DeviceLists(s, Unverified, out error);
            if (lists == null) return tanks;
            foreach (var l in lists)
                for (var i = 0; i < l.Count; i++)
                {
                    var bn = BlockName(s.GetBlock(l.GetAt(i))) ?? "";
                    if (bn.IndexOf("Pentaxid", StringComparison.OrdinalIgnoreCase) < 0 || bn.IndexOf("Tank", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var c = s.GetDevice<IContainer>(l.GetAt(i));
                    if (c != null && !tanks.Contains(c)) tanks.Add(c);
                }
            if (tanks.Count == 0 && error == null) error = "the pentaxid tank block can't be opened as a container";
            return tanks;
        }

        /// <summary>Takes up to n crystals out of the pentaxid tank block(s); returns how many came out (counted).</summary>
        static int RemovePentaxid(IStructure s, int itemId, int n)
        {
            var taken = 0;
            foreach (var c in PentaxidTankBlocks(s, out _))
            {
                if (taken >= n) break;
                var had = c.GetTotalItems(itemId);
                if (had <= 0) continue;
                c.RemoveItems(itemId, Math.Min(had, n - taken)); // return value not trusted (see ITransferContainer.Remove)
                taken += Math.Max(0, had - c.GetTotalItems(itemId));
            }
            return taken;
        }
    }
}
