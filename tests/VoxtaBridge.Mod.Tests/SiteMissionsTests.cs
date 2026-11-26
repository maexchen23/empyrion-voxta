using System.IO;
using System.Linq;
using VoxtaBridge;
using Xunit;
using Xunit.Abstractions;

public class SiteMissionsTests(ITestOutputHelper output)
{
    // shaped like RE2's Eden_PolMinerMissionT2 / Eden_MiningT4_2A (cut down)
    const string Ecf = """
        { +Dialogue Name: Site_Init
          Variable_1: "Eden_Complete", param1: dbplayerpoi_int
          Next_1: Site_Deny
          NextIf_1: "Eden_Complete == 1"
          Next_2: Site_Deny2
          NextIf_2: "GetReputation(Faction.Polaris) < Reputation.NeutralMin"
          Next_3: Site_Greet
        }
        { +Dialogue Name: Site_Check
          Execute: "var requiredItems = new List<(string, int)> {<nl>    ('Eden_IlmeniteOre', 6000),<nl>};<nl>HandleQuestItemDrop('CargoDrop', requiredItems)"
          Next_1: Site_Reward
          NextIf_1: "CargoComplete == 1"
        }
        { +Dialogue Name: Site_Reward
          Execute: "Eden_Complete = 1; Player.Credit += 200000;<nl>AddReputation(Faction.Polaris, Random(250,375))"
        }
        { +Dialogue Name: Var_Init
          Variable_1: "Eden_Complete", param1: dbplayerpoipos_int
          NextIf_1: "Eden_Complete == 1"
          Execute: "var playfield = ModManager.ModApi.ClientPlayfield; Quantity = 100"
        }
        { +Dialogue Name: Var_Check
          Execute: "var requiredItems = new List<(string, int)> {<nl>    ('Eden_ZacositeOre', Quantity),<nl>};<nl>HandleQuestItemDrop('CargoDrop', requiredItems)"
        }
        { +Dialogue Name: Var_Reward
          Execute: "Eden_Complete = 1; AddItem('PlatinumCard', 65)"
        }
        { +Dialogue Name: Computed_Init
          Variable_1: "Eden_Complete", param1: dbplayerpoi_int
          NextIf_1: "Eden_Complete == 1"
        }
        { +Dialogue Name: Computed_Check
          Execute: "var requiredItems = new List<(string, int)> {<nl>    (OreType, OreCount),<nl>};<nl>HandleQuestItemDrop('CargoDrop', requiredItems)"
        }
        """;

    static SiteMissions Load()
    {
        var s = new SiteMissions();
        s.LoadDialogues(new StringReader(Ecf));
        return s;
    }

    [Fact]
    public void Fixed_delivery_job_with_gate_and_reward()
    {
        var m = Load().ByStart("Site_Init")!;
        Assert.Equal([("Eden_IlmeniteOre", 6000)], m.Needs);
        Assert.Equal(("CargoDrop", "Eden_Complete", 200000, false), (m.Container, m.DoneVar, m.Credits, m.RewardChoice));
        Assert.Equal(("Polaris", "Neutral"), (m.RepFaction, m.RepMinTier));
    }

    [Fact]
    public void Amount_from_a_variable_the_family_sets_computed_lists_are_skipped()
    {
        var s = Load();
        Assert.Equal([("Eden_ZacositeOre", 100)], s.ByStart("Var_Init")!.Needs);
        Assert.Null(s.ByStart("Computed_Init")); // the refinery hand-in: items from variables, not a fixed job
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void Real_files()
    {
        var scenario = System.Environment.GetEnvironmentVariable("GAMETEXT_SCENARIO");
        var dlg = scenario == null ? null : Path.Combine(scenario, "Content", "Configuration", "Dialogues.ecf");
        if (dlg == null || !File.Exists(dlg)) return;
        var s = new SiteMissions();
        using (var r = new StreamReader(dlg)) s.LoadDialogues(r);
        foreach (var m in s.All)
            output.WriteLine($"{m.Start}: {string.Join(" + ", m.Needs.Select(n => $"{n.Count} {n.Item}"))} into {m.Container}, done={m.DoneVar}, " +
                             $"{m.Credits} CR{(m.RewardChoice ? " (choice)" : "")}, rep {m.RepFaction} {m.RepMinTier}");
        Assert.Equal([("Eden_IlmeniteOre", 6000)], s.ByStart("Eden_PolMinerMissionT2_Init")!.Needs);
        Assert.Equal(200000, s.ByStart("Eden_PolMinerMissionT2_Init")!.Credits);
        Assert.Equal([("Eden_ChromiteOre", 30000)], s.ByStart("Eden_MiningT4_1A_Init")!.Needs);
    }
}
