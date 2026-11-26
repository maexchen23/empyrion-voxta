using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class FightTests
{
    static readonly FightInfo Escort = new("eden_pda_fight", "Trigger: test", "active", null, "Board VIP Transport",
    [
        new(4, ["CoreNPC"], 4, "Bandit", null, 60, "A fleet of enemy ships have warped in!"),
        new(5, ["CoreNPC"], 5, "Bandit, Bandit Bomber", null, 30, null),
        new(7, ["CoreNPC"], 7, "Bandit, Bandit Bomber", "2 Polaris", 30, null),
    ]);

    static GameState S(int? cores, params FightInfo[] fights) => new()
    {
        Player = "Player1", Playfield = "Sector C", Fights = fights,
        DestroyedBlocks = cores is { } n ? new Dictionary<string, int> { ["CoreNPC"] = n, ["SentryGun03"] = 9 } : null,
    };

    static List<GameEvent> Of(string name, GameState prev, GameState next, GameEventConfig? config = null) =>
        GameEvents.Diff(prev, next, config: config).Where(e => e.Name == name).ToList();

    [Fact]
    public void Progress_counts_cores_since_the_fight_started()
    {
        var baselines = new Dictionary<string, int>();
        var before = GameEvents.WithFightProgress(null, S(10), baselines);         // no fight yet
        var start = GameEvents.WithFightProgress(before, S(10, Escort), baselines);
        Assert.Equal(0, start.Fights![0].Destroyed);
        var later = GameEvents.WithFightProgress(start, S(15, Escort), baselines);
        Assert.Equal(5, later.Fights![0].Destroyed);                                // sentry guns don't count
        // the playfield server restarted (counts from 0 again): keep the 5
        var restarted = GameEvents.WithFightProgress(later, S(1, Escort), baselines);
        Assert.Equal(6, restarted.Fights![0].Destroyed);
        GameEvents.WithFightProgress(restarted, S(1), baselines);
        Assert.Empty(baselines);
    }

    [Fact]
    public void Start_wave_down_and_all_down()
    {
        var start = Assert.Single(Of("FightStarted", S(0), S(0, Escort)));
        Assert.True(start.Important);
        Assert.StartsWith("A fight is starting: 3 waves: 4 Bandit ships, then 5 Bandit, Bandit Bomber ships, then 7 Bandit, Bandit Bomber ships; the first wave arrives in about 60 seconds.", start.Description);

        var wave = Assert.Single(Of("FightWaveDown", S(0, Escort with { Destroyed = 3 }), S(0, Escort with { Destroyed = 4 })));
        Assert.StartsWith("Wave 1 of 3 is down (4 of 16 enemy cores destroyed). Next: 5 Bandit, Bandit Bomber ships in about 30 seconds.", wave.Description);
        var wave2 = Assert.Single(Of("FightWaveDown", S(0, Escort with { Destroyed = 8 }), S(0, Escort with { Destroyed = 9 })));
        Assert.Contains("2 Polaris ships will come to help", wave2.Description);
        Assert.Empty(Of("FightWaveDown", S(0, Escort with { Destroyed = 4 }), S(0, Escort with { Destroyed = 6 })));

        var all = Assert.Single(Of("FightAllDown", S(0, Escort with { Destroyed = 15 }), S(0, Escort with { Destroyed = 16 })));
        Assert.StartsWith("All enemy ships are down (16 cores destroyed). Next: Board VIP Transport.", all.Description);
        Assert.Empty(Of("FightWaveDown", S(0, Escort with { Destroyed = 15 }), S(0, Escort with { Destroyed = 16 })));
    }

    [Fact]
    public void Quiet_cases()
    {
        Assert.Empty(Of("FightStarted", S(0, Escort), S(0, Escort)));                  // still running
        Assert.DoesNotContain(GameEvents.Diff(new GameState { Player = "Player1", Playfield = "Sector C" }, S(0, Escort)),
            e => e.Name.StartsWith("Fight"));                                           // unknown before
        Assert.Empty(Of("FightStarted", S(0), S(0, Escort), NewMissionTests.Cfg(("space_fight", "off"))));
        // the game ends the fight while fewer cores were counted (allies took some): still "all down"
        Assert.Equal("All enemy ships are down.", Assert.Single(Of("FightAllDown", S(0, Escort with { Destroyed = 10 }),
            S(0, Escort with { State = "completed", Destroyed = 10 }), NewMissionTests.Cfg(("space_fight", "silent")))).Description);
    }

    [Fact]
    public void Live_context_shows_the_wave()
    {
        Assert.Contains("Combat: wave 2 of 3, 6 of 16 enemy cores destroyed.", S(0, Escort with { Destroyed = 6 }).ToContext());
        Assert.Contains("Combat: all 16 targets down; next: Board VIP Transport.", S(0, Escort with { Destroyed = 16 }).ToContext());
    }
}

public class TimedTests
{
    static GameState S(TimedInfo? t, ScanSiteInfo? scan = null) => new()
    {
        Player = "Player1", Playfield = "Nebula",
        Sector = new SectorInfo("Nebula", null, null, false, scan, [], t is null ? [] : [t]),
    };

    static TimedInfo Pods(long left, long count, string state = "active") => new("Eden_Pods_Init", "Medical Assist Android", state, 1000 + left, 1000, count, 5);
    static readonly ScanSiteInfo PodScan = new(null, "Advanced Probe", 1, false, null, "open");

    static List<GameEvent> Of(string name, GameState prev, GameState next) => GameEvents.Diff(prev, next).Where(e => e.Name == name).ToList();

    [Fact]
    public void Start_names_time_and_probes()
    {
        var e = Assert.Single(Of("TimedMissionStarted", S(null), S(Pods(600, 0), PodScan)));
        Assert.True(e.Important);
        Assert.StartsWith("The clock is running on the Medical Assist Android's mission: 5 to save, 10 min left.", e.Description);
        Assert.Contains("1 Advanced Probe per scan, 5 in all", e.Description);
        Assert.Contains("empyrion_transfer_items", e.Description);
    }

    [Fact]
    public void Warnings_progress_all_done_and_time_up()
    {
        Assert.Contains("5 min left", Assert.Single(Of("TimedMissionWarning", S(Pods(310, 1)), S(Pods(299, 1)))).Description);
        Assert.Empty(Of("TimedMissionWarning", S(Pods(299, 1)), S(Pods(290, 1))));
        Assert.Contains("2 of 5 saved", Assert.Single(Of("TimedMissionProgress", S(Pods(400, 1)), S(Pods(390, 2)))).Description);
        Assert.Contains("Next: return to the Medical Assist Android", Assert.Single(Of("TimedMissionAllDone", S(Pods(100, 4)), S(Pods(90, 5)))).Description);
        Assert.Contains("3 of 5 saved", Assert.Single(Of("TimedMissionTimeUp", S(Pods(5, 3)), S(Pods(0, 3)))).Description);
        Assert.DoesNotContain(GameEvents.Diff(S(Pods(0, 3)), S(Pods(0, 3, "done"))), x => x.Name.StartsWith("Timed"));
    }

    [Fact]
    public void Live_context_counts_down_and_skips_the_gated_scan()
    {
        var text = S(Pods(150, 2), PodScan).ToContext();
        Assert.Contains("Against the clock: Medical Assist Android's mission, 2m 30s left, 2 of 5 done.", text);
        Assert.DoesNotContain("not scanned yet", text);
    }
}
