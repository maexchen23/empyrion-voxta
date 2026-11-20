namespace Voxta.Modules.Empyrion.Bridge;

/// <summary>
/// Which game chat messages go to the character: new ones only (the first list seen is the baseline, so a chat start
/// doesn't replay old messages), and with a prefix set only those starting with it (prefix removed).
/// </summary>
public sealed class GameChatFilter(string? prefix)
{
    private readonly string _prefix = (prefix ?? "").Trim();
    private long? _lastSeq;

    /// <summary>Eager (not an iterator): the baseline and the last seen message are set by the call itself.</summary>
    public List<string> Take(IReadOnlyList<(long Seq, string Channel, string Text)> lines)
    {
        var max = lines.Count > 0 ? lines.Max(l => l.Seq) : 0;
        var result = new List<string>();
        // a restarted game server numbers from 1 again: start over with a new baseline
        if (_lastSeq is not { } last || (lines.Count > 0 && max < last))
        {
            _lastSeq = max;
            return result;
        }
        foreach (var (_, _, text) in lines.Where(l => l.Seq > last).OrderBy(l => l.Seq))
            if (Strip(text) is { Length: > 0 } t) result.Add(t);
        if (max > last) _lastSeq = max;
        return result;
    }

    /// <summary>The message without the prefix, or null when it doesn't start with the prefix.</summary>
    public string? Strip(string text)
    {
        text = text.Trim();
        if (_prefix.Length == 0) return text;
        if (!text.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = text[_prefix.Length..];
        if (rest.Length > 0 && char.IsLetterOrDigit(rest[0]) && char.IsLetterOrDigit(_prefix[^1])) return null; // "@idaho"
        return rest.TrimStart(' ', ':', ',', '-').Trim();
    }
}
