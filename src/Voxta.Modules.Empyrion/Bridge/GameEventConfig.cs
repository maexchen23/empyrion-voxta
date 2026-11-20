using Voxta.Modules.Empyrion.Configuration;

namespace Voxta.Modules.Empyrion.Bridge;

/// <summary>
/// The predefined game events as configured (module settings "Game Events" and "Vitals and Tanks"): a mode per event and
/// per vital/tank level (on, silent, off) and the level thresholds. They run as hidden watches through the same matcher as
/// the character's watches (<see cref="Watches.Matches"/>): one per enabled event, condition event=&lt;id&gt;, announced or
/// silent. Hidden watches have negative ids and never show up in the watch list.
/// </summary>
public sealed class GameEventConfig
{
    public sealed record Thresholds(int Warning, int Low, int Empty);

    private readonly Dictionary<string, string> _modes;
    private readonly Dictionary<string, Thresholds> _thresholds;

    private GameEventConfig(Dictionary<string, string> modes, Dictionary<string, Thresholds> thresholds)
    {
        _modes = modes;
        _thresholds = thresholds;
        HiddenWatches = _modes.Where(kv => kv.Value != GameEventSettings.Off)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select((kv, i) => new Watch(-(i + 1), [new WatchCondition("event", kv.Key)], "", DateTimeOffset.MinValue)
                { Announce = kv.Value == GameEventSettings.On, Hidden = true })
            .ToList();
    }

    /// <summary>One hidden watch per enabled event or level.</summary>
    public IReadOnlyList<Watch> HiddenWatches { get; }

    /// <summary>The defaults of the settings (also what an unreadable setting falls back to).</summary>
    public static GameEventConfig Default { get; } = From(_ => null, null);

    /// <param name="eventMode">Setting value of a simple event's dropdown by id, null = default.</param>
    /// <param name="levelRows">Rows of the "Vitals and Tanks" table (string values), null = defaults.</param>
    public static GameEventConfig From(Func<string, string?> eventMode, IEnumerable<IReadOnlyDictionary<string, string>>? levelRows)
    {
        static string Mode(string? value, string fallback) =>
            value is GameEventSettings.On or GameEventSettings.Silent or GameEventSettings.Off ? value : fallback;
        static int Percent(IReadOnlyDictionary<string, string>? row, string key, int fallback) =>
            row != null && row.TryGetValue(key, out var v) && int.TryParse(v, out var p) && p is >= 0 and <= 100 ? p : fallback;

        var modes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in GameEventSettings.Definitions) modes[d.Id] = Mode(eventMode(d.Id), d.Mode);

        var rows = (levelRows ?? []).Where(r => r.ContainsKey("id")).GroupBy(r => r["id"]).ToDictionary(g => g.Key, g => g.First());
        var thresholds = new Dictionary<string, Thresholds>(StringComparer.Ordinal);
        foreach (var l in GameEventSettings.Levels)
        {
            rows.TryGetValue(l.Id, out var row);
            string Get(string key) => row != null && row.TryGetValue(key, out var v) ? v : "";
            modes[l.Id + "_warning"] = Mode(Get("warning_mode"), l.WarningMode);
            modes[l.Id + "_low"] = Mode(Get("low_mode"), l.LowMode);
            modes[l.Id + "_empty"] = Mode(Get("empty_mode"), l.EmptyMode);
            thresholds[l.Id] = new Thresholds(Percent(row, "warning", l.Warning), Percent(row, "low", l.Low), Percent(row, "empty", l.Empty));
        }
        return new GameEventConfig(modes, thresholds);
    }

    public string ModeOf(string id) => _modes.TryGetValue(id, out var m) ? m : GameEventSettings.Off;

    public bool Enabled(string id) => ModeOf(id) != GameEventSettings.Off;

    public Thresholds ThresholdsOf(string resource) => _thresholds.TryGetValue(resource, out var t) ? t : new Thresholds(25, 15, 0);

    /// <summary>
    /// Runs the raw built-in events through the hidden watches: an event no watch matches (its mode is off) is dropped;
    /// a matched one is announced or silent as its watch says.
    /// </summary>
    public List<GameEvent> Apply(IEnumerable<GameEvent> raw)
    {
        var list = new List<GameEvent>();
        foreach (var e in raw)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["event"] = e.Id ?? e.Name };
            var w = HiddenWatches.FirstOrDefault(x => Watches.Matches(x, snapshot));
            if (w != null) list.Add(e with { Important = w.Announce });
        }
        return list;
    }
}
