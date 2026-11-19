using System.Net;
using System.Text;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

/// <summary>Bridge auth as seen from the Voxta module, against a fake bridge (no game needed).</summary>
public class BridgeAuthTests
{
    // a dummy 48-hex-digit token, built from parts so secret scanners do not take it for a real key
    private const string Hex16 = "0123456789abcdef";
    private const string Token = Hex16 + Hex16 + Hex16;

    /// <summary>Answers like the VoxtaBridge mod: 200 for the right bearer token, 401 otherwise.</summary>
    private sealed class FakeBridge(string expectedToken) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var auth = request.Headers.Authorization;
            var ok = auth is { Scheme: "Bearer" } && auth.Parameter == expectedToken;
            return Task.FromResult(ok
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"players\":[],\"playfields\":[]}", Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"missing or wrong bearer token\"}"),
                });
        }
    }

    private static (BridgeClient client, FakeBridge bridge) Create(string configuredToken)
    {
        var bridge = new FakeBridge(Token);
        return (new BridgeClient("http://bridge.test:30016/", configuredToken, new HttpClient(bridge)), bridge);
    }

    [Fact]
    public async Task Sends_bearer_token_to_status_endpoint()
    {
        var (client, bridge) = Create(Token);

        using var doc = await client.GetStatusAsync(CancellationToken.None);

        var req = Assert.Single(bridge.Requests);
        Assert.Equal("http://bridge.test:30016/status", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal(Token, req.Headers.Authorization.Parameter);
        Assert.True(doc.RootElement.TryGetProperty("players", out _));
    }

    [Theory]
    [InlineData(" " + Token + " ")]
    [InlineData(Token + "\n")]
    [InlineData(Token + "\r\n")]
    [InlineData("\"" + Token + "\"")]
    [InlineData("Token=" + Token)]
    [InlineData("Token = " + Token + "\n")]
    [InlineData("Bearer " + Token)]
    public async Task Accepts_token_pasted_with_extras(string pasted)
    {
        var (client, _) = Create(pasted);

        using var doc = await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData(Token + "x")]
    public async Task Wrong_token_gives_readable_401_error(string configured)
    {
        var (client, _) = Create(configured);

        var exc = await Assert.ThrowsAsync<BridgeException>(() => client.GetStatusAsync(CancellationToken.None));
        Assert.Contains("401", exc.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Token=")]
    public async Task Empty_token_fails_without_calling_the_bridge(string configured)
    {
        var (client, bridge) = Create(configured);

        var exc = await Assert.ThrowsAsync<BridgeException>(() => client.GetStatusAsync(CancellationToken.None));
        Assert.Contains("No bridge token", exc.Message);
        Assert.Empty(bridge.Requests);
    }
}
