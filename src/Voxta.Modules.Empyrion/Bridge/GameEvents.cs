namespace Voxta.Modules.Empyrion.Bridge;

/// <summary>Something that happened between two polls.</summary>
/// <param name="Name">Short event id, e.g. <c>LowOxygen</c>.</param>
/// <param name="Description">What happened, written for the character.</param>
/// <param name="Important">True: the character should react out loud. False: it only needs to know.</param>
/// <param name="MissionKey">For new-mission events: the mission, so the description can be completed (what is in reach).</param>
public sealed record GameEvent(string Name, string Description, bool Important, string? MissionKey = null);

/// <summary>
/// Which trader mission events the character speaks up about (module settings); the others are passed on silently.
/// "Factory": the trader's trades wait for the mission (incl. its follow-ups); "trader": a shop that trades anyway.
/// </summary>
public sealed record MissionAnnounce(bool NewFactory = true, bool NewTrader = false, bool DoneFactory = true, bool DoneTrader = false, bool PursuedDone = true,
    bool ItemsReady = true);

/// <summary>
/// Smooths over two gaps in the game's "which ship is the player in":
/// <list type="bullet">
/// <item>A warp: on the new playfield the player is briefly on no structure until the ship arrives there too, which
/// would read as "left the ship, now on foot" and then "boarded the ship". Only then (ship gone and the playfield
/// changed) the last ship state is kept, for at most <see cref="HoldFor"/>.</item>
/// <item>Leaving the pilot seat: the game reports no structure for a moment, and the bridge falls back to "standing on"
/// the ship. Standing on (or in) the ship the player was aboard counts as still aboard; leaving it (standing on nothing
/// or on something else) is the exit. So getting out of the seat is no event, and being thrown out of a moving ship
/// becomes "left" once it has moved away.</item>
/// </list>
/// On the same playfield a missing ship is a real exit and shows at once.
/// </summary>
public sealed class WarpSmoother
{
    public static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(60);

    private GameState? _lastInShip;
    private DateTimeOffset? _lostAt;

    /// <summary>The state to show and diff: <paramref name="raw"/>, or it with the held ship while a warp may be running.</summary>
    public GameState? Apply(GameState? raw, DateTimeOffset now)
    {
        if (raw == null) { Reset(); return null; }
        if (raw.InShip) { _lastInShip = raw; _lostAt = null; return raw; }
        if (_lastInShip == null) return raw;
        if (raw.Standing && raw.ShipName != null && raw.ShipName == _lastInShip.ShipName && raw.Playfield == _lastInShip.Playfield)
            return raw with { Standing = false, IsPilot = false }; // still on the ship they were aboard: aboard
        if (raw.Playfield == _lastInShip.Playfield) { Reset(); return raw; } // same playfield: really got out
        _lostAt ??= now;
        if (now - _lostAt.Value > HoldFor) { Reset(); return raw; }
        var held = _lastInShip;
        return raw with
        {
            ShipName = held.ShipName, ShipType = held.ShipType, IsPilot = held.IsPilot,
            ShipFuel = held.ShipFuel, ShipFuelMax = held.ShipFuelMax, ShipOxygen = held.ShipOxygen, ShipOxygenMax = held.ShipOxygenMax,
            ShipPentaxid = held.ShipPentaxid, ShipPentaxidMax = held.ShipPentaxidMax, ShieldActive = held.ShieldActive, Powered = held.Powered,
        };
    }

    private void Reset() { _lastInShip = null; _lostAt = null; }
}

/// <summary>
/// Compares two polls and names what changed. Only crossings count (a tank that was already empty when
/// the chat started is not an event), so the character is not told the same thing every poll.
/// </summary>
public static class GameEvents
{
    public const double LowVitalPercent = 25;
    public const double CriticalVitalPercent = 10;
    public const double LowTankPercent = 15;

    /// <param name="prev">State of the previous poll; null when the player was offline or this is the first poll.</param>
    /// <param name="next">State now; null when the player is offline.</param>
    /// <param name="firstPoll">True for the first poll of a chat: nothing is reported, the context covers it.</param>
    public static List<GameEvent> Diff(GameState? prev, GameState? next, bool firstPoll = false, MissionAnnounce? announce = null)
    {
        var events = new List<GameEvent>();
        if (firstPoll) return events;

        if (prev == null && next != null)
        {
            events.Add(new("PlayerJoined", $"{next.Player} joined the game, on {next.Playfield}.", true));
            return events;
        }
        if (prev != null && next == null)
        {
            events.Add(new("PlayerLeft", $"{prev.Player} left the game.", false));
            return events;
        }
        if (prev == null || next == null) return events;

        if (prev.Playfield != next.Playfield && next.Playfield != null)
        {
            var warped = prev.InShip && next.InShip && prev.ShipName == next.ShipName;
            var desc = warped ? $"Warped aboard the {next.ShipType} '{next.ShipName}' to {next.Playfield}" : $"Arrived at {next.Playfield}";
            if (next.PlayfieldType != null) desc += $" ({next.PlayfieldType})";
            if (next.System != null && next.System != prev.System) desc += $" in the {next.System} system";
            desc += $", coming from {prev.Playfield}.";
            if (next.Pvp == true && prev.Pvp != true) desc += " This is a PvP zone: other players can attack.";
            events.Add(new("PlayfieldChanged", desc, true));
        }

        if (!prev.InShip && next.InShip)
            events.Add(new("BoardedShip", $"Boarded the {next.ShipType} '{next.ShipName}'{(next.IsPilot ? " and took the controls" : "")}.", false));
        else if (prev.InShip && !next.InShip)
            events.Add(new("LeftShip", $"Left the {prev.ShipType} '{prev.ShipName}', now on foot.", false));
        else if (prev.InShip && next.InShip && prev.ShipName != next.ShipName)
            events.Add(new("ChangedShip", $"Moved from '{prev.ShipName}' to the {next.ShipType} '{next.ShipName}'.", false));
        else if (prev.InShip && next.InShip && !prev.IsPilot && next.IsPilot)
            events.Add(new("TookControls", $"Took the controls of the {next.ShipType} '{next.ShipName}'.", false));
        else if (prev.InShip && next.InShip && prev.IsPilot && !next.IsPilot)
            events.Add(new("LeftPilotSeat", $"Left the pilot seat of the {next.ShipType} '{next.ShipName}', still aboard.", false));

        Vital(events, "Health", "health", prev.Health, prev.HealthMax, next.Health, next.HealthMax);
        Vital(events, "Oxygen", "suit oxygen", prev.Oxygen, prev.OxygenMax, next.Oxygen, next.OxygenMax);
        Vital(events, "Food", "food", prev.Food, prev.FoodMax, next.Food, next.FoodMax);

        if (next.Health is <= 0 && prev.Health is > 0)
            events.Add(new("PlayerDied", $"{next.Player} died.", true));

        // Ship tanks: only while the player stays on the same ship.
        if (prev.InShip && next.InShip && prev.ShipName == next.ShipName)
        {
            Tank(events, "Fuel", "fuel", next.ShipName, prev.ShipFuel, prev.ShipFuelMax, next.ShipFuel, next.ShipFuelMax, important: true);
            Tank(events, "ShipOxygen", "oxygen", next.ShipName, prev.ShipOxygen, prev.ShipOxygenMax, next.ShipOxygen, next.ShipOxygenMax, important: false);
            Tank(events, "Pentaxid", "pentaxid", next.ShipName, prev.ShipPentaxid, prev.ShipPentaxidMax, next.ShipPentaxid, next.ShipPentaxidMax, important: false);

            if (prev.ShieldActive == true && next.ShieldActive == false)
                events.Add(new("ShieldDown", $"The shield of '{next.ShipName}' went down.", true));
        }

        announce ??= new MissionAnnounce();
        if (prev.PursuingLine != null && prev.PursuingStatus is "open" or "locked" && next.PursuingStatus == "done")
            events.Add(new("PursuedMissionDone", $"The pursued trader mission is done: {next.PursuingLine}.", announce.PursuedDone));

        if (!prev.PursuingReady && next.PursuingReady && prev.PursuingLine != null && prev.PursuingLine == next.PursuingLine)
            events.Add(new("PursuedItemsReady",
                $"The player now carries everything the pursued mission wants: {next.PursuingLine}. They can hand it in at the trader.", announce.ItemsReady));

        // new trader missions: only against a known earlier list (the first list of a chat is the baseline)
        if (prev.OpenMissions is { } before && next.OpenMissions is { } now)
        {
            var known = before.Select(m => m.Key).ToHashSet();
            foreach (var m in now.Where(m => !known.Contains(m.Key)))
            {
                var factory = m.Kind == "factory";
                var speak = factory ? announce.NewFactory : announce.NewTrader;
                events.Add(new(factory ? "NewFactoryMission" : "NewTraderMission",
                    $"The player just got a new {(factory ? "factory" : "trader")} mission: {m.Line}." +
                    (speak ? " Mention it briefly; empyrion_pursue_quest keeps track of it if they want." : ""), speak, m.Key));
            }
            // gone from the open list = handed in (missions can't be dropped); the pursued one has its own event
            var open = now.Select(m => m.Key).ToHashSet();
            foreach (var m in before.Where(m => !open.Contains(m.Key) && !m.Pursued))
            {
                var factory = m.Kind == "factory";
                var where = m.Where ?? m.Line;
                var desc = factory
                    ? $"The player completed the factory mission for {where}{(m.FollowUp ? "" : "; its trades are unlocked now")}."
                    : $"The player completed the job for {where}.";
                events.Add(new(factory ? "FactoryMissionDone" : "TraderMissionDone", desc, factory ? announce.DoneFactory : announce.DoneTrader));
            }
        }

        if (prev.FactoryBlueprint == next.FactoryBlueprint && next.FactoryBlueprint != null
            && prev.FactoryRemainingS is > 0 && next.FactoryRemainingS is <= 0)
            events.Add(new("BlueprintReady", $"The factory finished '{next.FactoryBlueprint}'; it can be spawned now.", true));

        return events;
    }

    private static void Vital(List<GameEvent> events, string name, string label,
        double? pv, double? pmax, double? nv, double? nmax)
    {
        var before = GameState.Percent(pv, pmax);
        var now = GameState.Percent(nv, nmax);
        if (before is not { } b || now is not { } n) return;

        if (b > CriticalVitalPercent && n <= CriticalVitalPercent)
            events.Add(new($"Critical{name}", $"Critical: {label} is down to {n:0}%.", true));
        else if (b > LowVitalPercent && n <= LowVitalPercent)
            events.Add(new($"Low{name}", $"Warning: {label} is low, {n:0}%.", true));
    }

    private static void Tank(List<GameEvent> events, string name, string label, string? ship,
        double? pv, double? pmax, double? nv, double? nmax, bool important)
    {
        var before = GameState.Percent(pv, pmax);
        var now = GameState.Percent(nv, nmax);
        if (before is not { } b || now is not { } n) return;

        if (b > 0 && n <= 0)
            events.Add(new($"{name}Empty", $"The {label} tank of '{ship}' is empty.", important));
        else if (b > LowTankPercent && n <= LowTankPercent)
            events.Add(new($"Low{name}", $"The {label} tank of '{ship}' is low, {n:0}%.", important));
    }
}
