using VoxtaBridge;
using Xunit;

namespace VoxtaBridge.Mod.Tests;

public class SizeMemoryTests
{
    static readonly DateTime T0 = new(2026, 11, 18, 11, 5, 0, DateTimeKind.Utc);

    [Fact]
    public void RadiusIsHalfTheDiagonal()
    {
        var m = new SizeMemory();
        m.Record(18001, "Test Hauler", "Akua Sector", 84, 36, 110, T0);
        Assert.Equal(71.5, m.Get(18001)!.Radius, 1); // sqrt(84²+36²+110²)/2
    }

    [Fact]
    public void NewSizeIsPendingUntilSaved()
    {
        var m = new SizeMemory();
        m.Record(1, "A", "Akua Sector", 10, 10, 10, T0);
        var p = m.Pending();
        Assert.Single(p);
        m.Saved(p);
        Assert.False(m.Dirty);
    }

    [Fact]
    public void UnchangedSizeIsResavedOnlyAfterTheRefreshInterval()
    {
        var m = new SizeMemory();
        m.Record(1, "A", "Akua Sector", 10, 10, 10, T0);
        m.Saved(m.Pending());
        m.Record(1, "A", "Akua Sector", 10, 10, 10, T0.AddMinutes(1));
        Assert.False(m.Dirty);
        Assert.Equal(T0.AddMinutes(1), m.Get(1)!.SeenUtc); // memory is current anyway
        m.Record(1, "A", "Akua Sector", 10, 10, 10, T0 + SizeMemory.SeenRefresh);
        Assert.True(m.Dirty);
    }

    [Fact]
    public void ChangedSizeOrSectorIsPendingAtOnce()
    {
        var m = new SizeMemory();
        m.Record(18001, "Test Carrier", "Akua Sector", 84, 36, 110, T0);
        m.Saved(m.Pending());
        m.Record(18001, "Test Carrier", "Akua Sector", 86, 36, 110, T0.AddSeconds(30)); // built on
        Assert.True(m.Dirty);
        m.Saved(m.Pending());
        m.Record(18001, "Test Carrier", "Tallodar Sector", 86, 36, 110, T0.AddSeconds(60)); // warped: same id, same size
        Assert.True(m.Dirty);
    }

    [Fact]
    public void LoadedFromStoreNeedsNoSave()
    {
        var m = new SizeMemory();
        m.Loaded(new SizeMemory.Entry { Id = 5, Name = "B", Dx = 1, Dy = 2, Dz = 3, SeenUtc = T0, Playfield = "X" });
        Assert.False(m.Dirty);
        Assert.Equal(3f, m.Get(5)!.Dz);
    }
}
