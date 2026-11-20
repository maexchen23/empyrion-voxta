using VoxtaBridge;
using Xunit;

namespace VoxtaBridge.Mod.Tests;

public class ChatTextTests
{
    [Fact]
    public void Short_text_stays_one_message() => Assert.Equal(["Shield's up."], ChatText.Split("  Shield's up. ", 350));

    [Fact]
    public void Long_text_splits_at_sentences_within_the_limit()
    {
        var text = string.Join(" ", Enumerable.Range(1, 30).Select(i => $"Sentence number {i} is here."));
        var parts = ChatText.Split(text, 120);
        Assert.All(parts, p => Assert.True(p.Length <= 120));
        Assert.All(parts, p => Assert.EndsWith(".", p));
        Assert.Equal(text, string.Join(" ", parts));
    }

    [Fact]
    public void Lines_are_preferred_split_points()
    {
        var parts = ChatText.Split("Ores @ Test Hauler: 3586 silicon\nStone box: 7741 crushed stone\nGraphite: 267", 40);
        Assert.Equal(["Ores @ Test Hauler: 3586 silicon", "Stone box: 7741 crushed stone", "Graphite: 267"], parts);
    }
}
