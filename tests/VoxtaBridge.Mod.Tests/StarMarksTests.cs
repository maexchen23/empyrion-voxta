using VoxtaBridge;
using Xunit;

namespace VoxtaBridge.Mod.Tests;

public class StarMarksTests
{
    record struct S(string Name, double D);

    static readonly S[] Stars = [new("Elo Alpha", 18.9), new("Elo Beta", 30), new("Rubathra", 9.9), new("Isomnir", 11.3), new("Isomnir", 400), new(null, 1)];

    static S Find(string name, out List<string> sugg) => StarMarks.Find(Stars, s => s.Name, s => s.D, name, out sugg);

    [Fact]
    public void Names_and_suffix()
    {
        Assert.Equal("Elo Alpha (Voxta)", StarMarks.MarkerName("Elo Alpha", 0));
        Assert.Equal("2 Rubathra (Voxta)", StarMarks.MarkerName("Rubathra", 2));
        Assert.True(StarMarks.IsOurs("2 Rubathra (Voxta)"));
        Assert.False(StarMarks.IsOurs("Rubathra"));
        Assert.Equal(["Elo Alpha", "rubathra", "Isomnir"], StarMarks.SplitNames(" Elo Alpha, rubathra;Rubathra\n'Isomnir' ,, "));
    }

    [Fact]
    public void Exact_then_unique_prefix_then_suggestions()
    {
        Assert.Equal("Rubathra", Find("RUBATHRA", out _).Name);
        Assert.Equal(11.3, Find("isomnir", out _).D); // two with that name: the closer one
        Assert.Equal("Rubathra", Find("Ruba", out _).Name);
        Assert.Null(Find("Elo", out var sugg).Name); // two start with it
        Assert.Equal(["Elo Alpha", "Elo Beta"], sugg);
        Assert.Null(Find("Zzz", out var none).Name);
        Assert.Empty(none);
    }

    [Theory]
    [InlineData("  Water Outpost ", true)]
    [InlineData("", false)]
    [InlineData("Base (Voxta)", false)]
    [InlineData("base (VOXTA)", false)]
    [InlineData("done: 1 Rubathra", false)]
    [InlineData("A name that is definitely much longer than forty characters", false)]
    public void Permanent_names(string raw, bool ok)
    {
        Assert.Equal(ok, StarMarks.ValidPermanentName(raw, out var name, out var error));
        if (ok) Assert.Equal("Water Outpost", name); else Assert.NotNull(error);
    }
}
