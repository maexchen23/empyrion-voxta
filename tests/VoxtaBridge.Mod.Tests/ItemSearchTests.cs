using VoxtaBridge;
using Xunit;

namespace VoxtaBridge.Mod.Tests;

public class ItemSearchTests
{
    private static readonly (string, string)[] Items =
    [
        ("12.7x99mmBullet", "12.7mm Rounds"),
        ("5.56mmBullet", "5.56mm Rounds"),
        ("AmmoBox", "Ammo Box"),
        ("AmmoBoxSV", "Ammo Box"),
        ("Minigun", "MLK-76 Minigun"),
        ("SteelPlate", "Steel Plate"),
        ("IronIngot", "Iron Ingot"),
        ("CopperIngot", "Copper Ingot"),
    ];

    private static List<string> Names(string q) => ItemSearch.Search(q, Items).Select(h => h.Display).ToList();

    [Fact]
    public void Wrong_word_for_ammo_still_finds_the_rounds() =>
        Assert.Equal(["12.7mm Rounds"], Names("12.7mm Ammo"));

    [Fact]
    public void Exact_and_contains()
    {
        Assert.Equal(["MLK-76 Minigun"], Names("minigun"));
        Assert.Equal(["Steel Plate"], Names("steel plates"));
        Assert.Equal(["Iron Ingot"], Names("IronIngot"));
    }

    [Fact]
    public void Several_and_duplicates_merged()
    {
        Assert.Equal(["Copper Ingot", "Iron Ingot"], Names("ingot"));
        Assert.Single(Names("ammo box"));
    }

    [Fact]
    public void Nothing() => Assert.Empty(Names("pentaxid crystal"));
}
