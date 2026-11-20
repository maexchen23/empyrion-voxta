using System.Text;

namespace Voxta.Modules.Empyrion.Bridge;

/// <summary>One condition of a watch: field == value (case-insensitive).</summary>
public sealed record WatchCondition(string Field, string Value);

/// <summary>
/// A watch the character set ("tell me when …"): all conditions must equal the event's snapshot. The note says why the
/// character wants to know and comes back with the notification.
/// </summary>
public sealed record Watch(int Id, IReadOnlyList<WatchCondition> Conditions, string Note, DateTimeOffset Created)
{
    /// <summary>False: a quiet note only (no reply). The character's own watches are always announced.</summary>
    public bool Announce { get; init; } = true;
    /// <summary>A predefined game event from the settings: never listed, negative id.</summary>
    public bool Hidden { get; init; }

    public string ConditionText => string.Join("; ", Conditions.Select(c => $"{c.Field}={c.Value}"));
}

/// <summary>
/// The fixed vocabulary of watch events and snapshot fields, parsing and matching. Every event carries the same snapshot,
/// so the model sees all the state it needs; no stats (it can ask the tools).
/// </summary>
public static class Watches
{
    /// <summary>Default of the module setting "Maximum Watches".</summary>
    public const int DefaultMaxWatches = 20;
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    public static readonly string[] Events =
        ["seat_taken", "seat_left", "entered", "left", "docked", "undocked", "landed", "lifted_off", "signal", "device",
            "new_structure", "starter_placed"];

    /// <summary>Snapshot fields; null = free text (names), else the allowed values.</summary>
    public static readonly IReadOnlyDictionary<string, string[]?> Fields = new Dictionary<string, string[]?>
    {
        ["event"] = Events,
        ["structure"] = null,
        ["seat"] = ["pilot", "aboard", "on_foot"],
        ["where"] = ["space", "planet"],
        ["playfield"] = null,
        ["landed"] = ["true", "false"],
        ["docked"] = ["true", "false"],
        ["docked_to"] = null,
        ["other_structure"] = null,
        ["name"] = null,
        ["state"] = ["on", "off"],
        ["type"] = ["ba", "cv", "sv", "hv"],
    };

    /// <summary>"event=seat_left; structure=Zeus" (also ',' or '&amp;&amp;' between, '==' allowed) → conditions, or an error to show.</summary>
    public static IReadOnlyList<WatchCondition>? Parse(string? text, out string? error)
    {
        error = null;
        var parts = (text ?? "").Replace("&&", ";").Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) { error = "No conditions. Write field=value pairs separated by ';', e.g. 'event=seat_left; ship=Zeus'."; return null; }
        var list = new List<WatchCondition>();
        foreach (var p in parts)
        {
            var eq = p.IndexOf('=');
            if (eq <= 0) { error = $"'{p}' is not field=value."; return null; }
            var field = p[..eq].Trim().ToLowerInvariant();
            var value = p[(eq + 1)..].TrimStart('=').Trim().Trim('\'', '"');
            if (!Fields.TryGetValue(field, out var allowed))
            { error = $"Unknown field '{field}'. Fields: {string.Join(", ", Fields.Keys)}."; return null; }
            if (value.Length == 0) { error = $"'{field}' has no value."; return null; }
            if (allowed != null)
            {
                value = value.ToLowerInvariant();
                if (!allowed.Contains(value)) { error = $"'{field}' must be one of: {string.Join(", ", allowed)}."; return null; }
            }
            if (list.Any(c => c.Field == field)) { error = $"'{field}' appears twice; a watch is AND only (add a second watch for OR)."; return null; }
            list.Add(new WatchCondition(field, value));
        }
        if (list.All(c => c.Field != "event")) { error = $"An 'event' condition is required: {string.Join(", ", Events)}."; return null; }
        var ev = list.First(c => c.Field == "event").Value;
        if (ev is "signal" or "device" && list.All(c => c.Field != "name"))
        { error = $"A {ev} watch needs name=<the {ev}'s name>."; return null; }
        return list;
    }

    public static bool Matches(Watch w, IReadOnlyDictionary<string, string> snapshot) =>
        w.Conditions.All(c => snapshot.TryGetValue(c.Field, out var v) && string.Equals(v, c.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>The signal/device names watches ask about, per structure ("" = the player's ship).</summary>
    public static IReadOnlyDictionary<string, string[]> WatchedNames(IEnumerable<Watch> watches) =>
        watches.Where(w => w.Conditions.Any(c => c.Field == "event" && c.Value is "signal" or "device"))
            .GroupBy(w => w.Conditions.FirstOrDefault(c => c.Field == "structure")?.Value ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(w => w.Conditions.First(c => c.Field == "name").Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);

    private static string Where(GameState s) =>
        string.Equals(s.PlayfieldType, "space", StringComparison.OrdinalIgnoreCase) ? "space" : "planet";

    private static string Seat(GameState s) => s.InShip ? (s.IsPilot ? "pilot" : "aboard") : "on_foot";

    /// <summary>
    /// The snapshot of one event. Seat, where and playfield are the state now; the ship fields (docked, landed) are those
    /// of <paramref name="shipState"/>, the state that has the ship the event is about (before leaving it, after boarding).
    /// </summary>
    public static Dictionary<string, string> Snapshot(string ev, GameState now, GameState shipState, string? ship,
        string? otherShip = null, string? name = null, string? state = null)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["event"] = ev, ["seat"] = Seat(now), ["where"] = Where(now),
        };
        if (now.Playfield != null) d["playfield"] = now.Playfield;
        if (ship != null) d["structure"] = ship;
        if (ship != null && shipState.ShipName == ship && shipState.InShip)
        {
            if (shipState.ShipType is { Length: > 0 } t) d["type"] = t.ToLowerInvariant();
            d["docked"] = shipState.DockedTo != null ? "true" : "false";
            if (shipState.DockedTo != null) d["docked_to"] = shipState.DockedTo;
            if (shipState.Landed is { } l) d["landed"] = l ? "true" : "false";
        }
        if (otherShip != null) d["other_structure"] = otherShip;
        if (name != null) d["name"] = name;
        if (state != null) d["state"] = state;
        return d;
    }

    /// <summary>The watch events between two polls (ship, seat, docking, landing); signal/device events come from <see cref="StateEvents"/>.</summary>
    public static List<Dictionary<string, string>> Diff(GameState? prev, GameState? next)
    {
        var list = new List<Dictionary<string, string>>();
        if (prev == null || next == null) return list;
        var prevShip = prev.InShip ? prev.ShipName : null;
        var nextShip = next.InShip ? next.ShipName : null;
        var sameShip = prevShip != null && prevShip == nextShip;

        if (prevShip != null && !sameShip)
        {
            if (prev.IsPilot) list.Add(Snapshot("seat_left", next, prev, prevShip));
            list.Add(Snapshot("left", next, prev, prevShip));
        }
        if (nextShip != null && !sameShip)
        {
            list.Add(Snapshot("entered", next, next, nextShip));
            if (next.IsPilot) list.Add(Snapshot("seat_taken", next, next, nextShip));
        }
        if (sameShip)
        {
            if (!prev.IsPilot && next.IsPilot) list.Add(Snapshot("seat_taken", next, next, nextShip));
            if (prev.IsPilot && !next.IsPilot) list.Add(Snapshot("seat_left", next, next, nextShip));
            if (prev.DockedTo != next.DockedTo)
            {
                if (prev.DockedTo != null) list.Add(Snapshot("undocked", next, next, nextShip, otherShip: prev.DockedTo));
                if (next.DockedTo != null) list.Add(Snapshot("docked", next, next, nextShip, otherShip: next.DockedTo));
            }
            if (prev.Landed is { } wasLanded && next.Landed is { } isLanded && wasLanded != isLanded)
                list.Add(Snapshot(isLanded ? "landed" : "lifted_off", next, next, nextShip));
        }
        var knownNew = prev.NewStructures.Select(n => n.Id).ToHashSet();
        foreach (var n in next.NewStructures.Where(n => !knownNew.Contains(n.Id)))
        {
            var d = Snapshot(n.Kind == "blueprint" ? "new_structure" : "starter_placed", next, next, n.Name);
            d["type"] = n.Type.ToLowerInvariant();
            list.Add(d);
        }
        return list;
    }

    /// <summary>
    /// Signal/device events from the watched states of two polls: key "ship|name" (ship "" = the player's ship) →
    /// (kind, state on/off). A missing or unknown state on either side is no event.
    /// </summary>
    public static List<Dictionary<string, string>> StateEvents(GameState now,
        IReadOnlyDictionary<string, (string kind, string state, string ship)> prev,
        IReadOnlyDictionary<string, (string kind, string state, string ship)> next)
    {
        var list = new List<Dictionary<string, string>>();
        foreach (var (key, n) in next)
        {
            if (!prev.TryGetValue(key, out var p) || p.state == n.state) continue;
            if (n.state is not ("on" or "off") || p.state is not ("on" or "off")) continue;
            var name = key[(key.IndexOf('|') + 1)..];
            list.Add(Snapshot(n.kind, now, now, n.ship, name: name, state: n.state));
        }
        return list;
    }

    /// <summary>The text the model gets for a fired watch.</summary>
    public static string Notification(Watch w, IReadOnlyDictionary<string, string> snapshot)
    {
        var sb = new StringBuilder();
        sb.Append($"[WATCH #{w.Id} fired in Empyrion] Your note: \"{w.Note}\". ");
        sb.Append("Now: ").Append(string.Join(", ", snapshot.Select(kv => $"{kv.Key}={kv.Value}"))).Append(". ");
        sb.Append("Act on it as your note says if it still fits (tools, e.g. empyrion_ship_control), and say briefly what you did; " +
                  "if nothing is to be done, say so in a few words.");
        return sb.ToString();
    }
}
