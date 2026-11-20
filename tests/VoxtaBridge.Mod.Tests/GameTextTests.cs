using VoxtaBridge;
using Xunit;

namespace VoxtaBridge.Mod.Tests;

/// <summary>Samples copied from RE2's Localization.csv and ECF files (shortened).</summary>
public class GameTextTests
{
    private const string Csv =
        "KEY,English,Deutsch\n" +
        "RadarSuitT1,Detector,Detektor\n" +
        "CombatKnife,[c][37D2FF]Combat Knife[-][/c],\n" +
        "Pistol,[c][37D2FF]STN-33 Pistol[-][/c],[c][37D2FF]STN-33 Pistole[-][/c]\n" +
        "bkiDetector,A fixed radar dish inside a protective polymer dome.\\n[c][00FF00]Primary Scan (LMB):[-][/c] Detects objects at long range.,x\n" +
        "Multi,\"Line one,\nline \"\"two\"\"\",y\n";

    private const string Items = """
        # comment
        { +Item Id: 702, Name: RadarSuitT1
          Info: bkiDetector, display: true
          { Child 0
            Info: notThisOne
          }
        }
        { +Item Id: 10, Name: Pistol
          Info: bkiPistol
        }
        { +Item Id: 20, Name: PistolT2, Ref: Pistol
        }
        { +Item Id: 1305, Name: Token
          Class: Token
        }
        """;

    private const string Tokens = """
        { Token Id: 120, Name: [c][0088FF]Trading Card[/c][-][b] - [c][AAFF00]Akua[/c][/b]
          CustomIcon: TradingCard01
          Description: A collectible Location trading card.\n[c][FFFFFF]Akua is a temperate planet.[-][/c] 01/22
        }
        """;

    private static GameText Load()
    {
        var t = new GameText();
        t.LoadLocalizationCsv(new StringReader(Csv));
        t.LoadEcf(new StringReader(Items));
        t.LoadEcf(new StringReader(Tokens));
        return t;
    }

    [Theory]
    [InlineData("RadarSuitT1", "Detector")]
    [InlineData("CombatKnife", "Combat Knife")]
    [InlineData("Pistol", "STN-33 Pistol")]
    [InlineData("PistolT2", "STN-33 Pistol")] // no own row: parent's name via Ref
    [InlineData("Unknown", null)]
    public void Display_names(string name, string expected)
    {
        Assert.Equal(expected, Load().DisplayName(name));
    }

    [Fact]
    public void Description_via_info_key_without_markup()
    {
        Assert.Equal("A fixed radar dish inside a protective polymer dome. Primary Scan (LMB): Detects objects at long range.",
            Load().Description("RadarSuitT1"));
    }

    [Fact]
    public void Nested_child_blocks_do_not_override_info()
    {
        Assert.StartsWith("A fixed radar dish", Load().Description("RadarSuitT1"));
    }

    [Fact]
    public void Tokens_by_id_with_clean_name_and_description()
    {
        var t = Load();
        Assert.True(t.IsToken("Token"));
        Assert.False(t.IsToken("Pistol"));
        var token = t.GetToken(120);
        Assert.Equal("Trading Card - Akua", token.Name);
        Assert.Equal("A collectible Location trading card. Akua is a temperate planet. 01/22", token.Description);
        Assert.Null(t.GetToken(999));
    }

    [Fact]
    public void Long_token_descriptions_are_kept_whole()
    {
        // Like RE2's Gameplay Guide token (~1000 chars, several sections).
        var sections = string.Join("\\n\\n", Enumerable.Range(1, 12).Select(i => $"[c][FFFF00]Section {i}:[-][/c] Some explanation of topic {i} that runs for a while."));
        var t = new GameText();
        t.LoadEcf(new StringReader($"{{ Token Id: 6999, Name: Guide\n  # Description: commented out, ignored\n  Description: {sections}\n}}\n"));

        var desc = t.GetToken(6999).Description;
        Assert.StartsWith("Section 1:", desc);
        Assert.EndsWith("topic 12 that runs for a while.", desc);
    }

    [Fact]
    public void Runaway_descriptions_are_cut_at_a_sentence()
    {
        var text = string.Concat(Enumerable.Repeat("This is one sentence. ", 200));
        var t = new GameText();
        t.LoadEcf(new StringReader($"{{ Token Id: 1, Name: Long\n  Description: {text}\n}}\n"));

        var desc = t.GetToken(1).Description;
        Assert.True(desc.Length <= GameText.MaxDescription);
        Assert.EndsWith("sentence.", desc);
    }

    [Fact]
    public void Csv_quoted_fields_with_commas_quotes_and_newlines()
    {
        var t = Load();
        Assert.Equal("Line one, line \"two\"", t.DisplayName("Multi"));
        Assert.Equal("Detector", t.DisplayName("RadarSuitT1")); // rows after it still parse
    }

    [Fact]
    public void Scenario_overrides_base()
    {
        var t = Load();
        t.LoadLocalizationCsv(new StringReader("KEY,English\nRadarSuitT1,Scenario Detector\n"));
        Assert.Equal("Scenario Detector", t.DisplayName("RadarSuitT1"));
    }

    [Fact]
    public void Clean_drops_empty_bullet_lines_but_keeps_single_dashes()
    {
        Assert.Equal("RMB to set: Mining. Check the wheel.", GameText.Clean("RMB to set: Mining.\\n-\\n-\\nCheck the wheel."));
        Assert.Equal("Trading Card - Akua", GameText.Clean("Trading Card - Akua"));
    }

    [Theory]
    [InlineData("  \"GameConfig\": {\n    \"Seed\": 123456,\n    \"CustomScenario\": \"Reforged Eden 2\"\n  }", "Reforged Eden 2")] // AMP's JSON-style YAML
    [InlineData("GameConfig:\n  CustomScenario: Reforged Eden 2\n  Seed: 1", "Reforged Eden 2")]          // plain YAML
    [InlineData("GameConfig:\n  CustomScenario: 'Default Akua-Omicron'  \n", "Default Akua-Omicron")]
    [InlineData("GameConfig:\n  # CustomScenario: Foo\n  Seed: 1", null)]
    [InlineData("GameConfig:\n  CustomScenario: \"\"\n", null)]
    [InlineData("", null)]
    public void Scenario_from_dedicated_config(string config, string expected)
    {
        Assert.Equal(expected, GameText.ScenarioFromDedicatedConfig(config));
    }

    [Fact]
    public void Config_file_from_command_line()
    {
        Assert.Equal("empyriondedicated.yaml",
            GameText.ConfigFileFromArgs(["EmpyrionPlayfieldServer.exe", "-batchmode", "configFile=empyriondedicated.yaml"]));
        Assert.Equal("my config.yaml", GameText.ConfigFileFromArgs(["x.exe", "\"configFile=my config.yaml\""]));
        Assert.Equal("empyriondedicated.yaml",
            GameText.ConfigFileFromArgs(["EmpyrionDedicated.exe", "-batchmode", "-nographics", "-dedicated", "empyriondedicated.yaml", "-logFile", "-"]));
        Assert.Null(GameText.ConfigFileFromArgs(["x.exe", "-batchmode"]));
        Assert.Null(GameText.ConfigFileFromArgs(["x.exe", "-dedicated", "-logFile"]));
    }

    [Fact]
    public void Clean_keeps_brackets_that_are_not_markup()
    {
        Assert.Equal("Ammo [x50] pack", GameText.Clean("[c][FFAA00]Ammo [x50] pack[-][/c]"));
    }

    [Fact]
    public void Load_from_folders_skips_missing_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "gametext-" + Guid.NewGuid());
        var content = Path.Combine(root, "Content");
        var scenario = Path.Combine(root, "Scenario");
        Directory.CreateDirectory(Path.Combine(content, "Extras"));
        Directory.CreateDirectory(Path.Combine(scenario, "Content", "Configuration"));
        File.WriteAllText(Path.Combine(content, "Extras", "Localization.csv"), Csv);
        File.WriteAllText(Path.Combine(scenario, "Content", "Configuration", "TokenConfig.ecf"), Tokens);
        try
        {
            var logs = new List<string>();
            var t = GameText.Load(content, scenario, logs.Add);
            Assert.Equal("Detector", t.DisplayName("RadarSuitT1"));
            Assert.Equal("Trading Card - Akua", t.GetToken(120).Name);
            Assert.Equal(2, logs.Count);
        }
        finally { Directory.Delete(root, true); }
    }
}

public class GameTextVolumeTests
{
    [Fact]
    public void Volume_and_stack_size_inherited_via_ref()
    {
        var t = new GameText();
        t.LoadEcf(new StringReader("""
            { +Item Id: 1, Name: ComponentsTemplate
              Volume: 0.75, type: float, display: true, formatter: Liter
              StackSize: 999
            }
            { +Item Id: 77, Name: IronIngot, Ref: ComponentsTemplate
            }
            { +Item Id: 20, Name: Pistol
              StackSize: 1
            }
            { Block Id: 273, Name: ContainerMS01
              LootList: 66    # 128 slots
            }
            """));
        Assert.Equal(0.75, t.Volume("IronIngot"));
        Assert.Equal(999, t.StackSize("IronIngot"));
        Assert.Equal(1, t.StackSize("Pistol"));
        Assert.Equal(0, t.Volume("Pistol"));
        Assert.Equal(128, t.ContainerSlots("ContainerMS01"));
        Assert.Equal(64, t.ContainerSlots("Unknown"));
    }

    [Fact]
    public void Magazine_size_per_ammo_type_from_firing_modes()
    {
        var t = new GameText();
        t.LoadEcf(new System.IO.StringReader(string.Join("\n",
            "{ Item Id: 1, Name: Rifle",
            "  { Child 0",
            "    AmmoCapacity: 30, display: true",
            "    AmmoType: 5.56mmBullet, display: true",
            "  }",
            "  { Child 1",
            "    AmmoType: Grenade, display: true",
            "    AmmoCapacity: 4",
            "  }",
            "}",
            "{ Item Id: 2, Name: RifleEpic, Ref: Rifle",
            "}",
            "{ Item Id: 3, Name: Club",
            "}",
            "{ Item Id: 4, Name: LaserMinigun",
            "  { Child 0",
            "    AmmoCapacity: 250, display: true",
            "    AmmoType: PulseLaserChargeSS, display: true",
            "    UseSingleMagazine: true",
            "  }",
            "}")));
        Assert.Equal(30, t.MagazineOf("Rifle", "5.56mmBullet"));
        Assert.Equal(4, t.MagazineOf("Rifle", "Grenade"));
        Assert.Equal(30, t.MagazineOf("RifleEpic", "5.56mmBullet"));
        Assert.Equal(0, t.MagazineOf("Club", "5.56mmBullet"));
        Assert.Equal((250, true), t.Magazine("LaserMinigun", "PulseLaserChargeSS"));
        Assert.Equal((30, false), t.Magazine("Rifle", "5.56mmBullet"));
    }
}

public class GameTextHeaderTests
{
    [Fact]
    public void Blocks_without_an_id_are_read()
    {
        var t = new GameText();
        t.LoadLocalizationCsv(new StringReader("KEY,English\nTestJumpDriveCV,Large Test Jump Drive\n"));
        t.LoadEcf(new StringReader("""
            { Block Name: TestJumpDriveCV
              Class: Container
              Category: Devices
            }
            { Block Name: TestJumpDriveCVPlain, Ref: TestJumpDriveCV
            }
            { Token Name: NoId
            }
            """));
        Assert.Contains("TestJumpDriveCV", t.AllNames);
        Assert.True(t.IsPlayerFacing("TestJumpDriveCV"));
        Assert.Equal("Large Test Jump Drive", t.DisplayName("TestJumpDriveCVPlain"));
        Assert.DoesNotContain("NoId", t.AllNames);
    }

    [Fact]
    public void TankAcceptListsAndValues()
    {
        var t = new GameText();
        t.LoadEcf(new StringReader("""
            { +Item Id: 218, Name: EnergyCell
              FuelValue: 50, type: int, display: true, formatter: WattHour
            }
            { +Item Id: 63, Name: BioFuel
              FuelValue: 15, type: int, display: true, formatter: WattHour
            }
            { +Item Id: 80, Name: OxygenBottleLarge
              O2Value: 250, display: true, type: int  #   O2Value should be multiples of 200
            }
            { +Block Id: 1, Name: FuelTankA
              FuelAccept: "EnergyCell, EnergyCellLarge, FusionCell, BioFuel, BioFuelT2"
            }
            { +Block Id: 2, Name: FuelTankB
              FuelAccept: "EnergyCell, EnergyCellHydrogen"
            }
            { +Block Id: 3, Name: FuelTankC
              FuelAccept: "Eden_IndustrialFuel"
            }
            { +Block Id: 4, Name: O2TankA
              O2Accept: "OxygenBottleLarge,OxygenBottleSmall,RespiratorCharge"
            }
            { +Block Id: 5, Name: WarpTankA
              WarpAccept: PentaxidCrystal
            }
            """));
        Assert.Contains("BioFuel", t.TankAccepts("fuel"));
        Assert.Contains("EnergyCellHydrogen", t.TankAccepts("fuel"));
        Assert.Contains("Eden_IndustrialFuel", t.TankAccepts("fuel"));
        Assert.Equal("EnergyCell", t.TankDrainItem("fuel"));
        Assert.Equal("OxygenBottleLarge", t.TankDrainItem("oxygen"));
        Assert.Equal("PentaxidCrystal", t.TankDrainItem("pentaxid"));
        Assert.Equal(50, t.TankValue("fuel", "EnergyCell"));
        Assert.Equal(15, t.TankValue("fuel", "BioFuel"));
        Assert.Equal(250, t.TankValue("oxygen", "OxygenBottleLarge"));
        Assert.Equal(1, t.TankValue("pentaxid", "PentaxidCrystal"));
        Assert.Equal(0, t.TankValue("fuel", "OxygenBottleLarge"));
    }
}
