using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class WatchTests
{
    private static readonly GameState Ship = new()
    {
        Player = "Player1", Playfield = "Akua", PlayfieldType = "Planet", ShipName = "Test Hauler", ShipType = "CV",
        IsPilot = true, Landed = true,
    };

    private static Watch W(string conditions, int id = 1) => new(id, Watches.Parse(conditions, out _)!, "note", DateTimeOffset.UtcNow);

    [Fact]
    public void Parse_accepts_pairs_and_normalises()
    {
        var c = Watches.Parse("event == seat_left && structure=Test Hauler; DOCKED=True", out var err);
        Assert.Null(err);
        Assert.Equal(["event", "structure", "docked"], c!.Select(x => x.Field));
        Assert.Equal("true", c![2].Value);
        Assert.Equal("Test Hauler", c[1].Value);
    }

    [Theory]
    [InlineData("structure=Test Hauler", "An 'event' condition is required")]
    [InlineData("event=flying", "'event' must be one of")]
    [InlineData("event=entered; colour=red", "Unknown field 'colour'")]
    [InlineData("event=entered; structure=A; structure=B", "appears twice")]
    [InlineData("event=signal", "needs name=")]
    [InlineData("", "No conditions")]
    public void Parse_rejects_with_a_helpful_error(string text, string expected)
    {
        Assert.Null(Watches.Parse(text, out var err));
        Assert.Contains(expected, err);
    }

    [Fact]
    public void Unreadable_entries_are_skipped_kept_and_their_ids_stay_taken()
    {
        var dir = Path.Combine(Path.GetTempPath(), "watchtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, WatchStore.FileName("http://h:1", "-"));
            File.WriteAllText(file, """
                {"BridgeUrl":"http://h:1","SteamId":"-","NextId":4,"Watches":[
                  {"Id":1,"Conditions":"event=entered","Note":"ok","Created":"2026-11-20T00:00:00Z"},
                  {"Id":3,"Conditions":"event=boarded; ship=Test Miner","Note":"old names","Created":"2026-11-20T00:00:00Z"}]}
                """);
            var warnings = new List<string>();
            var store = new WatchStore(dir, "http://h:1", "-", warn: warnings.Add);
            Assert.Equal([1], store.All.Select(w => w.Id));
            Assert.Contains("watch #3", Assert.Single(warnings));
            Assert.Equal(4, store.Add(Watches.Parse("event=left", out _)!, "n", out _)!.Id);
            var saved = File.ReadAllText(file);
            Assert.Contains("event=boarded; ship=Test Miner", saved);  // kept as it was, to fix by hand
            Assert.Equal(2, store.Remove(null) - 1);                    // all: 2 readable + the skipped one
            Assert.Equal(5, store.Add(Watches.Parse("event=left", out _)!, "n", out _)!.Id);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Old_names_are_rejected()
    {
        Assert.Null(Watches.Parse("event=boarded", out var e1));
        Assert.Contains("'event' must be one of", e1);
        Assert.Null(Watches.Parse("event=entered; ship=X", out var e2));
        Assert.Contains("Unknown field 'ship'", e2);
    }

    [Fact]
    public void Leaving_the_seat_aboard_fires_seat_left_with_the_ship_state()
    {
        var e = Assert.Single(Watches.Diff(Ship, Ship with { IsPilot = false }));
        Assert.Equal("seat_left", e["event"]);
        Assert.Equal("aboard", e["seat"]);
        Assert.Equal("planet", e["where"]);
        Assert.Equal("true", e["landed"]);
        Assert.Equal("false", e["docked"]);
        Assert.Equal("cv", e["type"]);
        Assert.True(Watches.Matches(W("event=seat_left; structure=test hauler; landed=true"), e));
        Assert.False(Watches.Matches(W("event=seat_left; structure=Test Hauler; where=space"), e));
    }

    [Fact]
    public void Changing_ship_is_left_ship_then_boarded_and_left_ship_keeps_the_old_ships_docking()
    {
        var miner = Ship with { ShipName = "Test Miner", ShipType = "SV", IsPilot = false, DockedTo = "Test Hauler", Landed = null };
        var events = Watches.Diff(miner, Ship with { IsPilot = false });
        Assert.Equal(["left", "entered"], events.Select(e => e["event"]));
        Assert.Equal("Test Miner", events[0]["structure"]);
        Assert.Equal("true", events[0]["docked"]);
        Assert.Equal("Test Hauler", events[0]["docked_to"]);
        Assert.True(Watches.Matches(W("event=left; structure=Test Miner; docked=true"), events[0]));
    }

    [Fact]
    public void Boarding_into_the_pilot_seat_and_leaving_from_it()
    {
        var onFoot = Ship with { ShipName = null, ShipType = null, IsPilot = false, Landed = null };
        Assert.Equal(["entered", "seat_taken"], Watches.Diff(onFoot, Ship).Select(e => e["event"]));
        var left = Watches.Diff(Ship, onFoot);
        Assert.Equal(["seat_left", "left"], left.Select(e => e["event"]));
        Assert.Equal("on_foot", left[1]["seat"]);
    }

    [Fact]
    public void Docking_landing_and_their_reverse()
    {
        var flying = Ship with { Landed = false };
        Assert.Equal("lifted_off", Assert.Single(Watches.Diff(Ship, flying))["event"]);
        Assert.Equal("landed", Assert.Single(Watches.Diff(flying, Ship))["event"]);
        var docked = Ship with { DockedTo = "Test Corvette" };
        var d = Assert.Single(Watches.Diff(Ship, docked));
        Assert.Equal("docked", d["event"]);
        Assert.Equal("Test Corvette", d["other_structure"]);
        var u = Assert.Single(Watches.Diff(docked, Ship));
        Assert.Equal("undocked", u["event"]);
        Assert.Equal("Test Corvette", u["other_structure"]);
    }

    [Fact]
    public void Unknown_landing_gear_is_no_event_and_no_field()
    {
        var unknown = Ship with { Landed = null };
        Assert.Empty(Watches.Diff(Ship, unknown));
        var e = Assert.Single(Watches.Diff(unknown, unknown with { IsPilot = false }));
        Assert.False(e.ContainsKey("landed"));
        Assert.False(Watches.Matches(W("event=seat_left; landed=true"), e));
    }

    [Fact]
    public void Signal_changes_from_watched_states()
    {
        var prev = new Dictionary<string, (string, string, string)> { ["Test Hauler|POWERSAVER"] = ("signal", "off", "Test Hauler") };
        var next = new Dictionary<string, (string, string, string)> { ["Test Hauler|POWERSAVER"] = ("signal", "on", "Test Hauler") };
        var e = Assert.Single(Watches.StateEvents(Ship, prev, next));
        Assert.Equal("signal", e["event"]);
        Assert.Equal("POWERSAVER", e["name"]);
        Assert.Equal("on", e["state"]);
        Assert.True(Watches.Matches(W("event=signal; name=powersaver; state=on"), e));
        Assert.Empty(Watches.StateEvents(Ship, next, next));
        var missing = new Dictionary<string, (string, string, string)> { ["Test Hauler|POWERSAVER"] = ("", "missing", "Test Hauler") };
        Assert.Empty(Watches.StateEvents(Ship, missing, next));
    }

    [Fact]
    public void Watched_names_are_grouped_by_ship()
    {
        var names = Watches.WatchedNames([W("event=signal; name=POWERSAVER", 1), W("event=device; name=Multi Turret; structure=Test Hauler", 2),
            W("event=signal; name=Gear; structure=test hauler", 3), W("event=entered", 4)]);
        Assert.Equal(["POWERSAVER"], names[""]);
        Assert.Equal(["Multi Turret", "Gear"], names["Test Hauler"]);
    }

    [Fact]
    public void Notification_carries_note_and_state()
    {
        var w = new Watch(7, Watches.Parse("event=seat_left", out _)!, "parking mode", DateTimeOffset.UtcNow);
        var text = Watches.Notification(w, Watches.Diff(Ship, Ship with { IsPilot = false })[0]);
        Assert.StartsWith("[WATCH #7 fired in Empyrion] Your note: \"parking mode\".", text);
        Assert.Contains("event=seat_left", text);
        Assert.Contains("structure=Test Hauler", text);
    }

    [Fact]
    public void Store_keeps_watches_per_server_and_player_in_a_hashed_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "watchtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WatchStore(dir, "http://127.0.0.1:30016/", "76561198000000001");
            Assert.Matches("^[0-9a-f]{32}\\.json$", Path.GetFileName(store.FilePath));
            Assert.Equal(WatchStore.FileName("HTTP://127.0.0.1:30016", " 76561198000000001"), Path.GetFileName(store.FilePath));
            Assert.NotEqual(WatchStore.FileName("http://127.0.0.1:30017", "76561198000000001"), Path.GetFileName(store.FilePath));
            var a = store.Add(Watches.Parse("event=entered; structure=Test Hauler", out _)!, "say hi", out _)!;
            store.Add(Watches.Parse("event=seat_left", out _)!, "parking", out _);
            Assert.Equal(1, store.Remove(a.Id));

            var json = JsonDocument.Parse(File.ReadAllText(store.FilePath)).RootElement;
            Assert.Equal("http://127.0.0.1:30016/", json.GetProperty("BridgeUrl").GetString());
            Assert.Equal("76561198000000001", json.GetProperty("SteamId").GetString());

            var again = new WatchStore(dir, "http://127.0.0.1:30016/", "76561198000000001");
            var w = Assert.Single(again.All);
            Assert.Equal(2, w.Id);
            Assert.Equal("event=seat_left", w.ConditionText);
            Assert.Equal(3, again.Add(Watches.Parse("event=landed", out _)!, "x", out _)!.Id);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Store_limits_the_number_of_watches()
    {
        var dir = Path.Combine(Path.GetTempPath(), "watchtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WatchStore(dir, "http://h:1", "-", maxWatches: 3);
            for (var i = 0; i < 3; i++) Assert.NotNull(store.Add(Watches.Parse("event=entered", out _)!, "n", out _));
            Assert.Null(store.Add(Watches.Parse("event=entered", out _)!, "n", out var err));
            Assert.Contains("the limit is 3", err);

            // the limit lowered later: all are kept, adding stays refused until enough are removed
            var lowered = new WatchStore(dir, "http://h:1", "-", maxWatches: 1);
            Assert.Equal(3, lowered.All.Count);
            Assert.Null(lowered.Add(Watches.Parse("event=entered", out _)!, "n", out _));
            lowered.Remove(1); lowered.Remove(2);
            Assert.Null(lowered.Add(Watches.Parse("event=entered", out _)!, "n", out _));
            lowered.Remove(3);
            Assert.NotNull(lowered.Add(Watches.Parse("event=entered", out _)!, "n", out _));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class NewStructureTests
{
    private static readonly GameState Base = new() { Player = "Player1", Playfield = "Akua", PlayfieldType = "Planet" };
    private static readonly NewStructure Sherpa = new(163001, "Test Corvette", "HV", "blueprint", "Test Corvette");
    private static readonly NewStructure Starter = new(163003, "Base (BA)", "BA", "starter", "BA_Player");

    [Fact]
    public void Each_new_structure_is_one_event_by_kind()
    {
        var events = GameEvents.Diff(Base, Base with { NewStructures = [Sherpa, Starter] });
        Assert.Equal(["new_structure", "starter_placed"], events.Select(e => e.Id));
        Assert.Equal("A new hover vessel (HV) 'Test Corvette' was spawned from a blueprint.", events[0].Description);
        Assert.Equal("A starter block for a new base (BA) was placed ('Base (BA)').", events[1].Description);
        Assert.All(events, e => Assert.True(e.Important));
        // the bridge keeps listing them for a minute: no repeat
        Assert.Empty(GameEvents.Diff(Base with { NewStructures = [Sherpa, Starter] }, Base with { NewStructures = [Sherpa, Starter] }));
        Assert.Empty(GameEvents.Diff(Base with { NewStructures = [Sherpa] }, Base));
    }

    [Fact]
    public void Settings_modes_apply()
    {
        var cfg = NewMissionTests.Cfg(("new_structure", "silent"), ("starter_placed", "off"));
        var e = Assert.Single(GameEvents.Diff(Base, Base with { NewStructures = [Sherpa, Starter] }, config: cfg));
        Assert.Equal("new_structure", e.Id);
        Assert.False(e.Important);
    }

    [Fact]
    public void Watch_events_carry_name_and_type()
    {
        var events = Watches.Diff(Base, Base with { NewStructures = [Sherpa, Starter] });
        Assert.Equal("new_structure", events[0]["event"]);
        Assert.Equal("Test Corvette", events[0]["structure"]);
        Assert.Equal("hv", events[0]["type"]);
        var w = new Watch(1, Watches.Parse("event=starter_placed; type=ba", out _)!, "say hi", DateTimeOffset.UtcNow);
        Assert.True(Watches.Matches(w, events[1]));
        Assert.False(Watches.Matches(w, events[0]));
    }

    [Fact]
    public void Snapshot_list_is_read()
    {
        var json = """
            {
              "players": [{ "entityId": 4080, "name": "Player1", "steamId": "765", "playfield": "Akua", "online": true }],
              "playfields": [{ "name": "Akua", "snapshot": {
                "playfield": "Akua", "type": "Planet",
                "players": [{ "id": 4080, "name": "Player1", "isPilot": false,
                  "newStructures": [{ "id": 163001, "name": "Test Corvette", "type": "HV", "kind": "blueprint", "blueprint": "Test Corvette" }] }]
              }}]
            }
            """;
        var view = GameView.Create(System.Text.Json.JsonDocument.Parse(json).RootElement, null, out var err);
        Assert.NotNull(view);
        var s = Assert.Single(view!.State().NewStructures);
        Assert.Equal((163001, "Test Corvette", "HV", "blueprint"), (s.Id, s.Name, s.Type, s.Kind));
    }
}
