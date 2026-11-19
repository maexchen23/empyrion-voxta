using VoxtaBridge;
using Xunit;
using Xunit.Abstractions;

namespace VoxtaBridge.Mod.Tests;

public class PoiClassTests(ITestOutputHelper output)
{
    static PoiKind K(string group, int faction = 7, int traders = 0, int turrets = 0, int x = 20, int y = 10, int z = 20, string display = null, string dialogue = null, int count = -1) =>
        PoiClassifier.Classify(new PoiFacts { Group = group, Faction = faction, Traders = traders, TurretTypes = turrets, Turrets = count, SizeX = x, SizeY = y, SizeZ = z, Display = display, PoiDialogue = dialogue }).kind;

    [Fact]
    public void Device_group_count_from_header()
    {
        // [len]["cpgTurret"] 01 01 ff [uint16 count] then positions; two groups of the same name add up
        var h = new List<byte> { 0, 0, 0 };
        void Group(string name, ushort n) { h.Add((byte)name.Length); h.AddRange(System.Text.Encoding.ASCII.GetBytes(name)); h.AddRange(new byte[] { 1, 1, 0xff }); h.AddRange(BitConverter.GetBytes(n)); for (var i = 0; i < n * 4; i++) h.Add(0x80); }
        Group("cpgTurret", 8); Group("cpgSentry", 4); Group("cpgTurret", 2);
        var d = h.ToArray();
        Assert.Equal(10, TraderIndex.DeviceGroupCount(d, d.Length, "cpgTurret"));
        Assert.Equal(4, TraderIndex.DeviceGroupCount(d, d.Length, "cpgSentry"));
        Assert.Equal(-1, TraderIndex.DeviceGroupCount(d, d.Length, "cpgDoor"));
    }

    [Fact]
    public void Examples_from_the_save()
    {
        Assert.Equal(PoiKind.Decorative, K("R2TraderSpaceDeco", 19, x: 25, y: 14, z: 1, display: "Billboard"));
        Assert.Equal(PoiKind.Trading, K("R2ColonistSpaceMain", 19, traders: 6, turrets: 6, x: 83, y: 46, z: 66));
        Assert.Equal(PoiKind.Trading, K("R2FactoryPolaris1", 7, traders: 1));
        Assert.Equal(PoiKind.MinorFaction, K("R2ZiraxSpaceDefense", 2, turrets: 3, x: 19, y: 17, z: 19));
        Assert.Equal(PoiKind.MajorFaction, K("R2ZiraxSpaceT1", 2, turrets: 7, x: 29, y: 30, z: 37));
        Assert.Equal(PoiKind.MajorFaction, K("R2DroneBase", 2, turrets: 8, x: 45, y: 21, z: 45));
        Assert.Equal(PoiKind.Abandoned, K("R2AbandonedPOI", 71, turrets: 3, dialogue: "POI_AbandonedPad_Init"));
        Assert.Equal(PoiKind.Salvage, K("Eden_SpaceJunkT1", 68, x: 12, y: 5, z: 14));
        Assert.Equal(PoiKind.Salvage, K("Eden_RuinsLogisticsAuxT1", 8, x: 26, y: 9, z: 23));
        Assert.Equal(PoiKind.Resource, K("EdenAsteroidPentaxidT1", 255, x: 45, y: 29, z: 33));
        Assert.Equal(PoiKind.Decorative, K("Eden_DummyPOI2", 255, x: 1, y: 1, z: 1));
        Assert.Equal(PoiKind.Civilian, K("Eden_FrontierComArray", 15, x: 13, y: 27, z: 13));
        Assert.Equal(PoiKind.MinorFaction, K("Eden_ColDefenseSpaceT1", 19, turrets: 2, x: 21, y: 15, z: 9));
        Assert.Equal(PoiKind.Civilian, K("RE2_ProgHackSpaceT1", 15, turrets: 1, x: 17, y: 22, z: 17));
        Assert.Equal(PoiKind.Resource, PoiClassifier.ClassifyByName("AsteroidVoxel02Copper", "Copper Asteroid", 255).kind);
        Assert.Equal(PoiKind.Salvage, PoiClassifier.ClassifyByName("BA_CrashedCV_Wokan", "Crashed Ship", 255).kind);
        // real turret counts (cpgTurret) win over turret types: a small, unnamed armed POI with 12 turrets is major
        Assert.Equal(PoiKind.MajorFaction, K("R2SomethingT1", 2, turrets: 3, count: 12));
        Assert.Equal(PoiKind.MinorFaction, K("R2SomethingT1", 2, turrets: 7, count: 4));
        Assert.Equal(PoiKind.MinorFaction, K("R2ZiraxSpaceDefense", 2, turrets: 3, count: 11, x: 19, y: 17, z: 19)); // defense, not large
        Assert.True(PoiClassifier.IsRealTarget(PoiKind.Trading));
        Assert.False(PoiClassifier.IsRealTarget(PoiKind.Decorative));
    }

    /// <summary>Opt-in: POIS_TSV (bpname, facgroup, …) + TRADERS_ROOT: classify every existing POI and print the distribution.</summary>
    [Fact]
    public void Real_pois_distribution()
    {
        var tsv = Environment.GetEnvironmentVariable("POIS_TSV");
        var root = Environment.GetEnvironmentVariable("TRADERS_ROOT");
        if (tsv == null || root == null) return;
        var idx = new TraderIndex();
        using (var r = new StreamReader(Path.Combine(root, "Content", "Configuration", "TraderNPCConfig.ecf"))) idx.LoadTraderConfig(r);
        foreach (var f in Directory.GetFiles(Path.Combine(root, "Prefabs"), "*.epb")) idx.AddPrefab(Path.GetFileNameWithoutExtension(f), File.ReadAllBytes(f));
        output.WriteLine($"{idx.FactsCount} prefab facts");
        var byKind = new Dictionary<PoiKind, Dictionary<string, int>>();
        var missing = 0;
        foreach (var line in File.ReadLines(tsv))
        {
            var c = line.Split('\t');
            var facts = idx.Facts(c[0]);
            if (facts == null)
            {
                missing++;
                var (k0, w0) = PoiClassifier.ClassifyByName(c[0], c[0], int.Parse(c[1]));
                if (!byKind.TryGetValue(k0, out var d0)) byKind[k0] = d0 = new Dictionary<string, int>();
                var key0 = $"(no prefab) {System.Text.RegularExpressions.Regex.Replace(c[0], "[0-9]+", "")} ({w0})";
                d0[key0] = d0.TryGetValue(key0, out var n0) ? n0 + 1 : 1;
                continue;
            }
            var copy = facts.WithFaction(int.Parse(c[1]));
            var (kind, why) = PoiClassifier.Classify(copy);
            if (!byKind.TryGetValue(kind, out var d)) byKind[kind] = d = new Dictionary<string, int>();
            var key = $"{facts.Display} [{facts.Group}] f{c[1]} t{facts.TurretTypes}/T{facts.Turrets}/S{facts.Sentries} {facts.SizeX}x{facts.SizeY}x{facts.SizeZ} ({why})";
            d[key] = d.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        output.WriteLine($"no prefab facts for {missing} POIs");
        var withGroup = 0; var total = 0;
        foreach (var line in File.ReadLines(tsv)) { var f = idx.Facts(line.Split('\t')[0]); if (f == null) continue; total++; if (f.Turrets >= 0) withGroup++; }
        output.WriteLine($"cpgTurret group present for {withGroup} of {total} POIs with prefab facts");
        foreach (var (kind, d) in byKind.OrderByDescending(k => k.Value.Values.Sum()))
        {
            output.WriteLine($"{kind}: {d.Values.Sum()} POIs, {d.Count} kinds");
            foreach (var kv in d.OrderByDescending(x => x.Value).Take(14)) output.WriteLine($"   {kv.Value}× {kv.Key}");
        }
    }
}
