using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class ItemsTests
{
    private static string Render(string json) => GameView.ItemsResult(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Details_with_ammo_and_users()
    {
        var text = Render("""
            {"ok":true,"query":"12.7mm Ammo","count":1,"items":[
              {"name":"12.7mm Rounds","craftable":true,"constructors":["small/SV constructor","large constructor"],
               "recipe":{"makes":100,"seconds":5,"inputs":[{"name":"Steel Plates","count":10},{"name":"Copper Ingot","count":3},{"name":"Nitrocellulose","count":40}]},
               "usedBy":["NTS-56 Sniper Rifle","Gatling Gun (Light)","Light Gatling Turret"]}]}
            """);
        Assert.Contains("recipe: 10 Steel Plates + 3 Copper Ingot + 40 Nitrocellulose → 100 12.7mm Rounds per craft", text);
        Assert.Contains("- 12.7mm Rounds: craftable (small/SV constructor, large constructor); recipe:", text);
        Assert.Contains("used by: NTS-56 Sniper Rifle, Gatling Gun (Light), Light Gatling Turret", text);
    }

    [Fact]
    public void Weapon_shows_ammo()
    {
        var text = Render("""{"ok":true,"query":"minigun","count":1,"items":[{"name":"MLK-76 Minigun","craftable":false,"ammo":["5.56mm Rounds"]}]}""");
        Assert.Contains("- MLK-76 Minigun: not craftable; ammo: 5.56mm Rounds", text);
    }

    [Fact]
    public void Too_many_names_only()
    {
        var text = Render("""{"ok":true,"query":"ammo","count":42,"namesOnly":true,"names":["12.7mm Rounds","5.56mm Rounds"]}""");
        Assert.Contains("matches 42 items, too many for details", text);
        Assert.Contains("(and 40 more)", text);
        Assert.Contains("more precise name", text);
    }

    [Fact]
    public void None() => Assert.Contains("No item in the game matches 'xyz'", Render("""{"ok":true,"query":"xyz","count":0,"items":[]}"""));
}
