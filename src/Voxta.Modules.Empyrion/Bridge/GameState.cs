using System.Globalization;
using System.Text;

namespace Voxta.Modules.Empyrion.Bridge;

/// <summary>One item a mission wants: internal name, display name, count, token id for tokens.</summary>
public sealed record MissionNeed(string Item, string Name, int Count, int? TokenId = null);

/// <summary>One open trader mission: key = trader + mission number; kind "factory" (its trades wait for it) or "trader".</summary>
public sealed record OpenMission(string Key, string Kind, string Line, string? Where = null, bool FollowUp = false, bool Pursued = false,
    IReadOnlyList<MissionNeed>? Needs = null);

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

    /// <summary>The own ship/base the player is in or at (see <see cref="Standing"/>).</summary>
    public string? ShipName { get; init; }
    /// <summary>
    /// Only at the structure: on its hull or next to it, inside its bounding box (the bridge's "standing"), not inside
    /// it (the game reports that). Not "in the ship": no boarding/leaving events, no tank events.
    /// </summary>
    public bool Standing { get; init; }
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

    /// <summary>The trader mission the player pursues (one line, from the bridge), its status (open, locked, done, gone).</summary>
    public string? PursuingLine { get; init; }
    public string? PursuingStatus { get; init; }
    /// <summary>The pursued mission's items with how many the player carries (personal inventory: bag + toolbar).</summary>
    public IReadOnlyList<(MissionNeed Need, int Have)>? PursuingProgress { get; init; }
    /// <summary>Everything the pursued (open) mission wants is in the personal inventory.</summary>
    public bool PursuingReady => PursuingStatus is "open" or "locked" && PursuingProgress is { Count: > 0 } pr && pr.All(x => x.Have >= x.Need.Count);
    /// <summary>The player's open trader missions (null = not known this poll, e.g. an older bridge).</summary>
    public IReadOnlyList<OpenMission>? OpenMissions { get; init; }

    public string? FactoryBlueprint { get; init; }
    public double? FactoryRemainingS { get; init; }

    public bool InShip => ShipName != null && !Standing;

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
        if (Standing && ShipName != null)
        {
            sb.AppendLine($"On foot at the {ShipType} '{ShipName}' (outside, on its hull or next to it){(ActiveName == null ? Status() : "")}");
        }
        else if (InShip)
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

        if (PursuingLine != null)
        {
            var progress = PursuingReady ? " (all of it is in the player's inventory: ready to hand in)"
                : PursuingProgress is { Count: > 0 } pr && PursuingStatus is "open" or "locked"
                    ? " (in the player's inventory: " + string.Join(", ", pr.Select(x => $"{Math.Min(x.Have, x.Need.Count)}/{x.Need.Count} {x.Need.Name}")) + ")"
                    : "";
            sb.AppendLine(PursuingStatus is "done" or "gone" ? $"Pursued mission: {PursuingLine}." : $"Pursuing: {PursuingLine}{progress}.");
        }

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
