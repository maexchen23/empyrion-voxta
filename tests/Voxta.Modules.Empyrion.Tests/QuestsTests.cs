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
    public void New_factory_mission_is_spoken_trader_mission_off_by_default()
    {
        // other traders roll a mission on every talk: off by default, or a walk through a station spams the chat
        var events = GameEvents.Diff(S(), S(Factory, Shop));
        Assert.True(Assert.Single(events).Important);
        Assert.Equal("NewFactoryMission", events[0].Name);
    }

    [Fact]
    public void Settings_switch_both_ways()
    {
        var none = GameEvents.Diff(S(), S(Factory, Shop), config: Cfg(("new_factory_mission", "silent"), ("new_trader_mission", "silent")));
        Assert.Equal(2, none.Count);
        Assert.All(none, e => Assert.False(e.Important));
        var all = GameEvents.Diff(S(), S(Factory, Shop), config: Cfg(("new_factory_mission", "on"), ("new_trader_mission", "on")));
        Assert.All(all, e => Assert.True(e.Important));
        var off = GameEvents.Diff(S(), S(Factory, Shop), config: Cfg(("new_factory_mission", "off"), ("new_trader_mission", "silent")));
        Assert.Equal("NewTraderMission", Assert.Single(off).Name);
        Assert.Empty(GameEvents.Diff(S(), S(Factory, Shop), config: Cfg(("new_factory_mission", "off"))));
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
        Assert.Empty(GameEvents.Diff(S(Shop), S())); // trader missions: off by default
        var shop = Assert.Single(GameEvents.Diff(S(Shop), S(), config: Cfg(("trader_mission_done", "silent"))));
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

public class MiningContractEventTests
{
    static readonly HeldContract Cobalt = new(6820, "Mining Contract: Cobalt", "Polaris Megacorp", "12,000 Cobalt Ore", 960000, 500, 1);
    static readonly ContractOffer Refinery = new("173015--8/168/-3-OUZL V", 716703, "Orbital Refinery (OUZL V)");

    static GameState S(IReadOnlyList<HeldContract>? held, IReadOnlyList<ContractOffer>? offers, long credits) =>
        new() { Player = "Player1", Playfield = "OUZL V", HeldContracts = held, ContractOffers = offers, Credits = credits };

    static GameEventConfig Cfg(params (string id, string mode)[] modes) => NewMissionTests.Cfg(modes);

    [Fact]
    public void New_contract_when_the_refinery_offer_changes_silent_by_default()
    {
        var e = Assert.Single(GameEvents.Diff(S([], [Refinery with { Value = 700000 }], 0), S([Cobalt], [Refinery], 0)));
        Assert.Equal("NewMiningContract", e.Name);
        Assert.False(e.Important);
        Assert.Contains("Mining Contract: Cobalt for Polaris Megacorp: 12,000 Cobalt Ore for 960,000 credits at Orbital Refinery (OUZL V)", e.Description);
        // the first contract from a refinery: a new row
        Assert.Single(GameEvents.Diff(S([], [], 0), S([Cobalt], [Refinery], 0)));
        Assert.True(Assert.Single(GameEvents.Diff(S([], [], 0), S([Cobalt], [Refinery], 0), config: Cfg(("new_mining_contract", "on")))).Important);
        Assert.Empty(GameEvents.Diff(S([], [], 0), S([Cobalt], [Refinery], 0), config: Cfg(("new_mining_contract", "off"))));
    }

    [Fact]
    public void Moving_the_token_around_is_no_event()
    {
        // token into a container: gone from the inventory, no pay, offer unchanged
        Assert.Empty(GameEvents.Diff(S([Cobalt], [Refinery], 1000), S([], [Refinery], 1000)));
        // and back
        Assert.Empty(GameEvents.Diff(S([], [Refinery], 1000), S([Cobalt], [Refinery], 1000)));
        // unknown offers (no data this poll) never read as new
        Assert.Empty(GameEvents.Diff(S([], null, 0), S([Cobalt], [Refinery], 0)));
    }

    [Fact]
    public void Hand_in_when_the_token_leaves_and_the_pay_arrives()
    {
        var e = Assert.Single(GameEvents.Diff(S([Cobalt], [Refinery], 50000), S([], [Refinery], 1010000)));
        Assert.Equal("MiningContractDone", e.Name);
        Assert.False(e.Important);
        Assert.Contains("handed in Mining Contract: Cobalt for Polaris Megacorp: 960,000 credits and 500 Polaris Megacorp reputation", e.Description);
        Assert.Empty(GameEvents.Diff(S([Cobalt], [Refinery], 50000), S([], [Refinery], 60000))); // too little: sold something else
        Assert.True(Assert.Single(GameEvents.Diff(S([Cobalt], [Refinery], 0), S([], [Refinery], 960000), config: Cfg(("mining_contract_done", "on")))).Important);
    }
}

public class SiteJobTests
{
    static readonly SiteJob Ilmenite = new("112027", "Industrial Command Vessel (Sector A)", [("Eden_IlmeniteOre", "Ilmenite Ore", 6000)],
        "CargoDrop", 200000, false, "Polaris Neutral", false);

    static GameState S(string pf, params SiteJob[] jobs) => new() { Player = "Player1", Playfield = pf, SiteJobs = jobs };

    // arriving also gives the usual playfield event; only the job events matter here
    static List<GameEvent> Jobs(GameState prev, GameState next, GameEventConfig? config = null) =>
        GameEvents.Diff(prev, next, config: config).Where(e => e.Name == "MiningSiteJob").ToList();

    [Fact]
    public void Arrival_at_a_site_with_an_open_job_is_spoken_by_default()
    {
        var e = Assert.Single(Jobs(S("Elsewhere"), S("Sector A", Ilmenite)));
        Assert.Equal("MiningSiteJob", e.Name);
        Assert.True(e.Important);
        Assert.Equal("site:112027", e.MissionKey);
        Assert.Contains("On the local channel, the Industrial Command Vessel (Sector A) is broadcasting a job: they want 6,000 Ilmenite Ore in their 'CargoDrop' container, for 200,000 credits (they only deal with Polaris Neutral standing or better).", e.Description);
    }

    [Fact]
    public void Done_jobs_staying_and_the_first_poll_are_quiet()
    {
        Assert.Empty(Jobs(S("Elsewhere"), S("Sector A", Ilmenite with { Done = true })));
        Assert.Empty(Jobs(S("Sector A", Ilmenite), S("Sector A", Ilmenite)));
        Assert.Empty(Jobs(new GameState { Player = "Player1", Playfield = "X" }, S("Sector A", Ilmenite))); // unknown before
        Assert.False(Assert.Single(Jobs(S("Elsewhere"), S("Sector A", Ilmenite), NewMissionTests.Cfg(("mining_site_job", "silent")))).Important);
    }

    [Fact]
    public void Live_context_names_the_open_job()
    {
        var text = S("Sector A", Ilmenite, Ilmenite with { Key = "2", Where = "Done Vessel (Sector A)", Done = true }).ToContext();
        Assert.Contains("Job here: Industrial Command Vessel (Sector A) wants 6,000 Ilmenite Ore in its 'CargoDrop' container for 200,000 credits (needs Polaris Neutral standing); one time per player.", text);
        Assert.DoesNotContain("Done Vessel", text);
    }

    static string? Cargo(string json) => GameView.SiteCargoLine(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Cargo_warning_below_125_percent_of_the_load()
    {
        Assert.Equal("Cargo warning: the load takes about 30k of cargo volume, but the harvest controllers on 'Miner' have only 32k free; suggest emptying them first or bringing a bigger hold.",
            Cargo("""{"ok":true,"needL":30000,"space":{"ok":true,"structure":"Miner","harvestControllers":2,"capacityL":60000,"usedL":28000,"freeL":32000}}"""));
        Assert.Equal("The load takes about 30k of cargo volume; the harvest controllers on 'Miner' have 40k free, enough.",
            Cargo("""{"ok":true,"needL":30000,"space":{"ok":true,"structure":"Miner","harvestControllers":2,"capacityL":60000,"usedL":20000,"freeL":40000}}"""));
        Assert.Equal("The load takes about 3k of cargo volume; 'Shuttle' has no harvest controller, so it would all go by hand or another ship.",
            Cargo("""{"ok":true,"needL":3000,"space":{"ok":true,"structure":"Shuttle","harvestControllers":0,"freeL":0}}"""));
        Assert.Contains("about 96k of cargo volume; the harvest controllers on 'Zeus' have 640k free", // dev check: 6,000 Ilmenite vs. 2 controllers
            Cargo("""{"ok":true,"needL":96000,"space":{"ok":true,"structure":"Zeus","harvestControllers":2,"capacityL":640000,"usedL":0,"freeL":640000}}""")!);
        Assert.Contains("about 2.5k of cargo", Cargo("""{"ok":true,"needL":2500,"space":{"ok":true,"structure":"S","harvestControllers":1,"freeL":100000}}""")!);
        Assert.Null(Cargo("""{"ok":true,"needL":3000,"space":{"ok":false,"error":"not on a ship"}}"""));
    }
}

public class ItemsReadyTests
{
    static readonly MissionNeed Comms = new("CommunicationEquipment", "Communication Equipment", 12);
    static readonly MissionNeed Xenon = new("XenonGasBottle", "Xenon Gas Bottle", 50);
    const string Line = "Solaris-AMP Factory (Planet A) wants 12 Communication Equipment + 50 Xenon Gas Bottle to unlock its trades";

    [Fact]
    public void Ready_when_everything_is_in_reach_says_what_to_move()
    {
        var e = GameEvents.ItemsReady(Line, [(Comms, 12, 12), (Xenon, 10, 60)])!;
        Assert.Equal("PursuedItemsReady", e.Name);
        Assert.True(e.Important);
        Assert.Equal("Everything the pursued mission wants is in reach now: " + Line + ". To hand it in, the items must be in the player's inventory; " +
                     "still to move there: 40 Xenon Gas Bottle. Offer to move them (empyrion_transfer_items).", e.Description);
    }

    [Fact]
    public void All_in_the_inventory_is_ready_to_hand_in()
    {
        Assert.EndsWith("All of it is in the player's inventory: ready to hand in.", GameEvents.ItemsReady(Line, [(Comms, 12, 12), (Xenon, 50, 50)])!.Description);
    }

    [Fact]
    public void Missing_items_or_off_give_nothing_silent_is_quiet()
    {
        Assert.Null(GameEvents.ItemsReady(Line, [(Comms, 12, 12), (Xenon, 10, 49)]));
        Assert.Null(GameEvents.ItemsReady(Line, []));
        Assert.Null(GameEvents.ItemsReady(Line, [(Comms, 12, 12)], NewMissionTests.Cfg(("pursued_items_ready", "off"))));
        var silent = GameEvents.ItemsReady(Line, [(Comms, 0, 12)], NewMissionTests.Cfg(("pursued_items_ready", "silent")))!;
        Assert.False(silent.Important);
        Assert.DoesNotContain("Offer to move", silent.Description);
    }
}
