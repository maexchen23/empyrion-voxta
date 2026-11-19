using System.IO;
using System.Linq;
using VoxtaBridge;
using Xunit;
using Xunit.Abstractions;

namespace VoxtaBridge.Mod.Tests;

public class PoiTerminalsTests(ITestOutputHelper output)
{
    const string Ecf = """
        { +Dialogue Name: POI_TestBunker_Init
          NPCName: dlg_name
          Next_1: POI_TestBunker_Done
          NextIf_1: "IsSignalSet('X')"
          Next_2: POI_TestBunker_01
        }
        { +Dialogue Name: POI_TestBunker_01
          Output: dlg_01
          Option_1: dlg_opt_a
          OptionNext_1: POI_TestBunker_02, param1: POI_TestBunker_01
          Option_2: dlg_opt_end
          OptionNext_2: End
        }
        { +Dialogue Name: POI_TestBunker_02
          Output: dlg_02
          Option_1: dlg_opt_other
          OptionNext_1: POI_Other_01
        }
        { +Dialogue Name: POI_TestBunker_Done
          Output: dlg_done
        }
        { +Dialogue Name: POI_Other_01
          Output: dlg_other
        }
        """;

    const string Csv = "KEY,English,Deutsch\n" +
                       "dlg_name,Bunker Terminal,\n" +
                       "dlg_01,\"@d0 <b><color=#ff0000>[ BUNKER-7 ]</color></b>\\nAccess: <color=#00ff00>granted</color>\",\n" +
                       "dlg_02,The reactor is offline.,\n" +
                       "dlg_done,Already done.,\n" +
                       "dlg_opt_a,Status,\n" +
                       "dlg_opt_end,Leave,\n" +
                       "dlg_opt_other,Somewhere else,\n" +
                       "dlg_other,Not this POI.,\n";

    static PoiTerminals Load()
    {
        var t = new PoiTerminals();
        t.LoadDialogues(new StringReader(Ecf));
        t.LoadTexts(new StringReader(Csv));
        return t;
    }

    [Fact]
    public void Walks_the_pois_own_dialogue_tree_with_clean_text()
    {
        var t = Load();
        Assert.Equal("Bunker Terminal", t.TerminalName("POI_TestBunker_Init"));
        var pages = t.Pages("POI_TestBunker_Init");
        Assert.Equal(new[] { "POI_TestBunker_Done", "POI_TestBunker_01", "POI_TestBunker_02" }, pages.Select(p => p.Name));
        Assert.Equal("[ BUNKER-7 ]\nAccess: granted", pages[1].Text);
        Assert.Equal(new[] { "Status", "Leave" }, pages[1].Options);
        Assert.DoesNotContain(pages, p => p.Text == "Not this POI.");
    }

    [Fact]
    public void Unknown_dialogue_gives_nothing() => Assert.Empty(Load().Pages("POI_Missing_Init"));

    /// <summary>Opt-in: DIALOGUES_DIR with Dialogues.ecf + Dialogues.csv of a scenario prints a few real terminals.</summary>
    [Fact]
    public void Real_scenario_terminals()
    {
        var dir = System.Environment.GetEnvironmentVariable("DIALOGUES_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        var t = new PoiTerminals();
        using (var r = File.OpenText(Path.Combine(dir, "Dialogues.ecf"))) t.LoadDialogues(r);
        using (var r = File.OpenText(Path.Combine(dir, "Dialogues.csv"))) t.LoadTexts(r);
        output.WriteLine($"{t.DialogueCount} dialogues, {t.InitCount} POI terminals");
        foreach (var init in new[] { "POI_GhystBunkerControlRoom_Init", "POI_GhystBunkerReactor_Init", "POI_MissileBaseLaunch_Init" })
        {
            var pages = t.Pages(init);
            output.WriteLine($"== {init} ({t.TerminalName(init)}): {pages.Count} pages, {pages.Sum(p => p.Text.Length)} chars");
            foreach (var p in pages.Take(3)) output.WriteLine($"-- {p.Name}\n{p.Text}\n  options: {string.Join(" | ", p.Options)}");
        }
    }
}
