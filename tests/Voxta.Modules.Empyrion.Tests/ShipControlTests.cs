using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class ShipControlTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Find_lists_matches_with_state_and_structure_when_several()
    {
        var text = GameView.FindShipControlsResult(J("""
            {"ok":true,"query":"turret","matches":2,"structures":["Test Hauler","Test Corvette"],
             "controls":[{"structure":"Test Hauler","kind":"device","name":"Multi Turret","type":"TurretCVMinigun","state":"on"},
                         {"structure":"Test Corvette","kind":"signal","name":"Turrets Off","type":null,"state":"off"}]}
            """));
        Assert.Contains("Controls matching 'turret' on 'Test Hauler', 'Test Corvette': 2.", text);
        Assert.Contains("- 'Multi Turret' (device: TurretCVMinigun, on) on 'Test Hauler'", text);
        Assert.Contains("- 'Turrets Off' (signal, off) on 'Test Corvette'", text);
        Assert.Contains("empyrion_ship_control", text);
    }

    [Fact]
    public void Find_without_matches_passes_the_note()
    {
        var text = GameView.FindShipControlsResult(J("""
            {"ok":true,"query":"lamp","matches":0,"structures":["Test Hauler"],"controls":[],"note":"nothing matches"}
            """));
        Assert.Contains(": 0.", text);
        Assert.Contains("(nothing matches)", text);
        Assert.DoesNotContain("empyrion_ship_control", text);
    }

    [Fact]
    public void Control_reports_each_change_and_close_matches()
    {
        var text = GameView.ShipControlResult(J("""
            {"structure":"Test Hauler","results":[
              {"target":"POWERSAVER","kind":"signal","name":"POWERSAVER","before":"off","now":"on","ok":true,"result":"done"},
              {"target":"Prodution","closeMatches":[{"structure":"Test Hauler","kind":"signal","name":"Production","type":null,"state":"on"}],
               "ok":false,"result":"no signal or named device by that name; close matches listed"}],"ok":false}
            """));
        Assert.Contains("On 'Test Hauler':", text);
        Assert.Contains("- POWERSAVER: done (off → on)", text);
        Assert.Contains("- Prodution: no signal or named device by that name; close matches listed", text);
        Assert.Contains("Close matches: 'Production' (signal, on)", text);
    }

    [Fact]
    public void Control_error_is_passed_on()
    {
        Assert.Equal("Device control is off.", GameView.ShipControlResult(J("""{"ok":false,"error":"Device control is off."}""")));
    }
}
