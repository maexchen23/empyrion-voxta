using System.IO;
using System.Linq;
using VoxtaBridge;
using Xunit;

public class PdaRepeatsTests
{
    const string Yaml = """
        Chapters:
          - ChapterTitle: eden_pda_Research
            Category: SoloMission
            ChapterActivation:
              - Check: DialogOption
                Names:
                  - Some_Activation
                Value: 1
            RepeatConditions:
              NumRepeats: 0
              Delay: 180
              DelayAdd: 0
            Tasks:
              - TaskTitle: eden_pda_t1
                Actions:
                  - ActionTitle: eden_pda_a1
                    Check: DialogOption
                    Names:
                      - Lab_Complete
                    Value: 1
          - ChapterTitle: pda_Pacing
            RepeatConditions:
              NumRepeats: 0
              DelaySeconds: 30
            Tasks:
              - TaskTitle: pda_t2
          - ChapterTitle: pda_HiddenTrigger
            Visibility: Never
            RepeatConditions:
              NumRepeats: 0
              Delay: 60
            Tasks:
              - TaskTitle: pda_t4
          - ChapterTitle: pda_NoRepeat
            Tasks:
              - TaskTitle: pda_t3
        """;

    const string Csv = """
        KEY,English,Deutsch
        pda_Research,[c][00ff00]Research Unlock Points[-][/c],,
        pda_Pacing,Pacing,,
        """;

    const string Dialogues = """
        { +Dialogue Name: Lab_Init
          Variable_1: "Lab_Done", param1: dbplayerpoi_int
          Variable_2: "Lab_Count", param1: dbplayer_int
        }
        { +Dialogue Name: Lab_Complete
          OptionExecute_1: "Player.UpgradePoints += Lab_Count; Lab_Count = 1; Lab_Done = 1"
        }
        """;

    static PdaRepeats Load()
    {
        var r = new PdaRepeats();
        r.LoadPda(new StringReader(Yaml));
        r.LoadTitles(new StringReader(Csv));
        r.LinkDialogues(new StringReader(Dialogues));
        return r;
    }

    [Fact]
    public void Repeat_delay_in_minutes_title_and_once_per_poi_variable()
    {
        var r = Load();
        var research = Assert.Single(r.All); // 30 s pacing, hidden triggers and no-repeat chapters are left out
        Assert.Equal("eden_pda_Research", research.Chapter);
        Assert.Equal(180 * 60, research.DelaySeconds);
        Assert.Equal("Research Unlock Points", research.Title);
        Assert.Equal(new[] { "Lab_Complete" }, research.CompleteDialogues); // not the activation dialogue
        Assert.Equal("Lab_Done", research.OncePerPoiVar); // Lab_Count is per player, not per POI
    }
}
