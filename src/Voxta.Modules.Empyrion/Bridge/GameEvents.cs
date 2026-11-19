namespace Voxta.Modules.Empyrion.Bridge;

/// <summary>Something that happened between two polls.</summary>
/// <param name="Name">Short event id, e.g. <c>LowOxygen</c>.</param>
/// <param name="Description">What happened, written for the character.</param>
/// <param name="Important">True: the character should react out loud. False: it only needs to know.</param>
public sealed record GameEvent(string Name, string Description, bool Important);

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
    public static List<GameEvent> Diff(GameState? prev, GameState? next, bool firstPoll = false)
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
            var desc = $"Arrived at {next.Playfield}";
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
