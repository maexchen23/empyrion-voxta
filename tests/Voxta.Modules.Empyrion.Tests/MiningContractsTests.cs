using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class MiningContractsTests
{
    static string Text(string json) => GameView.MiningContractsResult(JsonDocument.Parse(json).RootElement);

    const string Rules = """ "rules":{"cooldownSeconds":7200,"offerFaction":"Polaris","choiceFromTier":"HonoredMin"} """;

    [Fact]
    public void Lookup_lists_contracts_and_rules()
    {
        var text = Text($$$"""
            {"ok":true,{{{Rules}}},"ore":"magnesium","count":1,
             "contracts":[{"token":6860,"name":"\"Mining Contract: Magnesium\"","faction":"Zirax Empire","credits":800000,"reputation":500,
                           "ores":[{"item":"MagnesiumOre","name":"Magnesium Ore","count":8000}]}]}
            """);
        Assert.Contains("Mining contracts matching 'magnesium'", text);
        Assert.Contains("- Zirax Empire: 8,000 Magnesium Ore -> 800,000 credits, +500 Zirax Empire reputation.", text);
        Assert.Contains("every 2 h", text);
        Assert.Contains("Honored with Polaris", text);
    }

    [Fact]
    public void Lookup_without_match()
    {
        var text = Text($$$"""{"ok":true,{{{Rules}}},"ore":"gold","faction":"zirax","count":0,"contracts":[]}""");
        Assert.StartsWith("No mining contract matches 'gold', 'zirax'.", text);
    }

    [Fact]
    public void Mine_reports_where_the_token_is_and_the_ore_in_reach()
    {
        var text = Text($$$"""
            {"ok":true,{{{Rules}}},
             "contracts":[
               {"token":6826,"name":"Mining Contract: Iron","faction":"Polaris Megacorp","credits":1000000,"reputation":500,
                "ores":[{"item":"IronOre","name":"Iron Ore","count":20000}]},
               {"token":6829,"name":"Mining Contract: Mixed","faction":"Polaris Megacorp","credits":350000,"reputation":500,
                "ores":[{"item":"IronOre","name":"Iron Ore","count":4000},{"item":"CopperOre","name":"Copper Ore","count":2000}]},
               {"token":6860,"faction":"Zirax Empire","credits":800000,"reputation":500,"ores":[{"item":"MagnesiumOre","name":"Magnesium Ore","count":8000}]}],
             "found":{"ok":true,
               "tokens":[{"token":6826,"where":"player bag","count":1,"inventory":true},
                         {"token":6829,"where":"Q Quest @ Test Hauler","count":1},
                         {"token":9999,"where":"player bag","count":1,"inventory":true}],
               "ores":[{"item":"IronOre","where":"I Ores @ Test Hauler","count":9000},
                       {"item":"IronOre","where":"Hold @ Test Hauler","count":3400},
                       {"item":"CopperOre","where":"Hold @ Test Hauler","count":2500}]}}
            """);
        Assert.Contains("Mining Contract: Iron for Polaris Megacorp, in the inventory: wants 20,000 Iron Ore, 12,400 in reach " +
                        "(I Ores @ Test Hauler 9,000, Hold @ Test Hauler 3,400), 7,600 short;", text);
        Assert.Contains("Mining Contract: Mixed for Polaris Megacorp, in 'Q Quest @ Test Hauler' (move it into the inventory to hand it in):", text);
        Assert.Contains("wants 2,000 Copper Ore, 2,500 in reach (Hold @ Test Hauler 2,500), enough;", text);
        Assert.DoesNotContain("Magnesium", text); // contracts the player doesn't have are not listed
        Assert.DoesNotContain("9999", text);
        Assert.Contains("the ore into the refinery's quest container", text);
    }

    [Fact]
    public void Mine_without_contracts()
    {
        var text = Text($$$"""{"ok":true,{{{Rules}}},"contracts":[{"token":6860,"faction":"Zirax Empire","ores":[]}],"found":{"ok":true,"tokens":[],"ores":[]}}""");
        Assert.StartsWith("The player has no mining contract", text);
    }

    [Fact]
    public void Errors_pass_through()
    {
        Assert.Equal("Nobody is online.", Text("""{"ok":false,"error":"Nobody is online."}"""));
        Assert.Equal("The player is not on this playfield right now.",
            Text("""{"ok":true,"contracts":[],"found":{"ok":false,"error":"The player is not on this playfield right now."}}"""));
    }
}
