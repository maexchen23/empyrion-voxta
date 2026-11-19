using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class FindItemTests
{
    private static string Render(string json) => GameView.FindResult(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Lists_locations_with_distance_open_flag_and_weapon_state()
    {
        var text = Render("""
            {"ok":true,"query":"pistol","items":[{"id":1,"name":"STN-33 Pistol","total":3,"locations":[
              {"where":"player toolbar","count":1,"states":[{"count":1,"ammo":7,"decay":120}]},
              {"where":"Weapons @ Test Hauler","count":1,"open":true},
              {"where":"Armory @ Test Base","count":1,"distance":340}]}]}
            """);

        Assert.Contains("STN-33 Pistol: 3 in total", text);
        Assert.Contains("- 'player toolbar': 1 (7 rounds loaded, wear 120)", text);
        Assert.Contains("- 'Weapons @ Test Hauler': 1 [open in the game]", text);
        Assert.Contains("- 'Armory @ Test Base': 1 (340 m away)", text);
    }

    [Fact]
    public void Many_items_only_totals()
    {
        var items = string.Join(",", Enumerable.Range(1, 7).Select(i => $$"""{"id":{{i}},"name":"Ingot {{i}}","total":{{i * 10}},"locations":[]}"""));
        var text = Render($$"""{"ok":true,"query":"ingot","items":[{{items}}]}""");

        Assert.Contains("matches 7 different items; totals only", text);
        Assert.Contains("- Ingot 7: 70", text);
    }

    [Fact]
    public void Long_location_lists_are_summarised()
    {
        var locs = string.Join(",", Enumerable.Range(1, 20).Select(i => $$"""{"where":"box {{i}} @ Test Carrier","count":10}"""));
        var text = Render($$"""{"ok":true,"query":"iron","items":[{"id":1,"name":"Iron Ingot","total":200,"locations":[{{locs}}]}]}""");

        Assert.Contains("- 'box 15 @ Test Carrier': 10", text);
        Assert.DoesNotContain("box 16 @ Test Carrier", text);
        Assert.Contains("… and 5 more containers with 50 in total", text);
    }

    [Fact]
    public void Nothing_found()
    {
        Assert.Contains("No 'gold' anywhere in reach", Render("""{"ok":true,"query":"gold","items":[]}"""));
    }
}
