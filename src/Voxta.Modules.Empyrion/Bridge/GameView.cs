using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Voxta.Modules.Empyrion.Bridge;

/// <summary>
/// Turns the bridge's <c>/status</c> JSON into short texts for the model.
/// The player is picked by SteamID64, or the only player online when none is configured.
/// </summary>
public sealed class GameView
{
    private readonly JsonElement _root;
    private readonly JsonElement? _api1Player;
    private readonly JsonElement? _pfPlayer;
    private readonly JsonElement? _snapshot;

    public string PlayerName { get; }
    public string? SteamId { get; private init; }

    /// <summary>A mission's "needs" array.</summary>
    public static List<MissionNeed> Needs(JsonElement m) =>
        Array(m, "needs").Select(n => new MissionNeed(Str(n, "item") ?? "", Str(n, "name") ?? Str(n, "item") ?? "", (int)(Long(n, "count") ?? 0),
            Long(n, "tokenId") is { } t ? (int)t : null)).Where(n => n.Item.Length > 0).ToList();

    /// <summary>How many of a mission's item the player carries (bag + toolbar; tokens by token id).</summary>
    private int Carried(MissionNeed need)
    {
        if (_api1Player is not { } a) return 0;
        return Array(a, "toolbar").Concat(Array(a, "bag"))
            .Where(i => need.TokenId is { } tid ? Long(i, "tokenId") == tid : string.Equals(Str(i, "name"), need.Item, StringComparison.OrdinalIgnoreCase))
            .Sum(i => (int)(Long(i, "count") ?? 0));
    }

    /// <summary>The bridge's fresh on-foot location (dedi position + structure boxes), or null.</summary>
    private JsonElement? Live =>
        SteamId != null && _root.TryGetProperty("live", out var all) && all.ValueKind == JsonValueKind.Object &&
        all.TryGetProperty(SteamId, out var l) && l.ValueKind == JsonValueKind.Object ? l : null;

    private JsonElement? Pursuing =>
        SteamId != null && _root.TryGetProperty("pursuing", out var all) && all.ValueKind == JsonValueKind.Object &&
        all.TryGetProperty(SteamId, out var p) && p.ValueKind == JsonValueKind.Object ? p : null;
    public string Playfield { get; }

    private GameView(JsonElement root, string name, string playfield, JsonElement? api1, JsonElement? pfPlayer, JsonElement? snapshot)
    {
        _root = root;
        PlayerName = name;
        Playfield = playfield;
        _api1Player = api1;
        _pfPlayer = pfPlayer;
        _snapshot = snapshot;
    }

    /// <summary>Returns null and an explanation when no single player can be picked.</summary>
    public static GameView? Create(JsonElement root, string? steamId, out string? error)
    {
        error = null;
        var players = Array(root, "players").Where(p => Bool(p, "online") != false).ToList();
        JsonElement player;
        if (!string.IsNullOrWhiteSpace(steamId))
        {
            var match = players.Where(p => Str(p, "steamId") == steamId.Trim()).ToList();
            if (match.Count == 0)
            {
                error = $"The player with SteamID {steamId} is not online on the game server.";
                return null;
            }
            player = match[0];
        }
        else if (players.Count == 1)
        {
            player = players[0];
        }
        else
        {
            error = players.Count == 0
                ? "Nobody is online on the game server."
                : $"Several players are online ({string.Join(", ", players.Select(p => Str(p, "name")))}); " +
                  "set the player's SteamID in the Empyrion module settings.";
            return null;
        }

        var entityId = Long(player, "entityId");
        var name = Str(player, "name") ?? "?";
        var playfield = Str(player, "playfield") ?? "?";

        JsonElement? api1 = null;
        if (root.TryGetProperty("api1", out var api1Root))
            foreach (var p in Array(api1Root, "players"))
                if (Long(p, "entityId") == entityId) api1 = p;

        JsonElement? pfPlayer = null, snapshot = null;
        foreach (var pf in Array(root, "playfields"))
        {
            if (!pf.TryGetProperty("snapshot", out var snap) || snap.ValueKind != JsonValueKind.Object) continue;
            foreach (var p in Array(snap, "players"))
                if (Long(p, "id") == entityId) { pfPlayer = p; snapshot = snap; }
        }

        return new GameView(root, name, playfield, api1, pfPlayer, snapshot) { SteamId = Str(player, "steamId") };
    }

    private JsonElement? Structure =>
        _pfPlayer is { } p && p.TryGetProperty("structure", out var s) && s.ValueKind == JsonValueKind.Object ? s : null;

    public string Location()
    {
        var sb = new StringBuilder();
        if (_snapshot is { } snap)
        {
            sb.Append($"Playfield: {Str(snap, "playfield")} ({Str(snap, "type")}");
            var planetType = Str(snap, "planetType");
            if (!string.IsNullOrEmpty(planetType)) sb.Append($", {planetType}");
            sb.AppendLine($"), solar system {Str(snap, "system")}.");
            sb.AppendLine(Bool(snap, "pvp") == true ? "PvP: yes (other players can attack)." : "PvP: no.");
        }
        else
        {
            sb.AppendLine($"Playfield: {Playfield} (no live data from that playfield yet).");
        }

        if (PlayerOnActive && Structure is { } s && Vec(s, "worldPos") is { } shipPos)
            sb.AppendLine($"Position (of the {Str(s, "type")} '{Str(s, "name")}' the player is in): {shipPos}.");
        else if (_api1Player is { } a && Vec(a, "pos") is { } pos)
            sb.AppendLine($"Player position: {pos}.");

        if (_snapshot is { } sn && sn.TryGetProperty("entityTypes", out var types) && types.ValueKind == JsonValueKind.Object)
        {
            var parts = types.EnumerateObject()
                .Where(t => t.Name != "Proxy")
                .Select(t => $"{t.Value.GetRawText()} {t.Name}");
            sb.AppendLine($"Entities on this playfield: {string.Join(", ", parts)}.");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>"P3: Planetside Production (Polaris base)" when the player is inside a structure that isn't theirs.</summary>
    private string? Visiting()
    {
        if (_pfPlayer is not { } pf || !pf.TryGetProperty("visiting", out var v) || v.ValueKind != JsonValueKind.Object) return null;
        var what = Str(v, "type") == "BA" ? "base" : Str(v, "type");
        var owner = Str(v, "owner");
        return $"{Str(v, "name")} ({(owner != null ? owner + " " : "")}{what})";
    }

    public string PlayerStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Player: {PlayerName}, on playfield {Playfield}.");
        var src = _api1Player ?? _pfPlayer;
        if (src is { } p)
        {
            sb.AppendLine($"Health {Num(p, "health")}, oxygen {Num(p, "oxygen")}, food {Num(p, "food")}.");
            if (_api1Player is { } a)
                sb.AppendLine($"Credits {Num(a, "credits")}. Inventory: {Num(a, "bagStacks")} stacks in the bag, {Num(a, "toolbarStacks")} on the toolbar (item names: {EmpyrionConstants.GetPlayerInventoryTool}).");
        }
        if (_pfPlayer is { } pf)
        {
            if (Structure is { } act && !PlayerOnActive)
            {
                var where = Visiting() is { } v2 ? $"On foot inside '{v2}' (not the player's)." : ActiveField("on", "name") is { } on2 ? $"Aboard '{on2}'." : "On foot (not in a ship or base).";
                sb.AppendLine($"{where} Active structure (containers, transfers, crafting): {Str(act, "type")} '{Str(act, "name")}' ({ActiveField(null, "how")}, {Num(Active!.Value, "distance")} m away).");
            }
            else if (Structure is not { } s)
                sb.AppendLine(Visiting() is { } v ? $"On foot inside '{v}' (not the player's)." : "On foot (not in a ship or base).");
            else sb.AppendLine(Bool(pf, "isPilot") == true
                ? $"Piloting the {Str(s, "type")} '{Str(s, "name")}'."
                : ActiveField(null, "how") == "standing"
                    ? $"On foot at the {Str(s, "type")} '{Str(s, "name")}': outside, on its hull or next to it (not inside)."
                    : $"Inside the {Str(s, "type")} '{Str(s, "name")}', not piloting.");
        }
        return sb.ToString().TrimEnd();
    }

    public string CurrentShip()
    {
        if (_pfPlayer == null)
            return "No live data from the player's playfield yet; try again in a few seconds.";
        if (Structure is not { } s)
            return Visiting() is { } v
                ? $"The player is not in a ship or base of their own; they are inside '{v}', which isn't theirs (its cargo and tanks are not shown)."
                : "The player is not in a ship or base right now.";

        var sb = new StringBuilder();
        if (!PlayerOnActive)
        {
            var where = Visiting() is { } v ? $"on foot inside '{v}'" : ActiveField("on", "name") is { } on ? $"aboard '{on}'" : "on foot";
            sb.AppendLine($"The player is {where}, not aboard this structure; it is their active structure (containers, transfers, crafting work from it):");
        }
        RenderStructureCore(sb, s);
        RenderReach(sb, s);

        var containers = Array(s, "containers").ToList();
        if (containers.Count == 0) sb.AppendLine("No containers.");
        else RenderContainers(sb, containers);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Type, name, size, pilot, power, shield and tanks of a structure.</summary>
    private static void RenderStructureCore(StringBuilder sb, JsonElement s)
    {
        sb.AppendLine($"{Str(s, "type")} '{Str(s, "name")}' (size class {Num(s, "sizeClass")}, {Num(s, "blocks")} blocks, mass {Num(s, "mass")} kg).");
        sb.AppendLine($"Pilot: {Str(s, "pilot") ?? "nobody"}. Power: {(Bool(s, "powered") == true ? "on" : "off")}. " +
                      $"Shield: {(Bool(s, "shieldActive") == true ? $"active, {Num(s, "shield")}" : "inactive")}.");
        foreach (var tank in new[] { "fuel", "oxygen", "pentaxid" })
        {
            if (!s.TryGetProperty(tank, out var t) || t.ValueKind != JsonValueKind.Object) continue;
            var pct = Double(t, "capacity") is { } cap and > 0 && Double(t, "content") is { } c ? $"{Math.Clamp(c / cap * 100, 0, 100):0}% (" : "";
            sb.AppendLine($"{char.ToUpperInvariant(tank[0])}{tank[1..]} tank: {pct}{Math.Round(Double(t, "content") ?? 0):0} / {Math.Round(Double(t, "capacity") ?? 0):0}{(pct.Length > 0 ? ")" : "")}.");
        }
    }

    /// <summary>Answer of POST /poi-terminal: the terminal's pages, with their options, as the game shows them.</summary>
    public static string PoiTerminalResult(JsonElement r)
    {
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            return Str(r, "error") ?? "Unknown error.";
        var sb = new StringBuilder($"Info terminal '{Str(r, "terminal") ?? "terminal"}' at {Str(r, "poi")} ({Str(r, "playfield")}). " +
                                   "What its screens say (the player can click through them in the game):");
        var n = 0;
        foreach (var p in Array(r, "pages"))
        {
            n++;
            sb.Append($"\n\n[screen {n}]\n{Str(p, "text")}");
            var opts = Array(p, "options").Select(o => o.GetString()).Where(o => !string.IsNullOrEmpty(o)).ToList();
            if (opts.Count > 0) sb.Append($"\n(options: {string.Join(" | ", opts)})");
        }
        return sb.ToString();
    }

    /// <summary>Answer of POST /quests.</summary>
    public static string QuestsResult(JsonElement r)
    {
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            return Str(r, "error") ?? "Unknown error.";
        var quests = Array(r, "quests").ToList();
        var filter = new List<string>();
        if (Str(r, "near") is { } near) filter.Add(double.TryParse(near, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? $"within {near} LY" : $"in this {near}");
        if (Str(r, "item") is { } item) filter.Add($"wanting '{item}'");
        var which = Str(r, "status") switch { "unlocked" => "unlocked factories", "all" => "trader missions", _ => "open trader missions" };
        if (quests.Count == 0)
            return Long(r, "tracked") is 0 or null
                ? "No trader missions yet: they start when the player talks to a factory's trader (the chief), who explains what they need."
                : $"No {which}{(filter.Count > 0 ? " " + string.Join(", ", filter) : "")}. ({Num(r, "tracked")} traders talked to in total.)";
        var sb = new StringBuilder($"{char.ToUpperInvariant(which[0])}{which[1..]}{(filter.Count > 0 ? " " + string.Join(", ", filter) : "")}, closest first:");
        foreach (var q in quests)
        {
            var dist = Double(q, "ly") is { } ly ? (ly < 0.05 ? "this sector" : $"{ly.ToString("0.#", CultureInfo.InvariantCulture)} LY") : "distance unknown";
            var head = $"\n- {Str(q, "factory")} ({Str(q, "playfield")}, {dist}{(Str(q, "faction") is { } f ? $", {f}" : "")})";
            if (Bool(q, "pursued") == true) head += " [pursued]";
            sb.Append(head);
            if (Str(q, "wants") is { } wants)
            {
                sb.Append($": wants {wants}");
                sb.Append(Bool(q, "followUp") == true ? " (optional follow-up; trades already)" : Bool(q, "tradesUnlocked") == false ? " to unlock its trades" : "");
                if (Str(q, "reward") is { } reward) sb.Append($". Reward: {reward}");
                if (Str(q, "workOrder") is { } wo) sb.Append($". Or a work order: {wo}");
                sb.Append('.');
            }
            else sb.Append(Str(q, "status") switch
            {
                "locked" => ": trades locked; talking to the trader gives the mission.",
                "unlocked" => Bool(q, "moreMissions") == true ? ": trades unlocked; more missions on offer (\"Offer assistance\")." : ": trades unlocked.",
                _ => ": no mission running.",
            });
        }
        if (Long(r, "more") is > 0) sb.Append($"\n(and {Num(r, "more")} more; filter by near or item)");
        return sb.ToString();
    }

    /// <summary>Answer of POST /quest-pursue.</summary>
    public static string PursueQuestResult(JsonElement r)
    {
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            return Str(r, "error") ?? "Unknown error.";
        return Str(r, "line") is { } line
            ? $"Now pursuing: {line}. It stays in the live game state until it's done or cleared."
            : "No mission pursued any more.";
    }

    /// <summary>Answer of POST /active (set or auto).</summary>
    public static string SetActiveResult(JsonElement r)
    {
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            return Str(r, "error") ?? "Unknown error.";
        var mode = Str(r, "mode") == "auto" ? "Back to automatic (aboard, else last aboard, else nearest own structure in range)." : "Active structure set.";
        if (!r.TryGetProperty("active", out var a) || a.ValueKind != JsonValueKind.Object) return mode;
        var fake = JsonDocument.Parse($"{{\"workingFrom\":{a.GetRawText()}}}").RootElement;
        return $"{mode} {WorkingFrom(fake)}";
    }

    /// <summary>Answer of POST /structure: one own ship or base, like the current ship (without container contents).</summary>
    public static string StructureInfoResult(JsonElement r)
    {
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            return Str(r, "error") ?? "Unknown error.";
        if (!r.TryGetProperty("structure", out var s) || s.ValueKind != JsonValueKind.Object) return "No structure data.";
        var sb = new StringBuilder();
        RenderStructureCore(sb, s);
        sb.AppendLine($"Distance from the player: {Distance(Double(s, "distance") ?? 0)}.");
        if (Str(s, "dockedTo") is { } carrier) sb.AppendLine($"Docked to '{carrier}'.");
        var docked = Array(s, "docked").Select(d => $"'{Str(d, "name")}' ({Str(d, "type")})").ToList();
        if (docked.Count > 0) sb.AppendLine($"Docked to it: {string.Join(", ", docked)}.");
        sb.AppendLine($"{Num(s, "containerCount")} containers (contents: {EmpyrionConstants.GetCurrentShipTool} with structure '{Str(s, "name")}', within transfer range of the player).");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// "Structures in reach" as a tree: the docking group (carrier → docked ships; Empyrion has no chain docking),
    /// then own structures in transfer range (bases never have anything docked).
    /// </summary>
    private static void RenderReach(StringBuilder sb, JsonElement s)
    {
        var group = Array(s, "group").ToList();
        var inRange = Array(s, "inRange").ToList();
        if (group.Count == 0 && inRange.Count == 0) return;

        sb.AppendLine("Structures in reach (container labels end with '@ <structure>'):");
        string Count(JsonElement e) => Num(e, "containers") == "1" ? "1 container" : $"{Num(e, "containers")} containers";
        string Aboard(JsonElement e) => Bool(e, "aboard") == true ? "  <- the player is aboard" : "";
        foreach (var m in group)
        {
            var indent = Str(m, "role") == "docked" ? "  " : "";
            sb.AppendLine($"{indent}- '{Str(m, "name")}' ({Str(m, "type")}, {Str(m, "role")}, {Count(m)}){Aboard(m)}");
        }
        foreach (var r in inRange)
            sb.AppendLine($"- '{Str(r, "name")}' ({Str(r, "type")}, {Num(r, "distance")} m away, {Count(r)}; " +
                          "contents via this tool with structure=<name>)");
    }

    /// <summary>More matching item types than this: totals only.</summary>
    public const int MaxFindItems = 5;

    /// <summary>Locations listed per item; the rest is summarised.</summary>
    public const int MaxFindLocations = 15;

    /// <summary>Answer of POST /find, as text for the model.</summary>
    public static string FindResult(JsonElement result)
    {
        if (!(result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            return Str(result, "error") ?? "Unknown error.";
        var items = Array(result, "items").ToList();
        var query = Str(result, "query");
        if (items.Count == 0)
            return $"No '{query}' anywhere in reach (player inventory, the ship and its docked ships, own structures in range).";

        var sb = new StringBuilder();
        if (items.Count > MaxFindItems)
        {
            sb.AppendLine($"'{query}' matches {items.Count} different items; totals only (search more precisely for locations):");
            foreach (var it in items) sb.AppendLine($"- {Str(it, "name")}: {Num(it, "total")}");
            return sb.ToString().TrimEnd();
        }

        foreach (var it in items)
        {
            sb.AppendLine($"{Str(it, "name")}: {Num(it, "total")} in total");
            var locations = Array(it, "locations").ToList();
            foreach (var l in locations.Take(MaxFindLocations))
            {
                var line = $"- '{Str(l, "where")}': {Num(l, "count")}";
                if (l.TryGetProperty("distance", out var d) && d.ValueKind == JsonValueKind.Number) line += $" ({Num(l, "distance")} m away)";
                if (Bool(l, "open") == true) line += " [open in the game]";
                var states = Array(l, "states").ToList();
                if (states.Count > 0)
                    line += " (" + string.Join("; ", states.Select(st =>
                    {
                        var parts = new List<string>();
                        if (Long(st, "ammo") is > 0) parts.Add($"{Num(st, "ammo")} rounds loaded");
                        if (Long(st, "decay") is > 0) parts.Add($"wear {Num(st, "decay")}");
                        return (Long(st, "count") is > 1 ? $"{Num(st, "count")}x " : "") + string.Join(", ", parts);
                    })) + ")";
                sb.AppendLine(line);
            }
            if (locations.Count > MaxFindLocations)
            {
                var rest = locations.Skip(MaxFindLocations).ToList();
                sb.AppendLine($"- … and {rest.Count} more containers with {rest.Sum(l => Long(l, "count") ?? 0)} in total");
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>"5 Iron Ingot → 10 Steel Plates per craft", or null.</summary>
    private static string? RecipeLine(JsonElement e, string? name)
    {
        if (!e.TryGetProperty("recipe", out var r) || r.ValueKind != JsonValueKind.Object) return null;
        var inputs = string.Join(" + ", Array(r, "inputs").Select(x => $"{Num(x, "count")} {Str(x, "name")}"));
        return $"{inputs} → {Num(r, "makes")} {name} per craft";
    }

    /// <summary>Answer of POST /items, as text for the model.</summary>
    public static string ItemsResult(JsonElement r)
    {
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            return Str(r, "error") ?? "Unknown error.";
        var query = Str(r, "query");
        if (Bool(r, "namesOnly") == true)
        {
            var count = Long(r, "count") ?? 0;
            var names = Array(r, "names").Select(x => x.GetString()).ToList();
            var more = count > names.Count ? $" (and {count - names.Count} more)" : "";
            return $"'{query}' matches {count} items, too many for details. Names: {string.Join(", ", names)}{more}. " +
                   "Search again with a more precise name to get crafting and ammo details.";
        }
        var items = Array(r, "items").ToList();
        if (items.Count == 0) return $"No item in the game matches '{query}'. Try another word or part of the name.";
        var sb = new StringBuilder();
        foreach (var it in items)
        {
            string List(string key) => string.Join(", ", Array(it, key).Select(x => x.GetString()));
            sb.Append($"- {Str(it, "name")}: ");
            sb.Append(Bool(it, "craftable") == true ? $"craftable ({List("constructors")})" : "not craftable");
            if (RecipeLine(it, Str(it, "name")) is { } recipe) sb.Append($"; recipe: {recipe}");
            if (List("ammo") is { Length: > 0 } ammo) sb.Append($"; ammo: {ammo}");
            if (List("usedBy") is { Length: > 0 } users)
            {
                var more = Long(it, "moreUsers") is > 0 ? $" and {Num(it, "moreUsers")} more" : "";
                sb.Append($"; used by: {users}{more}");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Answer of POST /pois as short lines for the model: one line per POI or complex, closest first.</summary>
    public static string PoiScanResult(JsonElement r)
    {
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            return Str(r, "error") ?? "Unknown error.";
        var all = Str(r, "scope") == "all";
        var sb = new System.Text.StringBuilder();
        var groups = Array(r, "groups").ToList();
        var where = Str(r, "playfield");
        if (groups.Count == 0)
            sb.Append($"No known POIs {(all ? "on" : "within 10 km on")} {where}.");
        else
        {
            sb.Append($"Known POIs {(all ? "on" : "within 10 km on")} {where} (as of {Str(r, "asOf")}), closest first:");
            foreach (var g in groups)
            {
                var parts = new List<string>();
                var cls = Str(g, "class");
                var buildings = Long(g, "buildings");
                parts.Add(buildings is > 1 ? $"{cls} complex of {buildings} buildings ({Str(g, "members")})" : cls ?? "unknown");
                if (Str(g, "faction") is { } fac) parts.Add(Str(g, "standing") is { } st ? $"{fac}, {st}" : fac);
                if (Long(g, "turrets") is { } t and > 0) parts.Add($"{t} turrets");
                if (Long(g, "traders") is { } tr and > 0) parts.Add($"{tr} trader{(tr == 1 ? "" : "s")}");
                var places = Array(g, "tradingPlaces").Select(t =>
                    $"{Str(t, "name")} ({Long(t, "traders")} trader{(Long(t, "traders") == 1 ? "" : "s")}, {Distance(Double(t, "distance") ?? 0)})").ToList();
                if (places.Count > 0) parts.Add("trading places: " + string.Join("; ", places));
                if (Str(g, "terminal") is { } term)
                    parts.Add(Str(g, "terminalPoi") is { } tp && tp != Str(g, "name") ? $"info terminal '{term}' at {tp}" : $"info terminal '{term}'");
                if (Bool(g, "onlyInRange") == true) parts.Add("not discovered yet, only seen");
                parts.Add(Distance(Double(g, "distance") ?? 0));
                sb.Append($"\n- {Str(g, "name")}: {string.Join(", ", parts)}");
            }
        }
        if (Long(r, "moreGroups") is { } more and > 0)
        {
            var civ = Long(r, "moreCivilian") ?? 0;
            sb.Append(civ == more ? $"\n{more} more civilian buildings not listed." : civ > 0 ? $"\n{more} more not listed ({civ} of them civilian)." : $"\n{more} more further away.");
        }
        if (Long(r, "resources") is { } res and > 0) sb.Append($"\nAlso {res} asteroids/resource deposits.");
        var own = OwnStructures(r, Distance);
        if (own != null) sb.Append($"\nThe player's own ships and bases within 2 km: {own} (details: {EmpyrionConstants.GetStructureTool}).");
        if (!all) sb.Append("\nscope 'all' lists the whole playfield.");
        return sb.ToString();
    }

    /// <summary>
    /// Answer of POST /system: one known system's bodies with types ("visited" = someone of the player's side entered it),
    /// or the list of known systems (system=known).
    /// </summary>
    public static string SystemInfoResult(JsonElement r)
    {
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            return Str(r, "error") ?? "Unknown error.";
        if (r.TryGetProperty("systems", out _))
        {
            var systems = Array(r, "systems").Select(s =>
                $"{Str(s, "system")} ({Str(s, "starClass")}, {(Double(s, "ly") is { } d && d < 0.05 ? "here" : $"{Num(s, "ly")} LY")}, " +
                $"{Num(s, "planets")} planets, {Num(s, "moons")} moons{(Bool(s, "byPlayer") == true ? "" : ", known through the faction")})").ToList();
            var total = Long(r, "knownSystems") ?? systems.Count;
            return $"Systems known to the player or their faction ({total}), closest first: {string.Join("; ", systems)}" +
                   (total > systems.Count ? $"; and {total - systems.Count} more." : ".");
        }
        var bodies = Array(r, "bodies").ToList();
        string Line(JsonElement b) =>
            $"{Str(b, "name")} ({Str(b, "type")}{(Str(b, "difficulty") is { } d ? $", {d}" : "")}){(Bool(b, "here") == true ? " - the player is here" : Bool(b, "entered") == true ? ", visited" : "")}";
        var away = Double(r, "ly") is { } ly && ly >= 0.05 ? $", {Num(r, "ly")} LY away" : "";
        var via = Bool(r, "byPlayer") == false ? ", known through the faction" : "";
        var sb = new System.Text.StringBuilder($"System {Str(r, "system")}, star class {Str(r, "starClass")}{away}{via}.");
        foreach (var (kind, label) in new[] { ("planet", "Planets"), ("gas giant", "Gas giants"), ("moon", "Moons") })
        {
            var list = bodies.Where(b => Str(b, "kind") == kind).Select(Line).ToList();
            if (list.Count > 0) sb.Append($"\n{label}: {string.Join(", ", list)}.");
        }
        if (r.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Object)
        {
            sb.Append($"\n\n{Str(det, "name")} ({Str(det, "type")} {Str(det, "kind")}");
            if (Str(det, "difficulty") is { } diff) sb.Append($", difficulty {diff}");
            sb.Append("):");
            if (Str(det, "description") is { Length: > 0 } desc) sb.Append($"\n{desc}");
            foreach (var e in Array(det, "effects").Select(x => x.GetString())) sb.Append($"\n{e}");
            var facts = new List<string>();
            if (Double(det, "gravityG") is { } g) facts.Add($"gravity {g:0.##} g");
            if (Double(det, "atmosphere") is { } a) facts.Add(a < 0.05 ? "no atmosphere" : $"atmosphere {a:0.##}");
            if (Double(det, "oxygenPercent") is { } o) facts.Add($"oxygen {o:0} %");
            if (Bool(det, "breathable") is { } br) facts.Add(br ? "breathable" : "not breathable");
            if (Double(det, "tempMin") is { } tmin && Double(det, "tempMax") is { } tmax) facts.Add($"temperature {tmin:0} to {tmax:0} °C");
            if (Double(det, "tempNight") is { } tn) facts.Add($"night {tn:0} °C");
            if (Double(det, "radiation") is { } rad) facts.Add($"radiation {rad:0.#}");
            if (facts.Count > 0) sb.Append($"\n{string.Join(", ", facts)}.");
            var res = Array(det, "resources").Select(x => x.GetString()).ToList();
            if (res.Count > 0) sb.Append($"\nResources: {string.Join(", ", res)}.");
        }
        return sb.ToString();
    }

    private static readonly string[] Tiers = ["below market", "about market", "above market", "far above market"];

    /// <summary>
    /// Answer of POST /poi-trade. Without detail the item names are grouped by price tier against the market price
    /// (an LLM handles "cheap / fair / expensive" better than factors); with detail each item has tier and credits.
    /// </summary>
    public static string PoiTradeResult(JsonElement r)
    {
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            return Str(r, "error") ?? "Unknown error.";
        var where = Str(r, "playfield") is { } pf ? $" on {pf}" : "";
        var traderNames = string.Join(", ", Array(r, "traders").Select(x => x.GetString()));
        var sb = new System.Text.StringBuilder(Bool(r, "general") == true
            ? $"No {Str(r, "poi")} is known to the player; in general, one has the traders {traderNames} and usually trades:"
            : $"{Str(r, "poi")}{where}, traders: {traderNames}.");
        void Add(string key, bool selling)
        {
            var label = selling ? "Sells" : "Buys";
            var n = Long(r, key + "Count") ?? 0;
            var everyday = Long(r, key + "Everyday") ?? 0;
            var items = Array(r, key).ToList();
            var daily = everyday > 0 ? $" Plus {everyday} everyday item{(everyday == 1 ? "" : "s")} (food, cheap components, building blocks), not listed." : "";
            if (n == 0) { sb.Append($"\n{label}: {(everyday > 0 ? "only everyday items." : "nothing.")}{daily}"); return; }
            var more = n > items.Count ? $"; and {n - items.Count} more" : "";
            var detail = items.Any(x => Str(x, "credits") != null);
            if (detail)
            {
                var list = items.Select(x =>
                {
                    var bits = new List<string>();
                    if (Str(x, "tier") is { } t) bits.Add(t);
                    if (Str(x, "credits") is { } c) bits.Add($"{c} credits");
                    return bits.Count > 0 ? $"{Str(x, "item")} ({string.Join(", ", bits)})" : Str(x, "item");
                });
                sb.Append($"\n{label} ({n}): {string.Join(", ", list)}{more}.{daily}");
                return;
            }
            var groups = items.GroupBy(x => Str(x, "tier") ?? "")
                .OrderBy(g => g.Key.Length == 0 ? 99 : System.Array.IndexOf(Tiers, g.Key))
                .Select(g => $"{(g.Key.Length == 0 ? "no market price" : (selling ? "" : "pays ") + g.Key)}: {string.Join(", ", g.Select(x => Str(x, "item")))}");
            sb.Append($"\n{label} ({n}) - {string.Join("; ", groups)}{more}.{daily}");
        }
        Add("sells", true);
        Add("buys", false);
        if (Bool(r, "detailWithheld") == true)
            sb.Append("\nExact prices are only known once the player has been there.");
        sb.Append("\nPrices are against the item's normal market price; what is in stock varies.");
        return sb.ToString();
    }

    /// <summary>
    /// One context line with the closest notable places from POST /pois (trading, faction bases and outposts, dungeons;
    /// not civilian or salvage), at most 3, distances rounded so the context doesn't change on every step. Null if none.
    /// </summary>
    public static string? NearbyLine(JsonElement r)
    {
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)) return null;
        var notable = new[] { "trading", "major faction base", "small faction outpost", "abandoned dungeon" };
        var parts = new List<string>();
        foreach (var g in Array(r, "groups").Where(g => notable.Contains(Str(g, "class"))).Take(3))
        {
            var cls = Str(g, "class")!.Replace("faction ", "").Replace("small ", "");
            var bits = new List<string> { Str(g, "faction") is { } f ? $"{f} {cls}" : cls };
            if (Str(g, "standing") is { } st && st != "Neutral") bits.Add(st);
            if (Long(g, "turrets") is { } t and >= 5) bits.Add($"{t} turrets");
            var places = Array(g, "tradingPlaces").Select(x => $"{Str(x, "name")} {Rough(Double(x, "distance") ?? 0)}").ToList();
            if (places.Count > 1) bits.Add("traders at " + string.Join(", ", places));
            else bits.Add(Rough(Double(g, "distance") ?? 0));
            parts.Add($"{Str(g, "name")} ({string.Join(", ", bits)})");
        }
        // own ships: a real distance ("70 m"), never "here" (that reads as "aboard")
        var own = OwnStructures(r, m => m < 1000 ? $"{Math.Max(10, Math.Round(m / 10) * 10):0} m" : Rough(m));
        if (parts.Count == 0 && own == null) return null;
        var line = parts.Count > 0 ? "Nearby: " + string.Join("; ", parts) + "." : "";
        if (own != null) line += (line.Length > 0 ? " " : "") + $"Own ships/bases nearby: {own}.";
        return line;
    }

    /// <summary>"'Hammer' (SV, 120 m); 'Ark' (CV, 1.4 km, 2 docked)" from yourStructures, or null.</summary>
    private static string? OwnStructures(JsonElement r, Func<double, string> distance)
    {
        var list = Array(r, "yourStructures").Select(s =>
        {
            var docked = Long(s, "docked") is { } d and > 0 ? $", {d} docked" : "";
            return $"'{Str(s, "name")}' ({Str(s, "type")}, {distance(Double(s, "distance") ?? 0)}{docked})";
        }).ToList();
        return list.Count == 0 ? null : string.Join("; ", list);
    }

    /// <summary>Distance rounded for the live context: 100 m steps below 1 km, 0.5 km steps above.</summary>
    private static string Rough(double m) =>
        m < 150 ? "here" : m < 1000 ? $"{Math.Round(m / 100) * 100:0} m" : $"{Math.Round(m / 500) * 0.5:0.#} km";

    private static string Distance(double m) => m < 1000 ? $"{Math.Round(m / 10) * 10:0} m" : $"{m / 1000:0.#} km";

    /// <summary>
    /// Answer of POST /where-to-buy, as text for the model. The bridge's "hidden" list (places of factions below Neutral)
    /// is left out on purpose: the character should not offer them; it stays in the JSON for other tools.
    /// </summary>
    public static string WhereToBuyResult(JsonElement r)
    {
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            return Str(r, "error") ?? "Unknown error.";
        var query = Str(r, "query");
        if (Bool(r, "namesOnly") == true)
        {
            var names = Array(r, "names").Select(x => x.GetString()).ToList();
            return $"'{query}' matches {Num(r, "count")} items that traders sell: {string.Join(", ", names)}. Ask again with the exact name.";
        }
        var items = Array(r, "items").ToList();
        if (items.Count == 0)
        {
            var matched = Str(r, "matchedItem");
            if (matched == null) return $"No trader sells anything called '{query}'.";
            var buyers = Long(r, "boughtBy") ?? 0;
            return $"No NPC trader sells {matched}." + (buyers > 0 ? $" {buyers} trader(s) buy it, though." : "");
        }
        var sb = new StringBuilder();
        if (r.TryGetProperty("player", out var pl) && Str(pl, "standings") == "starting values")
            sb.AppendLine("(Faction standings: the player's current standings could not be read, starting standings used.)");
        foreach (var it in items)
        {
            var name = Str(it, "name");
            sb.AppendLine($"{name}:");
            var known = Array(it, "known").ToList();
            if (known.Count > 0)
            {
                sb.AppendLine("  Sellers the player already knows (closest first):");
                foreach (var k in known)
                {
                    var dist = Double(k, "ly") is { } ly ? (ly < 0.05 ? "in this sector" : $"{ly.ToString("0.#", CultureInfo.InvariantCulture)} LY away") : "distance unknown";
                    var fac = Str(k, "faction") is { } f ? $", {f}{(Str(k, "tier") is { } t ? $" ({t})" : "")}" : "";
                    var via = Str(k, "via") == "traded" ? ", traded there before" : ", visited";
                    var locked = Str(k, "tradesLocked") is { } lk ? $"; trades locked for the player, {lk}" : Bool(k, "tradesUnlocked") == true ? "; trades unlocked" : "";
                    sb.AppendLine($"  - {Str(k, "trader")} at {Str(k, "poi")} in {Str(k, "playfield")}: {dist}{fac}{via}{locked}");
                }
                if (Long(it, "moreKnown") is > 0) sb.AppendLine($"  - and {Num(it, "moreKnown")} more known");
            }
            var sellers = Array(it, "sellers").ToList();
            if (sellers.Count > 0)
            {
                sb.AppendLine("  Traders that sell it and where they are found:");
                foreach (var s in sellers)
                {
                    var pois = Array(s, "pois").Select(p =>
                    {
                        var where = string.Join(", ", Array(p, "where").Select(x => x.GetString()));
                        if (Long(p, "moreWhere") is > 0) where += $" and {Num(p, "moreWhere")} more";
                        var fac = Str(p, "faction") is { } f ? $" ({f}{(Str(p, "tier") is { } t ? $", {t}" : "")})" : "";
                        var gate = Bool(p, "tradeMission") == true ? " [trades only after a mission for its trader]" : "";
                        return $"{Str(p, "name")}{fac}{gate} on/in: {where}";
                    });
                    var more = Long(s, "morePois") is > 0 ? $"; {Num(s, "morePois")} more POI types" : "";
                    var buys = Bool(s, "alsoBuys") == true ? ", also buys it" : "";
                    // no stock range: the real stock isn't known, amounts only mislead
                    sb.AppendLine($"  - {Str(s, "trader")}{(buys.Length > 0 ? $" ({buys.TrimStart(',', ' ')})" : "")}: {string.Join("; ", pois)}{more}");
                }
                if (Long(it, "moreSellers") is > 0) sb.AppendLine($"  - and {Num(it, "moreSellers")} more traders");
            }
            else if (known.Count == 0)
                sb.AppendLine("  No trader the player can use sells it anywhere in this galaxy.");
            string Likely(long c) => c >= 80 ? "very likely" : c >= 50 ? "likely" : "maybe";
            var planets = Array(it, "knownPlanets").ToList();
            if (planets.Count > 0)
            {
                var list = string.Join("; ", planets.Select(p =>
                {
                    var how = Bool(p, "entered") == true ? "visited" : Bool(p, "byPlayer") == true ? "seen on the map" : "known through the faction";
                    var ly = Double(p, "ly") is { } d && d < 0.05 ? "this system" : $"{Num(p, "ly")} LY away";
                    var pois = string.Join(", ", Array(p, "pois").Select(x => x.GetString()));
                    var type = Str(p, "type") ?? "";
                    var kind = Str(p, "kind") ?? "";
                    var what = type.Contains(kind, StringComparison.OrdinalIgnoreCase) ? type : $"{type} {kind}".Trim();
                    return $"{Str(p, "name")} ({what}, system {Str(p, "system")}, {ly}, {how}): {pois}, {Likely(Long(p, "chance") ?? 0)}";
                }));
                var more = Long(it, "moreKnownPlanets") is > 0 ? $" ({Num(it, "moreKnownPlanets")} more)" : "";
                sb.AppendLine("  Known planets whose type can have such a trader (from the planet type only, not confirmed: nobody has seen " +
                              $"one there): {list}{more}");
            }
            var stars = Array(it, "stars").ToList();
            if (stars.Count > 0)
            {
                var list = string.Join("; ", stars.Select(st =>
                    $"{Str(st, "star")} ({Str(st, "class")} star, {Str(st, "territory")}, {Num(st, "ly")} LY away): {Likely(Long(st, "chance") ?? 0)}"));
                sb.AppendLine("  Closest unexplored stars where such a trader is likely, judged only from the star class and territory on the galaxy map " +
                              $"(not confirmed, nobody has looked there yet): {list}");
            }
            if (Long(it, "sellersNotPlaced") is > 0)
                sb.AppendLine($"  ({Num(it, "sellersNotPlaced")} more trader(s) sell it but their stations don't appear in this galaxy.)");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Answer of POST /marks, as text for the model.</summary>
    public static string MarksResult(JsonElement r)
    {
        string Unknown() => string.Join("; ", Array(r, "unknown").Select(u =>
        {
            var dym = Array(u, "didYouMean").Select(x => x.GetString()).ToList();
            return $"'{Str(u, "name")}'" + (dym.Count > 0 ? $" (did you mean {string.Join(", ", dym)}?)" : "");
        }));
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
        {
            var err = Str(r, "error") ?? "Unknown error.";
            var unknown = Unknown();
            return unknown.Length > 0 ? $"{err} Not found: {unknown}." : err;
        }
        if (Bool(r, "permanent") == true)
        {
            var cls = Str(r, "class") is { } c0 ? $"{c0} star" : "star";
            var ly = Double(r, "ly") is { } d0 ? $", {d0.ToString("0.#", CultureInfo.InvariantCulture)} LY away" : "";
            return $"Added the marker '{Str(r, "marker")}' on {Str(r, "star")} ({cls}{ly}). It stays until the player removes it in the game. " +
                   (Bool(r, "hud") == false ? "It shows on the galaxy map only, not on the HUD."
                                            : "It shows on the galaxy map now and on the HUD after the player's next login.");
        }
        if (Str(r, "action") == "clear")
        {
            var n = Array(r, "marks").Count();
            return n == 0 ? "There are no Voxta markers on the galaxy map." :
                $"Voxta's {n} marker(s) will be removed when the player logs off (removing them while the player is in the game can break the game's marker dialog).";
        }
        var marked = Array(r, "marked").Select(m =>
        {
            var cls = Str(m, "class") is { } c ? $"{c} star" : "star";
            var ly = Double(m, "ly") is { } d ? $", {d.ToString("0.#", CultureInfo.InvariantCulture)} LY away" : "";
            return $"{Str(m, "marker")} ({cls}{ly})";
        }).ToList();
        var sb = new StringBuilder();
        sb.Append($"Marked on the galaxy map: {string.Join("; ", marked)}.");
        if (Long(r, "unusedUntilLogoff") is > 0) sb.Append($" {Num(r, "unusedUntilLogoff")} earlier marker(s) are now named 'unused (Voxta)' and go away when the player logs off.");
        sb.Append(" The markers show on the galaxy map now; the HUD shows the new ones after the player's next login and until then still the previous Voxta markers. " +
                  "A reached system's marker is renamed 'done: …' and removed when the player logs off.");
        var unk = Unknown();
        if (unk.Length > 0) sb.Append($" Not found: {unk}.");
        return sb.ToString();
    }

    /// <summary>Answer of POST /craft, as text for the model.</summary>
    public static string CraftResult(JsonElement r)
    {
        string Items(string key) => string.Join(", ", Array(r, key).Select(x => $"{Num(x, "count")} {Str(x, "name")}"));
        var sb = new StringBuilder();
        var ok = r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        var container = Str(r, "container");
        var missing = Items("missing");
        if (!ok)
        {
            sb.AppendLine(Str(r, "error") ?? "Crafting failed.");
            if (missing.Length > 0) sb.AppendLine($"Missing: {missing}.");
            if (RecipeLine(r, Str(r, "item")) is { } recipeLine) sb.AppendLine($"Recipe: {recipeLine}.");
            sb.AppendLine("Nothing was crafted.");
            return sb.ToString().TrimEnd();
        }
        var minutes = Long(r, "craftSeconds") is long s && s > 0 ? $" (a real constructor would need about {Math.Max(1, (s + 30) / 60)} min)" : "";
        if (Bool(r, "crafted") == true)
        {
            sb.AppendLine($"Crafted {Num(r, "amount")} {Str(r, "item")} in '{container}'{minutes}.");
            sb.AppendLine($"Used: {Items("uses")}.");
            sb.AppendLine($"Now in '{container}': {Items("produces")} (results and leftovers).");
            return sb.ToString().TrimEnd();
        }
        sb.AppendLine($"Plan for {Num(r, "amount")} {Str(r, "item")} from '{container}'{minutes}:");
        sb.AppendLine($"- uses: {Items("uses")}");
        sb.AppendLine($"- puts back into the container: {Items("produces")}");
        if (RecipeLine(r, Str(r, "item")) is { } recipe) sb.AppendLine($"- recipe: {recipe}");
        var steps = Items("steps");
        if (steps.Length > 0) sb.AppendLine($"- steps: {steps}");
        if (Bool(r, "open") == true) sb.AppendLine($"Note: '{container}' is open in the game right now; it must be closed to craft.");
        sb.AppendLine("Nothing changed yet. Tell the player and craft only after they agree.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Answer of POST /microjump (plan or jump).</summary>
    public static string MicroJumpResult(JsonElement r)
    {
        var ok = r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        if (!ok) return Str(r, "error") ?? "The micro jump failed. Nothing happened.";
        string Km(long? m) => m is not { } x ? "?" : x >= 1000 ? $"{x / 1000.0:0.#} km" : $"{x} m";
        var target = Str(r, "target") ?? "?";
        var kind = Str(r, "targetKind");
        var where = kind == "distance"
            ? $"{Km(Long(r, "jump"))} straight ahead"
            : $"to {target} ({KindLabel(kind)}, {Km(Long(r, "targetDistance"))} away), stopping {Km(Long(r, "stopAt"))} from its centre";
        var sb = new StringBuilder();
        if (Bool(r, "jumped") == true)
        {
            sb.AppendLine($"Jumped {Str(r, "ship")} {(kind == "distance" ? where : $"{Km(Long(r, "jump"))} {where}")}.");
            sb.AppendLine($"Used {Num(r, "cost")} Refined Pentaxid from {Str(r, "container")} ({Num(r, "pentaxidAfter")} left).");
            return sb.ToString().TrimEnd();
        }
        sb.AppendLine($"Micro jump plan for {Str(r, "ship")}: {Km(Long(r, "jump"))} " + (kind == "distance" ? "straight ahead." : where + "."));
        sb.AppendLine($"Cost: {Num(r, "cost")} Refined Pentaxid from {Str(r, "container")} (has {Num(r, "pentaxid")}). Range up to {Km(Long(r, "maxRange"))}.");
        if (Long(r, "cooldownS") is long cd && cd > 0) sb.AppendLine($"The drive is cooling down: ready in {cd} s.");
        sb.AppendLine(kind == "distance"
            ? "Nothing changed yet. Tell the player and jump only after they agree."
            : $"Nothing changed yet. Tell the player and jump only after they agree; pass target='{target}' so the jump stops if the ship turned away.");
        return sb.ToString().TrimEnd();
    }

    private static string KindLabel(string? kind) => kind switch
    {
        "entity" => "object",
        "waypoint" => "HUD marker",
        "planet" => "planet",
        "moon" => "moon",
        _ => kind ?? "target",
    };

    /// <summary>Answer of POST /containers: one structure's containers.</summary>
    public static string StructureContainers(JsonElement result)
    {
        if (!(result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            return Str(result, "error") ?? "Unknown error.";
        var sb = new StringBuilder();
        sb.AppendLine($"'{Str(result, "structure")}':");
        var containers = Array(result, "containers").ToList();
        if (containers.Count == 0) sb.AppendLine("No containers.");
        else RenderContainers(sb, containers);
        return sb.ToString().TrimEnd();
    }

    private static void RenderContainers(StringBuilder sb, List<JsonElement> containers)
    {
        sb.AppendLine("Containers (label, kind: contents; use the label for transfers):");
        var totals = new Dictionary<string, long>();
        var empty = new List<string>();
        foreach (var c in containers)
        {
            var label = Str(c, "label") is { Length: > 0 } l ? $"'{l}'"
                : Str(c, "name") is { Length: > 0 } n ? $"'{n}'" : "(unnamed)";
            var items = Array(c, "items").ToList();
            var open = Bool(c, "open") == true;
            if (open) label += " [open in the game: contents may change, can't be used for transfers until closed]";
            if (items.Count == 0 && !open)
            {
                empty.Add(label);
                continue;
            }
            var content = items.Count == 0 ? "empty" : string.Join(", ", items.Select(i => $"{ItemName(i)} x{Num(i, "count")}"));
            sb.AppendLine($"- {label} ({KindName(Str(c, "type"))}): {content}");
            foreach (var i in items)
            {
                var key = ItemName(i);
                totals[key] = totals.GetValueOrDefault(key) + (Long(i, "count") ?? 0);
            }
        }
        // Big ships have dozens of empty boxes: one line, not one each (keeps the context small).
        if (empty.Count > 0)
            sb.AppendLine($"- empty ({empty.Count}): {string.Join(", ", empty)}");
        if (containers.Count > 1 && totals.Count > 0)
            sb.AppendLine("Totals: " + string.Join(", ", totals.OrderBy(t => t.Key).Select(t => $"{t.Key} x{t.Value}")));
    }

    /// <summary>More matching item types than this: names only, no descriptions.</summary>
    public const int MaxDescribedItems = 3;

    /// <summary>Total characters of descriptions in one inventory answer.</summary>
    public const int MaxDescriptionChars = 4000;

    private static string Cut(string text, int max)
    {
        if (max <= 0) return "…";
        var cut = text.LastIndexOf(". ", Math.Min(max, text.Length - 1), StringComparison.Ordinal);
        return (cut > max / 2 ? text[..(cut + 1)] : text[..max].TrimEnd()) + " (cut short)";
    }

    public string PlayerInventory(string? filter)
    {
        if (_api1Player is not { } a)
            return "No inventory data for the player yet; try again in a few seconds.";

        var f = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        bool Match(JsonElement i) => f == null || ItemMatches(i, f);

        var toolbar = Array(a, "toolbar").Where(Match).ToList();
        var bag = Array(a, "bag").Where(Match).ToList();
        var sb = new StringBuilder();
        if (f != null && toolbar.Count == 0 && bag.Count == 0)
            return $"The player carries nothing matching '{f}'.";

        sb.AppendLine(toolbar.Count == 0
            ? "Toolbar: empty" + (f != null ? " (for this filter)." : ".")
            : "Toolbar: " + string.Join(", ", toolbar.Select(i => $"{ItemName(i)} x{Num(i, "count")}")));
        sb.AppendLine(bag.Count == 0
            ? "Bag: empty" + (f != null ? " (for this filter)." : ".")
            : "Bag: " + string.Join(", ", bag.Select(i => $"{ItemName(i)} x{Num(i, "count")}")));

        var totals = toolbar.Concat(bag)
            .GroupBy(ItemName)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} x{g.Sum(i => Long(i, "count") ?? 0)}")
            .ToList();
        if (totals.Count > 0) sb.AppendLine("Totals for items in several stacks: " + string.Join(", ", totals));

        // Descriptions only for a narrow, filtered look-up: a tool result stays in the chat history, so a broad
        // filter ("a") with dozens of long descriptions would push older context out.
        if (f != null)
        {
            var described = toolbar.Concat(bag)
                .Where(i => Str(i, "desc") != null)
                .GroupBy(ItemName)
                .Select(g => (name: g.Key, desc: Str(g.First(), "desc")!))
                .ToList();
            if (described.Count > MaxDescribedItems)
            {
                sb.AppendLine($"({described.Count} different items match, too many for descriptions; " +
                              "ask about one specific item to hear what it is.)");
            }
            else if (described.Count > 0)
            {
                sb.AppendLine("What they are:");
                var budget = MaxDescriptionChars;
                foreach (var (name, desc) in described)
                {
                    var text = desc.Length <= budget ? desc : Cut(desc, budget);
                    sb.AppendLine($"- {name}: {text}");
                    budget -= text.Length;
                    if (budget <= 0) break;
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The numbers events are detected from; compared between polls.</summary>
    /// <summary>The snapshot's "active" object (the structure the item features work from), or null.</summary>
    private JsonElement? Active =>
        _pfPlayer is { } pf && pf.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.Object ? a : null;

    /// <summary>The player is on the active structure (or there is no "active" info: older bridge).</summary>
    private bool PlayerOnActive => Active is not { } a || Bool(a, "playerOn") != false;

    private string? ActiveField(string? sub, string prop)
    {
        if (Active is not { } a) return null;
        if (sub == null) return Str(a, prop);
        return a.TryGetProperty(sub, out var o) && o.ValueKind == JsonValueKind.Object ? Str(o, prop) : null;
    }

    /// <summary>"Working from: CV 'Test Hauler' (last aboard, 80 m away)" for a playfield answer's workingFrom, or null.</summary>
    public static string? WorkingFrom(JsonElement r)
    {
        if (!r.TryGetProperty("workingFrom", out var w) || w.ValueKind != JsonValueKind.Object) return null;
        var note = Str(w, "note") is { } n ? $" Note: {n}." : "";
        if (Str(w, "name") is not { } name) return $"Working from: no own ship or base (none aboard or within transfer range).{note}";
        var how = Str(w, "how") switch
        {
            "pilot" => "piloting it", "aboard" => "aboard", "standing" => "standing on it", "pinned" => "set by the player",
            "last aboard" => "last aboard", "nearest" => "nearest own", var h => h,
        };
        var d = Double(w, "distance") ?? 0;
        var away = Bool(w, "playerOn") == true || d < 1 ? "" : $", {(d < 1000 ? $"{Math.Max(10, Math.Round(d / 10) * 10):0} m" : $"{d / 1000:0.#} km")} away";
        return $"Working from: {Str(w, "type")} '{name}' ({how}{away}).{note}";
    }

    public GameState State()
    {
        var a = _api1Player;
        var s = Structure;
        double? Get(JsonElement? e, string p) =>
            e is { } x && x.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        JsonElement? Tank(string name) =>
            s is { } st && st.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Object ? t : null;

        // Where the player is. Seats and "inside an own ship" come from the playfield server (instant there); on foot,
        // its position lags up to a minute, so the bridge's "live" answer (fresh dedi position + structure boxes) wins.
        var how = ActiveField(null, "how");
        var seated = (PlayerOnActive && s != null && how != "standing") || ActiveField("on", "name") != null;
        string? shipName = PlayerOnActive ? (s is { } s1 ? Str(s1, "name") : null) : ActiveField("on", "name");
        string? shipType = PlayerOnActive ? (s is { } s2 ? Str(s2, "type") : null) : ActiveField("on", "type");
        var standing = PlayerOnActive && s != null && how == "standing";
        JsonElement? visiting = _pfPlayer is { } vp && vp.TryGetProperty("visiting", out var vis) && vis.ValueKind == JsonValueKind.Object ? vis : null;
        string? activeName = !PlayerOnActive && s is { } s5 ? Str(s5, "name") : null;
        string? activeType = !PlayerOnActive && s is { } s6 ? Str(s6, "type") : null;
        string? activeHow = !PlayerOnActive && s != null ? how : null;
        double? activeDistance = !PlayerOnActive && s != null && Active is { } ad ? Double(ad, "distance") : null;
        if (!seated && Live is { } live)
        {
            visiting = live.TryGetProperty("visiting", out var lv) && lv.ValueKind == JsonValueKind.Object ? lv : null;
            var at = live.TryGetProperty("standing", out var ls) && ls.ValueKind == JsonValueKind.Object ? ls : (JsonElement?)null;
            standing = at != null;
            shipName = at is { } a1 ? Str(a1, "name") : null;
            shipType = at is { } a2 ? Str(a2, "type") : null;
            // the active structure: the playfield server's choice, shown unless the player is at it
            if (s is { } act && Str(act, "name") is { } actName && actName != shipName)
            {
                activeName = actName;
                activeType = Str(act, "type");
                activeHow = how == "standing" ? "nearest" : how;
                activeDistance = Array(live, "own").Where(o => Str(o, "name") == actName).Select(o => Double(o, "distance")).FirstOrDefault()
                                 ?? (Active is { } ad2 ? Double(ad2, "distance") : null);
            }
            else { activeName = null; activeType = null; activeHow = null; activeDistance = null; }
        }

        return new GameState
        {
            Player = PlayerName,
            Playfield = _snapshot is { } sn ? Str(sn, "playfield") ?? Playfield : Playfield,
            PlayfieldType = _snapshot is { } sn2 ? Str(sn2, "type") : null,
            System = _snapshot is { } sn3 ? Str(sn3, "system") : null,
            Pvp = _snapshot is { } sn4 ? Bool(sn4, "pvp") : null,
            Health = Get(a, "health"), HealthMax = Get(a, "healthMax"),
            Oxygen = Get(a, "oxygen"), OxygenMax = Get(a, "oxygenMax"),
            Food = Get(a, "food"), FoodMax = Get(a, "foodMax"),
            Radiation = Get(a, "radiation"), RadiationMax = Get(a, "radiationMax"),
            // physically aboard: the active structure when the player is on it, else the own structure they stand on
            ShipName = shipName,
            ShipType = shipType,
            Standing = standing,
            ActiveName = activeName,
            ActiveType = activeType,
            ActiveHow = activeHow,
            ActiveDistance = activeDistance,
            ActiveNote = ActiveField(null, "note"),
            IsPilot = _pfPlayer is { } pf && Bool(pf, "isPilot") == true,
            ShipFuel = Get(Tank("fuel"), "content"), ShipFuelMax = Get(Tank("fuel"), "capacity"),
            ShipOxygen = Get(Tank("oxygen"), "content"), ShipOxygenMax = Get(Tank("oxygen"), "capacity"),
            ShipPentaxid = Get(Tank("pentaxid"), "content"), ShipPentaxidMax = Get(Tank("pentaxid"), "capacity"),
            ShieldActive = s is { } s3 ? Bool(s3, "shieldActive") : null,
            Powered = s is { } s4 ? Bool(s4, "powered") : null,
            VisitingName = visiting is { } v1 ? Str(v1, "name") : null,
            VisitingType = visiting is { } v2 ? Str(v2, "type") : null,
            VisitingOwner = visiting is { } v3 ? Str(v3, "owner") : null,
            PursuingLine = Pursuing is { } pq ? Str(pq, "line") : null,
            PursuingStatus = Pursuing is { } pq2 ? Str(pq2, "status") : null,
            OpenMissions = SteamId != null && _root.TryGetProperty("missions", out var ms) && ms.ValueKind == JsonValueKind.Object &&
                           ms.TryGetProperty(SteamId, out var mine) && mine.ValueKind == JsonValueKind.Array
                ? mine.EnumerateArray().Select(m => new OpenMission(Str(m, "key") ?? "", Str(m, "kind") ?? "trader", Str(m, "line") ?? "",
                    Str(m, "where"), Bool(m, "followUp") == true, Bool(m, "pursued") == true, Needs(m))).ToList()
                : null,
            PursuingProgress = Pursuing is { } pq3 && Needs(pq3) is { Count: > 0 } pn ? pn.Select(n => (n, Carried(n))).ToList() : null,
            FactoryBlueprint = a is { } f1 && f1.TryGetProperty("factory", out var fac) ? Str(fac, "blueprint") : null,
            FactoryRemainingS = a is { } f2 && f2.TryGetProperty("factory", out var fac2) ? Get(fac2, "remainingS") : null,
        };
    }

    private static string KindName(string? type) => type switch
    {
        "AmmoCntr" => "ammo box",
        "Container" => "cargo box",
        "Fridge" => "fridge",
        "HarvestCntr" => "cargo/harvest box",
        _ => type ?? "container",
    };

    /// <summary>The game's display name (from its own files, any scenario), else the internal name.</summary>
    private static string ItemName(JsonElement item) =>
        Str(item, "display") ?? Str(item, "name") ?? $"item #{Num(item, "id")}";

    private static bool ItemMatches(JsonElement item, string filter)
    {
        static string Squash(string s) => s.Replace(" ", "").Replace("-", "");
        foreach (var candidate in new[] { Str(item, "display"), Str(item, "name") })
        {
            if (candidate == null) continue;
            if (candidate.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
            if (Squash(candidate).Contains(Squash(filter), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
            : [];

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static long? Long(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static double? Double(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string Num(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number) return "?";
        var d = v.GetDouble();
        return Math.Abs(d - Math.Round(d)) < 0.05
            ? Math.Round(d).ToString("0", CultureInfo.InvariantCulture)
            : d.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string? Vec(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        return string.Join(", ", v.EnumerateArray().Select(x => x.GetDouble().ToString("0", CultureInfo.InvariantCulture)));
    }
}
