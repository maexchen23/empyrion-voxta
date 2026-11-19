using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class MarksTests
{
    private static string Render(string json) => GameView.MarksResult(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Route_with_unknown_star()
    {
        var text = Render("""
            {"ok":true,"action":"add","unusedUntilLogoff":1,"marked":[{"marker":"1 Naamumamina (Voxta)","star":"Naamumamina","class":"M3","ly":8.7},
             {"marker":"2 Isomnir (Voxta)","star":"Isomnir","class":"M5","ly":11.3}],"numbered":true,
             "unknown":[{"name":"Elo","didYouMean":["Elo Alpha","Elo Beta"]}]}
            """);
        Assert.Contains("Marked on the galaxy map: 1 Naamumamina (Voxta) (M3 star, 8.7 LY away); 2 Isomnir (Voxta) (M5 star, 11.3 LY away).", text);
        Assert.Contains("1 earlier marker(s) are now named 'unused (Voxta)'", text);
        Assert.Contains("the HUD shows the new ones after the player's next login", text);
        Assert.Contains("Not found: 'Elo' (did you mean Elo Alpha, Elo Beta?).", text);
    }

    [Fact]
    public void Clear_and_errors()
    {
        Assert.StartsWith("Voxta's 2 marker(s) will be removed when the player logs off", Render("""{"ok":true,"action":"clear","pendingUntilLogoff":true,"marks":[{"marker":"a (Voxta)"},{"marker":"b (Voxta)"}]}"""));
        Assert.Equal("There are no Voxta markers on the galaxy map.", Render("""{"ok":true,"action":"clear","marks":[]}"""));
        Assert.Equal("None of these stars exists. Not found: 'Zzz'.", Render("""{"ok":false,"error":"None of these stars exists.","unknown":[{"name":"Zzz","didYouMean":[]}]}"""));
    }

    [Fact]
    public void Permanent_marker()
    {
        Assert.Equal("Added the marker 'Water Outpost' on Rubathra (M3 star, 9.9 LY away). It stays until the player removes it in the game. It shows on the galaxy map only, not on the HUD.",
            Render("""{"ok":true,"action":"permanent","permanent":true,"marker":"Water Outpost","star":"Rubathra","class":"M3","hud":false,"ly":9.9}"""));
        Assert.EndsWith("on the HUD after the player's next login.",
            Render("""{"ok":true,"action":"permanent","permanent":true,"marker":"Home","star":"Isomnir","hud":true}"""));
    }
}
