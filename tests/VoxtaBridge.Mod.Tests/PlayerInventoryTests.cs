using VoxtaBridge;
using Xunit;

namespace VoxtaBridge.Mod.Tests;

public class PlayerInventoryTests
{
    private const int Ingot = 8269, Knife = 8204;

    [Theory]
    [InlineData("player", true, true)]
    [InlineData("Me", true, true)]
    [InlineData("my inventory", true, true)]
    [InlineData("bag", true, false)]
    [InlineData("hotbar", false, true)]
    public void Player_words(string spec, bool bag, bool toolbar)
    {
        Assert.True(Transfers.IsPlayerSpec(spec, out var b, out var t));
        Assert.Equal(bag, b);
        Assert.Equal(toolbar, t);
    }

    [Theory]
    [InlineData("Hold")]
    [InlineData("player box")]
    [InlineData("")]
    public void Container_names_are_not_the_player(string spec)
    {
        Assert.False(Transfers.IsPlayerSpec(spec, out _, out _));
    }

    [Fact]
    public void Takes_from_bag_first_smaller_stacks_first_and_keeps_other_stacks()
    {
        var bag = new List<InvStack> { new(Ingot, 999, 0), new(Knife, 1, 1, ammo: 7, decay: 3), new(Ingot, 50, 2) };
        var toolbar = new List<InvStack> { new(Ingot, 100, 0) };

        var taken = Stacks.Total(Transfers.TakeFromInventory(bag, toolbar, Ingot, 100, useBag: true, useToolbar: true));

        Assert.Equal(100, taken);
        Assert.Equal(949, Transfers.CountIn(bag, Ingot));      // 50-stack emptied first, then 50 of the 999
        Assert.Equal(2, bag.Count);                              // emptied stack removed
        Assert.Equal(100, Transfers.CountIn(toolbar, Ingot));   // toolbar untouched
        var knife = bag.Single(s => s.Id == Knife);
        Assert.Equal((1, 7, 3, 1), (knife.Count, knife.Ammo, knife.Decay, knife.Slot));
    }

    [Fact]
    public void Falls_back_to_toolbar_and_stops_at_what_is_there()
    {
        var bag = new List<InvStack> { new(Ingot, 30) };
        var toolbar = new List<InvStack> { new(Ingot, 20) };

        Assert.Equal(50, Stacks.Total(Transfers.TakeFromInventory(bag, toolbar, Ingot, 80, true, true)));
        Assert.Empty(bag);
        Assert.Empty(toolbar);
    }

    [Fact]
    public void Toolbar_only_leaves_bag_alone()
    {
        var bag = new List<InvStack> { new(Ingot, 30) };
        var toolbar = new List<InvStack> { new(Ingot, 20) };

        Assert.Equal(20, Stacks.Total(Transfers.TakeFromInventory(bag, toolbar, Ingot, 25, useBag: false, useToolbar: true)));
        Assert.Equal(30, Transfers.CountIn(bag, Ingot));
    }
}
