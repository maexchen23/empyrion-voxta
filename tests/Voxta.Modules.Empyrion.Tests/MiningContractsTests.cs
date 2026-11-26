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
        Assert.Contains("the ore into the quest container there", text);
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

public class ContractAdviceTests
{
    static string Text(string json) => GameView.MiningContractsResult(JsonDocument.Parse(json).RootElement);

    // two Colonist contracts (Iron common, Ice rare), one Zirax (Magnesium), one Polaris (Titanium)
    const string Contracts = """
        "contracts":[
          {"token":6801,"faction":"Colonists","credits":400000,"reputation":500,"weight":0.5,"ores":[{"item":"IronOre","name":"Iron Ore","count":4000}]},
          {"token":6804,"faction":"Colonists","credits":300000,"reputation":500,"weight":0.25,"ores":[{"item":"IceOre","name":"Ice","count":3000}]},
          {"token":6860,"faction":"Zirax Empire","credits":800000,"reputation":500,"weight":0.5,"ores":[{"item":"MagnesiumOre","name":"Magnesium Ore","count":8000}]},
          {"token":6821,"faction":"Polaris Megacorp","credits":720000,"reputation":500,"weight":0.5,"ores":[{"item":"TitanOre","name":"Titanium Ore","count":8000}]}]
        """;

    const string Standings = """
        "standings":[{"faction":"Colonists","standing":20000,"tier":"Friendly"},{"faction":"Zirax Empire","standing":3000,"tier":"Hated"},
                     {"faction":"Polaris Megacorp","standing":30000,"tier":"Honored"}]
        """;

    static string Json(string ores, string offer = """{"faction":"Polaris","standing":30000,"tier":"Honored","choiceTier":"Honored","canChoose":true}""") =>
        $$$"""{"ok":true,"advise":true,{{{Contracts}}},{{{Standings}}},"offer":{{{offer}}},"found":{"ok":true,"tokens":[],"ores":[{{{ores}}}]}}""";

    [Fact]
    public void Reputation_prefers_factions_close_to_honored()
    {
        var text = Text(Json(""));
        Assert.Contains("- Colonists: Friendly (20,000), about 9 contracts to Honored.", text);
        Assert.Contains("- Zirax Empire: Hated (3,000)", text);
        Assert.True(text.IndexOf("Colonists: Friendly", StringComparison.Ordinal) < text.IndexOf("Zirax Empire: Hated", StringComparison.Ordinal));
        Assert.Contains("(Already Honored: Polaris Megacorp.)", text);
        Assert.Contains("none of the contract ores is in reach", text);
    }

    [Fact]
    public void Safe_bet_needs_20_percent_spare_on_every_contract()
    {
        var text = Text(Json("""{"item":"MagnesiumOre","where":"I Ores","count":9600},{"item":"IronOre","where":"I Ores","count":5000}"""));
        Assert.Contains("- Safe bet: Zirax Empire", text);
        // Colonists: Iron (weight 0.5) is covered, Ice (0.25) isn't: 67 %
        // Colonists: Iron (weight 0.5) is covered, Ice (0.25) isn't: 67 % of the ore on average, 67 % chance
        Assert.Contains("- Colonists: on average 67% of the drawn contract's ore is on board; 67% chance it can be handed in right away (covered: Iron Ore)", text);
        Assert.DoesNotContain("Polaris Megacorp: 0%", text);
    }

    [Fact]
    public void Just_short_of_the_spare_is_no_safe_bet_but_ranks_first()
    {
        var text = Text(Json("""{"item":"MagnesiumOre","where":"I Ores","count":8500},{"item":"TitanOre","where":"Hold","count":2000}"""));
        Assert.DoesNotContain("Safe bet", text);
        Assert.Contains("- Zirax Empire: on average 100% of the drawn contract's ore is on board; 100% chance", text);
        Assert.Contains("- Polaris Megacorp: on average 25% of the drawn contract's ore is on board; 0% chance it can be handed in right away;", text);
    }

    static string Contract(int token, string faction, string ore, int count) =>
        $$$"""{"token":{{{token}}},"faction":"{{{faction}}}","credits":100000,"reputation":500,"weight":0.5,"ores":[{"item":"{{{ore}}}","name":"{{{ore}}}","count":{{{count}}}}]}""";

    [Fact]
    public void Score_is_the_share_of_ore_on_board_not_only_full_contracts()
    {
        // A: 3 of 8 ores at 100 %, 2 at 80 %, 3 at 0 % = 57.5 % (57.4999… in floating point); B: 1 at 100 %, 7 at 20 % = 30 %
        var a = Enumerable.Range(0, 8).Select(i => Contract(6800 + i, "Alpha", "A" + i, 1000));
        var b = Enumerable.Range(0, 8).Select(i => Contract(6820 + i, "Beta", "B" + i, 1000));
        var have = new[] { ("A0", 1000), ("A1", 1000), ("A2", 1000), ("A3", 800), ("A4", 800), ("B0", 1000) }
            .Concat(Enumerable.Range(1, 7).Select(i => ("B" + i, 200)))
            .Select(h => $$$"""{"item":"{{{h.Item1}}}","where":"Hold","count":{{{h.Item2}}}}""");
        var text = Text($$$"""
            {"ok":true,"advise":true,"contracts":[{{{string.Join(",", a.Concat(b))}}}],"standings":[],
             "found":{"ok":true,"tokens":[],"ores":[{{{string.Join(",", have)}}}]}}
            """);
        Assert.Contains("- Alpha: on average 57% of the drawn contract's ore is on board; 38% chance", text);
        Assert.Contains("- Beta: on average 30% of the drawn contract's ore is on board; 13% chance", text);
        Assert.True(text.IndexOf("- Alpha", StringComparison.Ordinal) < text.IndexOf("- Beta", StringComparison.Ordinal));
    }

    [Fact]
    public void Multi_ore_contract_multiplies_the_shares()
    {
        var c = """{"token":6829,"faction":"Polaris Megacorp","credits":350000,"reputation":500,"ores":[{"item":"IronOre","name":"Iron Ore","count":4000},{"item":"CopperOre","name":"Copper Ore","count":2000}]}""";
        var text = Text($$$"""
            {"ok":true,"advise":true,"contracts":[{{{c}}}],"standings":[],
             "found":{"ok":true,"tokens":[],"ores":[{"item":"IronOre","where":"Hold","count":2000},{"item":"CopperOre","where":"Hold","count":1000}]}}
            """);
        Assert.Contains("on average 25% of the drawn contract's ore is on board", text); // 50 % x 50 %
    }

    [Fact]
    public void Without_the_faction_choice_take_any()
    {
        var text = Text(Json("", """{"faction":"Polaris","standing":20000,"tier":"Friendly","choiceTier":"Honored","canChoose":false}"""));
        Assert.StartsWith("The player can't choose the contract's faction yet: that needs Honored with Polaris (now Friendly, 20,000).", text);
        Assert.Contains("just taking whatever it offers", text);
    }
}

public class RefineryCooldownTests
{
    static string Text(string json) => GameView.MiningContractsResult(JsonDocument.Parse(json).RootElement);
    static string Cool(string json) => GameView.CooldownsResult(JsonDocument.Parse(json).RootElement);

    // game time 710,000: A waits 1 h 52 min, B is ready in 30 s (far), C is ready (near), D is ready but 150 LY away
    const string Rows = """
        [{"key":"1--8/168/-3-Sector A","value":716703,"where":"Orbital Refinery (Sector A)","ly":3.2},
         {"key":"2--8/168/-3-Sector B","value":710030,"where":"Orbital Refinery (Sector B)","ly":40},
         {"key":"3--8/168/-3-Sector C","value":700000,"where":"Orbital Refinery (Sector C)","ly":12.5},
         {"key":"4--8/168/-3-Sector D","value":600000,"where":"Orbital Refinery (Sector D)","ly":150}]
        """;

    [Fact]
    public void Mining_answer_names_the_nearest_ready_refinery_within_100_ly()
    {
        var text = Text($$$"""{"ok":true,"contracts":[],"gameTimeS":710000,"offers":{{{Rows}}},"found":{"ok":true,"tokens":[],"ores":[]}}""");
        Assert.Contains("Refinery contract cooldowns within 100 LY", text);
        Assert.Contains("- Nearest ready: Orbital Refinery (Sector C), 12.5 LY.", text);
        Assert.Contains("- Also ready: Orbital Refinery (Sector B), 40 LY.", text); // ready within 10 minutes counts
        Assert.Contains("- Orbital Refinery (Sector A), 3.2 LY: next contract in 1 h 52 min.", text);
        Assert.DoesNotContain("Sector D", text);
        Assert.Contains("(1 known refinery farther than 100 LY left out.)", text);
    }

    [Fact]
    public void Discovered_refinery_without_a_contract_is_ready()
    {
        var text = Cool("""
            {"ok":true,"gameTimeS":710000,"missions":[],
             "refineries":[{"key":"1--8/168/-3-A","value":716703,"where":"Orbital Refinery (A)","ly":3},
                           {"key":"poi:2","value":0,"where":"Orbital Refinery (B)","ly":20,"discovered":true}]}
            """);
        Assert.Contains("- Nearest ready: Orbital Refinery (B), 20 LY.", text);
        Assert.Contains("- Orbital Refinery (A), 3 LY: next contract in 1 h 52 min.", text);
    }

    [Fact]
    public void Nothing_ready_soon_says_so()
    {
        var text = Cool("""{"ok":true,"gameTimeS":710000,"refineries":[{"key":"k","value":716703,"where":"Orbital Refinery (Sector A)","ly":3.2}],"missions":[]}""");
        Assert.Contains("- None ready within 10 minutes.", text);
        Assert.Contains("next contract in 1 h 52 min", text);
    }

    [Fact]
    public void Advice_without_choice_still_lists_the_cooldowns()
    {
        var text = Text($$$"""
            {"ok":true,"advise":true,"contracts":[],"standings":[],"gameTimeS":710000,"offers":{{{Rows}}},
             "offer":{"faction":"Polaris","standing":16000,"tier":"Neutral","choiceTier":"Honored","canChoose":false},
             "found":{"ok":true,"tokens":[],"ores":[]}}
            """);
        Assert.StartsWith("The player can't choose", text);
        Assert.Contains("Nearest ready: Orbital Refinery (Sector C)", text);
    }

    [Fact]
    public void Unknown_game_time_shows_nothing()
    {
        var text = Text("""{"ok":true,"contracts":[],"offers":[{"key":"k","value":716703,"where":"X"}],"found":{"ok":true,"tokens":[],"ores":[]}}""");
        Assert.DoesNotContain("cooldown", text);
        Assert.StartsWith("The server's game time is unknown", Cool("""{"ok":true,"missions":[]}"""));
    }

    [Fact]
    public void Research_cooldown_and_vessels_already_used()
    {
        var text = Cool("""
            {"ok":true,"gameTimeS":710000,"refineries":[],
             "missions":[{"chapter":"eden_pda_UuuGC","title":"Research Unlock Points","delayS":10800,"completedAt":705000,"readyAt":715800,
                          "perPoi":[{"key":"63684-Avin","value":1,"where":"UCH Research Vessel (Avin)","ly":8},
                                    {"key":"71204-X","value":0,"where":"UCH Research Vessel (X)","ly":2}]}]}
            """);
        Assert.Contains("no refinery has given the player a contract yet", text);
        Assert.Contains("Research Unlock Points (repeatable every 3 h): next run in 1 h 37 min.", text);
        Assert.Contains("already done at: UCH Research Vessel (Avin), 8 LY.", text);
        Assert.DoesNotContain("(X)", text); // visited, but not done there
    }

    [Fact]
    public void Research_without_a_recorded_run()
    {
        var text = Cool("""
            {"ok":true,"gameTimeS":710000,"refineries":[],
             "missions":[{"chapter":"c","title":"Research Unlock Points","delayS":10800,"perPoi":[{"key":"1-A","value":1,"where":"UCH Research Vessel (A)"}]}]}
            """);
        Assert.Contains("last run not recorded", text);
        Assert.Contains("already done at: UCH Research Vessel (A).", text);
    }
}

public class HandInPointTests
{
    static string Text(string json) => GameView.MiningContractsResult(JsonDocument.Parse(json).RootElement);

    const string Contract = """
        "contracts":[{"token":6826,"name":"Mining Contract: Iron","faction":"Polaris Megacorp","credits":1000000,"reputation":500,
                      "ores":[{"item":"IronOre","name":"Iron Ore","count":20000}]}]
        """;

    [Fact]
    public void Contract_check_names_the_nearest_hand_in_points()
    {
        var text = Text($$$"""
            {"ok":true,{{{Contract}}},
             "handIn":[{"where":"Orbital Refinery (A)","ly":0},{"where":"Industrial Command Vessel (B)","ly":4.5},
                       {"where":"Distribution Center (C)","ly":9},{"where":"Orbital Refinery (D)","ly":30}],
             "found":{"ok":true,"tokens":[{"token":6826,"where":"player bag","count":1,"inventory":true}],"ores":[]}}
            """);
        Assert.Contains("no cooldown for handing in", text);
        Assert.Contains("Nearest hand-in points the player found: Orbital Refinery (A), this system; Industrial Command Vessel (B), 4.5 LY; Distribution Center (C), 9 LY.", text);
        Assert.DoesNotContain("(D)", text);
    }

    [Fact]
    public void None_found_says_where_to_look()
    {
        var text = Text($$$"""{"ok":true,{{{Contract}}},"handIn":[],"found":{"ok":true,"tokens":[{"token":6826,"where":"player bag","count":1,"inventory":true}],"ores":[]}}""");
        Assert.Contains("No hand-in point found yet", text);
    }
}
