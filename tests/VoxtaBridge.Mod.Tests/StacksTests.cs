using VoxtaBridge;
using Xunit;
using Box = VoxtaBridge.Mod.Tests.TransfersTests.Box;

namespace VoxtaBridge.Mod.Tests;

public class StacksTests
{
    private const int Ingot = 8269, Pistol = 8212, Token = 9497, Food = 9000;
    private static int Size(int id) => id == Pistol ? 1 : 999;

    [Fact]
    public void Place_merges_first_then_next_free_slot()
    {
        var bag = new List<InvStack> { new(Ingot, 990, 0), new(Food, 1, 1, decay: 5), new(Ingot, 10, 3) };

        var left = Stacks.Place(bag, [new InvStack(Ingot, 1200)], Stacks.BagSlots, Size);

        Assert.Empty(left);
        Assert.Equal(999, bag.Single(s => s.Slot == 0).Count);    // topped up 9
        Assert.Equal(999, bag.Single(s => s.Slot == 3).Count);    // topped up 989
        Assert.Equal(202, bag.Single(s => s.Slot == 2).Count);    // rest into the first free slot (2)
        Assert.Equal(2200, Stacks.Total(bag, Ingot));
    }

    [Fact]
    public void Stacks_with_state_never_merge()
    {
        var bag = new List<InvStack> { new(Pistol, 1, 0, ammo: 10, decay: 50) };

        Stacks.Place(bag, [new InvStack(Pistol, 1, 0, ammo: 3, decay: 200)], Stacks.BagSlots, Size);

        Assert.Equal(2, bag.Count);
        Assert.Contains(bag, s => s.Slot == 1 && s.Ammo == 3 && s.Decay == 200);
    }

    [Fact]
    public void Full_bag_returns_leftover()
    {
        var bag = Enumerable.Range(0, Stacks.BagSlots).Select(i => new InvStack(Food, 1, i, decay: 1)).ToList();
        var left = Stacks.Place(bag, [new InvStack(Ingot, 50)], Stacks.BagSlots, Size);
        Assert.Equal(50, Stacks.Total(left));
    }

    [Fact]
    public void Take_splits_and_keeps_state()
    {
        var list = new List<InvStack> { new(Token, 5, 4, ammo: 120) };
        var taken = Stacks.Take(list, Token, 2);
        Assert.Equal((2, 120), (taken.Single().Count, taken.Single().Ammo));
        Assert.Equal(3, list.Single().Count);
    }

    [Fact]
    public void Encode_decode_round_trip()
    {
        var s = new List<InvStack> { new(Pistol, 1, 0, 10, 50), new(Ingot, 999) };
        var back = Stacks.Decode(Stacks.Encode(s));
        Assert.Equal([(Pistol, 1, 10, 50), (Ingot, 999, 0, 0)], back.Select(x => (x.Id, x.Count, x.Ammo, x.Decay)));
    }

    // --- stack-mode container moves ---------------------------------------------------------

    [Fact]
    public void Loaded_worn_pistol_keeps_ammo_and_wear()
    {
        var src = new Box("Weapons @ Test Carrier", "Weapons", "Container").WithStack(new InvStack(Pistol, 1, 0, ammo: 7, decay: 321));
        var dst = new Box("Hold @ Test Carrier", "Hold", "Container");

        Assert.True(Transfers.NeedsStackMode(Pistol, [src], isToken: false));
        var r = Transfers.MoveStacks(Pistol, 1, dst, [src], 10000, Size);

        Assert.True(r.Ok, r.Error);
        var p = dst.Content.Single();
        Assert.Equal((Pistol, 7, 321), (p.Id, p.Ammo, p.Decay));
        Assert.Empty(src.Content);
    }

    [Fact]
    public void Tokens_move_with_their_token_id()
    {
        var src = new Box("Loot @ Test Carrier", "Loot", "Container").WithStack(new InvStack(Token, 1, 0, ammo: 6999));
        var dst = new Box("Cards @ Test Carrier", "Cards", "Container");

        var r = Transfers.MoveStacks(Token, 1, dst, [src], 10000, Size);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(6999, dst.Content.Single().Ammo);
    }

    [Fact]
    public void Full_target_in_stack_mode_moves_nothing_and_keeps_source()
    {
        var src = new Box("A @ Test Carrier", "A", "Container").WithStack(new InvStack(Pistol, 1, 0, ammo: 7));
        var dst = new Box("B @ Test Carrier", "B", "Container", maxSlots: 1).WithStack(new InvStack(Food, 1, 0, decay: 3));

        var r = Transfers.MoveStacks(Pistol, 1, dst, [src], 10000, Size);

        Assert.False(r.Ok);
        Assert.Single(src.Content);
        Assert.Contains("full", r.Error);
    }

    [Fact]
    public void Game_ignoring_SetContent_restores_both()
    {
        var src = new Box("A @ Test Carrier", "A", "Container").WithStack(new InvStack(Pistol, 1, 0, ammo: 7));
        var dst = new Box("B @ Test Carrier", "B", "Container") { IgnoresSetContent = true };

        var r = Transfers.MoveStacks(Pistol, 1, dst, [src], 10000, Size);

        Assert.False(r.Ok);
        Assert.Equal(7, src.Content.Single().Ammo);
        Assert.Empty(dst.Content);
    }

    [Fact]
    public void RemoveOnly_in_stack_mode_returns_stacks_with_state()
    {
        var src = new Box("A @ Test Carrier", "A", "Container").WithStack(new InvStack(Pistol, 1, 0, ammo: 7, decay: 9)).WithStack(new InvStack(Pistol, 1, 0, ammo: 2));

        var r = Transfers.RemoveOnly(Pistol, 2, [src], 10000, stackMode: true);

        Assert.True(r.Ok);
        Assert.Equal(2, r.Moved);
        Assert.Contains(r.Stacks, s => s.Ammo == 7 && s.Decay == 9);
        Assert.Empty(src.Content);
    }

    [Fact]
    public void PutStacksInto_places_plain_by_count_and_state_whole()
    {
        var dst = new Box("B @ Test Carrier", "B", "Container");
        var left = Transfers.PutStacksInto([new InvStack(Ingot, 100), new InvStack(Pistol, 1, 0, ammo: 7)], [dst], Size);

        Assert.Empty(left);
        Assert.Equal(100, dst.Count(Ingot));
        Assert.Equal(7, dst.Content.Single(s => s.Id == Pistol).Ammo);
    }
}

public class VolumeLimitTests
{
    private const int Ingot = 8269, Logs = 8434, Unknown = 1;
    private static double Vol(int id) => id switch { Ingot => 0.75, Logs => 10, _ => 0 };

    [Fact]
    public void Caps_to_what_fits_under_the_limit()
    {
        // 900 L used, 1000 L limit → 100 L free → 133 ingots (0.75 L), rest stays out
        var (fits, rest) = Stacks.CapByVolume([new InvStack(Ingot, 250)], 900, 1000, Vol);
        Assert.Equal(133, Stacks.Total(fits));
        Assert.Equal(117, Stacks.Total(rest));
    }

    [Fact]
    public void Full_inventory_takes_nothing()
    {
        var (fits, rest) = Stacks.CapByVolume([new InvStack(Logs, 5)], 1000, 1000, Vol);
        Assert.Empty(fits);
        Assert.Equal(5, Stacks.Total(rest));
    }

    [Fact]
    public void Unknown_volume_always_fits_and_state_is_kept()
    {
        var (fits, rest) = Stacks.CapByVolume([new InvStack(Unknown, 3, 0, ammo: 7), new InvStack(Logs, 20)], 900, 1000, Vol);
        Assert.Contains(fits, s => s.Id == Unknown && s.Ammo == 7 && s.Count == 3);
        Assert.Equal(10, Stacks.Total(fits, Logs));   // 100 L / 10 L
        Assert.Equal(10, Stacks.Total(rest, Logs));
    }

    [Fact]
    public void Volume_sums_count_times_volume()
    {
        Assert.Equal(187.5 + 900, Stacks.Volume([new InvStack(Ingot, 250), new InvStack(Logs, 90)], Vol), 3);
    }
}

public class WriteVerificationTests
{
    [Theory]
    // adding (give): before 0, expected 90
    [InlineData(0, 90, 90, Stacks.WriteOutcome.Done, 90)]
    [InlineData(0, 90, 95, Stacks.WriteOutcome.Done, 90)]       // player picked up more meanwhile: still done, 90
    [InlineData(0, 90, 0, Stacks.WriteOutcome.NotApplied, 0)]   // nothing changed → put back
    [InlineData(0, 90, 40, Stacks.WriteOutcome.Partial, 40)]
    // taking: before 100, expected 75
    [InlineData(100, 75, 75, Stacks.WriteOutcome.Done, 25)]
    [InlineData(100, 75, 100, Stacks.WriteOutcome.NotApplied, 0)]
    [InlineData(100, 75, 120, Stacks.WriteOutcome.NotApplied, 0)] // never negative
    [InlineData(100, 75, 90, Stacks.WriteOutcome.Partial, 10)]
    public void Classify(int before, int expected, int observed, Stacks.WriteOutcome outcome, int applied)
    {
        Assert.Equal((outcome, applied), Stacks.Classify(before, expected, observed));
    }

    [Fact]
    public void Regression_stale_reply_must_not_count_as_not_applied()
    {
        // 2026-11-17: the SetInventory reply still showed 0 logs although 90 had arrived; judged "not applied",
        // the 90 were put back into the container as well → duplicated. The re-read count (90) is what counts.
        var staleReply = Stacks.Classify(0, 90, 0);
        var reRead = Stacks.Classify(0, 90, 90);
        Assert.Equal(Stacks.WriteOutcome.NotApplied, staleReply.outcome); // why the reply must not be used
        Assert.Equal((Stacks.WriteOutcome.Done, 90), reRead);
    }
}
