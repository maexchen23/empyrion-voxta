using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class WhereToBuyTests
{
    private static string Render(string json) => GameView.WhereToBuyResult(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Known_sellers_first_then_places_and_hidden()
    {
        var text = Render("""
            {"ok":true,"query":"reactor core","player":{"name":"Player1","factionGroup":0,"factionId":1,"standings":"game.dat"},"count":1,
             "items":[{"name":"Reactor Core",
               "known":[{"trader":"ManufacturingPower","poi":"Solaris-AMP Factory","playfield":"Xyz","via":"visited","ly":4.24,"faction":"Polaris","tier":"Neutral"},
                        {"trader":"QuantumSTAR","poi":"Kenex Station 62","playfield":"Akua Sector","via":"traded","ly":0,"faction":"Polaris","tier":"Neutral"}],
               "sellers":[{"trader":"ManufacturingPower","stock":"5-12","pois":[{"name":"Solaris-AMP Factory","faction":"Polaris","tier":"Neutral",
                   "where":["Desert planet","Arid planet"],"moreWhere":3}]}],
               "sellersNotPlaced":1,
               "stars":[{"star":"Naamumamina","class":"M3","territory":"neutral space","ly":8.7,"chance":89},{"star":"Elo Alpha","class":"G","territory":"Polaris","ly":12,"chance":55}],
               "hidden":[{"trader":"SalvagerPirates","poi":"Smuggler Market","faction":"Pirates","tier":"Unfriendly"}]}]}
            """);
        Assert.Contains("ManufacturingPower at Solaris-AMP Factory in Xyz: 4.2 LY away, Polaris (Neutral), visited", text);
        Assert.Contains("QuantumSTAR at Kenex Station 62 in Akua Sector: in this sector, Polaris (Neutral), traded there before", text);
        Assert.Contains("ManufacturingPower: Solaris-AMP Factory (Polaris, Neutral) on/in: Desert planet, Arid planet and 3 more", text);
        Assert.Contains("1 more trader(s) sell it but their stations don't appear", text);
        Assert.Contains("Naamumamina (M3 star, neutral space, 8.7 LY away): very likely; Elo Alpha (G star, Polaris, 12 LY away): likely", text);
        Assert.DoesNotContain("SalvagerPirates", text);
        Assert.DoesNotContain("Smuggler", text);
        Assert.DoesNotContain("starting standings", text);
    }

    [Fact]
    public void Starting_standings_are_mentioned()
    {
        var text = Render("""
            {"ok":true,"query":"x","player":{"name":"a","standings":"starting values"},"count":1,
             "items":[{"name":"X","sellers":[{"trader":"T","stock":"1","alsoBuys":true,"pois":[{"name":"P","where":["Alien planet"]}]}]}]}
            """);
        Assert.Contains("starting standings used", text);
        Assert.Contains("T (also buys it): P on/in: Alien planet", text);
    }

    [Fact]
    public void Nothing_sold_and_too_many()
    {
        Assert.Equal("No NPC trader sells Iron Ore. 4 trader(s) buy it, though.",
            Render("""{"ok":true,"query":"iron ore","count":0,"matchedItem":"Iron Ore","boughtBy":4}"""));
        Assert.Equal("No trader sells anything called 'zzz'.", Render("""{"ok":true,"query":"zzz","count":0}"""));
        Assert.Contains("matches 12 items that traders sell: A, B",
            Render("""{"ok":true,"query":"ammo","count":12,"namesOnly":true,"names":["A","B"]}"""));
        Assert.Equal("The trader data is not loaded yet.", Render("""{"ok":false,"error":"The trader data is not loaded yet."}"""));
    }

    [Fact]
    public void Known_planets_whose_type_can_have_the_trader()
    {
        var text = Render("""
            {"ok":true,"query":"reactor core","count":1,"items":[{"name":"Reactor Core","sellers":[],
              "knownPlanets":[{"name":"Viunean","kind":"planet","type":"Stormy Desert","system":"Phaealla","ly":0,"chance":62,"entered":true,"byPlayer":true,"pois":["Solaris-AMP Factory"]},
                              {"name":"Rubathra I","kind":"planet","type":"Arid","system":"Rubathra","ly":4.2,"chance":40,"entered":false,"byPlayer":false,"pois":["Solaris-AMP Factory","Polaris Depot"]}],
              "moreKnownPlanets":2}]}
            """);
        Assert.DoesNotContain("Moon moon", Render("""
            {"ok":true,"query":"x","count":1,"items":[{"name":"X","sellers":[],"knownPlanets":[{"name":"M","kind":"moon","type":"Barren Moon","system":"S","ly":1,"chance":60,"pois":["P"]}]}]}
            """));
        Assert.Contains("Known planets whose type can have such a trader (from the planet type only, not confirmed: nobody has seen one there): " +
                        "Viunean (Stormy Desert planet, system Phaealla, this system, visited): Solaris-AMP Factory, likely; " +
                        "Rubathra I (Arid planet, system Rubathra, 4.2 LY away, known through the faction): Solaris-AMP Factory, Polaris Depot, maybe (2 more)", text);
    }
}
