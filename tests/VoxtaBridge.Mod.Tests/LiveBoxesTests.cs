using VoxtaBridge;
using Xunit;

namespace VoxtaBridge.Mod.Tests;

public class LiveBoxesTests
{
    /// <summary>A box at world (1000, 100, 2000), block origin (0, 128, 0), optionally turned 90° around y.</summary>
    static LiveBox Box(bool own, string name, int half, bool turned = false, int id = 1) => new()
    {
        PlayerId = 7, Own = own, Id = id, Name = name, Type = own ? "CV" : "BA", Owner = own ? null : "Polaris",
        Px = 1000, Py = 100, Pz = 2000, Ox = 0, Oy = 128, Oz = 0,
        // turned: world +x is block +z, world +z is block −x
        M = turned ? [0, 0, -1, 0, 1, 0, 1, 0, 0] : [1, 0, 0, 0, 1, 0, 0, 0, 1],
        MinX = -half, MinY = 128 - 5, MinZ = -half, MaxX = half, MaxY = 128 + 5, MaxZ = half,
    };

    [Fact]
    public void Contains_follows_the_rotation_and_margin()
    {
        var long_ = Box(true, "Long", 0);
        long_.MinZ = -40; long_.MaxZ = 40; long_.MinX = -5; long_.MaxX = 5; // 81 long along block z
        Assert.True(long_.Contains(1000, 100, 2030, 0));   // unturned: along world z
        Assert.False(long_.Contains(1030, 100, 2000, 0));
        long_.M = [0, 0, -1, 0, 1, 0, 1, 0, 0];
        Assert.True(long_.Contains(1030, 100, 2000, 0));   // turned: block z is world x now
        Assert.False(long_.Contains(1000, 100, 2030, 0));
        Assert.False(long_.Contains(1000, 100, 2007, 0));  // 7 m beside the 5-wide hull
        Assert.True(long_.Contains(1000, 100, 2007, 3));   // within the 3-block margin
    }

    [Fact]
    public void Locate_picks_the_smallest_box_and_sorts_own_by_distance()
    {
        var poi = Box(false, "Big Station", 100, id: 1);
        var inner = Box(false, "Shop", 10, id: 2);
        var ship = Box(true, "Test Hauler", 20, id: 3);
        ship.Px = 1080; ship.Ox = 0; // ship centred 80 m east
        var (visiting, standing, own) = LiveBox.Locate([poi, inner, ship], 1002, 100, 2003);
        Assert.Equal("Shop", visiting!.Name);
        Assert.Null(standing);
        Assert.Equal(("Test Hauler", 78), (own[0].box.Name, (int)Math.Round(own[0].distance)));
        var (_, at, _) = LiveBox.Locate([poi, ship], 1062, 100, 2000);
        Assert.Equal("Test Hauler", at!.Name);
    }

    [Fact]
    public void Encode_and_parse_round_trip()
    {
        var b = Box(false, "P3: Planetside | Production", 12, turned: true);
        var c = LiveBox.Parse(b.Encode())!;
        Assert.Equal("P3: Planetside / Production", c.Name);
        Assert.Equal("Polaris", c.Owner);
        Assert.Equal(b.M, c.M);
        Assert.Equal((b.MinX, b.MaxY, b.MaxZ), (c.MinX, c.MaxY, c.MaxZ));
        Assert.True(c.Contains(1003, 100, 2003, 0));
        Assert.Null(LiveBox.Parse("garbage"));
    }
}
