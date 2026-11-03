using ParentControl.Core;

namespace ParentControl.Core.Tests;

/// <summary>Controllable clock in a fixed time zone (Europe/Zurich-like, UTC+1, no DST).</summary>
internal sealed class FakeTime : TimeProvider
{
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");

    public DateTimeOffset UtcNow { get; set; }

    public FakeTime(DateTimeOffset utcNow) => UtcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
    public override TimeZoneInfo LocalTimeZone => Zone;

    public void Advance(TimeSpan by) => UtcNow += by;
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Scriptable server: records requests, returns a response or throws.</summary>
internal sealed class FakeApi : IGuardApi
{
    public List<SyncRequest> Requests { get; } = [];
    public Func<SyncRequest, SyncResponse> Respond { get; set; } = _ => new SyncResponse();
    public Exception? Throw { get; set; }

    public Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (Throw is not null) throw Throw;
        return Task.FromResult(Respond(request));
    }

    public Task<RegisterResult> RegisterAsync(string code, string machineName, string clientVersion, CancellationToken ct) =>
        Task.FromResult(new RegisterResult(Guid.NewGuid(), "secret"));

    public List<IReadOnlyList<TamperEvent>> Reports { get; } = [];
    public bool ServerTamperLocked { get; set; }
    public List<(string Machine, string Reason, Guid? DeviceId)> Beacons { get; } = [];

    public Task<ReportResult> ReportAsync(DeviceCredentials creds, IReadOnlyList<TamperEvent> events, CancellationToken ct)
    {
        if (Throw is not null) throw Throw;
        Reports.Add(events);
        return Task.FromResult(new ReportResult(events.Count, ServerTamperLocked));
    }

    public bool AutoUpdate { get; set; } = true;

    public Task<UpdatePolicy> UpdatePolicyAsync(DeviceCredentials creds, CancellationToken ct) =>
        Throw is not null ? Task.FromException<UpdatePolicy>(Throw) : Task.FromResult(new UpdatePolicy(AutoUpdate));

    public Task BeaconAsync(string machineName, string reason, Guid? deviceId, CancellationToken ct)
    {
        if (Throw is not null) throw Throw;
        Beacons.Add((machineName, reason, deviceId));
        return Task.CompletedTask;
    }
}

internal static class Hb
{
    public static Heartbeat At(DateTimeOffset ts, string state = HeartbeatStates.Active, string? sid = "S-1") => new()
    {
        Ts = ts,
        LocalDate = DateOnly.FromDateTime(ts.ToOffset(TimeSpan.FromHours(1)).DateTime),
        State = state,
        Sid = sid,
        User = "misha",
    };
}
