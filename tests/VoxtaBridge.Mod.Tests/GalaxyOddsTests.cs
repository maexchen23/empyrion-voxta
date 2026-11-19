using VoxtaBridge;
using Xunit;
using Xunit.Abstractions;

namespace VoxtaBridge.Mod.Tests;

public class GalaxyOddsTests(ITestOutputHelper output)
{
    const string Galaxy = """
        { GalaxyConfig, Name: G Type Star   # comment
          StarClass: G1
          SolarSystemConfigSuffix: _MainHot
        }
        { GalaxyConfig, Name: Starting
          StarClass: GStartingSystem
        }
        { GalaxyConfig, Name: K
          StarClass: K2V
          SolarSystemConfigSuffix: _MainCold
        }
        { +Territories
          { Child Territory_1
            Faction: Polaris
            Center: "100, 0, 0"
            Radius: 50  # in LY
            Other_1: Pirates, param1: "0, 0.2"
            Other_2: Eden_PolarisTerritory, param1: "1, 1"
          }
          { Child Territory_None  # outside all
            Other_1: Zirax, param1: "1"
            Other_2: Trader, param1: "0.25"
          }
        }
        """;

    const string Defines = """
        GenerationParams:
          PlanetTypes:
              - Name: Desert
                Placement:
                    - HabitableHot
                MoonProb: '0:0.5, 2:0.5'
                MoonType: [MoonA, MoonB]
                OrbitType: [DefaultOrbit]
                Playfields:
                    - RareDesert, 0.1 # rare, before the others
                    - Desert, 1
          MoonTypes:
              - Name: MoonA
                Playfields:
                    - MoonA_V2
              - Name: MoonB
                Playfields:
                    - MoonB_V2
          OrbitTypes:
              - Name: DefaultOrbit
                Playfields:
                    - SpaceOrbit
          POITypes:
              - Name: Station
                Playfields:
                    - TradeStation
        """;

    const string Hot = """
        GenerationParams:
          PlanetCountProbs:
             - '0 : 0.5,   1 : 0.5'                 # 1 - Desert
             - '0 : 1.0'
          PlanetLists:
            - 'Desert:0.5, Other:0.5'
            - 'Desert:1'
          POICountProbs: '0 : 0.5, 2 : 0.5'
          POIs: 'Station: 1.0'
        """;

    static GalaxyOdds Odds()
    {
        var o = new GalaxyOdds();
        o.LoadGalaxyConfig(Galaxy);
        o.AddPreset("SolarSystemConfig_Defines", Defines);
        o.AddPreset("SolarSystemConfig_MainHot", Hot);
        return o;
    }

    [Fact]
    public void Star_classes_in_file_order()
    {
        var o = Odds();
        Assert.Equal(3, o.ClassCount);
        Assert.Equal("G1", o.Class(0).StarClass);
        Assert.Null(o.Class(1).Suffix);
        Assert.Equal("SolarSystemConfig_MainCold", GalaxyOdds.PresetName(o.Class(2)));
    }

    [Fact]
    public void Territory_rules()
    {
        var o = Odds();
        Assert.Single(o.Territories);
        Assert.NotNull(o.Neutral);
        var centre = o.TerritoriesAt(100, 0, 0);
        var edge = o.TerritoriesAt(150, 0, 0);
        var neutral = o.TerritoriesAt(0, 0, 0);
        Assert.Equal("Territory_None", Assert.Single(neutral).t.Name);

        // no FactionTerritory: own faction must be allowed there
        Assert.Equal(1, GalaxyOdds.SpawnAllowed("Polaris", null, centre));
        Assert.Equal(0, GalaxyOdds.SpawnAllowed("Polaris", null, neutral));
        Assert.Equal(0.25, GalaxyOdds.SpawnAllowed("Trader", null, neutral), 6);
        Assert.Equal(0.2, GalaxyOdds.SpawnAllowed("Pirates", null, edge), 6);
        Assert.Equal(0, GalaxyOdds.SpawnAllowed("Pirates", null, centre));
        Assert.Equal(1, GalaxyOdds.SpawnAllowed("None", null, neutral));
        // FactionTerritory lists, All, True
        Assert.Equal(1, GalaxyOdds.SpawnAllowed("Polaris", ["Eden_PolarisTerritory"], centre));
        Assert.Equal(0, GalaxyOdds.SpawnAllowed("Polaris", ["Eden_PolarisTerritory"], neutral));
        Assert.Equal(1, GalaxyOdds.SpawnAllowed("Rados", ["Zirax"], neutral));
        Assert.Equal(1, GalaxyOdds.SpawnAllowed("Polaris", ["All"], neutral));
        Assert.Equal(1, GalaxyOdds.SpawnAllowed("Polaris", ["True"], centre));
        Assert.Equal(0, GalaxyOdds.SpawnAllowed("Polaris", ["True"], neutral));
    }

    [Fact]
    public void Expected_playfields_per_system()
    {
        var e = Odds().Expected("SolarSystemConfig_MainHot");
        // 0.5 planets × 0.5 Desert = 0.25 Desert-type planets; the second list never rolls a planet
        Assert.Equal(0.025, e["RareDesert"], 6);
        Assert.Equal(0.225, e["Desert"], 6);
        // moons: 1 expected per desert planet, split over two moon types
        Assert.Equal(0.125, e["MoonA_V2"], 6);
        Assert.Equal(0.25, e["SpaceOrbit"], 6);
        Assert.Equal(1.0, e["TradeStation"], 6);
        Assert.Empty(Odds().Expected("SolarSystemConfig_Missing"));
    }

    [Fact]
    public void Helpers()
    {
        Assert.Equal(1.3, GalaxyOdds.ExpectedCount("0 : 0.2, 1 : 0.3, 2 : 0.5"), 6);
        var w = GalaxyOdds.Weights("A:0.3, B:0.1, C:0.0");
        Assert.Equal(["A", "B"], w.Select(x => x.name));
        Assert.Equal(0.75, w[0].weight, 6);
        var pick = GalaxyOdds.PickInOrder([("a", 0.05), ("b", 0.5), ("c", 1)]);
        Assert.Equal(0.05, pick[0].p, 6);
        Assert.Equal(0.475, pick[1].p, 6);
        Assert.Equal(0.475, pick[2].p, 6);
        Assert.Equal(0.632, GalaxyOdds.AtLeastOne(1), 3);
    }

    /// <summary>Opt-in: TRADERS_ROOT (scenario copy with RandomPresets) and GALAXY_CONFIG (GalaxyConfig.ecf).</summary>
    [Fact]
    public void Real_files_reactor_core_chance_per_class()
    {
        var root = Environment.GetEnvironmentVariable("TRADERS_ROOT");
        var gc = Environment.GetEnvironmentVariable("GALAXY_CONFIG");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(gc)) return;
        var o = new GalaxyOdds();
        o.LoadGalaxyConfig(File.ReadAllText(gc));
        foreach (var f in Directory.GetFiles(Path.Combine(root, "RandomPresets"), "SolarSystemConfig*.yaml"))
            o.AddPreset(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f));
        output.WriteLine($"{o.ClassCount} classes, {o.PresetCount} presets; 1={o.Class(1).StarClass} 15={o.Class(15).StarClass} 28={o.Class(28).StarClass} 82={o.Class(82).StarClass}");
        Assert.Equal("K2V", o.Class(28).StarClass);
        Assert.Equal("A4V", o.Class(82).StarClass);

        var idx = new TraderIndex();
        foreach (var dir in Directory.GetDirectories(Path.Combine(root, "Playfields")))
            foreach (var f in Directory.GetFiles(dir, "*.yaml")) idx.LoadPlayfield(Path.GetFileName(dir), File.ReadAllText(f));
        foreach (var group in new[] { "R2FactoryPolaris3", "R2FactoryPolaris1" })
        {
            var hit = idx.Spawns(group).GroupBy(s => s.Playfield).ToDictionary(g => g.Key, g => g.Max(s => s.Chance));
            foreach (var preset in new[] { "SolarSystemConfig_MainHotT1", "SolarSystemConfig_MainHabitableT1", "SolarSystemConfig_MainColdT1", "SolarSystemConfig_FactionTraderT1" })
            {
                var e = o.Expected(preset);
                var lambda = e.Sum(kv => hit.TryGetValue(kv.Key, out var h) ? kv.Value * h : 0);
                output.WriteLine($"{group} in {preset}: {e.Count} templates, chance {GalaxyOdds.AtLeastOne(lambda):P0}");
            }
        }
    }
}
