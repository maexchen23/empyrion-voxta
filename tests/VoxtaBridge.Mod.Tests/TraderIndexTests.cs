using System.IO.Compression;
using System.Text;
using VoxtaBridge;
using Xunit;
using Xunit.Abstractions;

namespace VoxtaBridge.Mod.Tests;

public class TraderIndexTests(ITestOutputHelper output)
{
    const string Traders = """
        { Trader Name: ManufacturingPower
          SellingText: "Power # not a comment"
          Item1: "ReactorCore, mf=1.0-1.25, 10-20, 0, 0"
          Item2: "Electronics, mf=1.0-1.25, 0-0, mf=0.5-0.75, 10-100"   # buys only
        }
        { Trader Name: SalvagerPirates
          Item1: "ReactorCore, mf=1.25-1.5, 0-1, mf=0.5, 5-10"
        }
        """;

    const string Factions = """
        { Faction Id: 2, Name: "Zirax"       # comment
          Abbrev: Zirax
          Reputation: true
        }
        { Faction Id: 7, Name: "Polaris"
          Abbrev: Polaris
          Reputation: true
        }
        { Faction Id: 15, Name: "Civilian"
          Abbrev: Civilian
        }
        { Faction Id: 16, Name: "Pirates"
          Reputation: true
        }
        """;

    const string Rep = """
        { Reputation Name: "Human:1"
          Zirax: 0
          Polaris: 15000
          Pirates: 9000
        }
        { Reputation Name: "Action.Reputation"
          KilledMember: "-1000, -1000"
        }
        """;

    const string Playfield = """
        PlayfieldType: Planet
        PlanetType: AridDesert

        POIs:
            Random:
                - GroupName: R2FactoryPolaris3
                  Faction: Polaris
                  CountMinMax: [ 1, 1 ]
                  Properties:
                    - Key: RegenAfter
                      Value: 720
                  DroneProb: 0.5
        # - GroupName: Commented
                - GroupName: Unused
                  Faction: Zirax
                  CountMinMax: [ 0, 0 ]
                - GroupName: PirateYard
                  Faction: Pirates
                  CountMinMax: [ 1, 2 ]
        """;

    static TraderIndex Index()
    {
        var idx = new TraderIndex();
        idx.LoadTraderConfig(new StringReader(Traders));
        idx.LoadFactions(new StringReader(Factions));
        idx.LoadDefReputation(new StringReader(Rep));
        idx.LoadPlayfield("Arid_V2", Playfield);
        return idx;
    }

    /// <summary>A minimal .epb: header with property 7 (group) and 16 (name), then a zip with an entity "Type".</summary>
    static byte[] Prefab(string group, string display, params string[] types)
    {
        var ms = new MemoryStream();
        void Prop(int key, string s)
        {
            ms.Write(BitConverter.GetBytes(key));
            ms.Write(new byte[4]);
            ms.WriteByte((byte)s.Length);
            ms.Write(Encoding.ASCII.GetBytes(s));
        }
        ms.Write("EPB\x01\x02"u8);
        Prop(7, group);
        Prop(16, display);
        var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
        using (var s = zip.CreateEntry("0").Open())
            foreach (var t in types)
            {
                s.Write("\x03Ent\x07Trader2\x01\x04Type"u8);
                s.WriteByte((byte)t.Length);
                s.Write(Encoding.UTF8.GetBytes(t));
                s.Write("\x00\x03Pay"u8);
            }
        ms.Write(zipBytes.ToArray());
        return ms.ToArray();
    }

    [Fact]
    public void Trader_config_sellers_and_buyers()
    {
        var idx = Index();
        Assert.Equal(2, idx.TraderCount);
        var sellers = idx.Sellers("ReactorCore");
        Assert.Equal(["ManufacturingPower", "SalvagerPirates"], sellers.Select(s => s.Trader));
        Assert.Equal((10, 20), (sellers[0].StockMin, sellers[0].StockMax));
        Assert.Empty(idx.Sellers("Electronics"));
        Assert.Single(idx.Buyers("Electronics"));
        Assert.Contains("ReactorCore", idx.SoldItems);
        Assert.DoesNotContain("Electronics", idx.SoldItems);
    }

    [Fact]
    public void Playfield_groups_skip_zero_counts_and_comments_and_keep_faction_after_nested_lists()
    {
        var idx = Index();
        var s = Assert.Single(idx.Spawns("R2FactoryPolaris3"));
        Assert.Equal("Polaris", s.Faction);
        Assert.Equal("Arid Desert planet", s.Kind);
        Assert.Empty(idx.Spawns("Unused"));
        Assert.Empty(idx.Spawns("Commented"));
        Assert.Equal("Pirates", Assert.Single(idx.Spawns("PirateYard")).Faction);
    }

    [Fact]
    public void Kind_labels()
    {
        Assert.Equal("Barren Moon", TraderIndex.KindLabel("Moon", "BarrenMoon", "x"));
        Assert.Equal("Poor Quality Asteroid Field (space)", TraderIndex.KindLabel("Space", "PoorQualityAsteroidField", "x"));
        Assert.Equal("Alien planet", TraderIndex.KindLabel("Planet", "Alien", "x"));
        Assert.Equal("Starter Akua Orbit (space)", TraderIndex.KindLabel("Space", null, "Starter_Akua_Orbit"));
    }

    [Fact]
    public void Prefab_header_and_traders()
    {
        var idx = Index();
        var p = idx.AddPrefab("R2BA_PolarisPowerplant01", Prefab("R2FactoryPolaris3", "Fusion Powerplant", "ManufacturingPower", "SecurityGuard"));
        Assert.NotNull(p);
        Assert.Equal("R2FactoryPolaris3", p.Group);
        Assert.Equal("Fusion Powerplant", p.Display);
        Assert.Equal(["ManufacturingPower"], p.Traders);
        Assert.Same(p, Assert.Single(idx.PrefabsWith("ManufacturingPower")));
        Assert.Null(idx.AddPrefab("NoTraders", Prefab("G", "Nothing here", "SecurityGuard")));
    }

    /// <summary>Layout from the dev/prod game.dat: count, then [uint16 standing][int32 player faction][0][npc id].</summary>
    static byte[] GameDat(params (int standing, int pf, int npc)[] rows)
    {
        var ms = new MemoryStream();
        ms.Write("FR\x94x\x15\x00\x00\x00junk header 1234"u8);
        ms.Write(BitConverter.GetBytes(rows.Length));
        foreach (var (st, pf, npc) in rows)
        {
            ms.Write(BitConverter.GetBytes((ushort)st));
            ms.Write(BitConverter.GetBytes(pf));
            ms.WriteByte(0);
            ms.WriteByte((byte)npc);
        }
        ms.Write("\x07\x06\x00\x00S\xd3\xb8z\x05\x02\x00\x00\x00\x07+v1.20.1"u8);
        return ms.ToArray();
    }

    [Fact]
    public void Standings_from_game_dat_with_start_values_as_fallback()
    {
        var idx = Index();
        var dat = GameDat((0, 1, 2), (0, 100, 2), (804, 100, 2), (14951, 1, 7), (19255, 100, 7));
        var map = TraderIndex.ReadStandings(dat, idx.IsNpcFaction);
        Assert.Equal(14951, map[1][7]);
        Assert.Equal(19255, map[100][7]);
        Assert.Equal(804, map[100][2]); // the later row wins

        var polaris = idx.Faction("Polaris");
        Assert.Equal(14951, idx.Standing(map, 1, polaris));
        Assert.Equal(9000, idx.Standing(map, 1, idx.Faction("Pirates")));   // not in game.dat → start value
        Assert.Equal(15000, idx.Standing(map, -1, polaris));                // no player faction row → start value
        Assert.Null(idx.Standing(map, 1, idx.Faction(15)));                 // Civilian has no reputation
    }

    [Theory]
    [InlineData(0, "Hated")]
    [InlineData(6000, "Hated")]
    [InlineData(6001, "Unfriendly")]
    [InlineData(12000, "Unfriendly")]
    [InlineData(14951, "Neutral")]
    [InlineData(18001, "Friendly")]
    [InlineData(24001, "Honored")]
    public void Tiers(int standing, string tier)
    {
        Assert.Equal(tier, TraderIndex.Tier(standing));
        Assert.Equal(standing > 12000, TraderIndex.NeutralOrBetter(standing));
    }

    /// <summary>Opt-in: TRADERS_ROOT = a copy of the scenario folder (Content/Configuration, Playfields, Prefabs).</summary>
    [Fact]
    public void Real_scenario_files()
    {
        var root = Environment.GetEnvironmentVariable("TRADERS_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        var idx = new TraderIndex();
        var cfg = Path.Combine(root, "Content", "Configuration");
        using (var r = new StreamReader(Path.Combine(cfg, "TraderNPCConfig.ecf"))) idx.LoadTraderConfig(r);
        if (File.Exists(Path.Combine(cfg, "Factions.ecf"))) using (var r = new StreamReader(Path.Combine(cfg, "Factions.ecf"))) idx.LoadFactions(r);
        foreach (var dir in Directory.GetDirectories(Path.Combine(root, "Playfields")))
            foreach (var f in Directory.GetFiles(dir, "*.yaml")) idx.LoadPlayfield(Path.GetFileName(dir), File.ReadAllText(f));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in Directory.GetFiles(Path.Combine(root, "Prefabs"), "*.epb")) idx.AddPrefab(Path.GetFileNameWithoutExtension(f), File.ReadAllBytes(f));
        output.WriteLine($"{idx.TraderCount} traders, {idx.PrefabCount} prefabs with traders ({sw.ElapsedMilliseconds} ms), {idx.GroupCount} groups");
        foreach (var s in idx.Sellers("ReactorCore"))
            foreach (var p in idx.PrefabsWith(s.Trader))
                output.WriteLine($"{s.Trader} {s.StockMin}-{s.StockMax}: {p.Display} [{p.Prefab}, {p.Group}] → " +
                                 string.Join(", ", idx.Spawns(p.Group).Select(x => $"{x.Kind} ({x.Faction})")));
        var kenex = idx.Prefab("R2BAO_PolKenexStory");
        Assert.NotNull(kenex);
        Assert.Equal("Kenex Station 62", kenex.Display);
        Assert.Contains("QuantumSTAR", kenex.Traders);
        Assert.NotEmpty(idx.Spawns(kenex.Group));
    }
}

public class GameDatRealTests(ITestOutputHelper output)
{
    /// <summary>Opt-in: GAMEDAT_FILES = ';'-separated game.dat copies; prints what the scan finds.</summary>
    [Fact]
    public void Real_game_dat()
    {
        var files = Environment.GetEnvironmentVariable("GAMEDAT_FILES");
        if (string.IsNullOrEmpty(files)) return;
        var npc = new HashSet<int> { 2, 6, 7, 10, 11, 12, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 28, 29, 30 };
        foreach (var f in files.Split(';'))
        {
            var map = TraderIndex.ReadStandings(File.ReadAllBytes(f), npc.Contains);
            foreach (var (pf, row) in map)
                output.WriteLine($"{Path.GetFileName(f)} player faction {pf}: " + string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}")));
            Assert.NotEmpty(map);
        }
    }

    [Fact]
    public void Planet_type_maps_to_playfield_folders()
    {
        var idx = new TraderIndex();
        idx.LoadPlayfield("EdenDesertStorm", "PlayfieldType: Planet\nPlanetType: Stormy Desert\n");
        idx.LoadPlayfield("EdenBarrenHot", "PlayfieldType: Planet\nPlanetType: \"Barren\"   # hot\n");
        idx.LoadPlayfield("Start_Barren", "PlayfieldType: Planet\r\nPlanetType: Barren\r\n");
        Assert.Equal(new[] { "EdenDesertStorm" }, idx.FoldersForPlanetType("Stormy Desert"));
        Assert.Equal(new[] { "EdenBarrenHot", "Start_Barren" }, idx.FoldersForPlanetType("barren").OrderBy(x => x));
        Assert.Empty(idx.FoldersForPlanetType("K4"));
    }

    [Fact]
    public void Spawn_near_an_optional_anchor_is_only_as_likely_as_the_anchor()
    {
        var idx = new TraderIndex();
        idx.LoadPlayfield("EdenMoonBarrenCopper", string.Join("\n",
            "PlayfieldType: Moon", "PlanetType: Varnished Barren Moon", "POIs:", "    Random:",
            "        - GroupName: R2PolarisAuxT2", "          Faction: Polaris", "          CountMinMax: [ 0, 1 ]",
            "        - GroupName: R2FactoryPolaris3", "          Faction: Polaris", "          CountMinMax: [ 1, 1 ]",
            "          SpawnPOINear: [R2PolarisAuxT2]", "          SpawnPOINearRange: [100, 250]",
            "        - GroupName: Lonely", "          Faction: Polaris", "          CountMinMax: [ 1, 1 ]",
            "          SpawnPOINear: [NotInThisFile]", ""));
        Assert.Equal(0.5, idx.Spawns("R2PolarisAuxT2").Single().Chance);
        Assert.Equal(0.5, idx.Spawns("R2FactoryPolaris3").Single().Chance);
        Assert.Equal(1.0, idx.Spawns("Lonely").Single().Chance);
    }

    [Fact]
    public void Anchor_chance_comes_from_an_entry_with_the_same_territory_rule()
    {
        var idx = new TraderIndex();
        idx.LoadPlayfield("Moon", string.Join("\n",
            "PlayfieldType: Moon", "PlanetType: Varnished Barren Moon", "POIs:", "    Random:",
            "        - GroupName: Aux", "          Faction: Polaris", "          CountMinMax: [ 0, 1 ]",
            "        - GroupName: Factory", "          Faction: Polaris", "          CountMinMax: [ 1, 1 ]", "          SpawnPOINear: [Aux]",
            "        - GroupName: Aux", "          Faction: Polaris", "          FactionTerritory: [Eden_PolarisTerritory]", "          CountMinMax: [ 1, 1 ]",
            "        - GroupName: Depot", "          Faction: Polaris", "          CountMinMax: [ 1, 1 ]", "          SpawnPOINear: [OnlyInTerritory]",
            "        - GroupName: OnlyInTerritory", "          Faction: Polaris", "          FactionTerritory: [Eden_PolarisTerritory]", "          CountMinMax: [ 1, 1 ]",
            ""));
        Assert.Equal(0.5, idx.Spawns("Factory").Single().Chance);
        Assert.Empty(idx.Spawns("Depot"));
    }

    /// <summary>Opt-in: TRADERS_ROOT + PLANET_TYPE dumps the spawn entries per folder of that planet type.</summary>
    [Fact]
    public void Dump_spawns_for_planet_type()
    {
        var root = System.Environment.GetEnvironmentVariable("TRADERS_ROOT");
        var type = System.Environment.GetEnvironmentVariable("PLANET_TYPE");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(type)) return;
        var idx = new TraderIndex();
        foreach (var dir in Directory.GetDirectories(Path.Combine(root, "Playfields")))
            foreach (var f in Directory.GetFiles(dir, "*.yaml")) idx.LoadPlayfield(Path.GetFileName(dir), File.ReadAllText(f));
        foreach (var folder in idx.FoldersForPlanetType(type))
            foreach (var g in new[] { "R2FactoryPolaris1", "R2FactoryPolaris3" })
                foreach (var sp in idx.Spawns(g).Where(x => x.Playfield == folder))
                    output.WriteLine($"{folder} {g} fac={sp.Faction} chance={sp.Chance} terr={(sp.Territory == null ? "-" : string.Join(",", sp.Territory))}");
    }
}
