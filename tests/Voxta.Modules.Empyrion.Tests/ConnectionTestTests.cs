using System.Net;
using System.Text;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

/// <summary>The "Test connection" button on the module settings page.</summary>
public class ConnectionTestTests
{
    private const string Token = "secret-token";

    private const string StatusTwoPlayers =
        "{\"players\":[{\"name\":\"Player1\",\"steamId\":\"76561198000000001\",\"online\":true}," +
        "{\"name\":\"friend\",\"steamId\":\"76561198000000002\",\"online\":true}]," +
        "\"playfields\":[{\"name\":\"Akua\"}]}";

    private sealed class Bridge(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static BridgeClient Client(string token, string status = StatusTwoPlayers) =>
        new("http://bridge.test:30016", token, new HttpClient(new Bridge(req =>
            req.Headers.Authorization?.Parameter == Token
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(status, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized))));

    [Fact]
    public async Task Reports_players_and_playfields_when_connected()
    {
        var (ok, text) = await BridgeConnectionTest.RunAsync(Client(Token), "76561198000000001", CancellationToken.None);

        Assert.True(ok);
        Assert.Contains("2 player(s) online (Player1, friend)", text);
        Assert.Contains("1 playfield(s)", text);
        Assert.DoesNotContain("SteamID", text);
    }

    [Fact]
    public async Task Counts_only_online_players()
    {
        const string status =
            "{\"players\":[{\"name\":\"Player1\",\"steamId\":\"76561198000000001\",\"online\":false}]," +
            "\"playfields\":[]}";

        var (ok, text) = await BridgeConnectionTest.RunAsync(Client(Token, status), null, CancellationToken.None);

        Assert.True(ok);
        Assert.Contains("0 player(s) online", text);
        Assert.Contains("1 known player(s) offline", text);
    }

    [Fact]
    public async Task Wrong_token_is_reported_not_thrown()
    {
        var (ok, text) = await BridgeConnectionTest.RunAsync(Client("wrong"), null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("401", text);
    }

    [Fact]
    public async Task Warns_when_configured_player_is_not_online()
    {
        var (ok, text) = await BridgeConnectionTest.RunAsync(Client(Token), "76561198000000099", CancellationToken.None);

        Assert.True(ok);
        Assert.Contains("76561198000000099 is not online", text);
    }

    [Fact]
    public async Task Asks_for_steamid_when_several_players_and_none_set()
    {
        var (_, text) = await BridgeConnectionTest.RunAsync(Client(Token), "", CancellationToken.None);

        Assert.Contains("set the player's SteamID", text);
    }

    [Fact]
    public async Task Unreachable_bridge_is_reported_not_thrown()
    {
        var client = new BridgeClient("http://bridge.test:30016", Token,
            new HttpClient(new Bridge(_ => throw new HttpRequestException("Connection refused"))));

        var (ok, text) = await BridgeConnectionTest.RunAsync(client, null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("Connection refused", text);
    }
}
