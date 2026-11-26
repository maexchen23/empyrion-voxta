using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class SectorTests
{
    const string Report = "A dark matter nebula casts an otherworldly glow over this sector.";
    static readonly HelpCallInfo Distress = new("555", "Damaged Freighter (Sector B)", "Please help us! Our ship's …", false, false);

    static SectorInfo Sec(string pf, bool first = true, ScanSiteInfo? scan = null, params HelpCallInfo[] calls) =>
        new(pf, Report, "science", first, scan, calls);

    static GameState S(string pf, SectorInfo? sector) => new() { Player = "Player1", Playfield = pf, Sector = sector };

    static List<GameEvent> Of(string name, GameState prev, GameState next, GameEventConfig? config = null) =>
        GameEvents.Diff(prev, next, config: config).Where(e => e.Name == name).ToList();

    static readonly GameState Before = S("Elsewhere", new SectorInfo("Elsewhere", null, null, false, null, []));

    [Fact]
    public void First_visit_passes_on_the_sector_report()
    {
        var e = Assert.Single(Of("SectorBriefing", Before, S("Sector B", Sec("Sector B", scan: new ScanSiteInfo(null, "Probe System T1", 1, false)))));
        Assert.True(e.Important);
        Assert.Contains("Arriving in Sector B, the ship picked up the sector's science report: \"" + Report + "\"", e.Description);
        Assert.DoesNotContain("Probe", e.Description); // scans come as jobs
        Assert.Empty(Of("SectorBriefing", Before, S("Sector B", Sec("Sector B", first: false))));
    }

    [Fact]
    public void Staying_unknown_before_and_off_are_quiet()
    {
        Assert.Empty(Of("SectorBriefing", S("Sector B", Sec("Sector B")), S("Sector B", Sec("Sector B"))));
        Assert.Empty(Of("SectorBriefing", S("Elsewhere", null), S("Sector B", Sec("Sector B"))));
        Assert.Empty(Of("SectorBriefing", Before, S("Sector B", Sec("Sector B")), NewMissionTests.Cfg(("sector_briefing", "off"))));
        Assert.False(Assert.Single(Of("SectorBriefing", Before, S("Sector B", Sec("Sector B")), NewMissionTests.Cfg(("sector_briefing", "silent")))).Important);
        // the status still describes the old sector right after the jump: wait for the new one
        Assert.Empty(Of("SectorBriefing", Before, S("Sector B", Sec("Elsewhere"))));
    }

    [Fact]
    public void Calls_for_help_on_arrival_and_when_they_show_up()
    {
        var e = Assert.Single(Of("HelpCall", Before, S("Sector B", Sec("Sector B", calls: Distress))));
        Assert.True(e.Important);
        Assert.Equal("help:555", e.MissionKey);
        Assert.Contains("On the local channel, the Damaged Freighter (Sector B) is calling for help: \"Please help us! Our ship's …\"", e.Description);
        // same sector: only new ones; done ones never
        Assert.Empty(Of("HelpCall", S("Sector B", Sec("Sector B", calls: Distress)), S("Sector B", Sec("Sector B", calls: Distress))));
        Assert.Single(Of("HelpCall", S("Sector B", Sec("Sector B")), S("Sector B", Sec("Sector B", calls: Distress))));
        Assert.Empty(Of("HelpCall", Before, S("Sector B", Sec("Sector B", calls: Distress with { Done = true }))));
        Assert.Empty(Of("HelpCall", Before, S("Sector B", Sec("Sector B", calls: Distress)), NewMissionTests.Cfg(("help_call", "off"))));
    }

    [Fact]
    public void Live_context_names_open_calls_and_the_scan()
    {
        var known = Distress with { Key = "2", Where = "Freighter (Sector B)", Plea = "Please help us! Our ship's sensors have taken damage.", Specific = true };
        var text = S("Sector B", Sec("Sector B", false, new ScanSiteInfo(null, "Probe System T1", 4, false), Distress, known,
            Distress with { Key = "3", Where = "Old Wreck", Done = true })).ToContext();
        Assert.Contains("Call for help here: Damaged Freighter (Sector B).", text);
        Assert.Contains("Call for help here: Freighter (Sector B) (Please help us! Our ship's sensors have taken damage.).", text);
        Assert.DoesNotContain("Old Wreck", text);
        Assert.Contains("Exploration site, not scanned yet: the ship's scanning table resolves the anomalies with Probe System T1 (4 needed).", text);
        Assert.DoesNotContain(Report, text);
    }

    [Fact]
    public void A_call_that_became_a_job_is_not_listed_twice()
    {
        var state = S("Sector B", Sec("Sector B", false, null, Distress)) with
        {
            OpenMissions = [new OpenMission("555-Sector B#4", "job", "Damaged Freighter (Sector B) needs 1 Propulsion Repair Kit")],
        };
        Assert.DoesNotContain("Call for help here", state.ToContext());
        Assert.Contains("Call for help here", S("Sector B", Sec("Sector B", false, null, Distress)).ToContext());
    }

    [Fact]
    public void Parsed_from_the_status()
    {
        var json = """
            {"players":[{"entityId":1,"steamId":"765","name":"Player1","online":true,"playfield":"Sector B"}],
             "api1":{"players":[{"entityId":1,"playfield":"Sector B"}]}, "playfields":[],
             "sector":{"765":{"playfield":"Sector B","briefingKind":"security","briefing":"Bandits.","firstVisit":true,
               "scan":{"label":"Class 2","probeName":"Probe System T1","probes":3},
               "calls":[{"key":"9","where":"Freighter (Sector B)","plea":"Help!","specific":true}]}}}
            """;
        var s = GameView.Create(System.Text.Json.JsonDocument.Parse(json).RootElement, null, out _)!.State();
        Assert.Equal("security", s!.Sector!.BriefingKind);
        Assert.True(s.Sector.FirstVisit);
        Assert.Equal(3, s.Sector.Scan!.Probes);
        Assert.False(s.Sector.Scan.Done);
        Assert.True(Assert.Single(s.Sector.Calls).Specific);
    }
}

public class JobTests
{
    static readonly OpenMission Beacon = new("31000-Sector B#3", "job", "Emergency Beacon at Wreck (Sector B) needs 3 Optical Fiber", "Wreck (Sector B)",
        Needs: [new MissionNeed("OpticalFiber", "Optical Fiber", 3)]);
    static readonly OpenMission Scan = new("scan-Sector B#1", "scan",
        "Class 1 Exploration Site (Sector B): the ship's scanning table needs 1 Probe System T1 to resolve the anomalies", "Class 1 Exploration Site (Sector B)",
        Needs: [new MissionNeed("Eden_ProbeSystemT1", "Probe System T1", 1)]);

    static GameState S(string pf, SectorInfo? sector = null, params OpenMission[] missions) =>
        new() { Player = "Player1", Playfield = pf, OpenMissions = missions, Sector = sector };

    static List<GameEvent> Of(string name, GameState prev, GameState next, GameEventConfig? config = null) =>
        GameEvents.Diff(prev, next, config: config).Where(e => e.Name == name).ToList();

    [Fact]
    public void A_new_job_offers_to_get_the_items_into_the_inventory()
    {
        var e = Assert.Single(Of("NewJob", S("Sector B"), S("Sector B", null, Beacon)));
        Assert.True(e.Important);
        Assert.Equal(Beacon.Key, e.MissionKey); // the instance adds what is in reach
        Assert.StartsWith("The player just took on a job: Emergency Beacon at Wreck (Sector B) needs 3 Optical Fiber.", e.Description);
        Assert.Contains("offer to move them there (empyrion_transfer_items), or to craft them (empyrion_craft)", e.Description);
        Assert.Empty(Of("NewTraderMission", S("Sector B"), S("Sector B", null, Beacon)));
        Assert.False(Assert.Single(Of("NewJob", S("Sector B"), S("Sector B", null, Beacon), NewMissionTests.Cfg(("new_job", "silent")))).Important);
    }

    [Fact]
    public void A_scan_site_is_a_job_on_arrival()
    {
        var e = Assert.Single(Of("NewJob", S("Elsewhere"), S("Sector B", null, Scan)));
        Assert.StartsWith("The ship's sensors register unresolved anomalies: Class 1 Exploration Site (Sector B): the ship's scanning table needs 1 Probe System T1", e.Description);
    }

    [Fact]
    public void Done_jobs_and_scans()
    {
        Assert.Contains("finished the job for Wreck (Sector B)", Assert.Single(Of("JobDone", S("Sector B", null, Beacon), S("Sector B"))).Description);
        // leaving an unscanned site drops its row: not done
        Assert.Empty(Of("JobDone", S("Sector B", null, Scan), S("Elsewhere")));
        Assert.Empty(Of("JobDone", S("Sector B", null, Scan), S("Sector B", new SectorInfo("Sector B", null, null, false, new ScanSiteInfo(null, "Probe System T1", 1, false), []))));
        var done = Assert.Single(Of("JobDone", S("Sector B", null, Scan),
            S("Sector B", new SectorInfo("Sector B", null, null, false, new ScanSiteInfo(null, "Probe System T1", 1, true), []))));
        Assert.False(done.Important); // silent by default
        Assert.Contains("finished the probe scan at Class 1 Exploration Site (Sector B)", done.Description);
    }
}
