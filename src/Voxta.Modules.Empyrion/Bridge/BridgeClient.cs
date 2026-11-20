using System.Net.Http.Headers;
using System.Text.Json;

namespace Voxta.Modules.Empyrion.Bridge;

/// <summary>HTTP client for the VoxtaBridge game mod.</summary>
public sealed class BridgeClient
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;

    public BridgeClient(string baseUrl, string token, HttpClient? http = null)
    {
        _http = http ?? SharedHttp;
        _baseUrl = baseUrl.Trim().TrimEnd('/');
        _token = NormalizeToken(token);
    }

    /// <summary>
    /// The token as pasted by a person: tolerate surrounding whitespace/newlines, quotes, and the whole
    /// <c>Token=...</c> line copied from <c>VoxtaBridge.cfg</c>.
    /// </summary>
    public static string NormalizeToken(string? token)
    {
        var t = (token ?? "").Trim().Trim('"', '\'').Trim();
        if (t.StartsWith("Token", StringComparison.OrdinalIgnoreCase))
        {
            var eq = t.IndexOf('=');
            if (eq > 0 && t[..eq].Trim().Equals("Token", StringComparison.OrdinalIgnoreCase))
                t = t[(eq + 1)..].Trim();
        }
        if (t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) t = t[7..].Trim();
        return t;
    }

    /// <summary>
    /// POST /transfer. Game-side refusals (wrong container, nothing to move, not owned …) come back as
    /// <c>{"ok":false,"error":…}</c>, also with 4xx/5xx codes; only auth and transport problems throw.
    /// </summary>
    public async Task<JsonDocument> TransferAsync(TransferRequest t, CancellationToken cancellationToken)
    {
        if (_token.Length == 0)
            throw new BridgeException("No bridge token is set. Enter it in the Empyrion module settings.");

        var form = new List<KeyValuePair<string, string>>
        {
            new("item", t.Item), new("amount", t.Amount), new("to", t.To),
        };
        if (!string.IsNullOrWhiteSpace(t.From)) form.Add(new("from", t.From));
        if (!string.IsNullOrWhiteSpace(t.SteamId)) form.Add(new("player", t.SteamId.Trim()));

        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/transfer") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var res = await _http.SendAsync(req, cancellationToken);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new BridgeException("The game server rejected the bridge token (401). Check the module settings.");
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new BridgeException($"The game server answered {(int)res.StatusCode} without a readable result.");
        }
    }

    /// <summary>POST /find: where is an item (player inventory + all containers in reach).</summary>
    public Task<JsonDocument> FindAsync(string item, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("item", item) };
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/find", form, cancellationToken);
    }

    /// <summary>POST /where-to-buy: traders selling an item, their POIs, and the sellers the player knows.</summary>
    public Task<JsonDocument> WhereToBuyAsync(string item, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("item", item) };
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/where-to-buy", form, cancellationToken);
    }

    /// <summary>POST /pois: the POIs on the player's playfield the player knows of (discovered or within 2 km), classified and grouped.</summary>
    public Task<JsonDocument> ScanPoisAsync(bool all, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("scope", all ? "all" : "nearby") };
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/pois", form, cancellationToken);
    }

    /// <summary>POST /active: set the player's active structure by name, or "auto".</summary>
    public Task<JsonDocument> SetActiveAsync(string name, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("name", name) };
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/active", form, cancellationToken);
    }

    /// <summary>POST /structure: one of the player's own ships or bases on the playfield, by name.</summary>
    public Task<JsonDocument> StructureInfoAsync(string name, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("name", name) };
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/structure", form, cancellationToken);
    }

    /// <summary>POST /system: star, planets and moons of the player's current system with their types.</summary>
    public Task<JsonDocument> SystemInfoAsync(string? system, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(system)) form.Add(new("system", system.Trim()));
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/system", form, cancellationToken);
    }

    /// <summary>POST /poi-terminal: the info terminal texts of a POI the player is at or has visited.</summary>
    public Task<JsonDocument> PoiTerminalAsync(string poi, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("poi", poi) };
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/poi-terminal", form, cancellationToken);
    }

    /// <summary>POST /quests: the player's trader missions (status open|unlocked|all, near playfield|system|LY, item).</summary>
    public Task<JsonDocument> QuestsAsync(string? status, string? near, string? item, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(status)) form.Add(new("status", status.Trim()));
        if (!string.IsNullOrWhiteSpace(near)) form.Add(new("near", near.Trim()));
        if (!string.IsNullOrWhiteSpace(item)) form.Add(new("item", item.Trim()));
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/quests", form, cancellationToken);
    }

    /// <summary>POST /quest-pursue: pursue one trader mission (its line goes into the live context), or "none".</summary>
    public Task<JsonDocument> PursueQuestAsync(string name, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("name", name) };
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/quest-pursue", form, cancellationToken);
    }

    /// <summary>POST /chat-say: a private message to the player in the game chat, under a sender name.</summary>
    public Task<JsonDocument> ChatSayAsync(string text, string from, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("text", text), new("from", from) };
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/chat-say", form, cancellationToken);
    }

    /// <summary>POST /poi-trade: what the traders at a POI sell and buy.</summary>
    public Task<JsonDocument> PoiTradeAsync(string poi, bool detail, bool all, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("poi", poi), new("detail", detail ? "1" : "0"), new("all", all ? "1" : "0") };
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/poi-trade", form, cancellationToken);
    }

    /// <summary>POST /marks action=permanent: a marker the player keeps (never touched by the mod again).</summary>
    public Task<JsonDocument> AddMapMarkerAsync(string star, string name, bool hud, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("action", "permanent"), new("star", star), new("name", name), new("hud", hud ? "1" : "0") };
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/marks", form, cancellationToken);
    }

    /// <summary>POST /marks: Voxta's star markers on the galaxy map (action add or clear).</summary>
    public Task<JsonDocument> MarksAsync(string action, string? stars, bool numbered, bool replace, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("action", action), new("numbered", numbered ? "1" : "0"), new("replace", replace ? "1" : "0") };
        if (!string.IsNullOrWhiteSpace(stars)) form.Add(new("stars", stars));
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/marks", form, cancellationToken);
    }

    /// <summary>POST /items: search the game's item list (no player needed).</summary>
    public Task<JsonDocument> SearchItemsAsync(string query, CancellationToken cancellationToken) =>
        PostFormAsync("/items", [new("query", query)], cancellationToken);

    /// <summary>POST /craft: plan (confirm=false) or craft from the VC container.</summary>
    public Task<JsonDocument> CraftAsync(string item, string amount, string vcSuffix, bool confirm, string? steamId, CancellationToken cancellationToken,
        string? structure = null)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("item", item), new("amount", amount), new("vc", vcSuffix), new("confirm", confirm ? "1" : "0"),
        };
        if (!string.IsNullOrWhiteSpace(structure)) form.Add(new("structure", structure.Trim()));
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/craft", form, cancellationToken);
    }

    /// <summary>POST /microjump: plan (confirm=false) or jump. distance in metres, null = aim; expect = target name from the plan; vj = jump container suffix.</summary>
    public Task<JsonDocument> MicroJumpAsync(string? distanceMeters, string? expect, string vjSuffix, bool confirm, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("confirm", confirm ? "1" : "0"), new("vj", vjSuffix) };
        if (!string.IsNullOrWhiteSpace(distanceMeters)) form.Add(new("distance", distanceMeters));
        if (!string.IsNullOrWhiteSpace(expect)) form.Add(new("expect", expect.Trim()));
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/microjump", form, cancellationToken);
    }

    /// <summary>POST /containers: containers of one structure in the player's transfer scope.</summary>
    public Task<JsonDocument> ContainersAsync(string structure, string? steamId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>> { new("structure", structure) };
        if (!string.IsNullOrWhiteSpace(steamId)) form.Add(new("player", steamId.Trim()));
        return PostFormAsync("/containers", form, cancellationToken);
    }

    private async Task<JsonDocument> PostFormAsync(string path, List<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        if (_token.Length == 0)
            throw new BridgeException("No bridge token is set. Enter it in the Empyrion module settings.");
        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var res = await _http.SendAsync(req, cancellationToken);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new BridgeException("The game server rejected the bridge token (401). Check the module settings.");
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new BridgeException($"The game server answered {(int)res.StatusCode} without a readable result."); }
    }

    public async Task<JsonDocument> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_token.Length == 0)
            throw new BridgeException("No bridge token is set. Enter it in the Empyrion module settings.");

        using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/status");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var res = await _http.SendAsync(req, cancellationToken);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new BridgeException("The game server rejected the bridge token (401). Check the module settings.");
        if (!res.IsSuccessStatusCode)
            throw new BridgeException($"The game server answered {(int)res.StatusCode}.");
        var body = await res.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }
}

public sealed record TransferRequest(string Item, string Amount, string To, string? From, string? SteamId);

public static class BridgeTransfer
{
    /// <summary>One line for the model: what moved, or why not. Never throws for game-side refusals.</summary>
    public static string Describe(JsonElement r)
    {
        static string? S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var ok = r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        if (!ok) return "Transfer not done: " + (S(r, "error") ?? "unknown error");

        var moved = r.TryGetProperty("moved", out var m) && m.TryGetInt32(out var mv) ? mv : 0;
        if (r.TryGetProperty("lost", out var l) && l.TryGetInt32(out var lost) && lost > 0)
            return $"Moved {moved} {S(r, "item")}, but {lost} could not be placed anywhere and are LOST. Tell the player.";
        var sb = new System.Text.StringBuilder($"Moved {moved} {S(r, "item")} into '{S(r, "to")}'");
        if (r.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Array && from.GetArrayLength() > 0)
            sb.Append(" (from " + string.Join(", ", from.EnumerateArray().Select(x =>
                x.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? $"'{S(x, "container")}' {n}" : $"'{S(x, "container")}'")) + ")");
        sb.Append('.');
        if (S(r, "note") is { } note) sb.Append($" Note: {note}.");
        return sb.ToString();
    }
}

public static class BridgeConnectionTest
{
    /// <summary>One line for the settings page: who is online, or what went wrong. Never throws.</summary>
    public static async Task<(bool ok, string text)> RunAsync(BridgeClient client, string? steamId, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await client.GetStatusAsync(cancellationToken);
            var root = doc.RootElement;
            // The game lists every player who ever joined; only those with online=true are connected.
            var known = root.TryGetProperty("players", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray()
                    .Select(x => (name: x.TryGetProperty("name", out var n) ? n.GetString() : null,
                                  steamId: x.TryGetProperty("steamId", out var s) ? s.GetString() : null,
                                  online: !x.TryGetProperty("online", out var o) || o.ValueKind != JsonValueKind.False))
                    .ToList()
                : [];
            var players = known.Where(x => x.online).ToList();
            var playfields = root.TryGetProperty("playfields", out var pf) && pf.ValueKind == JsonValueKind.Array
                ? pf.GetArrayLength()
                : 0;

            var text = $"Connected. {players.Count} player(s) online" +
                       (players.Count > 0 ? $" ({string.Join(", ", players.Select(x => x.name))})" : "") +
                       $", {playfields} playfield(s) with live data" +
                       (known.Count > players.Count ? $", {known.Count - players.Count} known player(s) offline." : ".");

            if (!string.IsNullOrWhiteSpace(steamId) && players.All(x => x.steamId != steamId.Trim()))
                text += $" The configured SteamID {steamId.Trim()} is not online right now.";
            else if (string.IsNullOrWhiteSpace(steamId) && players.Count > 1)
                text += " Several players are online: set the player's SteamID.";
            return (true, text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BridgeException exc)
        {
            return (false, exc.Message);
        }
        catch (Exception exc)
        {
            return (false, $"Could not reach the bridge: {exc.Message}");
        }
    }
}

public sealed class BridgeException(string message) : Exception(message);
