using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class MicroJumpTests
{
    private static string Render(string json) => GameView.MicroJumpResult(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Aimed_plan_names_target_cost_and_asks()
    {
        var text = Render("""
            {"targetDistance":28139,"stopAt":572,"targetRadius":72,"radiusSource":"remembered","ok":true,"ship":"Test Corvette","shipId":4091,
             "shipType":"SV","target":"Test Hauler","targetKind":"entity","jump":27567,"cost":5,"pentaxid":180,"container":"Pentaxid Tank @ Test Hauler","maxRange":30000,
             "land":[1,2,3],"rotation":[0,0,0],"planned":true}
            """);
        Assert.Contains("Micro jump plan for Test Corvette: 27.6 km to Test Hauler (object, 28.1 km away), stopping 572 m from its centre.", text);
        Assert.Contains("Cost: 5 Refined Pentaxid from Pentaxid Tank @ Test Hauler (has 180)", text);
        Assert.Contains("target='Test Hauler'", text);
    }

    [Fact]
    public void Straight_plan_with_cooldown()
    {
        var text = Render("""
            {"ok":true,"ship":"Test Corvette","target":"straight ahead","targetKind":"distance","jump":10000,"cost":2,"pentaxid":50,
             "maxRange":25000,"planned":true,"cooldownS":45}
            """);
        Assert.Contains("10 km straight ahead.", text);
        Assert.Contains("ready in 45 s", text);
        Assert.DoesNotContain("target='", text);
    }

    [Fact]
    public void Jumped()
    {
        var text = Render("""
            {"ok":true,"ship":"Test Corvette","target":"Akua Moon","targetKind":"moon","targetDistance":17035,"stopAt":2455,"jump":14580,
             "cost":2,"pentaxid":50,"container":"Pentaxid Tank @ Test Hauler","paid":true,"pentaxidAfter":48,"jumped":true}
            """);
        Assert.Contains("Jumped Test Corvette 14.6 km to Akua Moon (moon, 17 km away)", text);
        Assert.Contains("Used 2 Refined Pentaxid from Pentaxid Tank @ Test Hauler (48 left).", text);
    }

    [Fact]
    public void Refusal_is_passed_through()
    {
        Assert.Equal("The ship has no Prototype Micro Jump Drive. Nothing happened.",
            Render("""{"ok":false,"error":"The ship has no Prototype Micro Jump Drive. Nothing happened."}"""));
    }
}
