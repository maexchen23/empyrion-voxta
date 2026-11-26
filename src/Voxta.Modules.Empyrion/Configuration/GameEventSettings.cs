using System.Text.Json;
using Voxta.Model.Shared.Forms;

namespace Voxta.Modules.Empyrion.Configuration;

/// <summary>
/// The predefined game events (built-in watches): one fixed row each in the settings, with a mode (on = the character
/// reacts, silent = it is only told, off) and, for vitals and tanks, a threshold in percent. Rows can't be added,
/// removed or renamed. They never show up in the character's watch list.
/// </summary>
public static class GameEventSettings
{
    public const string On = "on";
    public const string Silent = "silent";
    public const string Off = "off";

    /// <summary>One predefined event (mode only).</summary>
    public sealed record Definition(string Id, string Label, string Mode);

    /// <summary>One vital or tank with three levels (warning, low, empty): mode and threshold in percent each.</summary>
    public sealed record LevelDefinition(string Id, string Label, string WarningMode, string LowMode, string EmptyMode,
        int Warning = 25, int Low = 15, int Empty = 0);

    /// <summary>The simple events in display order (the agreed list, 2026-11-20).</summary>
    public static readonly Definition[] Definitions =
    [
        new("player_joined", "Player joined the game", On),
        new("player_left", "Player left the game", Silent),
        new("playfield_changed", "Arrived on another planet or sector", On),
        new("entered_structure", "Entered a ship or base", Silent),
        new("left_structure", "Left a ship or base", Silent),
        new("took_controls", "Took the pilot seat", Silent),
        new("left_pilot_seat", "Left the pilot seat", Silent),
        new("shield_down", "Ship shield went down", On),
        new("new_structure", "New ship or base spawned from a blueprint", On),
        new("starter_placed", "Starter block placed (new base or vessel)", On),
        new("new_factory_mission", "New factory mission", On),
        new("new_trader_mission", "New trader mission", Off),
        new("factory_mission_done", "Factory mission handed in", On),
        new("trader_mission_done", "Trader mission handed in", Off),
        new("pursued_mission_done", "Pursued mission done", On),
        new("pursued_items_ready", "Pursued mission: all items in reach", On),
        new("new_mining_contract", "New mining contract", Silent),
        new("mining_site_job", "Job broadcast at a mining site (on arrival)", On),
        new("sector_briefing", "Sector report (first visit of special sectors)", On),
        new("new_job", "New job: beacon repair, ship in distress, probe scan (offers the items)", On),
        new("space_fight", "Scripted fights: start, wave cleared, all enemies down", On),
        new("timed_mission", "Missions against the clock: start, time warnings, progress, time up", On),
        new("job_done", "Job done", Silent),
        new("help_call", "Call for help on the local channel (on arrival)", On),
        new("mining_contract_done", "Mining contract handed in", Silent),
        new("blueprint_ready", "Factory blueprint ready", On),
    ];

    /// <summary>Vitals and tanks. Health "empty" (0 %) is the player's death.</summary>
    public static readonly LevelDefinition[] Levels =
    [
        new("health", "Health", On, On, On),
        new("suit_oxygen", "Suit oxygen", On, On, On),
        new("food", "Food", On, On, On),
        new("ship_oxygen", "Ship oxygen tank", Silent, Silent, On),
        new("fuel", "Ship fuel tank", On, On, On),
        new("pentaxid", "Ship pentaxid tank", Silent, Silent, Silent),
    ];

    private static readonly FormChoice[] Modes =
    [
        new() { Value = On, Label = "On (the character reacts)" },
        new() { Value = Silent, Label = "Silent (only told)" },
        new() { Value = Off, Label = "Off" },
    ];

    private static FormTextField Threshold(string name, string label) => new()
    {
        Name = name, Label = label, ValidationRegex = "^([0-9]|[1-9][0-9]|100)?$",
    };

    /// <summary>One dropdown per simple event (setting name "Event_&lt;id&gt;").</summary>
    public static readonly FormChoicesField[] EventChoiceFields = Definitions.Select(d => new FormChoicesField
    {
        Name = "Event_" + d.Id,
        Label = d.Label,
        Choices = Modes,
        DefaultValue = d.Mode,
    }).ToArray();

    public static FormField[] EventFields => EventChoiceFields.Cast<FormField>().ToArray();

    public static readonly FormArrayOfObjectsField LevelsField = new()
    {
        Name = "GameEventLevels",
        Label = "Vitals and Tanks",
        Text = "Three levels each: the event fires once when the value drops to or below the threshold (in percent), the most " +
               "severe level wins, and it re-arms when the value is back above. Ship tanks only while the player is aboard. " +
               "Health empty (0 %) means the player died. Ship oxygen empty means the player is now breathing from the suit.",
        AllowAddRemove = false,
        AllowRename = false,
        AllowDisable = false,
        UniqueFields = ["id"],
        FieldTemplate =
        [
            new FormTextField { Name = "name", Label = "Vital or tank" },
            new FormHiddenField { Name = "id", Label = "Id" },
            new FormChoicesField { Name = "warning_mode", Label = "Warning", Choices = Modes, DefaultValue = On },
            Threshold("warning", "Warning at (%)"),
            new FormChoicesField { Name = "low_mode", Label = "Low", Choices = Modes, DefaultValue = On },
            Threshold("low", "Low at (%)"),
            new FormChoicesField { Name = "empty_mode", Label = "Empty", Choices = Modes, DefaultValue = On },
            Threshold("empty", "Empty at (%)"),
        ],
        DefaultValue = JsonSerializer.Serialize(Levels.Select(d => new Dictionary<string, string>
        {
            ["name"] = d.Label, ["id"] = d.Id,
            ["warning_mode"] = d.WarningMode, ["warning"] = d.Warning.ToString(),
            ["low_mode"] = d.LowMode, ["low"] = d.Low.ToString(),
            ["empty_mode"] = d.EmptyMode, ["empty"] = d.Empty.ToString(),
        })),
    };
}
