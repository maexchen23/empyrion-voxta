using VoxtaBridge;
using Xunit;

namespace VoxtaBridge.Mod.Tests;

public class TransfersTests
{
    /// <summary>
    /// In-memory container with real stacks (slot + state) and optional capacity (max total items).
    /// The game's return values are not used by Move; Remove/Add here deliberately return nothing.
    /// </summary>
    internal sealed class Box(string label, string name, string kind, string vessel = null, int capacity = int.MaxValue, bool locked = false, int maxSlots = 64)
        : ITransferContainer
    {
        public List<InvStack> Content = new();
        public string Label { get; } = label;
        public string Name { get; } = name;
        public string Kind { get; } = kind;
        public string Vessel { get; } = vessel ?? "Test Carrier";
        public bool IsCurrent => vessel == null;
        public bool Locked { get; } = locked;
        public int MaxSlots { get; } = maxSlots;
        public bool RefuseAll;     // simulates a container that won't take this item (e.g. ammo box vs. ingots)
        public bool SwallowsAdds;  // simulates the worst case: items vanish on Add
        public bool IgnoresSetContent; // simulates the game silently not applying SetContent
        public int Count(int id) => Content.Where(s => s.Id == id).Sum(s => s.Count);
        public IEnumerable<int> ItemIds() => Content.Where(s => s.Count > 0).Select(s => s.Id).Distinct();
        public void Remove(int id, int count) => Stacks.Take(Content, id, count);
        public void Add(int id, int count)
        {
            if (RefuseAll || SwallowsAdds) return;
            var space = capacity - Content.Sum(s => s.Count);
            var n = Math.Min(count, Math.Max(0, space));
            if (n > 0) Stacks.Place(Content, [new InvStack(id, n)], MaxSlots, _ => 999);
        }
        public List<InvStack> GetStacks() => Content.ToList();
        public void SetStacks(List<InvStack> stacks) { if (!IgnoresSetContent && !RefuseAll) Content = stacks.ToList(); }
        public Box With(int id, int n) { Stacks.Place(Content, [new InvStack(id, n)], MaxSlots, _ => 999); return this; }
        public Box WithStack(InvStack st) { st.Slot = Enumerable.Range(0, MaxSlots).First(x => Content.All(c => c.Slot != x)); Content.Add(st); return this; }
    }

    private const int Ammo = 100, Knife = 200, Ore = 300, Ammo2 = 101;
    private static string Internal(int id) => id switch { Ammo => "45mmBullet", Ammo2 => "50mmBullet", Knife => "CombatKnife", Ore => "IronOre", _ => null };
    private static string Display(int id) => id switch { Ammo => "45mm Ammo", Ammo2 => "50mm Ammo", Knife => "Combat Knife", Ore => "Iron Ore", _ => null };

    [Fact]
    public void Labels_use_unique_names_else_numbered_kinds_and_vessel_suffix()
    {
        var labels = Transfers.MakeLabels([
            ("Hold", "HarvestCntr", "Test Carrier"),
            (null, "AmmoCntr", "Test Carrier"),
            ("", "AmmoCntr", "Test Carrier"),
            ("Box", "Container", "Test Carrier"),
            ("Box", "Container", "Test Carrier"),       // duplicate name → numbered
            ("Hold", "HarvestCntr", "Hauler"),  // same name on another structure is fine
        ]);
        Assert.Equal(["Hold @ Test Carrier", "ammo box 1 @ Test Carrier", "ammo box 2 @ Test Carrier", "cargo box 1 @ Test Carrier", "cargo box 2 @ Test Carrier", "Hold @ Hauler"], labels);
    }

    private static List<ITransferContainer> Ship() =>
    [
        new Box("Hold @ Test Carrier", "Hold", "HarvestCntr").With(Knife, 1).With(Ammo, 300),
        new Box("ammo box 1 @ Test Carrier", null, "AmmoCntr"),
        new Box("Spare @ Test Carrier", "Spare", "Container").With(Ammo, 500).With(Ammo2, 50),
        new Box("Hold @ Hauler", "Hold", "HarvestCntr", "Hauler").With(Ore, 1000),
    ];

    [Theory]
    [InlineData("Hold @ Test Carrier", new[] { "Hold @ Test Carrier" })]
    [InlineData("hold @ hauler", new[] { "Hold @ Hauler" })]
    [InlineData("Hold", new[] { "Hold @ Test Carrier", "Hold @ Hauler" })]   // bare name: ambiguous → caller errors
    [InlineData("Spare", new[] { "Spare @ Test Carrier" })]                    // bare name, unique
    [InlineData("ammo box 1", new[] { "ammo box 1 @ Test Carrier" })]          // numbered label without structure
    [InlineData("ammo box", new[] { "ammo box 1 @ Test Carrier" })]
    [InlineData("cargo", new[] { "Hold @ Test Carrier", "Spare @ Test Carrier", "Hold @ Hauler" })]
    [InlineData("ship", new[] { "Hold @ Test Carrier", "ammo box 1 @ Test Carrier", "Spare @ Test Carrier" })]  // the structure the player is on
    [InlineData("nope", new string[0])]
    public void Container_matching(string spec, string[] expected)
    {
        Assert.Equal(expected, Transfers.Match(Ship(), spec).Select(c => c.Label));
    }

    [Theory]
    [InlineData("CombatKnife", Knife)]
    [InlineData("combat knife", Knife)]
    [InlineData("Combat-Knife", Knife)]
    [InlineData("knife", Knife)]
    [InlineData("45mm", Ammo)]
    public void Item_resolution(string spec, int expected)
    {
        Assert.Equal(expected, Transfers.ResolveItem(spec, [Ammo, Ammo2, Knife], Internal, Display, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Ambiguous_item_lists_options()
    {
        Assert.Equal(0, Transfers.ResolveItem("ammo", [Ammo, Ammo2, Knife], Internal, Display, out var error));
        Assert.Contains("45mm Ammo", error);
        Assert.Contains("50mm Ammo", error);
    }

    [Fact]
    public void Missing_item_is_an_error()
    {
        Assert.Equal(0, Transfers.ResolveItem("gold", [Ammo], Internal, Display, out var error));
        Assert.Contains("No 'gold'", error);
    }

    [Fact]
    public void Moves_from_several_sources_in_order()
    {
        var ship = Ship();
        var target = ship[1];
        var r = Transfers.Move(Ammo, 600, target, [ship[0], ship[2]], 10000);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(600, r.Moved);
        Assert.Equal(600, target.Count(Ammo));
        Assert.Equal(0, ship[0].Count(Ammo));
        Assert.Equal(200, ship[2].Count(Ammo));
        Assert.Equal([("Hold @ Test Carrier", 300), ("Spare @ Test Carrier", 300)], r.Sources);
    }

    [Fact]
    public void All_moves_everything_available()
    {
        var ship = Ship();
        var r = Transfers.Move(Ammo, Transfers.All, ship[1], [ship[0], ship[2]], 10000);
        Assert.Equal(800, r.Moved);
    }

    [Fact]
    public void Asking_for_more_than_available_moves_what_there_is()
    {
        var ship = Ship();
        var r = Transfers.Move(Knife, 5, ship[2], [ship[0]], 10000);
        Assert.True(r.Ok);
        Assert.Equal(1, r.Moved);
        Assert.Contains("only 1 available", r.Note);
    }

    [Fact]
    public void Cap_per_transfer()
    {
        var ship = Ship();
        var r = Transfers.Move(Ammo, 800, ship[1], [ship[0], ship[2]], 100);
        Assert.Equal(100, r.Moved);
        Assert.Contains("capped at 100", r.Note);
    }

    [Fact]
    public void Full_target_returns_the_rest_to_the_source()
    {
        var small = new Box("Tiny", "Tiny", "AmmoCntr", capacity: 50);
        var src = new Box("Spare", "Spare", "Container").With(Ammo, 500);
        var r = Transfers.Move(Ammo, 200, small, [src], 10000);

        Assert.True(r.Ok);
        Assert.Equal(50, r.Moved);
        Assert.Equal(450, src.Count(Ammo)); // nothing lost
        Assert.Contains("'Tiny' is full", r.Note);
    }

    [Fact]
    public void Locked_target_is_refused()
    {
        var target = new Box("Hold", "Hold", "HarvestCntr", locked: true);
        var r = Transfers.Move(Ammo, 10, target, [new Box("Spare", "Spare", "Container").With(Ammo, 10)], 10000);
        Assert.False(r.Ok);
        Assert.Contains("'Hold' is open in the game", r.Error);
        Assert.Contains("Close it and try again", r.Error);
    }

    [Fact]
    public void Locked_sources_are_skipped_and_mentioned()
    {
        var open = new Box("Open", "Open", "Container", locked: true).With(Ammo, 100);
        var closed = new Box("Closed", "Closed", "Container").With(Ammo, 10);
        var target = new Box("T", "T", "AmmoCntr");
        var r = Transfers.Move(Ammo, 50, target, [open, closed], 10000);

        Assert.Equal(10, r.Moved);
        Assert.Equal(100, open.Count(Ammo));
        Assert.Contains("skipped containers that are open in the game: Open", r.Note);
    }

    [Fact]
    public void Target_is_never_its_own_source()
    {
        var box = new Box("Hold", "Hold", "HarvestCntr").With(Ammo, 10);
        var r = Transfers.Move(Ammo, 5, box, [box], 10000);
        Assert.False(r.Ok);
        Assert.Equal(10, box.Count(Ammo));
    }

    [Fact]
    public void Regression_all_ingots_from_one_box_into_another()
    {
        // 2026-11-17: "Iron Ingot, all, from B Loot 1 to A Production" lost 250 ingots.
        var loot = new Box("B Loot 1", "B Loot 1", "Container").With(Ore, 250);
        var production = new Box("A Production", "A Production", "Container");

        var r = Transfers.Move(Ore, Transfers.All, production, [loot], 10000);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(250, r.Moved);
        Assert.Equal(250, production.Count(Ore));
        Assert.Equal(0, loot.Count(Ore));
        Assert.Equal(0, r.Lost);
    }

    [Fact]
    public void Target_refusing_the_item_puts_everything_back()
    {
        var src = new Box("Spare", "Spare", "Container").With(Ore, 100);
        var ammoBox = new Box("ammo box 1", null, "AmmoCntr") { RefuseAll = true };

        var r = Transfers.Move(Ore, 100, ammoBox, [src], 10000);

        Assert.False(r.Ok);
        Assert.Equal(100, src.Count(Ore));
        Assert.Equal(0, r.Lost);
        Assert.Contains("is full", r.Error);
    }

    [Fact]
    public void Items_that_cannot_be_put_back_are_reported_as_lost()
    {
        var src = new Box("Spare", "Spare", "Container").With(Ore, 100);
        src.SwallowsAdds = true; // source won't take them back either
        var target = new Box("T", "T", "Container") { RefuseAll = true };

        var r = Transfers.Move(Ore, 100, target, [src], 10000);

        Assert.Equal(100, r.Lost);
        Assert.Contains("WARNING", r.Error);
    }

    [Fact]
    public void Item_only_in_open_source_says_open_not_none()
    {
        var open = new Box("B Loot 1", "B Loot 1", "Container", locked: true).With(Knife, 1);
        var target = new Box("Hold", "Hold", "HarvestCntr");

        var r = Transfers.Move(Knife, 1, target, [open], 10000);

        Assert.False(r.Ok);
        Assert.Contains("'B Loot 1' is open in the game", r.Error);
        Assert.DoesNotContain("None of that item", r.Error);
        Assert.Equal(1, open.Count(Knife));
    }
}

public class MatchItemsTests
{
    private static string Internal(int id) => id switch { 1 => "IronIngot", 2 => "CopperIngot", 3 => "IronOre", 4 => "45mmBullet", _ => null };
    private static string Display(int id) => id switch { 1 => "Iron Ingot", 2 => "Copper Ingot", 3 => "Iron Ore", 4 => "45mm Ammo", _ => null };

    [Theory]
    [InlineData("Iron Ingot", new[] { 1 })]          // exact display name wins over "contains"
    [InlineData("IronOre", new[] { 3 })]             // exact internal name
    [InlineData("ingot", new[] { 1, 2 })]            // several items is fine for a search
    [InlineData("iron", new[] { 1, 3 })]
    [InlineData("45 mm", new[] { 4 })]               // spaces ignored
    [InlineData("gold", new int[0])]
    public void Matches(string spec, int[] expected)
    {
        Assert.Equal(expected, Transfers.MatchItems(spec, [1, 2, 3, 4], Internal, Display));
    }
}
