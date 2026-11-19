using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

/// <summary>
/// Auth against a running VoxtaBridge mod. Opt-in: set <c>VOXTABRIDGE_URL</c> and <c>VOXTABRIDGE_TOKEN</c>
/// (or <c>VOXTABRIDGE_TOKEN_FILE</c>); without them these tests pass without doing anything.
/// </summary>
public class LiveBridgeAuthTests
{
    private static (string url, string token)? Live()
    {
        var url = Environment.GetEnvironmentVariable("VOXTABRIDGE_URL");
        var token = Environment.GetEnvironmentVariable("VOXTABRIDGE_TOKEN");
        var file = Environment.GetEnvironmentVariable("VOXTABRIDGE_TOKEN_FILE");
        if (string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(file) && File.Exists(file)) token = File.ReadAllText(file);
        return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token) ? null : (url, token);
    }

    [Fact]
    public async Task Live_bridge_accepts_configured_token()
    {
        if (Live() is not var (url, token)) return;

        using var doc = await new BridgeClient(url, token).GetStatusAsync(CancellationToken.None);

        Assert.True(doc.RootElement.TryGetProperty("players", out _));
    }

    [Fact]
    public async Task Live_bridge_rejects_wrong_token()
    {
        if (Live() is not var (url, _)) return;

        var exc = await Assert.ThrowsAsync<BridgeException>(
            () => new BridgeClient(url, "not-the-token").GetStatusAsync(CancellationToken.None));
        Assert.Contains("401", exc.Message);
    }
}
