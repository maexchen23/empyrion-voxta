using VoxtaBridge;
using Xunit;

namespace VoxtaBridge.Mod.Tests;

public class JumpRulesTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1000, 1)]
    [InlineData(5000, 1)]
    [InlineData(5001, 2)]   // rounded up to the next Pentaxid
    [InlineData(9999, 2)]
    [InlineData(10000, 2)]
    [InlineData(10000.001f, 2)]
    [InlineData(25000, 5)]
    public void CostIsOnePentaxidPerStartedFiveKm(float distance, int cost) => Assert.Equal(cost, JumpRules.Cost(distance));

    [Theory]
    [InlineData(10000, 2.5, 4)]
    [InlineData(10001, 10, 2)]
    [InlineData(25000, 25, 1)]
    [InlineData(10000, 0, 2)]  // invalid setting falls back to 5 km
    public void CostUsesConfiguredKmPerPentaxid(float distance, double km, int cost) => Assert.Equal(cost, JumpRules.Cost(distance, km));

    [Theory]
    [InlineData(2, 978)]   // Tallodar Moon (HUD: 979 / 981)
    [InlineData(3, 1956)]  // Akua Moon 1955, Tallodar 1959
    [InlineData(4, 3912)]  // Akua 3916
    public void PlanetRadiusMatchesTheMeasurements(int size, int radius) => Assert.Equal(radius, JumpRules.PlanetRadius(size), 0);

    [Fact]
    public void StopDistance()
    {
        Assert.Equal(572f, JumpRules.StopDistance(72f));  // a carrier
        Assert.Equal(2000f, JumpRules.StopDistance(null)); // asteroid / unknown
        Assert.Equal(3956f, JumpRules.BodyStopDistance(1956f, 2000f)); // Akua Moon, default clearance
        Assert.Equal(3956f, JumpRules.BodyStopDistance(1956f, -1f));   // invalid setting → default
        Assert.Equal(6956f, JumpRules.BodyStopDistance(1956f, 5000f));
    }

    [Fact]
    public void RangeChecks()
    {
        Assert.Null(JumpRules.CheckRange(5000, 1000, 25000));
        Assert.Contains("at least 1 km", JumpRules.CheckRange(500, 1000, 25000));
        Assert.Contains("at most 25 km", JumpRules.CheckRange(40000, 1000, 25000));
    }

    [Fact]
    public void Cooldown()
    {
        var t = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0, JumpRules.CooldownLeft(null, t, 120));
        Assert.Equal(120, JumpRules.CooldownLeft(t, t, 120));
        Assert.Equal(30, JumpRules.CooldownLeft(t, t.AddSeconds(90), 120));
        Assert.Equal(0, JumpRules.CooldownLeft(t, t.AddSeconds(121), 120));
        Assert.Equal(0, JumpRules.CooldownLeft(t, t, 0));
    }

    [Fact]
    public void ForbiddenPlayfields()
    {
        Assert.True(JumpRules.IsForbiddenPlayfield("Central BH of M31"));
        Assert.True(JumpRules.IsForbiddenPlayfield("Accretion Disk 3"));
        Assert.False(JumpRules.IsForbiddenPlayfield("Akua Sector"));
    }

    [Theory]
    [InlineData(572, "572 m")]
    [InlineData(5706.95f, "5.71 km")]
    [InlineData(25000, "25 km")]
    public void Km(float m, string text) => Assert.Equal(text, JumpRules.Km(m));

    [Theory]
    [InlineData(500, true, 0.0f, 420, true, 0.0f, true)]    // shield took a hit
    [InlineData(500, true, 0.0f, 500, true, 0.01f, true)]   // block damage
    [InlineData(500, true, 0.0f, 520, true, 0.0f, false)]   // recharging
    [InlineData(500, true, 0.0f, 0, false, 0.0f, false)]    // switched off
    [InlineData(500, true, 0.02f, 500, true, 0.01f, false)] // repaired
    [InlineData(0, false, 0.0f, 0, false, 0.0f, false)]     // no shield, nothing happened
    public void HitIsShieldDropOrDamage(int ps, bool pon, float pd, int s, bool on, float d, bool hit) =>
        Assert.Equal(hit, JumpRules.IsHit(ps, pon, pd, s, on, d));
}
