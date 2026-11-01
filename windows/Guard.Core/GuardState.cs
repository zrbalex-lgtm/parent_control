using System.Text.Json;

namespace ParentControl.Core;

/// <summary>Persistent service state (state.json): last server answer + local minute log.</summary>
public sealed class GuardState
{
    /// <summary>Last successful device_sync answer, used offline.</summary>
    public SyncResponse? LastSync { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public DateTimeOffset? LastSyncAttemptAt { get; set; }
    public string? LastSyncError { get; set; }

    /// <summary>Set when the server rejects our secret; cleared by re-registration.</summary>
    public bool Unauthorized { get; set; }

    /// <summary>Counted minutes per PC-local date and Windows SID (unix minutes, for idempotency).</summary>
    public Dictionary<DateOnly, Dictionary<string, SortedSet<long>>> Usage { get; set; } = [];

    /// <summary>Executed commands to acknowledge on the next sync.</summary>
    public List<long> PendingAcks { get; set; } = [];

    public void AddUsage(DateOnly date, string sid, DateTimeOffset minute)
    {
        if (!Usage.TryGetValue(date, out var bySid)) Usage[date] = bySid = [];
        if (!bySid.TryGetValue(sid, out var minutes)) bySid[sid] = minutes = [];
        minutes.Add(minute.ToUnixTimeSeconds() / 60);
    }

    public int LocalMinutes(DateOnly date, IReadOnlySet<string> sids)
    {
        if (!Usage.TryGetValue(date, out var bySid)) return 0;
        // Union across SIDs: two accounts of one child never double count a minute.
        var all = new HashSet<long>();
        foreach (var (sid, minutes) in bySid)
        {
            if (sids.Contains(sid)) all.UnionWith(minutes);
        }
        return all.Count;
    }

    /// <summary>Keeps only the last few days of local usage.</summary>
    public void PruneUsage(DateOnly today, int keepDays = 3)
    {
        foreach (var d in Usage.Keys.Where(d => d < today.AddDays(-keepDays)).ToList()) Usage.Remove(d);
    }
}

public sealed class StateStore
{
    private readonly string _path;

    public StateStore(string path) => _path = path;

    public GuardState Load()
    {
        var text = AtomicFile.TryReadAllText(_path);
        if (string.IsNullOrWhiteSpace(text)) return new GuardState();
        try
        {
            return JsonSerializer.Deserialize<GuardState>(text, Json.Options) ?? new GuardState();
        }
        catch (JsonException)
        {
            // A corrupt state must not stop enforcement: start clean, keep a copy for debugging.
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch (IOException) { }
            return new GuardState();
        }
    }

    public void Save(GuardState state) =>
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(state, Json.Indented));
}

public sealed class CredentialStore
{
    private readonly string _path;

    public CredentialStore(string path) => _path = path;

    public DeviceCredentials? Load()
    {
        var text = AtomicFile.TryReadAllText(_path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonSerializer.Deserialize<DeviceCredentials>(text, Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(DeviceCredentials creds) =>
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(creds, Json.Indented));
}
