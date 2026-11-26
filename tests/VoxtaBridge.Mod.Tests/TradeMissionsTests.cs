using VoxtaBridge;
using Xunit;
using Xunit.Abstractions;

namespace VoxtaBridge.Mod.Tests;

public class TradeMissionsTests(ITestOutputHelper output)
{
    const string Ecf = """
{ +Dialogue Name: TD_PowerTest_Start
  NPCName: dialogue_npc
  Variable_1: "PowerMission", param1: dbplayerpoipos_int
  Variable_2: "CanTrade", param1: dbplayerpoipos_int
  Variable_3: "MoneyCounter", param1: int
  Variable_5: "TestJob", param1: dbplayer_int
  Next_1: TD_PowerTest_Trade
  NextIf_1: "CanTrade > 0"
  Next_2: TD_PowerTest_MissionRandom
  NextIf_2: "PowerMission == 0"
  Next_3: TD_PowerTest_WorkOrder
  NextIf_3: "HasItem('Token', 1, 4057)"
  Next_4: TD_PowerTest_MissionCheck
  NextIf_4: "PowerMission > 0"
}
{ +Dialogue Name: TD_PowerTest_Trade
  Option_1: dialogue_a
  OptionExecute_1: "OpenTraderWindow()"
  Option_2: dialogue_b
  OptionIf_2: "PowerMission == 0 && CanTrade < 3"
  OptionNext_2: TD_PowerTest_MissionBonus
}
{ +Dialogue Name: TD_PowerTest_MissionCheck
  Next_1: TD_PowerTest_Mission01
  NextIf_1: "PowerMission == 1"
  Next_2: TD_PowerTest_Mission02
  NextIf_2: "PowerMission == 2"
}
{ +Dialogue Name: TD_PowerTest_Mission01
  Output: dialogue_m1
  Option_1: dialogue_x
  OptionIf_1: "HasItem('CommunicationEquipment', 12)"
  OptionExecute_1: "RemoveItem('CommunicationEquipment', 12); CanTrade = CanTrade + 1; PowerMission = 0; AddReputation(Faction.Polaris, 150); AddItem('MoneyCard', 15000)"
  Option_2: dialogue_y
  OptionNext_2: End
}
{ +Dialogue Name: TD_PowerTest_Mission02
  Output: dialogue_m2
  Option_1: dialogue_x
  OptionIf_1: "HasItem('EnergyMatrix', 8) && HasItem('PowerCoil', 5)"
  OptionExecute_1: "RemoveItem('EnergyMatrix', 8); CanTrade = CanTrade + 1; PowerMission = 0; Player.Credit += 187000"
}
{ +Dialogue Name: GIN_Console_FreighterJob8
  Option_1: dialogue_g
  OptionExecute_1: "Player.Credit -= 80000; TestJob = TestJob - 1; AddItem('Token', 1, 4057)"
}
{ +Dialogue Name: TD_OpenShop_Start
  Variable_1: "ShopMission", param1: dbplayerpoipos_int
  Option_1: dialogue_a
  OptionExecute_1: "OpenTraderWindow()"
}
{ +Dialogue Name: TD_OpenShop_Mission01
  OptionIf_1: "HasItem('Token', 1, 133)"
  OptionExecute_1: "RemoveItem('Token', 1, 133); AddItem('MoneyCard', 19000); ShopMission = 6; if (GetFaction(LocalStructure.Entity.Id) == 'Polaris') AddReputation(Faction.Polaris, 200)"
}
""";

    static TradeMissions Load()
    {
        var t = new TradeMissions();
        t.LoadDialogues(new StringReader(Ecf));
        return t;
    }

    [Fact]
    public void Parses_family_missions_rewards_and_work_order()
    {
        var f = Load().ByStart("TD_PowerTest_Start")!;
        Assert.Equal("PowerMission", f.MissionVar);
        Assert.Equal("TestJob", f.JobVar);
        Assert.True(f.TradeGated);
        Assert.Equal(3, f.FollowUpsBelow);
        Assert.Equal(4057, f.WorkOrderToken);
        Assert.Equal(80000, f.WorkOrderPrice);
        Assert.Equal(2, f.Missions.Count);
        var m1 = f.Missions[1];
        Assert.Equal(("CommunicationEquipment", 12), (m1.Needs[0].Item, m1.Needs[0].Count));
        Assert.Equal(15000, m1.Money);
        Assert.Equal("MoneyCard", m1.MoneyAs);
        Assert.Equal(150, m1.Reputation["Polaris"]);
        Assert.True(m1.Unlocks);
        Assert.Equal(2, f.Missions[2].Needs.Count);
        Assert.Equal("Credits", f.Missions[2].MoneyAs);
    }

    [Fact]
    public void Ungated_shop_with_token_mission_and_own_faction_rep()
    {
        var f = Load().ByName("TD_OpenShop")!;
        Assert.False(f.TradeGated);
        Assert.Null(f.WorkOrderToken);
        var m = f.Missions[1];
        Assert.Equal(133, m.Needs[0].TokenId);
        Assert.Equal(200, m.Reputation["*"]);
        Assert.Equal("none", TradeMissions.State(f, new Dictionary<string, int> { ["ShopMission"] = 6 }).Status);
        Assert.Equal("open", TradeMissions.State(f, new Dictionary<string, int> { ["ShopMission"] = 1 }).Status);
    }

    [Fact]
    public void State_from_rows()
    {
        var f = Load().ByStart("TD_PowerTest_Start")!;
        var locked = TradeMissions.State(f, new Dictionary<string, int> { ["PowerMission"] = 1, ["CanTrade"] = 0 });
        Assert.Equal("open", locked.Status);
        Assert.False(locked.FollowUp);
        Assert.Equal(1, locked.Mission!.Number);
        var follow = TradeMissions.State(f, new Dictionary<string, int> { ["PowerMission"] = 2, ["CanTrade"] = 1 });
        Assert.True(follow.FollowUp);
        Assert.Equal("unlocked", TradeMissions.State(f, new Dictionary<string, int> { ["PowerMission"] = 0, ["CanTrade"] = 1 }).Status);
        Assert.Equal("locked", TradeMissions.State(f, new Dictionary<string, int>()).Status);
    }

    [Fact]
    public void Resolve_prefers_the_prefab_dialogue()
    {
        var t = Load();
        var vars = new Dictionary<string, int> { ["PowerMission"] = 1 };
        Assert.Equal("TD_PowerTest", t.Resolve(new[] { "TD_PowerTest_Start" }, vars)!.Name);
        Assert.Equal("TD_PowerTest", t.Resolve(null, vars)!.Name);
        Assert.Null(t.Resolve(new[] { "TD_OpenShop_Start" }, vars));
    }

    [Fact]
    public void Real_dialogues()
    {
        var scenario = Environment.GetEnvironmentVariable("GAMETEXT_SCENARIO");
        var path = scenario == null ? null : Path.Combine(scenario, "Content", "Configuration", "Dialogues.ecf");
        if (path == null || !File.Exists(path)) return;
        var t = new TradeMissions();
        using (var r = new StreamReader(path)) t.LoadDialogues(r);
        foreach (var f in t.Families.OrderBy(f => f.Name))
            output.WriteLine($"{f.Name}: {f.MissionVar} gated={f.TradeGated} job={f.JobVar} wo={f.WorkOrderToken}/{f.WorkOrderPrice} " +
                             string.Join(" | ", f.Missions.Values.Select(m => $"{m.Number}: {string.Join("+", m.Needs.Select(n => $"{n.Count} {n.Item}{(n.TokenId != null ? "#" + n.TokenId : "")}"))} → {m.Money} {m.MoneyAs} {string.Join(",", m.Reputation.Select(kv => kv.Key + kv.Value))}")));
        var solaris = t.ByName("TD_PowerSolaris")!;
        Assert.Equal(80000, solaris.WorkOrderPrice);
        Assert.Equal(5, solaris.Missions.Count);
        Assert.Equal(50, solaris.Missions[5].Needs[0].Count);
        Assert.Equal(50000, t.ByName("TD_IndustrialSmallPolaris")!.WorkOrderPrice);
        var foreman = t.ByName("Eden_PolarisForeman")!;
        Assert.Equal(("Eden_Mission", "Eden_Quantity", "Eden_Complete", true), (foreman.MissionVar, foreman.QuantityVar, foreman.DoneVar, foreman.TradeGated));
        Assert.Equal("EnergyCellLarge", foreman.Missions[1].Needs[0].Item);
        Assert.Equal(4, foreman.Missions.Count);
        output.WriteLine("quantity families: " + string.Join(", ", t.Families.Where(f => f.QuantityVar != null).Select(f => f.Name)));
    }

    // shaped like RE2's Mining Foreman (cut down)
    const string Foreman = """
        { +Dialogue Name: Eden_PolarisForeman_Init
          Next_1: Eden_PolarisForeman_Setup
          NextIf_1: "Eden_Mission == 0"
          Next_2: Eden_PolarisForeman_Greet
          Variable_1: "Eden_Complete", param1: dbplayerpoipos_int
          Variable_2: "Eden_Mission", param1: dbplayerpoipos_int
          Variable_3: "Eden_Quantity", param1: dbplayerpoipos_int
          Variable_4: "Type", param1: string
          Execute: "if (Eden_Mission == 1) Type = 'EnergyCellLarge'; if (Eden_Mission == 2) Type = 'MiningEquipment';"
          NPCName: eden_dialogue_foreman
        }
        { +Dialogue Name: Eden_PolarisForeman_Greet2
          OptionIf_1: "Eden_Complete == 1"
          OptionExecute_1: "OpenTraderWindow()"
        }
        { +Dialogue Name: Eden_PolarisForeman_Help
          OptionIf_1: "HasItem(Type, Eden_Quantity)"
          OptionNext_1: Eden_PolarisForeman_Complete
          Execute: "Eden_Complete = 2"
        }
        { +Dialogue Name: Eden_PolarisForeman_Complete
          Execute: "Eden_Complete = 1; RemoveItem(Type, Eden_Quantity); Player.Credit += Eden_RewardCredit"
        }
        """;

    [Fact]
    public void Foreman_one_rolled_mission_then_trades()
    {
        var t = new TradeMissions();
        t.LoadDialogues(new StringReader(Foreman));
        var f = t.ByStart("Eden_PolarisForeman_Init")!;
        Assert.True(f.TradeGated);
        // rolled: mission 2 (Mining Equipment), amount 5, asked for help (2)
        var open = TradeMissions.State(f, new Dictionary<string, int> { ["Eden_Mission"] = 2, ["Eden_Quantity"] = 5, ["Eden_Complete"] = 2 });
        Assert.Equal("open", open.Status);
        Assert.Equal(("MiningEquipment", 5), (open.Mission.Needs[0].Item, open.Mission.Needs[0].Count));
        Assert.Equal(0, open.CanTrade);
        // done: trades
        var done = TradeMissions.State(f, new Dictionary<string, int> { ["Eden_Mission"] = 2, ["Eden_Quantity"] = 5, ["Eden_Complete"] = 1 });
        Assert.Equal("unlocked", done.Status);
        Assert.True(done.CanTrade > 0);
        // a TD family with the same mission variable name doesn't get in the way: the prefab decides
        Assert.Same(f, t.Resolve(["Eden_PolarisForeman_Init"], new Dictionary<string, int> { ["Eden_Mission"] = 1 }));
    }

    [Fact]
    public void Real_prefab_links_its_trader_dialogue()
    {
        var path = Environment.GetEnvironmentVariable("GAMETEXT_PREFAB");
        if (path == null || !File.Exists(path)) return;
        var idx = new TraderIndex();
        idx.AddPrefab(Path.GetFileNameWithoutExtension(path), File.ReadAllBytes(path));
        var facts = idx.Facts(Path.GetFileNameWithoutExtension(path))!;
        output.WriteLine(string.Join(", ", facts.TradeDialogues));
        Assert.Contains(facts.TradeDialogues, d => d.StartsWith("TD_"));
    }
}
