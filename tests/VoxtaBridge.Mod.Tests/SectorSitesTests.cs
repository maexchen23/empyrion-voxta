using System.IO;
using System.Linq;
using VoxtaBridge;
using Xunit;
using Xunit.Abstractions;

public class SectorSitesTests(ITestOutputHelper output)
{
    const string Pda = """
        Chapters:
          - ChapterTitle: pda_brief
            Category: SoloMission
            Tasks:
              - TaskTitle: pda_task
                Actions:
                  - ActionTitle: pda_msg
                    Description: pda_report
            ChapterActivation:
              - Check: PlayfieldTypeEntered
                Names:
                  - Space_POI_ExploreT1_1
          - ChapterTitle: pda_hidden
            Visibility: Never
            ChapterActivation:
              - Check: PlayfieldTypeEntered
                Names:
                  - Space_POI_Hidden
          - ChapterTitle: pda_scan
            Category: SoloMission
            StartMessage: dlg;Scan_C1_Init|
            PlayfieldTypes:
              - Exploration Site Class 1
        """;
    const string PdaCsv = "KEY,English\npda_brief,Science Assessment\npda_report,\"Space_POI_ExploreT1_1 A [c]derelict[/c] satellite array drifts in this sector, still faintly transmitting.\"\npda_msg,Displaying a message\n";

    const string Ecf = """
        { +Dialogue Name: Scan_C1_Init
          Variable_1: "Eden_ScanComplete", param1: dbplayerpf_int
          Variable_2: "Eden_ProbeCount", param1: dbplayerpf_int
          Execute: "ProbeType = 'Eden_ProbeSystemT1'; SiteType = 'Class 1 Exploration Site'"
          NextIf_1: "Eden_ScanComplete == 1"
          Next_1: Scan_C1_Done
          NextIf_2: "Eden_SiteDifficulty == 0"
          Next_2: Scan_Randomize
        }
        { +Dialogue Name: Scan_Randomize
          Execute: "Eden_SiteDifficulty = Eden_Score + Random(1,2); Eden_RewardCount += (Eden_SiteDifficulty / 2); Eden_ProbeCount = Math.Clamp((Eden_SiteDifficulty / 2), 1, 4);<nl>Eden_ScanComplete = 0"
        }
        { +Dialogue Name: Help_Start
          Variable_1: "Done", param1: dbplayerpoi_int
          Variable_2: "Roll", param1: dbplayerpoi_int
          NextIf_1: "Done >= 1"
          Next_1: Help_Thanks
          NextIf_2: "Roll == 0"
          Next_2: Help_Randomize
        }
        { +Dialogue Name: Help_Randomize
          Execute: "Roll = Random(1,2)"
          Next_1: Help_Select
        }
        { +Dialogue Name: Help_Select
          NextIf_1: "Roll >= 2"
          Next_1: Help_Greet2
          NextIf_2: "Roll >= 1"
          Next_2: Help_Greet1
        }
        { +Dialogue Name: Help_Greet1
          Output: help_g1
        }
        { +Dialogue Name: Help_Greet2
          Output: help_g2
        }
        { +Dialogue Name: Help_Reward
          Execute: "Player.Credit += Random(25000,35000); Done = 1"
          Output: help_thanks
        }
        { +Dialogue Name: Shop_Init
          Variable_1: "Visited", param1: dbplayerpoi_int
          NextIf_1: "Visited == 1"
          Output: shop_hi
        }
        """;
    const string EcfCsv = "KEY,English\nhelp_g1,Please help us! Our ship's weapons have taken damage.\nhelp_g2,Please help us! Our ship's sensors have taken damage.\nhelp_thanks,Thank you for your help!\nshop_hi,Welcome! How can I help you?\n";

    [Fact]
    public void Briefings_scan_sites_and_calls_for_help()
    {
        var s = new SectorSites();
        s.LoadPda(new StringReader(Pda), new StringReader(PdaCsv));
        s.LoadDialogues(new StringReader(Ecf), new StringReader(EcfCsv));

        var b = s.Briefing("Space_POI_ExploreT1_1")!;
        Assert.Equal("science", b.Kind);
        Assert.Equal("A derelict satellite array drifts in this sector, still faintly transmitting.", b.Text);
        Assert.Null(s.Briefing("Space_POI_Hidden"));

        var scan = s.Scan(null, "Exploration Site Class 1")!;
        Assert.Equal(("Eden_ProbeSystemT1", "Class 1 Exploration Site", "Eden_ScanComplete", "Eden_ProbeCount"), (scan.ProbeItem, scan.Label, scan.DoneVar, scan.CountVar));
        Assert.Equal("Eden_Score", scan.ScoreVar);
        Assert.Equal((1, 1), scan.ProbeRange(0));   // difficulty 1-2 → 0-1 → at least 1
        Assert.Equal((3, 4), scan.ProbeRange(6));   // 7-8 → 3-4
        Assert.Equal((4, 4), scan.ProbeRange(20));  // capped

        var h = Assert.Single(s.HelpCalls);
        Assert.Equal(("Help_Start", "Done", 1, "Roll"), (h.Start, h.DoneVar, h.DoneValue, h.SelectorVar));
        Assert.Equal("Please help us! Our ship's …", h.Plea);
        Assert.Equal("Please help us! Our ship's sensors have taken damage.", h.PleaByValue[2]);
        Assert.Equal("Please help us! Our ship's weapons have taken damage.", h.PleaByValue[1]);

        var excluded = new SectorSites();
        excluded.LoadDialogues(new StringReader(Ecf), new StringReader(EcfCsv), new System.Collections.Generic.HashSet<string> { "Help_Start" });
        Assert.Empty(excluded.HelpCalls);
    }

    [Fact]
    public void Real_files()
    {
        var scenario = System.Environment.GetEnvironmentVariable("GAMETEXT_SCENARIO");
        if (scenario == null) return;
        var cfg = Path.Combine(scenario, "Content", "Configuration");
        var pda = Path.Combine(scenario, "Extras", "PDA");
        if (!File.Exists(Path.Combine(pda, "PDA.yaml")) || !File.Exists(Path.Combine(cfg, "Dialogues.csv"))) return;
        var s = new SectorSites();
        using (var y = new StreamReader(Path.Combine(pda, "PDA.yaml")))
        using (var c = new StreamReader(Path.Combine(pda, "PDA.csv"))) s.LoadPda(y, c);
        using (var e = new StreamReader(Path.Combine(cfg, "Dialogues.ecf")))
        using (var c = new StreamReader(Path.Combine(cfg, "Dialogues.csv"))) s.LoadDialogues(e, c);
        output.WriteLine($"briefings {s.BriefingCount}, scan types {s.ScanTypeCount}");
        foreach (var t in new[] { "EdenSpace_POI_CombatT2_1", "EdenSpace_POI_ExploreT3_1", "EdenSpace_POI_MiningT2_1", "Suspicious Radio Beacon" })
            output.WriteLine($"{t}: {s.Briefing(t)?.Kind} | {s.Briefing(t)?.Text}");
        foreach (var t in new[] { "Exploration Site Class 1", "Exploration Site Class 6" })
        {
            var sc = s.Scan(t);
            output.WriteLine($"{t}: {sc?.Dialogue} {sc?.Label} probe={sc?.ProbeItem} done={sc?.DoneVar} count={sc?.CountVar} score={sc?.ScoreVar} range0={sc?.ProbeRange(0)}");
            Assert.Equal((1, 1), sc!.ProbeRange(0));
        }
        foreach (var h in s.HelpCalls)
            output.WriteLine($"help {h.Start}: done {h.DoneVar}>={h.DoneValue} sel={h.SelectorVar} [{string.Join(" / ", h.PleaByValue.Select(kv => kv.Key + ":" + kv.Value[..System.Math.Min(50, kv.Value.Length)]))}] | {h.Plea[..System.Math.Min(160, h.Plea.Length)]}");
        Assert.Equal("security", s.Briefing("EdenSpace_POI_CombatT2_1")!.Kind);
        Assert.Contains("derelict shipyard", s.Briefing("EdenSpace_POI_CombatT2_1")!.Text);
        Assert.Equal("Eden_ProbeSystemT1", s.Scan("Exploration Site Class 1")!.ProbeItem);
        Assert.NotNull(s.Help("Eden_DistressCol_Start"));
        foreach (var t in s.Timed) output.WriteLine($"timed {t.Start} '{t.Npc}' timer={t.TimerVar}+{t.Seconds} state={t.StateVar}={t.ActiveValue}/{t.DoneValue} counter={t.CounterVar}/{t.Total}");
        var pods = s.Timed.Single(t => t.Start == "Eden_ExploreT3_1A_Init");
        Assert.Equal(("Eden_OxygenTimer", 600, "Eden_Complete", 2, "Eden_CrewCount", 5), (pods.TimerVar, pods.Seconds, pods.StateVar, pods.ActiveValue, pods.CounterVar, pods.Total));
        var podScan = s.Scan("EdenSpace_POI_ExploreT3_1")!;
        output.WriteLine($"pod scan: {podScan.Dialogue} {podScan.ProbeItem} gate={podScan.GateVar} range={podScan.ProbeRange(0)}");
        Assert.Equal("Eden_AnomalyBonus", podScan.GateVar);
        Assert.Null(s.Scan("Exploration Site Class 1")!.GateVar);
    }
}
