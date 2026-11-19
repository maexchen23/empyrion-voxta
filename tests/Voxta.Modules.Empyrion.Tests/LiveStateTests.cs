using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

/// <summary>Inventory tool, context summary and event detection from bridge /status JSON.</summary>
public class LiveStateTests
{
    // Shaped like the bridge's /status (dedi player list, API1 player info, playfield snapshot).
    private const string Status = """
    {
      "players": [{ "entityId": 4080, "name": "Player1", "steamId": "765", "playfield": "Akua", "online": true }],
      "api1": { "attached": true, "players": [{
        "entityId": 4080, "name": "Player1", "steamId": "765", "playfield": "Akua", "pos": [139, 101, 67],
        "health": 500, "healthMax": 500, "oxygen": 450, "oxygenMax": 600, "food": 480, "foodMax": 500,
        "radiation": 0, "radiationMax": 100, "credits": 1200,
        "toolbar": [ { "id": 1, "name": "Pistol", "count": 1, "slot": 0 }, { "id": 2, "name": "PistolAmmo", "count": 120, "slot": 1 } ],
        "bag": [ { "id": 2, "name": "PistolAmmo", "count": 200, "slot": 0 }, { "id": 3, "name": "OxygenBottleSmall", "count": 4, "slot": 1 } ],
        "factory": { "blueprint": "Test Corvette", "remainingS": 95 }
      }]},
      "playfields": [{ "name": "Akua", "snapshot": {
        "playfield": "Akua", "type": "Planet", "planetType": "TemperateStarter", "system": "Alpha", "pvp": false,
        "players": [{ "id": 4080, "name": "Player1", "isPilot": true, "structure": {
          "name": "Test Corvette", "type": "SV", "powered": true, "shieldActive": false,
          "fuel": { "content": 1500, "capacity": 2000 }, "oxygen": { "content": 0, "capacity": 750 },
          "pentaxid": { "content": 0, "capacity": 200 }, "containers": [] } }]
      }}]
    }
    """;

    private static GameView View(string json = Status)
    {
        var root = JsonDocument.Parse(json).RootElement;
        return GameView.Create(root, null, out var error) ?? throw new Exception(error);
    }

    [Fact]
    public void Inventory_lists_toolbar_and_bag_with_totals()
    {
        var text = View().PlayerInventory(null);

        Assert.Contains("Toolbar: Pistol x1, PistolAmmo x120", text);
        Assert.Contains("Bag: PistolAmmo x200, OxygenBottleSmall x4", text);
        Assert.Contains("PistolAmmo x320", text);
    }

    [Fact]
    public void Inventory_filter_matches_part_of_name()
    {
        var text = View().PlayerInventory("oxygen");

        Assert.Contains("OxygenBottleSmall x4", text);
        Assert.DoesNotContain("Pistol", text);
    }

    [Fact]
    public void Inventory_filter_without_match_says_so()
    {
        Assert.Contains("nothing matching 'gold'", View().PlayerInventory("gold"));
    }

    [Fact]
    public void Context_summarises_location_vitals_ship_and_factory()
    {
        var ctx = View().State().ToContext();

        Assert.Contains("Location: Akua (Planet), system Alpha", ctx);
        Assert.Contains("health 100%", ctx);
        Assert.Contains("suit oxygen 75%", ctx);
        Assert.Contains("Piloting SV 'Test Corvette'", ctx);
        Assert.Contains("fuel 75%, O2 0%, pentaxid 0%", ctx);
        Assert.Contains("Factory: building 'Test Corvette', 1m 35s left", ctx);
    }

    private static GameState Base => View().State();

    [Fact]
    public void First_poll_reports_nothing()
    {
        Assert.Empty(GameEvents.Diff(null, Base, firstPoll: true));
    }

    [Fact]
    public void Unchanged_state_reports_nothing()
    {
        Assert.Empty(GameEvents.Diff(Base, Base));
    }

    [Fact]
    public void Already_empty_tank_is_not_reported_again()
    {
        // Ship O2 is 0 in both polls: no event.
        Assert.DoesNotContain(GameEvents.Diff(Base, Base with { ShipFuel = 1499 }), e => e.Name.Contains("ShipOxygen"));
    }

    [Fact]
    public void Playfield_change_is_important()
    {
        var next = Base with { Playfield = "Akua Moon", PlayfieldType = "Moon" };

        var e = Assert.Single(GameEvents.Diff(Base, next));
        Assert.Equal("PlayfieldChanged", e.Name);
        Assert.True(e.Important);
        Assert.Contains("Arrived at Akua Moon (Moon), coming from Akua", e.Description);
    }

    [Fact]
    public void Pvp_zone_entry_is_mentioned()
    {
        var next = Base with { Playfield = "Zirax Sector", Pvp = true };

        Assert.Contains("PvP zone", Assert.Single(GameEvents.Diff(Base, next)).Description);
    }

    [Fact]
    public void Low_then_critical_oxygen()
    {
        var low = Base with { Oxygen = 140 };      // 23%
        var critical = Base with { Oxygen = 50 };  // 8%

        Assert.Equal("LowOxygen", Assert.Single(GameEvents.Diff(Base, low)).Name);
        Assert.Equal("CriticalOxygen", Assert.Single(GameEvents.Diff(low, critical)).Name);
        Assert.Empty(GameEvents.Diff(critical, critical with { Oxygen = 40 }));
    }

    [Fact]
    public void Low_fuel_is_important_and_empty_fuel_too()
    {
        var low = Base with { ShipFuel = 250 };    // 12.5%
        var empty = Base with { ShipFuel = 0 };

        var lowEvent = Assert.Single(GameEvents.Diff(Base, low));
        Assert.Equal("LowFuel", lowEvent.Name);
        Assert.True(lowEvent.Important);
        Assert.Equal("FuelEmpty", Assert.Single(GameEvents.Diff(low, empty)).Name);
    }

    [Fact]
    public void Leaving_and_boarding_ship_are_silent()
    {
        var onFoot = Base with { ShipName = null, ShipType = null, IsPilot = false };

        var left = Assert.Single(GameEvents.Diff(Base, onFoot));
        Assert.Equal("LeftShip", left.Name);
        Assert.False(left.Important);
        Assert.Equal("BoardedShip", Assert.Single(GameEvents.Diff(onFoot, Base)).Name);
    }

    [Fact]
    public void Tank_changes_while_switching_ships_are_ignored()
    {
        var otherShip = Base with { ShipName = "Hauler", ShipFuel = 10 };

        var e = Assert.Single(GameEvents.Diff(Base, otherShip));
        Assert.Equal("ChangedShip", e.Name);
    }

    [Fact]
    public void Join_and_leave()
    {
        Assert.Equal("PlayerJoined", Assert.Single(GameEvents.Diff(null, Base)).Name);
        Assert.Equal("PlayerLeft", Assert.Single(GameEvents.Diff(Base, null)).Name);
    }

    [Fact]
    public void Blueprint_ready()
    {
        var done = Base with { FactoryRemainingS = 0 };

        var e = Assert.Single(GameEvents.Diff(Base, done));
        Assert.Equal("BlueprintReady", e.Name);
        Assert.True(e.Important);
    }

    [Fact]
    public void Shield_down_is_important()
    {
        var up = Base with { ShieldActive = true };

        Assert.Equal("ShieldDown", Assert.Single(GameEvents.Diff(up, Base)).Name);
    }

    [Fact]
    public void Status_says_standing_on_board()
    {
        var json = Status.Replace("\"isPilot\": true,", "\"isPilot\": false, \"aboard\": \"standing\",");
        Assert.Contains("Standing on board the SV 'Test Corvette'", View(json).PlayerStatus());
    }
}
