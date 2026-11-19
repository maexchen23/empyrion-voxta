using VoxtaBridge;
using Xunit;
using Xunit.Abstractions;

namespace VoxtaBridge.Mod.Tests;

public class RecipesTests(ITestOutputHelper output)
{
    // Shortened from RE2's Templates.ecf
    private const string Templates = """
        { +Template Name: IronOre
          BaseItem: true   # comment
          CraftTime: 10
          Target: "SurvC,SmallC,HoverC,BaseC,LargeC,AdvC"
          { Child Inputs
            RockDust: 25
          }
        }
        { +Template Name: IronIngot
          BaseItem: true
          OutputCount: 10
          CraftTime: 5
          Target: "SurvC,SmallC,HoverC,BaseC,LargeC,AdvC,Furn"
          { Child Inputs
            IronOre: 5
          }
        }
        { +Template Name: SteelPlate
          OutputCount: 10
          CraftTime: 1
          Target: "SurvC,SmallC,HoverC,BaseC,LargeC,AdvC"
          { Child Inputs
            IronIngot: 5
          }
        }
        { +Template Name: WoodPlanks
          OutputCount: 12
          CraftTime: 2
          Target: "SurvC,SmallC,HoverC,BaseC,LargeC,AdvC"
          { Child Inputs
            WoodLogs: 1
          }
        }
        { +Template Name: Fiber
          OutputCount: 8
          CraftTime: 2
          Target: "SurvC,SmallC,HoverC,BaseC,LargeC,AdvC"
          { Child Inputs
            WoodPlanks: 3
          }
        }
        { +Template Name: NCPowder
          OutputCount: 24
          CraftTime: 1
          Target: "SurvC,SmallC,HoverC,BaseC,LargeC,AdvC"
          { Child Inputs
            Fiber: 32
          }
        }
        { +Template Name: 12.7x99mmBullet
          OutputCount: 100
          CraftTime: 5
          Target: "SmallC,HoverC,BaseC,LargeC,AdvC"
          { Child Inputs
            SteelPlate: 10
        	CopperIngot: 3
            NCPowder: 40
          }
        }
        { Template Name: NoTarget
          Target: ""
          { Child Inputs
            WoodLogs: 1
          }
        }
        """;

    private static Recipes Load()
    {
        var r = new Recipes();
        r.Load(new StringReader(Templates));
        return r;
    }

    [Fact]
    public void Parses_inputs_output_time_targets()
    {
        var b = Load().Get("12.7x99mmBullet");
        Assert.Equal(100, b.OutputCount);
        Assert.Equal(5, b.CraftTime);
        Assert.Contains("LargeC", b.Targets);
        Assert.DoesNotContain("SurvC", b.Targets);
        Assert.Equal([("SteelPlate", 10), ("CopperIngot", 3), ("NCPowder", 40)], b.Inputs); // tab-indented line too
    }

    [Fact]
    public void Ammo_from_raw_materials_like_a_real_test()
    {
        var stock = new Dictionary<string, int> { ["IronIngot"] = 250, ["CopperIngot"] = 150, ["WoodLogs"] = 90 };

        var p = Load().MakePlan("12.7x99mmBullet", 5000, stock, maxDepth: 5);

        Assert.True(p.Ok, string.Join(", ", p.Missing));
        Assert.Equal(50, p.Batches["12.7x99mmBullet"]);
        Assert.Equal(50, p.Batches["SteelPlate"]);
        Assert.Equal(84, p.Batches["WoodPlanks"]);        // 84 logs
        Assert.Equal(250, p.Consumed["IronIngot"]);
        Assert.Equal(150, p.Consumed["CopperIngot"]);
        Assert.Equal(84, p.Consumed["WoodLogs"]);
        Assert.Equal(16, p.Leftover["NCPowder"]);           // 2016 - 2000
        Assert.False(p.Consumed.ContainsKey("RockDust"));
    }

    [Fact]
    public void Missing_is_reported_at_the_highest_level_not_the_deepest()
    {
        var stock = new Dictionary<string, int> { ["IronIngot"] = 200, ["CopperIngot"] = 150, ["WoodLogs"] = 90 };

        var p = Load().MakePlan("12.7x99mmBullet", 5000, stock, maxDepth: 5);

        Assert.False(p.Ok);
        Assert.Equal(50, p.Missing["IronIngot"]);          // not "RockDust"
        Assert.False(p.Missing.ContainsKey("RockDust"));
    }

    [Fact]
    public void Uses_existing_intermediates_first()
    {
        var stock = new Dictionary<string, int> { ["SteelPlate"] = 500, ["CopperIngot"] = 150, ["NCPowder"] = 2000 };

        var p = Load().MakePlan("12.7x99mmBullet", 5000, stock, maxDepth: 5);

        Assert.True(p.Ok);
        Assert.False(p.Batches.ContainsKey("SteelPlate"));
        Assert.Equal(500, p.Consumed["SteelPlate"]);
        Assert.Empty(p.Leftover);
    }

    [Fact]
    public void Depth_limit_stops_expansion()
    {
        var stock = new Dictionary<string, int> { ["IronIngot"] = 250, ["CopperIngot"] = 150, ["WoodLogs"] = 90 };

        var p = Load().MakePlan("12.7x99mmBullet", 5000, stock, maxDepth: 2); // logs→planks→fiber→powder needs depth 4

        Assert.False(p.Ok);
        Assert.True(p.Missing.ContainsKey("NCPowder") || p.Missing.ContainsKey("Fiber"));
    }

    [Fact]
    public void Constructor_filter_and_no_target_recipes()
    {
        var r = Load();
        var stock = new Dictionary<string, int> { ["WoodLogs"] = 10 };
        Assert.False(r.MakePlan("NoTarget", 1, stock, 3).Ok);
        // only a portable constructor: the ammo recipe has no SurvC
        Assert.False(r.MakePlan("12.7x99mmBullet", 100, new Dictionary<string, int> { ["SteelPlate"] = 10, ["CopperIngot"] = 3, ["NCPowder"] = 40 }, 3, t => t == "SurvC").Ok);
    }

    private static readonly Dictionary<string, int> Ids = new()
        { ["IronIngot"] = 1, ["CopperIngot"] = 2, ["WoodLogs"] = 3, ["SteelPlate"] = 4, ["NCPowder"] = 5, ["12.7x99mmBullet"] = 6, ["Pistol"] = 7 };
    private static int IdOf(string n) => Ids.TryGetValue(n, out var i) ? i : 0;
    private static string NameOf(int id) => Ids.FirstOrDefault(kv => kv.Value == id).Key;

    [Fact]
    public void Apply_takes_inputs_and_puts_results_and_leftovers_back()
    {
        var stacks = new List<InvStack> { new(1, 250, 0), new(2, 150, 1), new(3, 90, 2), new(7, 1, 3, ammo: 8, decay: 5), new(1, 5, 4, decay: 3) };
        var stock = Recipes.StockOf(stacks, NameOf);
        Assert.Equal(250, stock["IronIngot"]);  // the worn stack doesn't count

        var plan = Load().MakePlan("12.7x99mmBullet", 5000, stock, 5);
        var (result, error) = Recipes.Apply(stacks, plan, IdOf, 64, _ => 999);

        Assert.Null(error);
        Assert.Equal(5000, Stacks.Total(result, 6));
        Assert.Equal(16, Stacks.Total(result, 5));
        Assert.Equal(6, Stacks.Total(result, 3));               // 90 - 84 logs
        Assert.Equal(5, Stacks.Total(result, 1));               // only the worn stack is left
        Assert.Contains(result, x => x.Id == 7 && x.Ammo == 8);  // pistol untouched
        Assert.Equal(0, Stacks.Total(result, 2));
    }

    [Fact]
    public void Apply_refuses_when_results_do_not_fit()
    {
        var stacks = new List<InvStack> { new(1, 250, 0), new(2, 150, 1), new(3, 90, 2) };
        var plan = Load().MakePlan("12.7x99mmBullet", 5000, Recipes.StockOf(stacks, NameOf), 5);
        var (result, error) = Recipes.Apply(stacks, plan, IdOf, 4, _ => 999); // 5000 bullets need 6 slots

        Assert.Null(result);
        Assert.Contains("don't fit", error);
    }

    [Fact]
    public void Scenario_file_replaces_the_base_file()
    {
        var root = Directory.CreateTempSubdirectory("recipes").FullName;
        try
        {
            var content = Path.Combine(root, "Content");
            var scenario = Path.Combine(root, "Scenario");
            Directory.CreateDirectory(Path.Combine(content, "Configuration"));
            Directory.CreateDirectory(Path.Combine(scenario, "Content", "Configuration"));
            File.WriteAllText(Path.Combine(content, "Configuration", "Templates.ecf"),
                "{ Template Name: AntiParasitePills\n  Target: \"FoodP\"\n  { Child Inputs\n    Fiber: 1\n  }\n}\n" +
                "{ Template Name: IronIngot\n  Target: \"Furn\"\n  { Child Inputs\n    IronOre: 1\n  }\n}\n");
            Assert.NotNull(Recipes.LoadFiles(content, scenario, null).Get("AntiParasitePills")); // no scenario file: base

            File.WriteAllText(Path.Combine(scenario, "Content", "Configuration", "Templates.ecf"),
                "{ +Template Name: IronIngot\n  OutputCount: 10\n  Target: \"Furn\"\n  { Child Inputs\n    IronOre: 5\n  }\n}\n");
            var r = Recipes.LoadFiles(content, scenario, null);
            Assert.Null(r.Get("AntiParasitePills"));
            Assert.Equal(10, r.Get("IronIngot").OutputCount);
            Assert.Equal(1, r.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Real_RE2_templates_if_available()
    {
        var content = Environment.GetEnvironmentVariable("GAMETEXT_CONTENT");
        if (string.IsNullOrEmpty(content)) return;
        var r = Recipes.LoadFiles(content, Environment.GetEnvironmentVariable("GAMETEXT_SCENARIO"), output.WriteLine);
        output.WriteLine($"{r.Count} recipes");
        var p = r.MakePlan("12.7x99mmBullet", 5000,
            new Dictionary<string, int> { ["IronIngot"] = 250, ["CopperIngot"] = 150, ["WoodLogs"] = 90 }, 5);
        output.WriteLine($"ok={p.Ok} missing={string.Join(",", p.Missing)} consumed={string.Join(",", p.Consumed)} " +
                         $"leftover={string.Join(",", p.Leftover)} batches={string.Join(",", p.Batches)} time={p.CraftSeconds}s");
        Assert.True(p.Ok);
    }
}
