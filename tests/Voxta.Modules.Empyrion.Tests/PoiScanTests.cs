using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class PoiScanTests
{
    private static string Render(string json) => GameView.PoiScanResult(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void One_line_per_poi_or_complex()
    {
        var text = Render("""
            {"ok":true,"playfield":"Viunean","scope":"nearby","asOf":"12:00:00 UTC","groups":[
              {"name":"Stack-15","class":"trading","distance":348,"faction":"Polaris","standing":"Neutral","turrets":4,"traders":1},
              {"name":"Placeholder Ghyst Main","class":"major faction base","distance":2350,"faction":"Ghyst","standing":"Hated","buildings":45,
               "members":"1 major faction base, 44 small faction outpost","turrets":182},
              {"name":"Sanctuary","class":"trading","distance":17,"faction":"Polaris","standing":"Neutral","buildings":12,"members":"3 trading, 9 other","traders":6,
               "tradingPlaces":[{"name":"Sanctuary","traders":4,"distance":17},{"name":"Solaris-AMP Factory","traders":1,"distance":420},{"name":"P3: Planetside Production","traders":1,"distance":1250}]},
              {"name":"Abandoned Bunker","class":"abandoned dungeon","distance":4100,"terminal":"Reactor Control","terminalPoi":"Abandoned Bunker","onlyInRange":true}],
             "moreGroups":3,"resources":2,"decorationLeftOut":1,"unknownToPlayer":40}
            """);
        Assert.Contains("Known POIs within 10 km on Viunean (as of 12:00:00 UTC), closest first:", text);
        Assert.Contains("- Stack-15: trading, Polaris, Neutral, 4 turrets, 1 trader, 350 m", text);
        Assert.Contains("- Placeholder Ghyst Main: major faction base complex of 45 buildings (1 major faction base, 44 small faction outpost), Ghyst, Hated, 182 turrets, 2.4 km", text);
        Assert.Contains("- Abandoned Bunker: abandoned dungeon, info terminal 'Reactor Control', not discovered yet, only seen, 4.1 km", text);
        Assert.Contains("trading places: Sanctuary (4 traders, 20 m); Solaris-AMP Factory (1 trader, 420 m); P3: Planetside Production (1 trader, 1.3 km)", text);
        Assert.Contains("3 more further away.", text);
        Assert.Contains("Also 2 asteroids/resource deposits.", text);
        Assert.DoesNotContain("unknownToPlayer", text);
    }

    [Fact]
    public void Nearby_line_for_the_live_context()
    {
        var line = GameView.NearbyLine(JsonDocument.Parse("""
            {"ok":true,"groups":[
              {"name":"Sanctuary","class":"trading","distance":17,"faction":"Polaris","standing":"Neutral","turrets":8,
               "tradingPlaces":[{"name":"P3: Planetside Production","traders":1,"distance":17},{"name":"Solaris-AMP Factory","traders":1,"distance":410}]},
              {"name":"Small Wreckage","class":"wreck/salvage","distance":570},
              {"name":"Stack-15","class":"trading","distance":3083,"faction":"Pirates","standing":"Unfriendly","turrets":4},
              {"name":"Drone Supply Port","class":"small faction outpost","distance":3640,"faction":"House Ghyst","standing":"Hated","turrets":4},
              {"name":"Smuggler Hideout","class":"civilian","distance":5300}]}
            """).RootElement);
        Assert.Equal("Nearby: Sanctuary (Polaris trading, 8 turrets, traders at P3: Planetside Production here, Solaris-AMP Factory 400 m); " +
                     "Stack-15 (Pirates trading, Unfriendly, 3 km); Drone Supply Port (House Ghyst outpost, Hated, 3.5 km).", line);
        Assert.Null(GameView.NearbyLine(JsonDocument.Parse("""{"ok":true,"groups":[{"name":"W","class":"wreck/salvage","distance":5}]}""").RootElement));
    }

    [Fact]
    public void Empty_and_error()
    {
        Assert.Equal("No known POIs on Akua Sector.", Render("""{"ok":true,"playfield":"Akua Sector","scope":"all","groups":[],"resources":0}"""));
        Assert.Equal("Nobody is online.", Render("""{"error":"Nobody is online."}"""));
    }
}

public class SystemAndTradeTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void System_lists_planets_gas_giants_and_moons()
    {
        var text = GameView.SystemInfoResult(J("""
            {"ok":true,"system":"Phaealla","starClass":"K4","bodies":[
              {"name":"Orculia","kind":"star","type":"K4"},
              {"name":"Viunean","kind":"planet","type":"Stormy Desert","here":true},
              {"name":"Phaealla","kind":"gas giant","type":"Purple gas giant"},
              {"name":"Phaealla Moon","kind":"moon","type":"Lava Moon"}]}
            """));
        Assert.Equal("System Phaealla, star class K4.\nPlanets: Viunean (Stormy Desert) - the player is here.\nGas giants: Phaealla (Purple gas giant).\nMoons: Phaealla Moon (Lava Moon).", text);
    }

    [Fact]
    public void Planet_details()
    {
        var text = GameView.SystemInfoResult(J("""
            {"ok":true,"system":"Orculia","starClass":"K4","bodies":[{"name":"Viunean","kind":"planet","type":"Stormy Desert","difficulty":"Medium","here":true}],
             "details":{"name":"Viunean","kind":"planet","type":"Stormy Desert","difficulty":"Medium","description":"A desert planet with a global dust storm.",
               "gravityG":0.9,"atmosphere":0.73,"oxygenPercent":16,"breathable":false,"tempMin":10,"tempMax":64,"tempNight":54,"radiation":2.9,
               "effects":["Sensor Interference: POI detection range reduced."],"resources":["Cobalt","Copper"]}}
            """));
        Assert.Contains("Planets: Viunean (Stormy Desert, Medium) - the player is here.", text);
        Assert.Contains("Viunean (Stormy Desert planet, difficulty Medium):\nA desert planet with a global dust storm.\nSensor Interference: POI detection range reduced.", text);
        Assert.Contains("gravity 0.9 g, atmosphere 0.73, oxygen 16 %, not breathable, temperature 10 to 64 °C, night 54 °C, radiation 2.9.", text);
        Assert.Contains("Resources: Cobalt, Copper.", text);
    }

    [Fact]
    public void Other_known_system_and_list()
    {
        var other = GameView.SystemInfoResult(J("""
            {"ok":true,"system":"Rubathra","starClass":"G2","ly":4.2,"byPlayer":false,"bodies":[
              {"name":"Rubathra I","kind":"planet","type":"Arid","entered":true},{"name":"Rubathra II","kind":"planet","type":"Temperate","entered":false}]}
            """));
        Assert.Equal("System Rubathra, star class G2, 4.2 LY away, known through the faction.\nPlanets: Rubathra I (Arid), visited, Rubathra II (Temperate).", other);
        var list = GameView.SystemInfoResult(J("""
            {"ok":true,"knownSystems":2,"systems":[{"system":"Phaealla","starClass":"K4","ly":0,"planets":3,"moons":10,"byPlayer":true},
              {"system":"Rubathra","starClass":"G2","ly":4.2,"planets":2,"moons":1,"byPlayer":false}]}
            """));
        Assert.Equal("Systems known to the player or their faction (2), closest first: Phaealla (K4, here, 3 planets, 10 moons); Rubathra (G2, 4.2 LY, 2 planets, 1 moons, known through the faction).", list);
    }

    [Fact]
    public void Trade_groups_items_by_price_tier()
    {
        var text = GameView.PoiTradeResult(J("""
            {"ok":true,"poi":"Solaris-AMP Factory","playfield":"Viunean","traders":["Manufacturing Power"],
             "sellsCount":62,"sells":[{"item":"Computer","tier":"about market"},{"item":"Reactor Core","tier":"above market"},
               {"item":"Steel Plate","tier":"about market"},{"item":"Odd Thing"},{"item":"Canned Meat","tier":"below market"}],
             "sellsEveryday":40,"buysCount":1,"buys":[{"item":"Gold Ingot","tier":"below market"}]}
            """));
        Assert.Contains("and 57 more. Plus 40 everyday items (food, cheap components, building blocks), not listed.", text);
        Assert.Contains("Solaris-AMP Factory on Viunean, traders: Manufacturing Power.", text);
        Assert.Contains("Sells (62) - below market: Canned Meat; about market: Computer, Steel Plate; above market: Reactor Core; no market price: Odd Thing; and 57 more.", text);
        Assert.Contains("Buys (1) - pays below market: Gold Ingot.", text);
    }

    [Fact]
    public void Trade_detail_has_tier_and_credits()
    {
        var text = GameView.PoiTradeResult(J("""
            {"ok":true,"poi":"Stack-15","traders":["X"],"sellsCount":1,"sells":[{"item":"Computer","tier":"about market","credits":"500-625"}],
             "buysCount":0,"buys":[]}
            """));
        Assert.Contains("Sells (1): Computer (about market, 500-625 credits).", text);
        Assert.DoesNotContain("stock 1", text);
        Assert.DoesNotContain("credits, stock", text);
        Assert.Contains("Buys: nothing.", text);
    }

    [Fact]
    public void Trade_general_answer_and_withheld_detail()
    {
        var text = GameView.PoiTradeResult(J("""
            {"ok":true,"poi":"Solaris-AMP Factory","general":true,"visited":false,"detailWithheld":true,"traders":["Manufacturing Power"],
             "sellsCount":1,"sells":[{"item":"Reactor Core","tier":"above market"}],"buysCount":0,"buysEveryday":3,"buys":[]}
            """));
        Assert.StartsWith("No Solaris-AMP Factory is known to the player; in general, one has the traders Manufacturing Power and usually trades:", text);
        Assert.Contains("Buys: only everyday items. Plus 3 everyday items", text);
        Assert.Contains("Exact prices are only known once the player has been there.", text);
    }
}

public class VisitingTests
{
    [Fact]
    public void Inside_a_poi_is_not_the_players_ship()
    {
        var state = new GameState { Player = "Player1", Playfield = "Viunean", VisitingName = "P3: Planetside Production", VisitingType = "BA", VisitingOwner = "Polaris" };
        var text = state.ToContext();
        Assert.Contains("On foot inside the base 'P3: Planetside Production' (Polaris), not the player's.", text);
        Assert.DoesNotContain("Aboard", text);
        Assert.DoesNotContain("tanks", text);
    }
}

public class OwnStructureTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Own_ships_show_in_scan_and_nearby_line()
    {
        var r = J("""
            {"ok":true,"playfield":"Viunean","scope":"nearby","asOf":"12:00 UTC","groups":[{"name":"P3","class":"trading","distance":10,"faction":"Polaris"}],
             "yourStructures":[{"name":"Hammer","type":"SV","distance":84,"docked":0},{"name":"Ark","type":"CV","distance":1430,"docked":2}]}
            """);
        Assert.Contains("The player's own ships and bases within 2 km: 'Hammer' (SV, 80 m); 'Ark' (CV, 1.4 km, 2 docked) (details: empyrion_get_structure).", GameView.PoiScanResult(r));
        Assert.Equal("Nearby: P3 (Polaris trading, here). Own ships/bases nearby: 'Hammer' (SV, 80 m); 'Ark' (CV, 1.5 km, 2 docked).", GameView.NearbyLine(r));
        Assert.Equal("Own ships/bases nearby: 'Hammer' (SV, 290 m).",
            GameView.NearbyLine(J("""{"ok":true,"groups":[],"yourStructures":[{"name":"Hammer","type":"SV","distance":290,"docked":0}]}""")));
    }

    [Fact]
    public void Structure_details()
    {
        var text = GameView.StructureInfoResult(J("""
            {"ok":true,"structure":{"name":"Ark","type":"CV","sizeClass":5,"blocks":4200,"mass":250000,"powered":true,"shieldActive":false,
              "fuel":{"content":800,"capacity":1000},"distance":1430,"containerCount":12,"docked":[{"name":"Hammer","type":"SV"}]}}
            """));
        Assert.Contains("CV 'Ark' (size class 5, 4200 blocks, mass 250000 kg).", text);
        Assert.Contains("Pilot: nobody. Power: on. Shield: inactive.", text);
        Assert.Contains("Fuel tank: 80% (800 / 1000).", text);
        Assert.Contains("Distance from the player: 1.4 km.", text);
        Assert.Contains("Docked to it: 'Hammer' (SV).", text);
        Assert.Contains("12 containers (contents: empyrion_get_current_ship with structure 'Ark'", text);
        Assert.Equal("No own structure called 'X' on Viunean.", GameView.StructureInfoResult(J("""{"error":"No own structure called 'X' on Viunean."}""")));
    }
}

public class ActiveStructureTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Inside_a_poi_with_the_carrier_outside_as_active()
    {
        var text = new GameState
        {
            Player = "Player1", Playfield = "Viunean", VisitingName = "P3: Planetside Production", VisitingType = "BA", VisitingOwner = "Polaris",
            ActiveName = "Test Hauler", ActiveType = "CV", ActiveHow = "last aboard", ActiveDistance = 78,
            ShipFuel = 75, ShipFuelMax = 100,
        }.ToContext();
        Assert.Contains("On foot inside the base 'P3: Planetside Production' (Polaris), not the player's.", text);
        Assert.Contains("Active structure (containers, transfers, crafting): CV 'Test Hauler' (last aboard, 80 m away); tanks: fuel 75%", text);
        Assert.DoesNotContain("Aboard", text);
    }

    [Fact]
    public void Aboard_one_ship_with_the_base_set_active()
    {
        var text = new GameState
        {
            Player = "Player1", Playfield = "Viunean", ShipName = "Hammer", ShipType = "SV",
            ActiveName = "Home", ActiveType = "BA", ActiveHow = "pinned", ActiveDistance = 12, ShipFuel = 10, ShipFuelMax = 100,
        }.ToContext();
        Assert.Contains("Aboard SV 'Hammer'\n", text.Replace("\r", ""));
        Assert.Contains("Active structure (containers, transfers, crafting): BA 'Home' (set by the player, 10 m away); tanks: fuel 10%", text);
    }

    [Fact]
    public void Working_from_line()
    {
        Assert.Equal("Working from: CV 'Test Hauler' (last aboard, 80 m away).",
            GameView.WorkingFrom(J("""{"workingFrom":{"name":"Test Hauler","type":"CV","how":"last aboard","playerOn":false,"distance":78},"ok":true}""")));
        Assert.Equal("Working from: SV 'Hammer' (piloting it).",
            GameView.WorkingFrom(J("""{"workingFrom":{"name":"Hammer","type":"SV","how":"pilot","playerOn":true,"distance":0}}""")));
        Assert.Equal("Working from: no own ship or base (none aboard or within transfer range). Note: 'Home', set as active structure, is out of transfer range (1400 m); back to automatic.",
            GameView.WorkingFrom(J("""{"workingFrom":{"note":"'Home', set as active structure, is out of transfer range (1400 m); back to automatic"},"ok":false}""")));
        Assert.Null(GameView.WorkingFrom(J("""{"ok":true}""")));
        Assert.Equal("Active structure set. Working from: BA 'Home' (set by the player, 120 m away).",
            GameView.SetActiveResult(J("""{"ok":true,"mode":"set","active":{"name":"Home","type":"BA","how":"pinned","playerOn":false,"distance":118}}""")));
    }
}

public class PoiTerminalTests
{
    [Fact]
    public void Terminal_screens_with_options()
    {
        var text = GameView.PoiTerminalResult(JsonDocument.Parse("""
            {"ok":true,"poi":"Abandoned Bunker","playfield":"Viunean","terminal":"Reactor Control","near":true,"visited":false,
             "pages":[{"text":"< REACTOR CONTROL >\nTemperature: high","options":["Shut Down Reactor","Leave"]},{"text":"ACCESS DENIED","options":[]}]}
            """).RootElement);
        Assert.StartsWith("Info terminal 'Reactor Control' at Abandoned Bunker (Viunean).", text);
        Assert.Contains("[screen 1]\n< REACTOR CONTROL >\nTemperature: high\n(options: Shut Down Reactor | Leave)", text);
        Assert.Contains("[screen 2]\nACCESS DENIED", text);
        Assert.Equal("The player has to be at 'X' (within 2 km) or have visited it to read its terminal.",
            GameView.PoiTerminalResult(JsonDocument.Parse("""{"ok":false,"error":"The player has to be at 'X' (within 2 km) or have visited it to read its terminal."}""").RootElement));
    }
}

public class PoiScanLive(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Opt-in: POI_SCAN_JSON=&lt;file&gt; renders a real /pois answer.</summary>
    [Fact]
    public void Render_real_answer()
    {
        var f = System.Environment.GetEnvironmentVariable("POI_SCAN_JSON");
        if (string.IsNullOrEmpty(f)) return;
        var kind = System.Environment.GetEnvironmentVariable("RENDER_KIND") ?? "pois";
        var r = JsonDocument.Parse(System.IO.File.ReadAllText(f)).RootElement;
        output.WriteLine(kind switch
        {
            "system" => GameView.SystemInfoResult(r),
            "trade" => GameView.PoiTradeResult(r),
            "wtb" => GameView.WhereToBuyResult(r),
            "struct" => GameView.StructureInfoResult(r),
            "nearby" => GameView.NearbyLine(r) ?? "(none)",
            _ => GameView.PoiScanResult(r),
        });
    }
}
