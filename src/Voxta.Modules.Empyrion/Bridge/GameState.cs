using System.Globalization;
using System.Text;

namespace Voxta.Modules.Empyrion.Bridge;

/// <summary>Compact game state of one player, taken from one bridge poll.</summary>
public sealed record GameState
{
    public string? Player { get; init; }
    public string? Playfield { get; init; }
    public string? PlayfieldType { get; init; }
    public string? System { get; init; }
    public bool? Pvp { get; init; }

    public double? Health { get; init; }
    public double? HealthMax { get; init; }
    public double? Oxygen { get; init; }
    public double? OxygenMax { get; init; }
    public double? Food { get; init; }
    public double? FoodMax { get; init; }
    public double? Radiation { get; init; }
    public double? RadiationMax { get; init; }

    public string? ShipName { get; init; }
    public string? ShipType { get; init; }
    public bool IsPilot { get; init; }
    public double? ShipFuel { get; init; }
    public double? ShipFuelMax { get; init; }
    public double? ShipOxygen { get; init; }
    public double? ShipOxygenMax { get; init; }
    public double? ShipPentaxid { get; init; }
    public double? ShipPentaxidMax { get; init; }
    public bool? ShieldActive { get; init; }
    public bool? Powered { get; init; }

    /// <summary>
    /// The own structure the item features work from when the player isn't on it (on foot, or set to another one):
    /// name, type, why ("last aboard", "nearest", "pinned"), distance. Null when the player is aboard the active one.
    /// </summary>
    public string? ActiveName { get; init; }
    public string? ActiveType { get; init; }
    public string? ActiveHow { get; init; }
    public double? ActiveDistance { get; init; }
    /// <summary>Why a set active structure was dropped (out of range, gone, other playfield).</summary>
    public string? ActiveNote { get; init; }

    /// <summary>A structure the player is inside that isn't theirs (POI, trader station, someone else's ship).</summary>
    public string? VisitingName { get; init; }
    public string? VisitingType { get; init; }
    public string? VisitingOwner { get; init; }

    public string? FactoryBlueprint { get; init; }
    public double? FactoryRemainingS { get; init; }

    public bool InShip => ShipName != null;

    public static double? Percent(double? value, double? max) =>
        value is { } v && max is > 0 ? Math.Clamp(v / max.Value * 100, 0, 100) : null;

    /// <summary>The lines kept in the character's context while the chat runs.</summary>
    public string ToContext()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[ EMPYRION: live game state of {Player} ]");
        var where = $"Location: {Playfield}";
        if (PlayfieldType != null) where += $" ({PlayfieldType})";
        if (System != null) where += $", system {System}";
        if (Pvp == true) where += ", PvP zone";
        sb.AppendLine(where);

        var vitals = new List<string>();
        if (Percent(Health, HealthMax) is { } h) vitals.Add($"health {h:0}%");
        if (Percent(Oxygen, OxygenMax) is { } o) vitals.Add($"suit oxygen {o:0}%");
        if (Percent(Food, FoodMax) is { } f) vitals.Add($"food {f:0}%");
        if (Percent(Radiation, RadiationMax) is { } r && r >= 1) vitals.Add($"radiation {r:0}%");
        if (vitals.Count > 0) sb.AppendLine("Player: " + string.Join(", ", vitals));

        string Status()
        {
            var st = "";
            if (Powered == false) st += ", power off";
            if (ShieldActive == true) st += ", shield up";
            var tanks = new List<string>();
            if (Tank("fuel", ShipFuel, ShipFuelMax) is { } t1) tanks.Add(t1);
            if (Tank("O2", ShipOxygen, ShipOxygenMax) is { } t2) tanks.Add(t2);
            if (Tank("pentaxid", ShipPentaxid, ShipPentaxidMax) is { } t3) tanks.Add(t3);
            if (tanks.Count > 0) st += "; tanks: " + string.Join(", ", tanks);
            return st;
        }
        if (InShip)
        {
            // power/shield/tanks belong to the active structure: here only when the player is aboard that one
            sb.AppendLine($"{(IsPilot ? "Piloting" : "Aboard")} {ShipType} '{ShipName}'{(ActiveName == null ? Status() : "")}");
        }
        else if (VisitingName != null)
        {
            var what = VisitingType == "BA" ? "base" : VisitingType ?? "structure";
            sb.AppendLine($"On foot inside the {what} '{VisitingName}'{(VisitingOwner != null ? $" ({VisitingOwner})" : "")}, not the player's.");
        }
        else
        {
            sb.AppendLine("On foot.");
        }
        if (ActiveName != null)
        {
            var why = ActiveHow switch { "pinned" => "set by the player", "last aboard" => "last aboard", "nearest" => "nearest own", _ => ActiveHow };
            var away = ActiveDistance is { } d ? $", {(d < 1000 ? $"{Math.Max(10, Math.Round(d / 10) * 10):0} m" : $"{d / 1000:0.#} km")} away" : "";
            sb.AppendLine($"Active structure (containers, transfers, crafting): {ActiveType} '{ActiveName}' ({why}{away}){Status()}");
        }
        if (ActiveNote != null) sb.AppendLine($"Note: {ActiveNote}.");

        if (FactoryBlueprint != null)
            sb.AppendLine(FactoryRemainingS is > 0
                ? $"Factory: building '{FactoryBlueprint}', {FormatDuration(FactoryRemainingS.Value)} left"
                : $"Factory: '{FactoryBlueprint}' is ready");

        sb.Append("Use the empyrion_* tools for details (cargo, inventory).");
        return sb.ToString();
    }

    private static string? Tank(string label, double? value, double? max) =>
        Percent(value, max) is { } p ? $"{label} {p:0}%" : null;

    private static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}h {t.Minutes}m")
            : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";
    }
}
