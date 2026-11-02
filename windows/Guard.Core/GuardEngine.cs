using Microsoft.Extensions.Logging;

namespace ParentControl.Core;

/// <summary>What the platform layer observed on the console session this minute.</summary>
public sealed record SessionSample(
    string State,
    string? Sid = null,
    string? User = null,
    string? Exe = null,
    string? Product = null,
    string? Title = null,
    uint? SessionId = null);

public interface ISessionProbe
{
    SessionSample Sample();
}

public enum SyncOutcome { NotRegistered, Ok, Offline, Unauthorized }

public enum RequestTimeResult { Queued, AlreadyPending, NotAllowed }

/// <summary>A child's request as the PC sees it: still uploading, or waiting on the server.</summary>
public sealed record OpenRequest(int Minutes, bool Uploaded, string Kind = RequestKinds.Time);

/// <summary>
/// Platform-neutral core of the guard: records one heartbeat per minute,
/// keeps the local minute log, and syncs with the server.
/// </summary>
public sealed class GuardEngine
{
    public const int BatchSize = 500;
    private const int MaxBatchesPerSync = 10;

    public static readonly TimeSpan BeaconEvery = TimeSpan.FromMinutes(10);

    private readonly IGuardApi _api;
    private readonly HeartbeatQueue _queue;
    private readonly StateStore _stateStore;
    private readonly CredentialStore _credStore;
    private readonly IBackupStore _backup;
    private readonly TamperQueue _tamper;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly string _clientVersion;
    private DeviceCredentials? _creds;
    private DateTimeOffset? _lastMinute;
    private string? _lastLoggedError;
    private DateTimeOffset _lastErrorLogAt;

    public GuardEngine(
        IGuardApi api, HeartbeatQueue queue, StateStore stateStore, CredentialStore credStore,
        TimeProvider time, ILogger<GuardEngine> log, string clientVersion,
        IBackupStore? backup = null, TamperQueue? tamper = null, bool readOnly = false)
    {
        _api = api;
        _queue = queue;
        _stateStore = stateStore;
        _credStore = credStore;
        _backup = backup ?? NullBackupStore.Instance;
        _tamper = tamper ?? new TamperQueue(null);
        _time = time;
        _log = log;
        _clientVersion = clientVersion;
        State = stateStore.Load();
        if (readOnly)
        {
            // Inspection only (status command): never write while the service owns the files.
            _creds = credStore.Load() ?? SafeLoadBackup()?.Credentials;
            return;
        }
        RestoreFromBackup();
    }

    public GuardState State { get; }
    public int QueueLength => _queue.Count;
    public int TamperQueueLength => _tamper.Count;

    /// <summary>This PC can talk to the server as a registered device.</summary>
    public bool HasCredentials => _creds is not null;

    /// <summary>Registration is gone, but the PC used to be set up (it still knows the server's settings).</summary>
    public bool ConfigLost => _creds is null && State.LastSync is not null;

    // ---------------- backup and restore ----------------

    /// <summary>
    /// Brings back whatever is missing from the data folder using the backup, and reports it.
    /// Usage minutes are merged both ways, so the larger count always wins.
    /// </summary>
    private void RestoreFromBackup()
    {
        var backup = SafeLoadBackup();
        var restored = new List<string>();

        _creds = _credStore.Load();
        if (_creds is null && backup?.Credentials is { } bc)
        {
            _creds = bc;
            TrySaveCredentials(bc);
            restored.Add("device.json");
        }

        if (State.LastSync is null && backup?.LastSync is not null)
        {
            State.LastSync = backup.LastSync;
            State.LastSyncAt = backup.LastSyncAt;
            restored.Add("state.json");
        }

        if (backup is not null)
        {
            foreach (var (date, bySid) in backup.Usage)
            {
                foreach (var (sid, ranges) in bySid)
                {
                    foreach (var m in UsageRanges.FromRanges(ranges))
                    {
                        State.AddUsage(date, sid, DateTimeOffset.FromUnixTimeSeconds(m * 60));
                    }
                }
            }
            if (backup.TamperLocked && !State.TamperLocked)
            {
                State.TamperLocked = true;
                State.TamperLockedAt ??= _time.GetUtcNow();
            }
        }

        if (_creds is not null) State.KnownDeviceId = _creds.DeviceId;
        State.KnownDeviceId ??= backup?.DeviceId;

        if (restored.Count > 0)
        {
            _log.LogWarning("Restored from backup: {Files}", string.Join(", ", restored));
            ReportTamper(TamperTypes.ConfigRestored, new() { ["files"] = string.Join(",", restored) });
        }
        else
        {
            SaveState();
        }
    }

    private GuardBackup? SafeLoadBackup()
    {
        try
        {
            return _backup.Load();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Cannot read backup: {Message}", ex.Message);
            return null;
        }
    }

    private void SaveBackup()
    {
        try
        {
            _backup.Save(new GuardBackup
            {
                Credentials = _creds,
                DeviceId = _creds?.DeviceId ?? State.KnownDeviceId,
                LastSync = State.LastSync,
                LastSyncAt = State.LastSyncAt,
                Usage = UsageRanges.Pack(State.Usage),
                TamperLocked = State.TamperLocked,
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning("Cannot write backup: {Message}", ex.Message);
        }
    }

    private void TrySaveCredentials(DeviceCredentials creds)
    {
        try
        {
            _credStore.Save(creds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Cannot write device.json: {Message}", ex.Message);
        }
    }

    /// <summary>Saves state.json and the backup; a vanished state.json is reported once it is noticed.</summary>
    private void SaveState()
    {
        try
        {
            _stateStore.Save(State);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Cannot write state.json: {Message}", ex.Message);
        }
        SaveBackup();
    }

    /// <summary>
    /// Re-reads device.json: a newer registration (register command) wins; a deleted file is rewritten from memory.
    /// </summary>
    private void RefreshCredentials()
    {
        var file = _credStore.Load();
        if (file is not null)
        {
            if (file != _creds)
            {
                if (_creds is not null) _log.LogInformation("Device registration changed to {Id}", file.DeviceId);
                _creds = file;
                State.KnownDeviceId = file.DeviceId;
                SaveBackup();
            }
            return;
        }
        if (_creds is null) return;

        _log.LogWarning("device.json is missing; restoring it");
        TrySaveCredentials(_creds);
        ReportTamper(TamperTypes.ConfigRestored, new() { ["files"] = "device.json" });
    }

    /// <summary>Reports a change of the Windows time zone (a standard user can change it to shift the allowed hours).</summary>
    public void NoteTimeZone(string? zone, string? user)
    {
        if (string.IsNullOrEmpty(zone) || zone == State.TimeZone) return;
        var previous = State.TimeZone;
        State.TimeZone = zone;
        if (previous is null)
        {
            SaveState();
            return;
        }
        ReportTamper(TamperTypes.ClockChanged, new() { ["kind"] = "timezone", ["from"] = previous, ["to"] = zone }, user);
    }

    // ---------------- tamper ----------------

    /// <summary>Queues a tamper event and, for lock events, locks child accounts unless the admin chose "notify".</summary>
    public void ReportTamper(string type, Dictionary<string, string?>? details = null, string? user = null) =>
        Ingest(new TamperEvent
        {
            ClientId = Guid.NewGuid(),
            OccurredAt = _time.GetUtcNow(),
            Type = type,
            Details = details,
            User = user,
        });

    /// <summary>Queues an event produced here or by a helper process (watchdog inbox).</summary>
    public void Ingest(TamperEvent e)
    {
        _tamper.Enqueue(e);
        _log.LogWarning("Tamper event {Type} {Details}", e.Type,
            e.Details is null ? "" : string.Join(", ", e.Details.Select(kv => $"{kv.Key}={kv.Value}")));

        var action = State.LastSync?.TamperAction ?? TamperActions.Lock;
        if (TamperTypes.IsLockEvent(e.Type) && action == TamperActions.Lock && !State.TamperLocked)
        {
            State.TamperLocked = true;
            State.TamperLockedAt = _time.GetUtcNow();
            _log.LogWarning("Tamper lock is on");
        }
        SaveState();
    }

    /// <summary>Local override by an administrator on this PC (clear-tamper command); the server is told too.</summary>
    public void ClearTamperLocally(string? by)
    {
        var was = State.TamperLocked;
        State.TamperLocked = false;
        State.TamperLockedAt = null;
        _log.LogWarning("Tamper lock cleared locally (was {Was})", was ? "on" : "off");
        // Not a lock event: queue it without re-locking.
        _tamper.Enqueue(new TamperEvent
        {
            ClientId = Guid.NewGuid(),
            OccurredAt = _time.GetUtcNow(),
            Type = TamperTypes.TamperClearedLocally,
            User = by,
        });
        SaveState();
    }

    public DateOnly LocalToday => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    public static DateTimeOffset TruncateToMinute(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);

    /// <summary>Records the current minute once; returns null if this minute was already recorded.</summary>
    public Heartbeat? RecordMinute(SessionSample sample)
    {
        var minute = TruncateToMinute(_time.GetUtcNow());
        if (_lastMinute == minute) return null;

        var localDate = LocalToday;
        var hb = new Heartbeat
        {
            Ts = minute,
            LocalDate = localDate,
            State = sample.State,
            Sid = sample.Sid,
            User = sample.User,
            Exe = sample.Exe,
            Product = sample.Product,
            Title = sample.Title,
        };
        if (!_queue.Enqueue(hb))
        {
            _lastMinute = minute;
            return null;
        }
        _lastMinute = minute;

        if (HeartbeatStates.IsCounted(hb.State) && hb.Sid is not null)
        {
            State.AddUsage(localDate, hb.Sid, minute);
        }
        State.PruneUsage(localDate);
        if (!_stateStore.Exists)
        {
            // The service writes state.json every minute, so a missing file means someone deleted it.
            ReportTamper(TamperTypes.ConfigRestored, new() { ["files"] = "state.json" }, sample.User);
        }
        else
        {
            SaveState();
        }
        return hb;
    }

    /// <summary>Uploads queued heartbeats and refreshes settings; never throws on network errors.</summary>
    public async Task<SyncOutcome> SyncAsync(CancellationToken ct)
    {
        RefreshCredentials();
        var creds = _creds;
        if (creds is null)
        {
            await BeaconIfLostAsync(ct);
            return SyncOutcome.NotRegistered;
        }

        State.LastSyncAttemptAt = _time.GetUtcNow();
        try
        {
            // Tamper events first, so the sync answer already reflects a lock they cause.
            var events = _tamper.Snapshot();
            if (events.Count > 0)
            {
                var report = await _api.ReportAsync(creds, events, ct);
                _tamper.Remove(events);
                _log.LogInformation("Reported {Count} tamper event(s), server lock: {Locked}", events.Count, report.TamperLocked);
            }

            SyncResponse? last = null;
            for (var i = 0; i < MaxBatchesPerSync; i++)
            {
                var batch = _queue.Peek(BatchSize);
                var acks = State.PendingAcks.ToList();
                var requests = State.PendingRequests.ToList();
                var request = new SyncRequest
                {
                    DeviceId = creds.DeviceId,
                    Secret = creds.Secret,
                    ClientVersion = _clientVersion,
                    ClientNow = _time.GetUtcNow(),
                    LocalDate = LocalToday,
                    Heartbeats = batch,
                    AckedCommands = acks,
                    TimeRequests = requests,
                };
                last = await _api.SyncAsync(request, ct);
                _queue.Remove(batch);
                State.PendingAcks.RemoveAll(acks.Contains);
                State.PendingRequests.RemoveAll(requests.Contains);
                if (_queue.Count == 0 || batch.Count < BatchSize) break;
            }

            State.LastSync = last;
            State.LastSyncAt = _time.GetUtcNow();
            State.LastSyncError = null;
            ApplyServerTamperLock(last);
            if (State.Unauthorized) _log.LogInformation("Device accepted by server again");
            State.Unauthorized = false;
            _lastLoggedError = null;
            return SyncOutcome.Ok;
        }
        catch (DeviceUnauthorizedException ex)
        {
            if (!State.Unauthorized)
            {
                _log.LogError("Server rejected the device secret: {Message}. Re-register with a new code.", ex.Message);
            }
            State.Unauthorized = true;
            State.LastSyncError = ex.Message;
            return SyncOutcome.Unauthorized;
        }
        catch (ApiException ex)
        {
            State.LastSyncError = ex.Message;
            var now = _time.GetUtcNow();
            if (ex.Message != _lastLoggedError || now - _lastErrorLogAt > TimeSpan.FromMinutes(30))
            {
                _log.LogWarning("Sync failed, working offline: {Message} (queued: {Count})", ex.Message, _queue.Count);
                _lastLoggedError = ex.Message;
                _lastErrorLogAt = now;
            }
            return SyncOutcome.Offline;
        }
        finally
        {
            SaveState();
        }
    }

    /// <summary>The server is the authority once our events are uploaded; offline, the local lock stays.</summary>
    private void ApplyServerTamperLock(SyncResponse? r)
    {
        if (r is null) return;
        if (r.TamperLocked && !State.TamperLocked)
        {
            State.TamperLocked = true;
            State.TamperLockedAt = _time.GetUtcNow();
            _log.LogWarning("Server reports a tamper lock");
        }
        else if (!r.TamperLocked && State.TamperLocked && _tamper.Count == 0)
        {
            State.TamperLocked = false;
            State.TamperLockedAt = null;
            _log.LogInformation("Tamper lock cleared by the server");
        }
    }

    /// <summary>Tells the server the PC lost its registration (at most every 10 minutes).</summary>
    private async Task BeaconIfLostAsync(CancellationToken ct)
    {
        if (!ConfigLost) return;
        var now = _time.GetUtcNow();
        if (State.LastBeaconAt is { } at && now - at < BeaconEvery) return;
        State.LastBeaconAt = now;
        try
        {
            await _api.BeaconAsync(Environment.MachineName, "device.json and backup missing", State.KnownDeviceId, ct);
            _log.LogWarning("Registration lost; sent a beacon to the server");
        }
        catch (ApiException ex)
        {
            _log.LogWarning("Beacon failed: {Message}", ex.Message);
        }
        catch (DeviceUnauthorizedException ex)
        {
            _log.LogWarning("Beacon rejected: {Message}", ex.Message);
        }
        SaveState();
    }

    // ---------------- time requests ----------------

    /// <summary>Queues a request for more minutes for the child bound to <paramref name="sid"/>.</summary>
    public RequestTimeResult RequestTime(string? sid, int minutes, string? reason, string kind = RequestKinds.Time)
    {
        kind = kind == RequestKinds.Unlock ? RequestKinds.Unlock : RequestKinds.Time;
        var child = ChildOf(sid);
        if (child is null || sid is null) return RequestTimeResult.NotAllowed;
        if (OpenRequestFor(child.Value) is not null) return RequestTimeResult.AlreadyPending;

        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason is { Length: > 200 }) reason = reason[..200];
        State.PendingRequests.Add(new TimeRequestOut(Guid.NewGuid(), sid, Math.Clamp(minutes, 5, 240), reason, kind));
        SaveState();
        _log.LogInformation("Request queued ({Kind}): {Minutes} min for child {Child}", kind, minutes, child);
        return RequestTimeResult.Queued;
    }

    /// <summary>
    /// A request is queued, or uploaded and still pending for less than <paramref name="maxWait"/>:
    /// worth polling the server more often so the answer reaches the child quickly.
    /// Not while offline (each attempt would just wait for the network timeout).
    /// </summary>
    public bool AwaitingDecision(TimeSpan maxWait)
    {
        if (_creds is null || State.LastSyncError is not null) return false;
        if (State.PendingRequests.Count > 0) return true;
        var now = _time.GetUtcNow();
        return (State.LastSync?.Requests ?? new List<RequestInfo>())
            .Any(r => r.Status == RequestStatuses.Pending && now - r.CreatedAt < maxWait);
    }

    public OpenRequest? OpenRequestFor(Guid childId)
    {
        var local = State.PendingRequests.FirstOrDefault(r => ChildOf(r.Sid) == childId);
        if (local is not null) return new OpenRequest(local.Minutes, Uploaded: false, local.Kind);
        var server = State.LastSync?.Requests.FirstOrDefault(r => r.ChildId == childId && r.Status == RequestStatuses.Pending);
        return server is null ? null : new OpenRequest(server.Minutes, Uploaded: true, server.Kind);
    }

    /// <summary>Decided requests of the child whose result has not been shown yet.</summary>
    public IReadOnlyList<RequestInfo> UnnotifiedResults(Guid childId) =>
        (State.LastSync?.Requests ?? new List<RequestInfo>())
            .Where(r => r.ChildId == childId && r.Status != RequestStatuses.Pending && !State.NotifiedRequests.Contains(r.Id))
            .ToList();

    public void MarkRequestNotified(long id)
    {
        if (!State.NotifiedRequests.Add(id)) return;
        // Server keeps decided requests for 24h; drop ids it no longer sends.
        var live = (State.LastSync?.Requests ?? new List<RequestInfo>()).Select(r => r.Id).ToHashSet();
        State.NotifiedRequests.RemoveWhere(x => x != id && !live.Contains(x));
        SaveState();
    }

    // ---------------- admin messages ----------------

    public IReadOnlyList<MessageInfo> UnshownMessages(Guid childId) =>
        (State.LastSync?.Messages ?? new List<MessageInfo>())
            .Where(m => m.ChildId == childId && !State.ShownMessages.Contains(m.Id))
            .ToList();

    /// <summary>Marks a message as shown and acknowledges it to the server on the next sync.</summary>
    public void MarkMessageShown(long id)
    {
        if (!State.ShownMessages.Add(id)) return;
        if (!State.PendingAcks.Contains(id)) State.PendingAcks.Add(id);
        if (State.ShownMessages.Count > 200)
        {
            foreach (var old in State.ShownMessages.Order().Take(State.ShownMessages.Count - 200).ToList())
            {
                State.ShownMessages.Remove(old);
            }
        }
        SaveState();
    }

    // ---------------- queries used by enforcement and status ----------------

    /// <summary>Child bound to a Windows account, per the last server answer.</summary>
    public Guid? ChildOf(string? sid) =>
        sid is null ? null : State.LastSync?.Accounts.FirstOrDefault(a => a.Sid == sid)?.ChildId;

    public IReadOnlySet<string> SidsOf(Guid childId) =>
        (State.LastSync?.Accounts ?? new List<AccountInfo>())
            .Where(a => a.ChildId == childId)
            .Select(a => a.Sid)
            .ToHashSet();

    public DayInfo? DayOf(Guid childId, DateOnly date) =>
        State.LastSync?.Children.FirstOrDefault(c => c.ChildId == childId)?.Days.FirstOrDefault(d => d.LocalDate == date);

    /// <summary>
    /// Minutes the child has used on <paramref name="date"/>:
    /// max(local count on this PC, last server total + minutes queued since then).
    /// </summary>
    public int SpentMinutes(Guid childId, DateOnly date)
    {
        var sids = SidsOf(childId);
        var local = State.LocalMinutes(date, sids);
        var server = DayOf(childId, date)?.SpentMinutes;
        if (server is null) return local;

        var pending = _queue.Snapshot()
            .Where(h => h.LocalDate == date && HeartbeatStates.IsCounted(h.State) && h.Sid is not null && sids.Contains(h.Sid))
            .Select(h => h.Ts)
            .Distinct()
            .Count();
        return Math.Max(local, server.Value + pending);
    }
}
