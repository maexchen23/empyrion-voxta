using VoxtaBridge;
using Xunit;

namespace VoxtaBridge.Mod.Tests;

public class MiningContractsTests
{
    // shaped like RE2's Eden_ContractMining_* dialogues (cut down)
    const string Ecf = """
{ +Dialogue Name: Eden_ContractMiningOffer_Init
  Variable_1: "Eden_ContractCooldown", param1: dbplayerpoipos_int
  Next_1: Eden_ContractMiningOffer_Deny2
  NextIf_1: "GetReputation(Faction.Polaris) < Reputation.NeutralMin"
  Next_2: Eden_ContractMiningOffer_Deny
  NextIf_2: "GetGameTimeInSec() < Eden_ContractCooldown"
  Next_3: Eden_ContractMiningOffer_GreetT2
  NextIf_3: "GetReputation(Faction.Polaris) >= Reputation.HonoredMin"
  Next_4: Eden_ContractMiningOffer_Greet
}
{ +Dialogue Name: Eden_ContractMiningOffer_Greet
  Option_1: eden_dialogue_a
  OptionNext_1: Eden_ContractMiningOffer_Given
  OptionExecute_1: "AddItemsFromContainer(688, 1)"
}
{ +Dialogue Name: Eden_ContractMiningOffer_GreetT2
  Option_1: eden_dialogue_b
  OptionNext_1: Eden_ContractMiningOffer_Given
  OptionExecute_1: "AddItemsFromContainer(689, 1)"
  Option_2: eden_dialogue_c
  OptionNext_2: Eden_ContractMiningOffer_Given
  OptionExecute_2: "AddItemsFromContainer(690, 1)"
}
{ +Dialogue Name: Eden_ContractMiningOffer_Given
  Output: eden_dialogue_d
  Execute: "Eden_ContractCooldown = (GetGameTimeInSec() + 7200)"
}
{ +Dialogue Name: Eden_ContractMining_Greet
  Option_1: eden_dialogue_e
  OptionNext_1: Eden_ContractMining_Polaris
  OptionExecute_1: "RewardRepFaction = 'Polaris Megacorp'"
  Option_2: eden_dialogue_f
  OptionNext_2: Eden_ContractMining_Zirax
  OptionExecute_2: "RewardRepFaction = 'Zirax Empire'"
  Option_3: eden_dialogue_g
  OptionNext_3: End
}
{ +Dialogue Name: Eden_ContractMining_Polaris
  Option_1: eden_dialogue_h
  OptionIf_1: "HasItem('Token', 1, 6821)"
  OptionNext_1: Eden_ContractMining_Check
  OptionExecute_1: "TokenID = 6821; OreType = 'TitanOre'; OreCount = 8000; RewardCredit = 720000; RewardRepCount = 500"
  Option_2: eden_dialogue_i
  OptionIf_2: "HasItem('Token', 1, 6829)"
  OptionNext_2: Eden_ContractMining_Check2
  OptionExecute_2: "TokenID = 6829; OreType = 'IronOre'; OreCount = 4000; Ore2Type = 'CopperOre'; Ore2Count = 2000; RewardCredit = 350000; RewardRepCount = 500"
}
{ +Dialogue Name: Eden_ContractMining_Zirax
  Option_1: eden_dialogue_j
  OptionIf_1: "HasItem('Token', 1, 6860)"
  OptionNext_1: Eden_ContractMining_Check
  OptionExecute_1: "TokenID = 6860; OreType = 'MagnesiumOre'; OreCount = 8000; RewardCredit = 800000; RewardRepCount = 500"
  Option_2: eden_dialogue_k
  OptionIf_2: "HasItem('Token', 1, 6866)"
  OptionNext_2: Eden_ContractMining_Check
  OptionExecute_2: "TokenID = 6866; OreType = 'TitanOre'; OreCount = 16000; RewardCredit = 1440000; RewardRepCount = 500"
}
{ +Dialogue Name: TD_Unrelated_Start
  OptionExecute_1: "TokenID = 1; OreType = 'Nope'; OreCount = 1"
}
""";

    static MiningContracts Load()
    {
        var m = new MiningContracts();
        m.LoadDialogues(new StringReader(Ecf));
        return m;
    }

    static string Display(string item) => item switch
    {
        "TitanOre" => "Titanium Ore",
        "MagnesiumOre" => "Magnesium Ore",
        _ => null,
    };

    [Fact]
    public void Contracts_per_faction_branch()
    {
        var m = Load();
        Assert.Equal(4, m.Count);
        var mag = m.ByToken(6860)!;
        Assert.Equal("Zirax Empire", mag.Faction);
        Assert.Equal(("MagnesiumOre", 8000), Assert.Single(mag.Ores));
        Assert.Equal(800000, mag.Credits);
        Assert.Equal(500, mag.Reputation);
        Assert.Equal("Polaris Megacorp", m.ByToken(6821)!.Faction);
        Assert.Null(m.ByToken(1)); // other dialogues are ignored
    }

    [Fact]
    public void Multi_ore_contract_keeps_every_ore()
    {
        var two = Load().ByToken(6829)!;
        Assert.Equal(new[] { ("IronOre", 4000), ("CopperOre", 2000) }, two.Ores);
    }

    [Fact]
    public void Offer_rules()
    {
        var m = Load();
        Assert.Equal(7200, m.CooldownSeconds);
        Assert.Equal("Polaris", m.OfferFaction);
        Assert.Equal("Eden_ContractCooldown", m.CooldownVar);
        Assert.Equal("HonoredMin", m.ChoiceFromTier);
    }

    [Fact]
    public void Find_by_ore_display_name_and_faction()
    {
        var m = Load();
        Assert.Equal(6860, Assert.Single(m.Find("magnesium", null, Display)).TokenId);
        Assert.Equal(new[] { 6821, 6866 }, m.Find("titanium", null, Display).Select(c => c.TokenId)); // display name
        Assert.Equal(new[] { 6821, 6866 }, m.Find("titan", null, null).Select(c => c.TokenId));        // internal name
        Assert.Equal(6866, Assert.Single(m.Find("titan", "zirax", Display)).TokenId);
        Assert.Equal(2, m.Find(null, "Polaris", Display).Count);
        Assert.Equal(4, m.Find(" ", null, Display).Count);
        Assert.Empty(m.Find("gold", null, Display));
    }

    [Fact]
    public void Hand_in_container_names_from_quest_dialogues()
    {
        const string dialogues = """
{ +Dialogue Name: Eden_PolMinerMissionT2_Check
  Execute: "var requiredItems = new List<(string, int)> {<nl>    ('Eden_IlmeniteOre', 6000),<nl>};<nl>HandleQuestItemDrop('CargoDrop', requiredItems)"
}
{ +Dialogue Name: Eden_ContractMining_Check
  Execute: "HandleQuestItemDrop('CargoDrop', requiredItems)"
}
{ +Dialogue Name: Eden_Other_Check
  Execute: "HandleQuestItemDrop( 'Quest Box', requiredItems)"
  Functions: "void HandleQuestItemDrop(<nl>    string questContainerName,<nl>)"
}
""";
        Assert.Equal(new[] { "CargoDrop", "Quest Box" }, MiningContracts.DropContainerNames(new StringReader(dialogues)));
    }
}
