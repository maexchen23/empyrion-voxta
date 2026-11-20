using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class GameChatTests
{
    [Fact]
    public void First_list_is_the_baseline_then_new_prefixed_messages_pass()
    {
        var f = new GameChatFilter("@ida");
        Assert.Empty(f.Take([(1, "Global", "@ida old message")]));
        var sent = f.Take([(1, "Global", "@ida old message"), (2, "Faction", "hi all"), (3, "Faction", "@IDA: how's the ship?")]).ToList();
        Assert.Equal(["how's the ship?"], sent);
        Assert.Empty(f.Take([(3, "Faction", "@IDA: how's the ship?")]));
    }

    [Theory]
    [InlineData("@ida hello", "hello")]
    [InlineData("@ida, hello", "hello")]
    [InlineData("@idaho is a state", null)]
    [InlineData("hello @ida", null)]
    [InlineData("@ida", "")]
    public void Prefix(string text, string? expected) => Assert.Equal(expected, new GameChatFilter("@ida").Strip(text));

    [Fact]
    public void Empty_prefix_passes_everything()
    {
        var f = new GameChatFilter("");
        f.Take([]);
        Assert.Equal(["what's around?"], f.Take([(5, "Global", " what's around? ")]));
    }

    [Fact]
    public void Restarted_bridge_starts_a_new_baseline()
    {
        var f = new GameChatFilter("");
        f.Take([(40, "Global", "a")]);
        Assert.Empty(f.Take([(1, "Global", "after restart")]));
        Assert.Equal(["next"], f.Take([(1, "Global", "after restart"), (2, "Global", "next")]));
    }
}
