using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Voxta.Modules.Empyrion.Bridge;

/// <summary>
/// The watches of one player on one game server, kept in a JSON file so they survive chats and Voxta restarts:
/// <c>&lt;folder&gt;/&lt;md5(bridge URL + "|" + SteamID)&gt;.json</c>. The file also holds the bridge URL and SteamID in
/// plain text, so it can be told apart by looking inside. Thread-safe; every change is written at once.
/// Entries that don't parse (e.g. after an event or field was renamed) are skipped with a warning but kept in the file
/// as they are, and their ids stay taken: new watches never reuse them, so older chat messages still mean the same watch.
/// </summary>
public sealed class WatchStore
{
    private readonly string _path;
    private readonly string _bridgeUrl;
    private readonly string _steamId;
    private readonly int _maxWatches;
    private readonly object _lock = new();
    private List<Watch> _watches = [];
    private List<FileWatch> _unreadable = [];
    private readonly Action<string>? _warn;
    private int _nextId = 1;

    private sealed class FileData
    {
        public string BridgeUrl { get; set; } = "";
        public string SteamId { get; set; } = "";
        public int NextId { get; set; } = 1;
        public List<FileWatch> Watches { get; set; } = [];
    }

    private sealed class FileWatch
    {
        public int Id { get; set; }
        public string Conditions { get; set; } = "";
        public string Note { get; set; } = "";
        public DateTimeOffset Created { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <param name="maxWatches">Adding stops at this many (module setting); watches above it, e.g. after lowering the
    /// setting, are kept.</param>
    /// <param name="warn">Where to report skipped entries (the module passes Voxta's log).</param>
    public WatchStore(string folder, string bridgeUrl, string steamId, int maxWatches = Watches.DefaultMaxWatches, Action<string>? warn = null)
    {
        _warn = warn;
        _maxWatches = maxWatches;
        _bridgeUrl = bridgeUrl;
        _steamId = steamId;
        _path = Path.Combine(folder, FileName(bridgeUrl, steamId));
        Load();
    }

    /// <summary>md5 of "bridge URL|SteamID" (URL trimmed, without a trailing slash, lower case) + ".json".</summary>
    public static string FileName(string bridgeUrl, string steamId)
    {
        var key = $"{bridgeUrl.Trim().TrimEnd('/').ToLowerInvariant()}|{steamId.Trim()}";
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(key))) + ".json";
    }

    public string FilePath => _path;

    public IReadOnlyList<Watch> All { get { lock (_lock) return _watches.ToList(); } }

    public Watch? Add(IReadOnlyList<WatchCondition> conditions, string note, out string? error)
    {
        lock (_lock)
        {
            error = null;
            if (_watches.Count >= _maxWatches)
            {
                error = $"There are already {_watches.Count} watches (the limit is {_maxWatches}, module setting \"Maximum Watches\"); remove some first.";
                return null;
            }
            var w = new Watch(_nextId++, conditions, note, DateTimeOffset.UtcNow);
            _watches.Add(w);
            Save();
            return w;
        }
    }

    /// <summary>Removes one watch by id, or all with id null; returns how many were removed.</summary>
    public int Remove(int? id)
    {
        lock (_lock)
        {
            var n = id == null ? _watches.Count + _unreadable.Count : _watches.RemoveAll(w => w.Id == id) + _unreadable.RemoveAll(w => w.Id == id);
            if (id == null) { _watches.Clear(); _unreadable.Clear(); }
            if (n > 0) Save();
            return n;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(_path));
            if (data == null) return;
            _nextId = Math.Max(1, data.NextId);
            foreach (var fw in data.Watches)
            {
                if (Watches.Parse(fw.Conditions, out var error) is { } conds)
                    _watches.Add(new Watch(fw.Id, conds, fw.Note, fw.Created));
                else
                {
                    _unreadable.Add(fw);
                    _warn?.Invoke($"watch #{fw.Id} in {_path} skipped: '{fw.Conditions}': {error}");
                }
            }
            var ids = _watches.Select(w => w.Id).Concat(_unreadable.Select(u => u.Id)).ToList();
            if (ids.Count > 0) _nextId = Math.Max(_nextId, ids.Max() + 1);
        }
        catch (Exception e)
        {
            // unreadable file: start empty; keep a copy so the next change doesn't lose it
            _warn?.Invoke($"watch file {_path} unreadable, starting empty (a copy is kept as .bad): {e.Message}");
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch { }
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var data = new FileData
        {
            BridgeUrl = _bridgeUrl, SteamId = _steamId, NextId = _nextId,
            Watches = _watches.Select(w => new FileWatch { Id = w.Id, Conditions = w.ConditionText, Note = w.Note, Created = w.Created })
                .Concat(_unreadable).OrderBy(w => w.Id).ToList(),
        };
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
