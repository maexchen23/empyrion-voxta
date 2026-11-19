using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

/// <summary>Display names/descriptions come from the bridge (the game's own files); the module only shows them.</summary>
public class DisplayNameTests
{
    private const string Status = """
    {
      "players": [{ "entityId": 1, "name": "Player1", "steamId": "765", "playfield": "Akua", "online": true }],
      "api1": { "players": [{
        "entityId": 1, "name": "Player1", "playfield": "Akua",
        "toolbar": [ { "id": 8894, "name": "RadarSuitT1", "display": "Detector", "desc": "A fixed radar dish.", "count": 1 } ],
        "bag": [
          { "id": 9497, "name": "Token", "tokenId": 120, "display": "Trading Card - Akua", "desc": "A collectible trading card.", "count": 1 },
          { "id": 9497, "name": "Token", "tokenId": 121, "display": "Trading Card - Omicron", "count": 1 },
          { "id": 9999, "name": "SomethingNew", "count": 5 }
        ]
      }]},
      "playfields": [{ "name": "Akua", "snapshot": { "playfield": "Akua", "players": [{ "id": 1, "isPilot": true, "structure": {
        "name": "Test Corvette", "type": "SV",
        "containers": [{ "type": "HarvestCntr", "name": "Hold", "items": [
          { "id": 8204, "name": "CombatKnife", "display": "Combat Knife", "count": 1 } ] }] } }] } }]
    }
    """;

    private static GameView View() =>
        GameView.Create(JsonDocument.Parse(Status).RootElement, null, out _)!;

    [Fact]
    public void Inventory_uses_display_names_and_falls_back_to_internal()
    {
        var text = View().PlayerInventory(null);

        Assert.Contains("Toolbar: Detector x1", text);
        Assert.Contains("Trading Card - Akua x1, Trading Card - Omicron x1, SomethingNew x5", text);
        Assert.DoesNotContain("RadarSuitT1", text);
        Assert.DoesNotContain("What they are", text); // no descriptions without a filter
    }

    [Fact]
    public void Filter_matches_display_or_internal_name_and_adds_descriptions()
    {
        var byDisplay = View().PlayerInventory("detector");
        Assert.Contains("Toolbar: Detector x1", byDisplay);
        Assert.Contains("- Detector: A fixed radar dish.", byDisplay);

        Assert.Contains("Detector x1", View().PlayerInventory("RadarSuit"));
        Assert.Contains("Trading Card - Akua", View().PlayerInventory("trading card"));
    }

    [Fact]
    public void Distinct_tokens_are_not_merged_into_one_total()
    {
        Assert.DoesNotContain("Totals", View().PlayerInventory(null));
    }

    [Fact]
    public void Ship_cargo_uses_display_names()
    {
        Assert.Contains("'Hold' (cargo/harvest box): Combat Knife x1", View().CurrentShip());
    }
}

/// <summary>Descriptions must not flood the chat history on broad filters.</summary>
public class DescriptionLimitTests
{
    private static GameView ViewWith(IEnumerable<(string name, string desc)> items)
    {
        var bag = string.Join(",", items.Select((it, i) =>
            $$"""{ "id": {{9000 + i}}, "name": "{{it.name}}", "display": "{{it.name}}", "desc": "{{it.desc}}", "count": 1 }"""));
        var json = $$"""
        { "players": [{ "entityId": 1, "name": "p", "online": true, "playfield": "Akua" }],
          "api1": { "players": [{ "entityId": 1, "name": "p", "toolbar": [], "bag": [{{bag}}] }] },
          "playfields": [] }
        """;
        return GameView.Create(System.Text.Json.JsonDocument.Parse(json).RootElement, null, out _)!;
    }

    [Fact]
    public void Broad_filter_lists_names_without_descriptions()
    {
        var view = ViewWith(Enumerable.Range(1, 10).Select(i => ($"Item{i}", $"Description of item {i}.")));

        var text = view.PlayerInventory("item");

        Assert.Contains("Item1 x1", text);
        Assert.Contains("10 different items match", text);
        Assert.DoesNotContain("Description of item", text);
    }

    [Fact]
    public void Few_matches_get_full_descriptions()
    {
        var view = ViewWith([("Guide", "Read the PDA first. Missions are in the PDA. CPU: add CPU extenders."), ("Other", "x.")]);

        var text = view.PlayerInventory("guide");

        Assert.Contains("- Guide: Read the PDA first. Missions are in the PDA. CPU: add CPU extenders.", text);
    }

    [Fact]
    public void Total_description_text_is_capped()
    {
        var longText = string.Concat(Enumerable.Repeat("This is a sentence of a long guide. ", 100)); // ~3600 chars
        var view = ViewWith([("GuideA", longText), ("GuideB", longText), ("GuideC", longText)]);

        var text = view.PlayerInventory("guide");

        var descChars = text.Split('\n').Where(l => l.StartsWith("- ")).Sum(l => l.Length);
        Assert.True(descChars <= GameView.MaxDescriptionChars + 200, $"descriptions took {descChars} chars");
        Assert.Contains("(cut short)", text);
    }
}

public class ShipListingTests
{
    [Fact]
    public void Empty_containers_are_summarised_in_one_line()
    {
        var boxes = string.Join(",", Enumerable.Range(1, 20).Select(i => $$"""{ "label": "cargo box {{i}}", "type": "Container", "items": [] }"""));
        var json = $$"""
        { "players": [{ "entityId": 1, "name": "p", "online": true, "playfield": "Akua Sector" }],
          "api1": { "players": [] },
          "playfields": [{ "name": "Akua Sector", "snapshot": { "playfield": "Akua Sector", "players": [{ "id": 1, "isPilot": false, "aboard": "aboard",
            "structure": { "name": "Test Carrier", "type": "CV", "containers": [
              { "label": "Ship Ammo", "type": "Container", "items": [ { "name": "45mmBullet", "display": "45mm Ammo", "count": 500 } ] },
              {{boxes}},
              { "label": "Hold @ Test Corvette", "type": "HarvestCntr", "vessel": "Test Corvette", "items": [ { "name": "Flashlight", "display": "Flashlight", "count": 1 } ] }
            ] } }] } }] }
        """;
        var text = GameView.Create(System.Text.Json.JsonDocument.Parse(json).RootElement, null, out _)!.CurrentShip();

        Assert.Contains("- 'Ship Ammo' (cargo box): 45mm Ammo x500", text);
        Assert.Contains("- 'Hold @ Test Corvette' (cargo/harvest box): Flashlight x1", text);
        Assert.Contains("- empty (20): 'cargo box 1', 'cargo box 2'", text);
        Assert.Equal(1, text.Split('\n').Count(l => l.Contains("cargo box 7")));
    }
}

public class OpenContainerTests
{
    [Fact]
    public void Open_containers_are_marked_even_when_empty()
    {
        var json = """
        { "players": [{ "entityId": 1, "name": "p", "online": true, "playfield": "A" }], "api1": { "players": [] },
          "playfields": [{ "name": "A", "snapshot": { "playfield": "A", "players": [{ "id": 1, "isPilot": false,
            "structure": { "name": "Test Carrier", "type": "CV", "containers": [
              { "label": "B Loot 1", "type": "Container", "open": true, "items": [] },
              { "label": "C", "type": "Container", "items": [] } ] } }] } }] }
        """;
        var text = GameView.Create(System.Text.Json.JsonDocument.Parse(json).RootElement, null, out _)!.CurrentShip();

        Assert.Contains("- 'B Loot 1' [open in the game", text);
        Assert.Contains("- empty (1): 'C'", text);
    }
}

public class TransferScopeViewTests
{
    [Fact]
    public void Reach_tree_with_carrier_docked_and_in_range()
    {
        var json = """
        { "players": [{ "entityId": 1, "name": "p", "online": true, "playfield": "A" }], "api1": { "players": [] },
          "playfields": [{ "name": "A", "snapshot": { "playfield": "A", "players": [{ "id": 1, "isPilot": true,
            "structure": { "name": "Test Corvette", "type": "SV", "containers": [],
              "group": [
                { "name": "Test Hauler", "type": "CV", "role": "carrier", "aboard": false, "containers": 48 },
                { "name": "Test Corvette", "type": "SV", "role": "docked", "aboard": true, "containers": 2 },
                { "name": "Hauler", "type": "SV", "role": "docked", "aboard": false, "containers": 1 } ],
              "inRange": [ { "name": "Test Base", "type": "BA", "distance": 340, "containers": 25 } ] } }] } }] }
        """;
        var text = GameView.Create(System.Text.Json.JsonDocument.Parse(json).RootElement, null, out _)!.CurrentShip();

        Assert.Contains("- 'Test Hauler' (CV, carrier, 48 containers)\n", text.Replace("\r", ""));
        Assert.Contains("  - 'Test Corvette' (SV, docked, 2 containers)  <- the player is aboard", text);
        Assert.Contains("  - 'Hauler' (SV, docked, 1 container)", text);
        Assert.Contains("- 'Test Base' (BA, 340 m away, 25 containers; contents via this tool with structure=<name>)", text);
    }

    [Fact]
    public void Structure_containers_answer_and_error()
    {
        var ok = System.Text.Json.JsonDocument.Parse("""
            {"ok":true,"structure":"Test Base","containers":[
              {"label":"Ores @ Test Base","type":"Container","items":[{"name":"IronOre","display":"Iron Ore","count":999}]}]}
            """).RootElement;
        Assert.Contains("- 'Ores @ Test Base' (cargo box): Iron Ore x999", GameView.StructureContainers(ok));

        var err = System.Text.Json.JsonDocument.Parse("""{"ok":false,"error":"No structure 'X' in transfer range."}""").RootElement;
        Assert.Equal("No structure 'X' in transfer range.", GameView.StructureContainers(err));
    }
}
