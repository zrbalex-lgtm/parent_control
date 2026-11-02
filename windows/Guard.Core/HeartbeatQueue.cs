using System.Text.Json;

namespace ParentControl.Core;

/// <summary>
/// Durable queue of heartbeats not yet accepted by the server (JSON Lines).
/// Enqueue appends one line; removal rewrites the file (the queue is short when online).
/// </summary>
public sealed class HeartbeatQueue
{
    /// <summary>About 30 days of minutes; older items are dropped first.</summary>
    public const int MaxItems = 30 * 24 * 60;

    private readonly string _path;
    private readonly int _maxItems;
    private readonly List<Heartbeat> _items = [];
    private readonly HashSet<DateTimeOffset> _keys = [];
    private readonly object _lock = new();
    private bool _dirty;

    public HeartbeatQueue(string path, int maxItems = MaxItems)
    {
        _path = path;
        _maxItems = maxItems;
        Load();
    }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public IReadOnlyList<Heartbeat> Snapshot()
    {
        lock (_lock) return _items.ToList();
    }

    public IReadOnlyList<Heartbeat> Peek(int max)
    {
        lock (_lock) return _items.Take(max).ToList();
    }

    /// <returns>false if a heartbeat for this minute is already queued.</returns>
    public bool Enqueue(Heartbeat hb)
    {
        lock (_lock)
        {
            if (!_keys.Add(hb.Ts)) return false;
            _items.Add(hb);
            if (_items.Count > _maxItems)
            {
                var drop = _items.Count - _maxItems;
                foreach (var old in _items.Take(drop)) _keys.Remove(old.Ts);
                _items.RemoveRange(0, drop);
                Rewrite();
            }
            else if (_dirty || !File.Exists(_path))
            {
                // The file was lost or a write failed: rewrite everything still in memory.
                Rewrite();
            }
            else
            {
                Persist(() => File.AppendAllText(_path, JsonSerializer.Serialize(hb, Json.Options) + "\n"));
            }
            return true;
        }
    }

    public void Remove(IEnumerable<Heartbeat> sent)
    {
        lock (_lock)
        {
            var gone = sent.Select(h => h.Ts).ToHashSet();
            if (_items.RemoveAll(h => gone.Contains(h.Ts)) == 0) return;
            _keys.ExceptWith(gone);
            Rewrite();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        IEnumerable<string> fileLines;
        try
        {
            fileLines = File.ReadAllLines(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var line in fileLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Heartbeat? hb;
            try
            {
                hb = JsonSerializer.Deserialize<Heartbeat>(line, Json.Options);
            }
            catch (JsonException)
            {
                continue; // torn last line after a crash
            }
            if (hb is not null && _keys.Add(hb.Ts)) _items.Add(hb);
        }
        _items.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        if (_items.Count > _maxItems)
        {
            var drop = _items.Count - _maxItems;
            foreach (var old in _items.Take(drop)) _keys.Remove(old.Ts);
            _items.RemoveRange(0, drop);
        }
        Rewrite();
    }

    private void Rewrite()
    {
        var lines = _items.Select(h => JsonSerializer.Serialize(h, Json.Options));
        Persist(() => AtomicFile.WriteAllText(_path, _items.Count == 0 ? "" : string.Join("\n", lines) + "\n"));
    }

    /// <summary>Disk errors never lose a minute: the in-memory queue is the source of truth while running.</summary>
    private void Persist(Action write)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            write();
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dirty = true;
        }
    }
}
