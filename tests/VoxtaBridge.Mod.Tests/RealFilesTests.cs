using VoxtaBridge;
using Xunit;
using Xunit.Abstractions;

namespace VoxtaBridge.Mod.Tests;

/// <summary>
/// Opt-in check against real game files (not in git: proprietary). Set GAMETEXT_CONTENT to a copy of the
/// game's Content folder and optionally GAMETEXT_SCENARIO to a scenario folder; otherwise this does nothing.
/// </summary>
public class RealFilesTests(ITestOutputHelper output)
{
    [Fact]
    public void Real_files_parse_and_resolve_known_items()
    {
        var content = Environment.GetEnvironmentVariable("GAMETEXT_CONTENT");
        if (string.IsNullOrEmpty(content)) return;
        var scenario = Environment.GetEnvironmentVariable("GAMETEXT_SCENARIO");

        var t = GameText.Load(content, scenario, output.WriteLine);
        output.WriteLine($"{t.LocalizationCount} texts, {t.EntryCount} items/blocks, {t.TokenCount} tokens");

        foreach (var n in new[] { "RadarSuitT1", "CombatKnife", "SurvivalTool", "Flashlight", "Token", "PistolT2", "OxygenBottleSmall", "AmmoBox", "HarvestController", "CargoBox" })
            output.WriteLine($"{n} -> {t.DisplayName(n) ?? "(none)"} | {t.Description(n) ?? "(no desc)"}");
        foreach (var id in new[] { 1, 120, 121 })
            output.WriteLine($"token {id} -> {t.GetToken(id)?.Name} | {t.GetToken(id)?.Description}");

        Assert.True(t.LocalizationCount > 1000);
        Assert.Equal("Combat Knife", t.DisplayName("CombatKnife"));
        Assert.True(t.IsToken("Token"));
    }

    [Fact]
    public void Real_files_ammo_and_weapons()
    {
        var content = Environment.GetEnvironmentVariable("GAMETEXT_CONTENT");
        if (string.IsNullOrEmpty(content)) return;
        var t = GameText.Load(content, Environment.GetEnvironmentVariable("GAMETEXT_SCENARIO"), _ => { });
        foreach (var ammo in new[] { "12.7x99mmBullet", "5.56mmBullet" })
            output.WriteLine($"{ammo} ({t.DisplayName(ammo)}) used by: " +
                             string.Join("; ", t.UsersOf(ammo).Select(u => $"{t.DisplayName(u)} [{u}]")));
        foreach (var w in new[] { "Minigun", "MinigunEpic", "Sniper2Epic", "TurretBaseMinigun", "WeaponSV02" })
            output.WriteLine($"{w} ({t.DisplayName(w)}) uses: {string.Join(", ", t.AmmoOf(w))}");
        Assert.Contains("5.56mmBullet", t.AmmoOf("Minigun"));
        Assert.Contains("12.7x99mmBullet", t.AmmoOf("TurretBaseMinigun"));
        foreach (var w in new[] { "AssaultRifle", "Minigun", "Pistol", "Shotgun", "Sniper", "TurretBaseMinigun" })
            output.WriteLine($"{w}: " + string.Join(", ", t.AmmoOf(w).Select(a => $"{a} x{t.MagazineOf(w, a)}")));
        Assert.Equal(30, t.MagazineOf("AssaultRifle", "5.56mmBullet"));
        Assert.Equal((250, true), t.Magazine("MinigunEpic", "PulseLaserChargeSS"));
        Assert.Equal((20, true), t.Magazine("LaserRifle", "PulseLaserChargeRifle"));
    }
}
