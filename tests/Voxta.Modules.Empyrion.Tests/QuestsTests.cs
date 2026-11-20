using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class QuestsTests
{
    [Fact]
    public void Quest_list_renders_wants_reward_and_work_order()
    {
        var json = """
        {"ok":true,"status":"open","count":2,"quests":[
          {"factory":"Solaris-AMP Factory","playfield":"Viunean","ly":0,"faction":"Polaris","status":"open","tradesUnlocked":false,
           "wants":"12 Communication Equipment","reward":"15,000 credits as a money card, +150 reputation with Polaris",
           "workOrder":"Work Order - Solaris-AMP, 80,000 credits at a GIN console (no credit or reputation reward then)","pursued":true},
          {"factory":"P3: Planetside Production","playfield":"Viunean","ly":3.2,"status":"open","tradesUnlocked":true,"wants":"400 Tablets","followUp":true}
        ],"tracked":2}
        """;
        var text = GameView.QuestsResult(JsonDocument.Parse(json).RootElement);
        Assert.Contains("Solaris-AMP Factory (Viunean, this sector, Polaris) [pursued]: wants 12 Communication Equipment to unlock its trades", text);
        Assert.Contains("Or a work order: Work Order - Solaris-AMP, 80,000 credits", text);
        Assert.Contains("400 Tablets (optional follow-up; trades already)", text);
        Assert.Contains("3.2 LY", text);
    }

    [Fact]
    public void Empty_list_explains_how_missions_start()
    {
        var text = GameView.QuestsResult(JsonDocument.Parse("""{"ok":true,"status":"open","count":0,"quests":[],"tracked":0}""").RootElement);
        Assert.Contains("talks to a factory's trader", text);
    }

    [Fact]
    public void Pursued_line_in_context_and_done_event()
    {
        var open = new GameState { Player = "Player1", Playfield = "Viunean", PursuingLine = "Solaris-AMP Factory (Viunean) wants 12 Communication Equipment", PursuingStatus = "open" };
        Assert.Contains("Pursuing: Solaris-AMP Factory (Viunean) wants 12 Communication Equipment.", open.ToContext());
        var done = open with { PursuingLine = "Solaris-AMP Factory (Viunean): mission done, trades unlocked", PursuingStatus = "done" };
        Assert.Contains("Pursued mission: Solaris-AMP Factory (Viunean): mission done", done.ToContext());
        var e = Assert.Single(GameEvents.Diff(open, done));
        Assert.Equal("PursuedMissionDone", e.Name);
        Assert.True(e.Important);
        Assert.Empty(GameEvents.Diff(done, done));
    }
}

public class NewMissionTests
{
    static GameState S(params OpenMission[] m) => new() { Player = "Player1", Playfield = "Viunean", OpenMissions = m };
    static readonly OpenMission Factory = new("100148-1/2/3-Viunean#1", "factory", "Solaris-AMP Factory (Viunean) wants 12 Communication Equipment to unlock its trades");
    static readonly OpenMission Shop = new("100200-1/2/3-Viunean#2", "trader", "Market Cafe (Viunean) wants 3 Space Durian");

    /// <summary>Settings with the given event modes (id → on/silent/off), the rest default.</summary>
    internal static GameEventConfig Cfg(params (string id, string mode)[] modes) =>
        GameEventConfig.From(id => modes.Where(m => m.id == id).Select(m => m.mode).FirstOrDefault(), null);

    [Fact]
    public void New_factory_mission_is_spoken_trader_mission_silent_by_default()
    {
        var events = GameEvents.Diff(S(), S(Factory, Shop));
        Assert.Equal(2, events.Count);
        Assert.True(events.Single(e => e.Name == "NewFactoryMission").Important);
        Assert.False(events.Single(e => e.Name == "NewTraderMission").Important);
    }

    [Fact]
    public void Settings_switch_both_ways()
    {
        var none = GameEvents.Diff(S(), S(Factory, Shop), config: Cfg(("new_factory_mission", "silent"), ("new_trader_mission", "silent")));
        Assert.Equal(2, none.Count);
        Assert.All(none, e => Assert.False(e.Important));
        var all = GameEvents.Diff(S(), S(Factory, Shop), config: Cfg(("new_factory_mission", "on"), ("new_trader_mission", "on")));
        Assert.All(all, e => Assert.True(e.Important));
        var off = GameEvents.Diff(S(), S(Factory, Shop), config: Cfg(("new_factory_mission", "off")));
        Assert.Equal("NewTraderMission", Assert.Single(off).Name);
    }

    [Fact]
    public void Baseline_and_unknown_lists_announce_nothing()
    {
        Assert.Empty(GameEvents.Diff(S(Factory), S(Factory)));
        Assert.Empty(GameEvents.Diff(new GameState { Player = "Player1", Playfield = "Viunean" }, S(Factory)));
        var followUp = Factory with { Key = "100148-1/2/3-Viunean#5" };
        Assert.Equal("NewFactoryMission", Assert.Single(GameEvents.Diff(S(), S(followUp))).Name);
    }

    [Fact]
    public void Completed_missions_by_kind_and_pursued_not_twice()
    {
        var f = Factory with { Where = "Solaris-AMP Factory (Viunean)" };
        var done = Assert.Single(GameEvents.Diff(S(f, Shop), S(Shop)));
        Assert.Equal("FactoryMissionDone", done.Name);
        Assert.True(done.Important);
        Assert.Contains("trades are unlocked now", done.Description);
        var follow = Assert.Single(GameEvents.Diff(S(f with { FollowUp = true }), S()));
        Assert.DoesNotContain("unlocked", follow.Description);
        var shop = Assert.Single(GameEvents.Diff(S(Shop), S()));
        Assert.Equal("TraderMissionDone", shop.Name);
        Assert.False(shop.Important);
        Assert.True(Assert.Single(GameEvents.Diff(S(Shop), S(), config: Cfg(("trader_mission_done", "on")))).Important);
        Assert.False(Assert.Single(GameEvents.Diff(S(f), S(), config: Cfg(("factory_mission_done", "silent")))).Important);
        Assert.Empty(GameEvents.Diff(S(f with { Pursued = true }), S())); // the pursued one has PursuedMissionDone
    }

    [Fact]
    public void Pursued_done_toggle()
    {
        var open = new GameState { Player = "Player1", Playfield = "Viunean", PursuingLine = "x", PursuingStatus = "open" };
        var done = open with { PursuingStatus = "done" };
        Assert.True(Assert.Single(GameEvents.Diff(open, done)).Important);
        Assert.False(Assert.Single(GameEvents.Diff(open, done, config: Cfg(("pursued_mission_done", "silent")))).Important);
        Assert.Empty(GameEvents.Diff(open, done, config: Cfg(("pursued_mission_done", "off"))));
    }
}

public class SettingsFormTests
{
    [Fact]
    public async Task Game_events_and_vitals_sit_in_their_own_folded_sections()
    {
        var fields = await new Voxta.Modules.Empyrion.Configuration.ModuleConfigurationProvider()
            .GetModuleConfigurationFieldsAsync(null!, null!, CancellationToken.None);
        var list = fields.ToList();
        var titles = list.Select((f, i) => (f, i)).Where(x => x.f is Voxta.Model.Shared.Forms.FormTitleField t && t.Advanced).Select(x => x.i).ToList();
        Assert.Equal(2, titles.Count);
        var events = list.Skip(titles[0] + 1).TakeWhile(f => f is not Voxta.Model.Shared.Forms.FormTitleField).Select(f => f.Name).ToList();
        Assert.Equal(Voxta.Modules.Empyrion.Configuration.GameEventSettings.Definitions.Select(d => "Event_" + d.Id), events);
        Assert.DoesNotContain("Event_player_died", events);
        Assert.Equal(["GameEventLevels"], list.Skip(titles[1] + 1).Select(f => f.Name));
        Assert.Contains("VcSuffix", list.Take(titles[0]).Select(f => f.Name));
        Assert.Contains("Tools", list.Take(titles[0]).Select(f => f.Name));
    }
}

public class PursuedProgressTests
{
    static readonly MissionNeed Comms = new("CommunicationEquipment", "Communication Equipment", 12);

    static GameState S(int have, string status = "open") => new()
    {
        Player = "Player1", Playfield = "Viunean", PursuingStatus = status,
        PursuingLine = "Solaris-AMP Factory (Viunean) wants 12 Communication Equipment to unlock its trades",
        PursuingProgress = [(Comms, have)],
    };

    [Fact]
    public void Progress_in_the_context_and_ready_once()
    {
        Assert.Contains("(in the player's inventory: 8/12 Communication Equipment)", S(8).ToContext());
        Assert.Contains("ready to hand in", S(12).ToContext());
        // "items ready" is no event any more (needs a ship scan; open task), only the context shows it
        Assert.Empty(GameEvents.Diff(S(8), S(12)));
    }

    [Fact]
    public void Inventory_counts_items_and_tokens_from_the_status()
    {
        var json = """
        { "players": [{ "entityId": 1, "name": "Player1", "steamId": "765", "playfield": "Viunean", "online": true }],
          "api1": { "players": [{ "entityId": 1, "playfield": "Viunean",
            "toolbar": [{ "id": 5, "name": "CommunicationEquipment", "count": 4 }],
            "bag": [{ "id": 5, "name": "CommunicationEquipment", "count": 5 }, { "id": 9, "name": "Token", "count": 1, "tokenId": 4057 }] }] },
          "pursuing": { "765": { "status": "open", "line": "x", "needs": [
            { "item": "CommunicationEquipment", "name": "Communication Equipment", "count": 12 },
            { "item": "Token", "name": "Work Order - Solaris-AMP", "count": 1, "tokenId": 4057 } ] } },
          "playfields": [] }
        """;
        var st = GameView.Create(JsonDocument.Parse(json).RootElement, null, out _)!.State();
        Assert.Equal([9, 1], st.PursuingProgress!.Select(p => p.Have));
        Assert.False(st.PursuingReady);
    }
}

public class SettingsDependencyTests
{
    [Fact]
    public async Task Enabled_fields_exist_and_chat_settings_are_present()
    {
        var fields = (await new Voxta.Modules.Empyrion.Configuration.ModuleConfigurationProvider()
            .GetModuleConfigurationFieldsAsync(null!, null!, CancellationToken.None)).ToList();
        var names = fields.Select(f => f.Name).Where(n => n != null).ToHashSet();
        foreach (var f in fields)
            foreach (var target in f.EnablesFieldsOnActive ?? [])
                Assert.Contains(target, names);
        Assert.Contains("GameChatMirror", names);
        Assert.DoesNotContain("GameChatPrefix", names);
    }
}
