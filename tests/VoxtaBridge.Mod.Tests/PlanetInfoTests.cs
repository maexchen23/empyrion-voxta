using System.IO;
using VoxtaBridge;
using Xunit;
using Xunit.Abstractions;

namespace VoxtaBridge.Mod.Tests;

public class PlanetInfoTests(ITestOutputHelper output)
{
    const string Yaml = """
        PlayfieldType: Planet
        Gravity: -8.84105206
        AtmosphereDensity: 0.728964388
        AtmosphereO2: 0.161059588
        TemperatureNight: 54
        Radiation: 2.89823151
        PlanetType: Stormy Desert
        Description: >-
          Difficulty: [c][ffff00]Medium[-][/c]

          Sensor Interference: [c][ffff00]POI detection range reduced.[-][/c]


          A desert planet, devoid of any natural sources of water. A global dust storm engulfs the entire planet, lowering visibility.
        Seed: 535675
        TemperatureMinMax:
        - 10
        - 64
        RandomResources:
        - Name: CobaltResource
          IsImportant: true
          CountMinMax:
          - 1
          - 2
        - Name: CopperResource
          CountMinMax:
          - 2
          - 3
        Biome:
        - Name: Wadi
        """;

    [Fact]
    public void Description_values_and_resources()
    {
        var p = PlanetInfo.Parse(Yaml);
        Assert.Equal("Medium", p.Difficulty);
        Assert.Equal(new[] { "Sensor Interference: POI detection range reduced." }, p.Effects);
        Assert.Equal("A desert planet, devoid of any natural sources of water. A global dust storm engulfs the entire planet, lowering visibility.", p.Description);
        Assert.Equal(0.90, p.GravityG!.Value, 2);
        Assert.Equal(0.161, p.AtmosphereO2!.Value, 3);
        Assert.Equal((10.0, 64.0), (p.TemperatureMin!.Value, p.TemperatureMax!.Value));
        Assert.Equal(new[] { "CobaltResource", "CopperResource" }, p.Resources);
        Assert.Equal("Stormy Desert", p.PlanetType);
        Assert.False(p.Breathable); // no AtmosphereBreathable key: not breathable
        Assert.True(PlanetInfo.Parse("AtmosphereO2: 0.2175\nAtmosphereBreathable: true\n").Breathable);
        Assert.False(PlanetInfo.Parse("AtmosphereBreathable: False\n").Breathable);
    }

    [Fact]
    public void Empty_or_missing() => Assert.Null(PlanetInfo.Parse("").Description);

    /// <summary>Opt-in: PLANET_YAML = a save's Templates/&lt;playfield&gt;/playfield.yaml.</summary>
    [Fact]
    public void Real_template()
    {
        var f = System.Environment.GetEnvironmentVariable("PLANET_YAML");
        if (string.IsNullOrEmpty(f)) return;
        var p = PlanetInfo.Parse(File.ReadAllText(f));
        output.WriteLine($"{p.PlanetType}: {p.Difficulty}; {string.Join(" / ", p.Effects)}; {p.Description}");
        output.WriteLine($"g {p.GravityG:0.00}, atm {p.AtmosphereDensity:0.00}, O2 {p.AtmosphereO2:0.00}, T {p.TemperatureMin}..{p.TemperatureMax} night {p.TemperatureNight}, rad {p.Radiation:0.0}; {string.Join(", ", p.Resources)}");
    }
}
