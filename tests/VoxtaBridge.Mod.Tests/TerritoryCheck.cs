using VoxtaBridge;
using Xunit;
using Xunit.Abstractions;

namespace VoxtaBridge.Mod.Tests;

/// <summary>
/// Opt-in check of the territory rules against a real save: every POI that exists should have been allowed to spawn
/// where it is. POIS_TSV = bpname, facgroup, system, startype, x, y, z (LY), playfield (sqlite3 export of global.db);
/// TRADERS_ROOT and GALAXY_CONFIG as for the other real-file tests.
/// </summary>
public class TerritoryCheck(ITestOutputHelper output)
{
    [Fact]
    public void Existing_pois_are_allowed_where_they_are()
    {
        var tsv = Environment.GetEnvironmentVariable("POIS_TSV");
        var root = Environment.GetEnvironmentVariable("TRADERS_ROOT");
        var gc = Environment.GetEnvironmentVariable("GALAXY_CONFIG");
        if (tsv == null || root == null || gc == null) return;
        var odds = new GalaxyOdds();
        odds.LoadGalaxyConfig(File.ReadAllText(gc));
        output.WriteLine($"{odds.Territories.Count} territories: " + string.Join("; ", odds.Territories.Select(t => $"{t.Faction} r{t.Radius} +{t.Others.Count}")));
        var idx = new TraderIndex();
        foreach (var dir in Directory.GetDirectories(Path.Combine(root, "Playfields")))
            foreach (var f in Directory.GetFiles(dir, "*.yaml")) idx.LoadPlayfield(Path.GetFileName(dir), File.ReadAllText(f));
        var groupOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(Path.Combine(root, "Prefabs"), "*.epb"))
        {
            var d = File.ReadAllBytes(f);
            groupOf[Path.GetFileNameWithoutExtension(f)] = TraderIndex.HeaderString(d, Math.Min(d.Length, 4096), 7);
        }
        int rows = 0, checkedRows = 0, denied = 0, neutral = 0;
        var bad = new Dictionary<string, int>();
        foreach (var line in File.ReadLines(tsv))
        {
            var c = line.Split('\t');
            if (c.Length < 8) continue;
            rows++;
            if (!groupOf.TryGetValue(c[0], out var g) || g == null) continue;
            var spawns = idx.Spawns(g);
            if (spawns.Count == 0) continue;
            checkedRows++;
            var at = odds.TerritoriesAt(double.Parse(c[4], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(c[5], System.Globalization.CultureInfo.InvariantCulture), double.Parse(c[6], System.Globalization.CultureInfo.InvariantCulture));
            if (at.Count == 0) neutral++;
            var best = spawns.Max(s => GalaxyOdds.SpawnAllowed(s.Faction, s.Territory, at));
            if (best > 0) continue;
            denied++;
            var key = $"{g} [{string.Join(" | ", spawns.Select(s => $"{s.Faction}:{(s.Territory == null ? "-" : string.Join(",", s.Territory))}").Distinct().Take(4))}] at {c[2]} ({c[3]}) in {(at.Count == 0 ? "neutral" : string.Join("+", at.Select(a => a.t.Faction)))}";
            bad[key] = bad.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        output.WriteLine($"{rows} POIs, {checkedRows} with known spawn entries ({neutral} in neutral space), denied by the rules: {denied}");
        foreach (var kv in bad.OrderByDescending(k => k.Value).Take(25)) output.WriteLine($"  {kv.Value}× {kv.Key}");
    }
}
