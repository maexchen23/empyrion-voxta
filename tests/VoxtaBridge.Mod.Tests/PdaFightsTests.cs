using System.IO;
using System.Linq;
using VoxtaBridge;
using Xunit;
using Xunit.Abstractions;

public class PdaFightsTests(ITestOutputHelper output)
{
    [Fact]
    public void Real_files()
    {
        var scenario = System.Environment.GetEnvironmentVariable("GAMETEXT_SCENARIO");
        if (scenario == null) return;
        var pda = Path.Combine(scenario, "Extras", "PDA");
        if (!File.Exists(Path.Combine(pda, "PDA.yaml"))) return;
        var f = new PdaFights();
        using (var y = new StreamReader(Path.Combine(pda, "PDA.yaml")))
        using (var c = new StreamReader(Path.Combine(pda, "PDA.csv"))) f.Load(y, c);
        output.WriteLine($"fights: {f.Count}");
        foreach (var id in new[] { "eden_pda_GNOS0OS", "eden_pda_4qRaWaW", "eden_pda_8SuCuuW", "eden_pda_CWuSSNa" })
        {
            var ch = f.ById(id)!;
            output.WriteLine($"{id} '{ch.Title}' start='{ch.StartMessage}' after='{ch.After}'");
            foreach (var w in ch.Waves) output.WriteLine($"   {w.Targets} {string.Join("/", w.Types)} ships={w.Ships} [{string.Join(", ", w.What)}] allies={w.Allies} [{string.Join(", ", w.AlliesWhat)}] delay={w.DelayS} msg='{w.Message}'");
        }
        var escort = f.ById("eden_pda_GNOS0OS")!;
        Assert.Equal([4, 5, 7], escort.Waves.Select(w => w.Targets));
        Assert.Equal(16, escort.TotalTargets);
        Assert.Equal("Board VIP Transport", escort.After);
        var station = f.ById("eden_pda_4qRaWaW")!;
        Assert.Equal(7, station.Waves[2].Ships);
        Assert.Equal((2, "Polaris"), (station.Waves[2].Allies, station.Waves[2].AlliesWhat.Single()));
        var beacon = f.ById("eden_pda_8SuCuuW")!;
        Assert.Equal((2, 30, "Deactivate the emergency beacon."), (beacon.Waves.Single().Targets, beacon.Waves[0].DelayS, beacon.After));
    }
}

public class PdaFightsInlineTests
{
    const string Yaml = """
        Chapters:
          - ChapterTitle: pda_fight
            Visibility: Never
            Tasks:
              - TaskTitle: pda_wait
                Actions:
                  - ActionTitle: pda_x
                    Check: WaitAction
                    Amount: 60
              - TaskTitle: pda_near
                Actions:
                  - ActionTitle: pda_x
                    Check: NearPoi
                OnCompletePlayfieldOps:
                  - >-
                    SpawnOPV:
                    Warlord;RE2_OPVBanditT1;4;true;AttackTarget;Target;1000;0;true;25;false
                  - >-
                    SpawnOPV:
                    Eden_Defender;RE2_MPVPolarisT1;2;true;AttackTarget;Target;500;0;false;10;false
              - TaskTitle: pda_kill
                Actions:
                  - ActionTitle: pda_x
                    Check: BlockDestroyed
                    Names:
                      - RE2_OPVBanditT1
                    Types:
                      - CoreNPC
                    Amount: 4
                StartMessage: high;30|pda_incoming
              - TaskTitle: pda_after
                Actions:
                  - ActionTitle: pda_x
                    Check: DialogOption
        """;

    [Fact]
    public void Waves_ships_allies_and_what_comes_after()
    {
        var f = new PdaFights();
        f.Load(new StringReader(Yaml), new StringReader("KEY,English\npda_fight,Trigger: Test\npda_incoming,Enemy ships incoming!\npda_after,Talk to the captain\n"));
        var ch = f.ById("pda_fight")!;
        var w = ch.Waves.Single();
        Assert.Equal((4, 4, 2, 60), (w.Targets, w.Ships, w.Allies, w.DelayS));
        Assert.Equal(["Bandit"], w.What);
        Assert.Equal(["Polaris"], w.AlliesWhat);
        Assert.Equal(["CoreNPC"], w.Types);
        Assert.Equal("Enemy ships incoming!", w.Message);
        Assert.Equal("Talk to the captain", ch.After);
        Assert.Equal("Bandit Bomber", PdaFights.ShipLabel("RE2_OPVBanditBomberT1"));
    }
}
