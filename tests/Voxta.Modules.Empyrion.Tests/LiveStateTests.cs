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
        Assert.Contains("Warped aboard the", e.Description);
        Assert.Contains("to Akua Moon (Moon), coming from Akua", e.Description);
    }

    [Fact]
    public void Playfield_change_on_foot_is_an_arrival()
    {
        var onFoot = Base with { ShipName = null, ShipType = null, IsPilot = false };
        var e = Assert.Single(GameEvents.Diff(onFoot, onFoot with { Playfield = "Akua Moon" }));
        Assert.StartsWith("Arrived at Akua Moon", e.Description);
    }

    [Fact]
    public void Warp_gap_without_a_ship_is_not_leaving_it()
    {
        var w = new WarpSmoother();
        var t = DateTimeOffset.UtcNow;
        var gap = Base with { ShipName = null, ShipType = null, IsPilot = false, Playfield = "Akua Moon" };
        var prev = w.Apply(Base, t);
        var during = w.Apply(gap, t.AddSeconds(5));
        var after = w.Apply(Base with { Playfield = "Akua Moon" }, t.AddSeconds(12));

        var events = GameEvents.Diff(prev, during).Concat(GameEvents.Diff(during, after)).ToList();
        Assert.Equal("PlayfieldChanged", Assert.Single(events).Name);
        Assert.Contains("Warped aboard", events[0].Description);
    }

    [Fact]
    public void Leaving_the_ship_on_the_same_playfield_shows_at_once()
    {
        var w = new WarpSmoother();
        var t = DateTimeOffset.UtcNow;
        var onFoot = Base with { ShipName = null, ShipType = null, IsPilot = false };
        w.Apply(Base, t);
        Assert.False(w.Apply(onFoot, t.AddSeconds(2))!.InShip);
    }

    [Fact]
    public void Leaving_the_pilot_seat_and_standing_in_the_ship_is_no_exit()
    {
        var w = new WarpSmoother();
        var t = DateTimeOffset.UtcNow;
        var piloting = Base with { IsPilot = true };
        var standingOnIt = Base with { IsPilot = false, Standing = true };
        var aboard = Base with { IsPilot = false };
        var a = w.Apply(piloting, t);
        var b = w.Apply(standingOnIt, t.AddSeconds(2));
        var c = w.Apply(aboard, t.AddSeconds(4));

        Assert.True(b!.InShip);
        Assert.DoesNotContain(GameEvents.Diff(a, b).Concat(GameEvents.Diff(b, c)), e => e.Name is "LeftStructure" or "EnteredStructure" or "ChangedShip");
        Assert.Contains(GameEvents.Diff(a, b), e => e.Name == "LeftPilotSeat");
    }

    [Fact]
    public void Shield_down_is_reported_but_not_when_docking()
    {
        var shielded = Base with { ShieldActive = true };
        Assert.Contains(GameEvents.Diff(shielded, shielded with { ShieldActive = false }), e => e.Name == "ShieldDown");
        Assert.DoesNotContain(GameEvents.Diff(shielded, shielded with { ShieldActive = false, DockedTo = "Test Hauler" }), e => e.Name == "ShieldDown");
        var docked = shielded with { DockedTo = "Test Hauler" };
        Assert.DoesNotContain(GameEvents.Diff(docked, docked with { ShieldActive = false }), e => e.Name == "ShieldDown");
    }

    [Fact]
    public void Pilot_seat_changes_aboard_are_quiet_events()
    {
        var w = new WarpSmoother();
        var t = DateTimeOffset.UtcNow;
        var piloting = w.Apply(Base with { IsPilot = true }, t);
        var standingInIt = w.Apply(Base with { IsPilot = false, Standing = true }, t.AddSeconds(2));
        var seatedAgain = w.Apply(Base with { IsPilot = true }, t.AddSeconds(20));

        var left = Assert.Single(GameEvents.Diff(piloting, standingInIt));
        Assert.Equal("LeftPilotSeat", left.Name);
        Assert.False(left.Important);
        Assert.Equal("Left the pilot seat of the small vessel (SV) 'Test Corvette', still aboard.", left.Description);
        var took = Assert.Single(GameEvents.Diff(standingInIt, seatedAgain));
        Assert.Equal("TookControls", took.Name);
        Assert.Equal("Took the controls of the small vessel (SV) 'Test Corvette'.", took.Description);
    }

    [Fact]
    public void Walking_off_the_ship_after_standing_on_it_is_the_exit()
    {
        var w = new WarpSmoother();
        var t = DateTimeOffset.UtcNow;
        var a = w.Apply(Base, t);
        var b = w.Apply(Base with { IsPilot = false, Standing = true }, t.AddSeconds(2));
        var c = w.Apply(Base with { ShipName = null, ShipType = null, IsPilot = false, Standing = false }, t.AddSeconds(30));

        Assert.True(b!.InShip);
        Assert.False(c!.InShip);
        Assert.Equal("LeftStructure", Assert.Single(GameEvents.Diff(b, c), e => e.Name is "LeftStructure" or "EnteredStructure").Name);
    }

    [Fact]
    public void Standing_on_another_ship_is_not_aboard_the_old_one()
    {
        var w = new WarpSmoother();
        var t = DateTimeOffset.UtcNow;
        w.Apply(Base, t);
        var other = w.Apply(Base with { ShipName = "Test Hauler", IsPilot = false, Standing = true }, t.AddSeconds(2));
        Assert.False(other!.InShip);
    }

    [Fact]
    public void Arriving_without_the_ship_is_an_exit_after_the_hold()
    {
        var w = new WarpSmoother();
        var t = DateTimeOffset.UtcNow;
        var gap = Base with { ShipName = null, ShipType = null, IsPilot = false, Playfield = "Akua Moon" };
        w.Apply(Base, t);
        Assert.True(w.Apply(gap, t.AddSeconds(2))!.InShip);
        Assert.False(w.Apply(gap, t.AddSeconds(2) + WarpSmoother.HoldFor + TimeSpan.FromSeconds(1))!.InShip);
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

        Assert.Equal("suit_oxygen_warning", Assert.Single(GameEvents.Diff(Base, low)).Id);
        Assert.Equal("suit_oxygen_low", Assert.Single(GameEvents.Diff(low, critical)).Id);
        Assert.Empty(GameEvents.Diff(critical, critical with { Oxygen = 40 }));
    }

    [Fact]
    public void Low_fuel_is_important_and_empty_fuel_too()
    {
        var low = Base with { ShipFuel = 250 };    // 12.5%
        var empty = Base with { ShipFuel = 0 };

        var lowEvent = Assert.Single(GameEvents.Diff(Base, low)); // crosses warning and low at once: the most severe wins
        Assert.Equal("fuel_low", lowEvent.Id);
        Assert.True(lowEvent.Important);
        Assert.Equal("fuel_empty", Assert.Single(GameEvents.Diff(low, empty)).Id);
    }

    [Fact]
    public void Player_status_shows_stats_as_percent()
    {
        var text = View().PlayerStatus();
        Assert.Contains("Health 100%, suit oxygen 75%, food 96%.", text);
        Assert.DoesNotContain("radiation", text);
        Assert.DoesNotContain("450", text);
    }

    [Fact]
    public void Leaving_and_boarding_ship_are_silent()
    {
        var onFoot = Base with { ShipName = null, ShipType = null, IsPilot = false };

        var left = Assert.Single(GameEvents.Diff(Base, onFoot));
        Assert.Equal("LeftStructure", left.Name);
        Assert.False(left.Important);
        Assert.Equal("EnteredStructure", Assert.Single(GameEvents.Diff(onFoot, Base)).Name);
    }

    [Fact]
    public void Tank_changes_while_switching_ships_are_ignored()
    {
        var otherShip = Base with { ShipName = "Hauler", ShipFuel = 10 };

        Assert.Equal(["left_structure", "entered_structure"], GameEvents.Diff(Base, otherShip).Select(e => e.Id));
    }

    [Fact]
    public void A_level_switched_off_falls_through_to_the_next()
    {
        var low = Base with { ShipFuel = 250 }; // 12.5%: crosses warning and low
        var cfg = NewMissionTests.Cfg();
        var noLow = GameEventConfig.From(_ => null, [new Dictionary<string, string> { ["id"] = "fuel", ["low_mode"] = "off" }]);
        Assert.Equal("fuel_low", Assert.Single(GameEvents.Diff(Base, low, config: cfg)).Id);
        Assert.Equal("fuel_warning", Assert.Single(GameEvents.Diff(Base, low, config: noLow)).Id);
        var silent = GameEventConfig.From(_ => null, [new Dictionary<string, string> { ["id"] = "fuel", ["low_mode"] = "silent" }]);
        Assert.False(Assert.Single(GameEvents.Diff(Base, low, config: silent)).Important);
    }

    [Fact]
    public void Thresholds_come_from_the_table()
    {
        var cfg = GameEventConfig.From(_ => null, [new Dictionary<string, string> { ["id"] = "health", ["low"] = "50", ["warning_mode"] = "off" }]);
        var half = Base with { Health = (Base.HealthMax ?? 100) * 0.45 };
        Assert.Equal("health_low", Assert.Single(GameEvents.Diff(Base, half, config: cfg)).Id);
        Assert.Empty(GameEvents.Diff(Base, half with { Health = (Base.HealthMax ?? 100) * 0.6 }, config: cfg));
    }

    [Fact]
    public void Health_empty_is_death_and_ship_oxygen_empty_means_suit_oxygen()
    {
        var dead = Assert.Single(GameEvents.Diff(Base with { Health = 5 }, Base with { Health = 0 }));
        Assert.Equal("health_empty", dead.Id);
        Assert.Equal("Player1 died.", dead.Description);
        var o2 = Assert.Single(GameEvents.Diff(Base with { ShipOxygen = 10 }, Base with { ShipOxygen = 0 }));
        Assert.Equal("ship_oxygen_empty", o2.Id);
        Assert.True(o2.Important);
        Assert.Contains("breathing from the suit's oxygen now", o2.Description);
        Assert.Empty(GameEvents.Diff(Base with { ShipOxygen = 0 }, Base with { ShipOxygen = 0 }));
    }

    [Fact]
    public void Predefined_events_are_hidden_watches_with_negative_ids()
    {
        var cfg = NewMissionTests.Cfg(("player_left", "off"));
        Assert.All(cfg.HiddenWatches, w => { Assert.True(w.Hidden); Assert.True(w.Id < 0); });
        Assert.DoesNotContain(cfg.HiddenWatches, w => w.ConditionText == "event=player_left");
        Assert.False(cfg.HiddenWatches.Single(w => w.ConditionText == "event=entered_structure").Announce);
        Assert.True(cfg.HiddenWatches.Single(w => w.ConditionText == "event=shield_down").Announce);
        Assert.Empty(GameEvents.Diff(Base, null, config: cfg));
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

    /// <summary>The fixture with the player on foot per the (stale) playfield snapshot plus a "live" answer.</summary>
    private static GameView Live(string active, string live) =>
        View(Status.Replace("\"isPilot\": true,", $"\"isPilot\": false, \"active\": {active},")
                   .Replace("\"playfields\": [", $"\"live\": {{ \"765\": {live} }}, \"playfields\": ["));

    [Fact]
    public void Live_position_wins_over_a_stale_snapshot_on_foot()
    {
        // snapshot still says standing at the ship; the fresh position is inside a POI 80 m away
        var view = Live("""{ "how": "standing", "playerOn": true, "distance": 0 }""",
            """{ "pos": [1, 2, 3], "visiting": { "name": "Solaris-AMP Factory", "type": "BA", "owner": "Polaris" }, "own": [ { "name": "Test Corvette", "type": "SV", "distance": 80 } ] }""");
        var st = view.State();
        Assert.False(st.InShip);
        Assert.False(st.Standing);
        Assert.Equal("Solaris-AMP Factory", st.VisitingName);
        Assert.Equal(("Test Corvette", "nearest", 80.0), (st.ActiveName, st.ActiveHow, st.ActiveDistance));
        Assert.Contains("inside the base 'Solaris-AMP Factory'", st.ToContext());
    }

    [Fact]
    public void Live_standing_at_the_ship_before_the_snapshot_notices()
    {
        var view = Live("""{ "how": "nearest", "playerOn": false, "distance": 80 }""",
            """{ "pos": [1, 2, 3], "standing": { "name": "Test Corvette", "type": "SV" }, "own": [ { "name": "Test Corvette", "type": "SV", "distance": 4 } ] }""");
        var st = view.State();
        Assert.True(st.Standing);
        Assert.Equal("Test Corvette", st.ShipName);
        Assert.Null(st.ActiveName);
        Assert.Contains("On foot at the SV 'Test Corvette'", st.ToContext());
    }

    [Fact]
    public void Seated_stays_with_the_playfield_server()
    {
        var json = Status.Replace("\"playfields\": [", "\"live\": { \"765\": { \"pos\": [1,2,3], \"visiting\": { \"name\": \"Somewhere\", \"type\": \"BA\" }, \"own\": [] } }, \"playfields\": [");
        var st = View(json).State();
        Assert.True(st.InShip);
        Assert.Null(st.VisitingName);
    }

    [Fact]
    public void Status_says_standing_outside_the_ship()
    {
        var json = Status.Replace("\"isPilot\": true,", "\"isPilot\": false, \"active\": { \"how\": \"standing\", \"playerOn\": true },");
        var view = View(json);
        Assert.Contains("On foot at the SV 'Test Corvette': outside", view.PlayerStatus());
        Assert.True(view.State().Standing);
        Assert.False(view.State().InShip);
    }
}

public class StandingTests
{
    static readonly GameState Inside = new() { Player = "Player1", Playfield = "Viunean", ShipName = "Test Hauler", ShipType = "CV" };

    [Fact]
    public void Standing_next_to_the_ship_is_not_aboard()
    {
        var outside = Inside with { Standing = true };
        Assert.False(outside.InShip);
        Assert.Contains("On foot at the CV 'Test Hauler' (outside", outside.ToContext());
        Assert.DoesNotContain("Aboard", outside.ToContext());
    }

    [Fact]
    public void Walking_up_to_the_ship_and_in_gives_one_boarding()
    {
        var onFoot = Inside with { ShipName = null, ShipType = null };
        var outside = Inside with { Standing = true };
        Assert.Empty(GameEvents.Diff(onFoot, outside));
        Assert.Equal("EnteredStructure", Assert.Single(GameEvents.Diff(outside, Inside)).Name);
        Assert.Equal("LeftStructure", Assert.Single(GameEvents.Diff(Inside, outside)).Name);
    }
}
