using System.Net;
using System.Text;
using Voxta.Abstractions.Encryption;
using Voxta.Modules.Empyrion.Bridge;
using Xunit;

namespace Voxta.Modules.Empyrion.Tests;

/// <summary>
/// Voxta stores password fields encrypted and gives modules the encrypted value
/// (seen live: a 48-char token arrived at the bridge as 109 chars → 401).
/// </summary>
public class TokenEncryptionTests
{
    // a dummy 48-hex-digit token, built from parts so secret scanners do not take it for a real key
    private const string Hex16 = "0123456789abcdef";
    private const string Token = Hex16 + Hex16 + Hex16;

    /// <summary>Stands in for Voxta's encryption: a prefix + base64, like a real envelope only longer.</summary>
    private sealed class FakeEncryption : ILocalEncryptionProvider
    {
        private const string Prefix = "enc:v1:";

        public string Encrypt(string value) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        public string Decrypt(string value) =>
            value.StartsWith(Prefix)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(value[Prefix.Length..]))
                : throw new FormatException("not encrypted");
    }

    private sealed class FakeBridge : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Headers.Authorization?.Parameter == Token
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"players\":[],\"playfields\":[]}") }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    private static readonly FakeEncryption Encryption = new();

    [Fact]
    public void Decrypts_stored_token()
    {
        Assert.Equal(Token, BridgeSecrets.RevealToken(Encryption, Encryption.Encrypt(Token)));
    }

    [Fact]
    public void Plain_token_that_does_not_decrypt_is_used_as_is()
    {
        Assert.Equal(Token, BridgeSecrets.RevealToken(Encryption, Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_token_is_empty(string? stored)
    {
        Assert.Equal("", BridgeSecrets.RevealToken(Encryption, stored));
    }

    [Fact]
    public async Task Encrypted_token_sent_as_is_is_rejected()
    {
        var client = new BridgeClient("http://bridge.test:30016", Encryption.Encrypt(Token), new HttpClient(new FakeBridge()));

        var (ok, text) = await BridgeConnectionTest.RunAsync(client, null, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("401", text);
    }

    [Fact]
    public async Task Decrypted_token_is_accepted()
    {
        var token = BridgeSecrets.RevealToken(Encryption, Encryption.Encrypt(Token));
        var client = new BridgeClient("http://bridge.test:30016", token, new HttpClient(new FakeBridge()));

        var (ok, text) = await BridgeConnectionTest.RunAsync(client, null, CancellationToken.None);

        Assert.True(ok, text);
    }
}
