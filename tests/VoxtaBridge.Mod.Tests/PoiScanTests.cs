using System.Collections.Generic;
using System.Linq;
using VoxtaBridge;
using Xunit;

namespace VoxtaBridge.Mod.Tests;

public class PoiScanTests
{
    static ScanPoi P(string name, PoiKind kind, int faction, double x, double z, int turrets = 0, double dist = 0) =>
        new ScanPoi { Name = name, Kind = kind, FactionId = faction, X = x, Z = z, Turrets = turrets, Distance = dist == 0 ? System.Math.Sqrt(x * x + z * z) : dist };

    [Fact]
    public void Same_faction_buildings_close_together_form_one_complex_led_by_the_main_base()
    {
        var pois = new List<ScanPoi>
        {
            P("Vehicle Bay", PoiKind.MinorFaction, 7, 2000, 0, 5),
            P("Ghyst Main", PoiKind.MajorFaction, 7, 2400, 0, 12),
            P("Outpost", PoiKind.MinorFaction, 7, 2900, 0, 4),       // chained via the main base (500 m)
            P("Stack-15", PoiKind.Trading, 2, 2100, 100, 4),         // other faction: stays apart
            P("Small Wreckage", PoiKind.Salvage, 68, 2050, 0),       // salvage never joins
            P("Far Outpost", PoiKind.MinorFaction, 7, 9000, 0, 4),   // same faction, too far
        };
        var groups = PoiScan.Group(pois);
        Assert.Equal(4, groups.Count);
        var complex = groups.Single(g => g.IsComplex);
        Assert.Equal("Ghyst Main", complex.Lead.Name);
        Assert.Equal(PoiKind.MajorFaction, complex.Kind);
        Assert.Equal(3, complex.Members.Count);
        Assert.Equal(21, complex.Turrets);
        Assert.Equal(2000, complex.Distance);
        Assert.Equal("1 major faction base, 2 small faction outpost", PoiScan.MemberSummary(complex));
        Assert.Equal(new[] { "Ghyst Main", "Small Wreckage", "Stack-15", "Far Outpost" }, groups.Select(g => g.Lead.Name));
    }

    [Fact]
    public void Civilian_buildings_fill_only_the_slots_left()
    {
        var pois = new List<ScanPoi>();
        for (var i = 0; i < 5; i++) pois.Add(P("Condenser", PoiKind.Civilian, 2, 0, 1000 * (i + 1)));
        pois.Add(P("Stack-15", PoiKind.Trading, 16, 0, -3000));
        pois.Add(P("Wreck", PoiKind.Salvage, 68, 0, -9000));
        var picked = PoiScan.Pick(PoiScan.Group(pois), 4);
        Assert.Equal(new[] { "Condenser", "Condenser", "Stack-15", "Wreck" }, picked.Select(g => g.Lead.Name));
        // a civilian module next to a trading post of the same faction joins its complex
        var g = PoiScan.Group(new List<ScanPoi> { P("Sanctuary", PoiKind.Trading, 2, 0, 10), P("Habitat Module", PoiKind.Civilian, 2, 300, 10) });
        Assert.Equal("Sanctuary", g.Single().Lead.Name);
    }

    [Theory]
    [InlineData("MoonLava", "Lava Moon")]
    [InlineData("JupiterPurple", "Purple gas giant")]
    [InlineData("Stormy Desert", "Stormy Desert")]
    [InlineData("BarrenMetal", "Barren Metal")]
    [InlineData("K4", "K4")]
    public void Planet_type_labels(string raw, string label) => Assert.Equal(label, PoiScan.PlanetTypeLabel(raw));

    [Theory]
    [InlineData("mf=1.0-1.25", 100.0, "about market")]
    [InlineData("mf=0.5-0.75", 100.0, "below market")]
    [InlineData("mf=1.25-1.5", 100.0, "above market")]
    [InlineData("mf=8.0-12.5", 100.0, "far above market")]
    [InlineData("100-150", 100.0, "above market")]
    [InlineData("85-95", 100.0, "about market")]
    [InlineData("mf=1.0-1.25", null, null)]
    public void Price_tiers(string price, double? market, string tier) => Assert.Equal(tier, TraderIndex.PriceTier(price, market));

    [Theory]
    [InlineData("CannedMeat", "Food", 354.0, true)]
    [InlineData("SteelPlate", "Components", 10.0, true)]
    [InlineData("ReactorCore", "Components", 100000.0, false)]
    [InlineData("LargeOptronicBridge", "Components", 18974.0, false)]
    [InlineData("SpaceDurian", "Food", 2300.0, true)]
    [InlineData("Token:4057", null, null, false)]
    [InlineData("Mystery", null, null, false)]
    public void Everyday_items(string item, string category, double? market, bool everyday) =>
        Assert.Equal(everyday, TraderIndex.IsEveryday(item, category, market));

    [Fact]
    public void Credits_from_factor_or_absolute()
    {
        Assert.Equal((100.0, 125.0), TraderIndex.Credits("mf=1.0-1.25", 100));
        Assert.Equal((222.0, 333.0), TraderIndex.Credits("222-333", null));
        Assert.Null(TraderIndex.Credits("mf=1.0", null));
    }

    [Fact]
    public void Unknown_turret_counts_do_not_lower_the_sum()
    {
        var g = PoiScan.Group(new List<ScanPoi> { P("A", PoiKind.MinorFaction, 7, 0, 0, -1, 10), P("B", PoiKind.MinorFaction, 7, 100, 0, 3) }).Single();
        Assert.Equal(3, g.Turrets);
    }
}
