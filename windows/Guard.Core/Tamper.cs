using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParentControl.Core;

/// <summary>Event types sent to device_report (must match fn_is_lock_event on the server).</summary>
public static class TamperTypes
{
    public const string ConfigMissing = "config_missing";
    public const string ConfigRestored = "config_restored";
    public const string DataTampered = "data_tampered";
    public const string AclFixed = "acl_fixed";
    public const string ServiceDaclFixed = "service_dacl_fixed";
    public const string SafeBootFixed = "safeboot_fixed";
    public const string WatchdogFixed = "watchdog_fixed";
    public const string ClockChanged = "clock_changed";
    public const string ServiceStarted = "service_started";
    public const string TrayUnresponsive = "tray_unresponsive";
    public const string TamperClearedLocally = "tamper_cleared_locally";
    public const string UpdateFailed = "update_failed";
    public const string LoginBlocked = "login_blocked";

    private static readonly HashSet<string> LockTypes =
    [
        ConfigMissing, ConfigRestored, DataTampered, AclFixed, ServiceDaclFixed, SafeBootFixed, WatchdogFixed, ClockChanged,
    ];

    /// <summary>Events that put the device into the tamper lock (when the admin chose "lock").</summary>
    public static bool IsLockEvent(string type) => LockTypes.Contains(type);
}

public static class TamperActions
{
    public const string Lock = "lock";
    public const string Notify = "notify";
}

/// <summary>Something that looks like an attempt to get around the guard.</summary>
public sealed record TamperEvent
{
    public required Guid ClientId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string Type { get; init; }
    public Dictionary<string, string?>? Details { get; init; }
    public string? User { get; init; }
}

public sealed record ReportResult(int Accepted, bool TamperLocked);

/// <summary>
/// Tamper events waiting for upload (tamper.jsonl). Kept in memory as well,
/// so deleting the file does not lose them while the service runs.
/// </summary>
public sealed class TamperQueue
{
    public const int Capacity = 500;

    private readonly string? _path;
    private readonly List<TamperEvent> _items = [];
    private readonly object _lock = new();

    /// <param name="path">File to persist to; null keeps the queue in memory only.</param>
    public TamperQueue(string? path)
    {
        _path = path;
        if (path is null) return;
        var text = AtomicFile.TryReadAllText(path);
        if (string.IsNullOrEmpty(text)) return;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (JsonSerializer.Deserialize<TamperEvent>(line, Json.Options) is { } e) _items.Add(e);
            }
            catch (JsonException)
            {
                // Skip a broken line, keep the rest.
            }
        }
    }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public void Enqueue(TamperEvent e)
    {
        lock (_lock)
        {
            _items.Add(e);
            // Keep the newest events if the PC stays offline for very long.
            if (_items.Count > Capacity) _items.RemoveRange(0, _items.Count - Capacity);
            Persist();
        }
    }

    public IReadOnlyList<TamperEvent> Snapshot()
    {
        lock (_lock) return _items.ToList();
    }

    public void Remove(IReadOnlyCollection<TamperEvent> sent)
    {
        lock (_lock)
        {
            var ids = sent.Select(e => e.ClientId).ToHashSet();
            _items.RemoveAll(e => ids.Contains(e.ClientId));
            Persist();
        }
    }

    private void Persist()
    {
        if (_path is null) return;
        try
        {
            AtomicFile.WriteAllText(_path, string.Concat(_items.Select(e => JsonSerializer.Serialize(e, Json.Options) + "\n")));
        }
        catch (IOException)
        {
            // The in-memory copy is still uploaded.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

// ---------------- backup outside the data folder ----------------

/// <summary>
/// Copy of what the guard must not lose: registration, last server answer, today's minutes, tamper lock.
/// The service keeps it in the registry so deleting the data folder does not reset anything.
/// </summary>
public sealed record GuardBackup
{
    public DeviceCredentials? Credentials { get; init; }
    /// <summary>Kept apart from the secret so a lost registration can still be named in the beacon.</summary>
    public Guid? DeviceId { get; init; }
    public SyncResponse? LastSync { get; init; }
    public DateTimeOffset? LastSyncAt { get; init; }
    /// <summary>Counted minutes as [first, last] unix-minute ranges per local date and SID.</summary>
    public Dictionary<DateOnly, Dictionary<string, List<long[]>>> Usage { get; init; } = [];
    public bool TamperLocked { get; init; }
}

public interface IBackupStore
{
    GuardBackup? Load();
    void Save(GuardBackup backup);
}

/// <summary>No backup (tests, console tools).</summary>
public sealed class NullBackupStore : IBackupStore
{
    public static readonly NullBackupStore Instance = new();
    public GuardBackup? Load() => null;
    public void Save(GuardBackup backup) { }
}

/// <summary>Backup kept in memory; lets tests simulate a wiped data folder.</summary>
public sealed class MemoryBackupStore : IBackupStore
{
    public GuardBackup? Value { get; set; }
    public GuardBackup? Load() => Value;
    public void Save(GuardBackup backup) => Value = backup;
}

public static class UsageRanges
{
    public static List<long[]> ToRanges(IEnumerable<long> minutes)
    {
        var result = new List<long[]>();
        foreach (var m in minutes.Order())
        {
            if (result.Count > 0 && result[^1][1] + 1 >= m)
            {
                result[^1][1] = Math.Max(result[^1][1], m);
            }
            else
            {
                result.Add([m, m]);
            }
        }
        return result;
    }

    public static IEnumerable<long> FromRanges(IEnumerable<long[]> ranges)
    {
        foreach (var r in ranges)
        {
            // Ignore malformed or absurd ranges (more than a day).
            if (r.Length != 2 || r[1] < r[0] || r[1] - r[0] > 1440) continue;
            for (var m = r[0]; m <= r[1]; m++) yield return m;
        }
    }

    public static Dictionary<DateOnly, Dictionary<string, List<long[]>>> Pack(
        Dictionary<DateOnly, Dictionary<string, SortedSet<long>>> usage) =>
        usage.ToDictionary(d => d.Key, d => d.Value.ToDictionary(s => s.Key, s => ToRanges(s.Value)));
}

// ---------------- clock ----------------

/// <summary>
/// Detects jumps of the wall clock against the monotonic tick count (which keeps counting during sleep).
/// </summary>
public sealed class ClockWatch(TimeProvider time, Func<long> tickMs, TimeSpan threshold)
{
    private DateTimeOffset? _wall;
    private long _tick;

    /// <summary>Returns the jump (wall minus monotonic) if it exceeds the threshold; null otherwise.</summary>
    public TimeSpan? Check()
    {
        var wall = time.GetUtcNow();
        var tick = tickMs();
        TimeSpan? jump = null;
        if (_wall is { } lastWall)
        {
            var diff = (wall - lastWall) - TimeSpan.FromMilliseconds(tick - _tick);
            if (diff.Duration() > threshold) jump = diff;
        }
        _wall = wall;
        _tick = tick;
        return jump;
    }
}

// ---------------- accounts ----------------

/// <summary>Tells whether a Windows account is a local administrator.</summary>
public interface IAdminCheck
{
    bool IsAdmin(string sid);
}

public sealed class NoAdmins : IAdminCheck
{
    public static readonly NoAdmins Instance = new();
    public bool IsAdmin(string sid) => false;
}

// ---------------- built-in server settings ----------------

/// <summary>Server address compiled into the exe, so a missing appsettings.json cannot switch the guard off.</summary>
public static class BuiltInConfig
{
    public const string SupabaseUrl = "https://opufwbpkuofxhfmlqhqq.supabase.co";
    // Public anon key (the same one the admin web app ships); it only allows calling the device RPCs.
    public const string AnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9wdWZ3YnBrdW9meGhmbWxxaHFxIiwicm9sZSI6ImFub24iLCJpYXQiOjE3OTM0ODE2NDcsImV4cCI6MjEwOTA1NzY0N30.LPBLawYAVFyGB-aawM8kJshaeyvcAdjHsFE1SKa3mbU";
    public const string Schema = "parent_control";

    /// <summary>Fills whatever the config file did not set.</summary>
    public static GuardOptions ApplyTo(GuardOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.SupabaseUrl)) o.SupabaseUrl = SupabaseUrl;
        if (string.IsNullOrWhiteSpace(o.AnonKey)) o.AnonKey = AnonKey;
        if (string.IsNullOrWhiteSpace(o.Schema)) o.Schema = Schema;
        return o;
    }
}
