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
public sealed record OpenRequest(int Minutes, bool Uploaded);

/// <summary>
/// Platform-neutral core of the guard: records one heartbeat per minute,
/// keeps the local minute log, and syncs with the server.
/// </summary>
public sealed class GuardEngine
{
    public const int BatchSize = 500;
    private const int MaxBatchesPerSync = 10;

    private readonly IGuardApi _api;
    private readonly HeartbeatQueue _queue;
    private readonly StateStore _stateStore;
    private readonly CredentialStore _credStore;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly string _clientVersion;
    private DateTimeOffset? _lastMinute;
    private string? _lastLoggedError;
    private DateTimeOffset _lastErrorLogAt;

    public GuardEngine(
        IGuardApi api, HeartbeatQueue queue, StateStore stateStore, CredentialStore credStore,
        TimeProvider time, ILogger<GuardEngine> log, string clientVersion)
    {
        _api = api;
        _queue = queue;
        _stateStore = stateStore;
        _credStore = credStore;
        _time = time;
        _log = log;
        _clientVersion = clientVersion;
        State = stateStore.Load();
    }

    public GuardState State { get; }
    public int QueueLength => _queue.Count;

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
        _stateStore.Save(State);
        return hb;
    }

    /// <summary>Uploads queued heartbeats and refreshes settings; never throws on network errors.</summary>
    public async Task<SyncOutcome> SyncAsync(CancellationToken ct)
    {
        var creds = _credStore.Load();
        if (creds is null) return SyncOutcome.NotRegistered;

        State.LastSyncAttemptAt = _time.GetUtcNow();
        try
        {
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
            _stateStore.Save(State);
        }
    }

    // ---------------- time requests ----------------

    /// <summary>Queues a request for more minutes for the child bound to <paramref name="sid"/>.</summary>
    public RequestTimeResult RequestTime(string? sid, int minutes, string? reason)
    {
        var child = ChildOf(sid);
        if (child is null || sid is null) return RequestTimeResult.NotAllowed;
        if (OpenRequestFor(child.Value) is not null) return RequestTimeResult.AlreadyPending;

        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason is { Length: > 200 }) reason = reason[..200];
        State.PendingRequests.Add(new TimeRequestOut(Guid.NewGuid(), sid, Math.Clamp(minutes, 5, 240), reason));
        _stateStore.Save(State);
        _log.LogInformation("Time request queued: {Minutes} min for child {Child}", minutes, child);
        return RequestTimeResult.Queued;
    }

    public OpenRequest? OpenRequestFor(Guid childId)
    {
        var local = State.PendingRequests.FirstOrDefault(r => ChildOf(r.Sid) == childId);
        if (local is not null) return new OpenRequest(local.Minutes, Uploaded: false);
        var server = State.LastSync?.Requests.FirstOrDefault(r => r.ChildId == childId && r.Status == RequestStatuses.Pending);
        return server is null ? null : new OpenRequest(server.Minutes, Uploaded: true);
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
        _stateStore.Save(State);
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
        _stateStore.Save(State);
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
