using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class CraftTests
{
    private static string Render(string json) => GameView.CraftResult(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Plan_lists_uses_results_and_asks()
    {
        var text = Render("""
            {"ok":true,"item":"12.7mm Ammo","amount":5000,"container":"A Production VC @ Test Hauler","crafted":false,"planOnly":true,
             "craftSeconds":1224,"uses":[{"name":"Iron Ingot","count":250},{"name":"Copper Ingot","count":150}],
             "produces":[{"name":"12.7mm Ammo","count":5000},{"name":"NC Powder","count":16}],"missing":[],"steps":[{"name":"Steel Plate","count":500}]}
            """);
        Assert.Contains("Plan for 5000 12.7mm Ammo from 'A Production VC @ Test Hauler' (a real constructor would need about 20 min)", text);
        Assert.Contains("- uses: 250 Iron Ingot, 150 Copper Ingot", text);
        Assert.Contains("16 NC Powder", text);
        Assert.Contains("craft only after they agree", text);
    }

    [Fact]
    public void Crafted()
    {
        var text = Render("""
            {"ok":true,"item":"Steel Plate","amount":10,"container":"VC @ Test Carrier","crafted":true,"craftSeconds":1,
             "uses":[{"name":"Iron Ingot","count":5}],"produces":[{"name":"Steel Plate","count":10}],"missing":[],"steps":[]}
            """);
        Assert.Contains("Crafted 10 Steel Plate in 'VC @ Test Carrier'", text);
        Assert.Contains("Used: 5 Iron Ingot.", text);
    }

    [Fact]
    public void Missing_materials()
    {
        var text = Render("""
            {"ok":false,"error":"Not enough materials in 'VC @ Test Carrier' for 5000 12.7mm Ammo.","item":"12.7mm Ammo","amount":5000,"container":"VC @ Test Carrier",
             "crafted":false,"uses":[],"produces":[],"missing":[{"name":"Iron Ingot","count":50}],"steps":[]}
            """);
        Assert.Contains("Not enough materials", text);
        Assert.Contains("Missing: 50 Iron Ingot.", text);
        Assert.Contains("Nothing was crafted.", text);
    }

    [Fact]
    public void Recipe_line_shows_output_per_craft()
    {
        var text = Render("""
            {"ok":false,"error":"Not enough materials in 'VC @ Test Carrier' for 1 Steel Plates.","item":"Steel Plates","amount":1,"container":"VC @ Test Carrier",
             "crafted":false,"recipe":{"makes":10,"seconds":1,"inputs":[{"name":"Iron Ingot","count":5}]},
             "uses":[],"produces":[],"missing":[{"name":"Iron Ingot","count":5}],"steps":[]}
            """);
        Assert.Contains("Recipe: 5 Iron Ingot → 10 Steel Plates per craft.", text);
    }
}
