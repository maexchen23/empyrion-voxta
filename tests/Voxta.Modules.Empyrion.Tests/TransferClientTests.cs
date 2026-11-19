using System.Net;
using System.Text;
using System.Text.Json;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

public class TransferClientTests
{
    private sealed class Bridge(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path, Form, Auth;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            Auth = request.Headers.Authorization?.ToString();
            Form = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Posts_form_with_player_and_optional_source()
    {
        var bridge = new Bridge(HttpStatusCode.OK, "{\"ok\":true}");
        var client = new BridgeClient("http://b:30016", "tok", new HttpClient(bridge));

        using var _ = await client.TransferAsync(new TransferRequest("45mm Ammo", "all", "ammo box 1", null, "765"), CancellationToken.None);

        Assert.Equal("/transfer", bridge.Path);
        Assert.Equal("Bearer tok", bridge.Auth);
        Assert.Equal("item=45mm+Ammo&amount=all&to=ammo+box+1&player=765", bridge.Form);
    }

    [Fact]
    public async Task Game_side_refusal_with_error_status_is_returned_not_thrown()
    {
        var bridge = new Bridge(HttpStatusCode.Conflict, "{\"ok\":false,\"error\":\"Nobody is online.\"}");
        var client = new BridgeClient("http://b:30016", "tok", new HttpClient(bridge));

        using var doc = await client.TransferAsync(new TransferRequest("x", "1", "y", "z", null), CancellationToken.None);

        Assert.Equal("Transfer not done: Nobody is online.", BridgeTransfer.Describe(doc.RootElement));
        Assert.Contains("from=z", bridge.Form);
    }

    [Fact]
    public async Task Unauthorized_throws()
    {
        var client = new BridgeClient("http://b:30016", "tok", new HttpClient(new Bridge(HttpStatusCode.Unauthorized, "")));
        await Assert.ThrowsAsync<BridgeException>(() => client.TransferAsync(new TransferRequest("x", "1", "y", null, null), CancellationToken.None));
    }

    [Fact]
    public void Describes_success_with_sources_and_note()
    {
        var r = JsonDocument.Parse("""
            {"ok":true,"item":"45mm Ammo","moved":600,"to":"ammo box 1",
             "from":[{"container":"Hold","count":300},{"container":"Spare","count":300}],"note":"only 600 available"}
            """).RootElement;

        Assert.Equal("Moved 600 45mm Ammo into 'ammo box 1' (from 'Hold' 300, 'Spare' 300). Note: only 600 available.",
            BridgeTransfer.Describe(r));
    }

    [Fact]
    public void Regression_sources_without_count_do_not_crash()
    {
        // 2026-11-17: to-player results had sources without "count" → KeyNotFoundException → the character
        // was told "may not have happened", retried, and the player got 200 instead of 100 ingots.
        var r = JsonDocument.Parse("""
            {"ok":true,"item":"Iron Ingot","moved":100,"to":"player inventory","from":[{"container":"A Production @ Test Carrier"}]}
            """).RootElement;

        Assert.Equal("Moved 100 Iron Ingot into 'player inventory' (from 'A Production @ Test Carrier').", BridgeTransfer.Describe(r));
    }

    [Fact]
    public void Lost_items_are_spelled_out()
    {
        var r = JsonDocument.Parse("""{"ok":true,"item":"Iron Ingot","moved":90,"lost":10,"to":"Hold","from":[]}""").RootElement;
        Assert.Contains("10 could not be placed anywhere and are LOST", BridgeTransfer.Describe(r));
    }
}
