using Microsoft.Extensions.Logging.Abstractions;
using ParentControl.Core;

namespace ParentControl.Core.Tests;

public class GuardEngineTests : IDisposable
{
    private static readonly Guid Misha = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DeviceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 1, 14, 0, 30, TimeSpan.Zero)); // 15:00:30 local
    private readonly FakeApi _api = new();

    public void Dispose() => _dir.Dispose();

    private GuardEngine NewEngine(bool registered = true)
    {
        var creds = new CredentialStore(_dir.File("device.json"));
        if (registered) creds.Save(new DeviceCredentials(DeviceId, "s3cret"));
        return new GuardEngine(_api, new HeartbeatQueue(_dir.File("queue.jsonl")),
            new StateStore(_dir.File("state.json")), creds, _time, NullLogger<GuardEngine>.Instance, "0.1.0");
    }

    private static SyncResponse ServerSays(int spentToday, string sid = "S-1") => new()
    {
        ServerNow = DateTimeOffset.UtcNow,
        Accounts = [new AccountInfo(sid, "misha", Misha)],
        Children =
        [
            new ChildInfo(Misha, "Миша",
            [
                new DayInfo(new DateOnly(2026, 11, 1), 120, 0, "15:00", "20:00", spentToday),
                new DayInfo(new DateOnly(2026, 11, 2), 120, 0, "15:00", "20:00", 0),
            ]),
        ],
    };

    [Fact]
    public void Records_one_heartbeat_per_minute_with_local_date()
    {
        var e = NewEngine();
        var hb = e.RecordMinute(new SessionSample(HeartbeatStates.Active, "S-1", "misha"));
        Assert.NotNull(hb);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero), hb!.Ts);
        Assert.Equal(new DateOnly(2026, 11, 1), hb.LocalDate);
        Assert.Null(e.RecordMinute(new SessionSample(HeartbeatStates.Active, "S-1", "misha")));
        Assert.Equal(1, e.QueueLength);
    }

    [Fact]
    public void Local_date_follows_pc_time_zone_across_midnight()
    {
        _time.UtcNow = new DateTimeOffset(2026, 11, 1, 23, 10, 0, TimeSpan.Zero); // 00:10 local next day
        var hb = NewEngine().RecordMinute(new SessionSample(HeartbeatStates.Active, "S-1"));
        Assert.Equal(new DateOnly(2026, 11, 2), hb!.LocalDate);
    }

    [Fact]
    public async Task Sync_uploads_queue_and_caches_answer()
    {
        var e = NewEngine();
        _api.Respond = _ => ServerSays(10);
        e.RecordMinute(new SessionSample(HeartbeatStates.Active, "S-1"));

        Assert.Equal(SyncOutcome.Ok, await e.SyncAsync(default));
        var req = Assert.Single(_api.Requests);
        Assert.Equal(DeviceId, req.DeviceId);
        Assert.Equal(new DateOnly(2026, 11, 1), req.LocalDate);
        Assert.Single(req.Heartbeats);
        Assert.Equal(0, e.QueueLength);
        Assert.NotNull(e.State.LastSync);
        Assert.Equal(Misha, e.ChildOf("S-1"));
    }

    [Fact]
    public async Task Offline_keeps_queue_and_reports_offline()
    {
        var e = NewEngine();
        _api.Throw = new ApiException("network: no route");
        e.RecordMinute(new SessionSample(HeartbeatStates.Active, "S-1"));
        Assert.Equal(SyncOutcome.Offline, await e.SyncAsync(default));
        Assert.Equal(1, e.QueueLength);
        Assert.Equal("network: no route", e.State.LastSyncError);
    }

    [Fact]
    public async Task Unauthorized_sets_flag_and_success_clears_it()
    {
        var e = NewEngine();
        _api.Throw = new DeviceUnauthorizedException("unauthorized");
        Assert.Equal(SyncOutcome.Unauthorized, await e.SyncAsync(default));
        Assert.True(e.State.Unauthorized);

        _api.Throw = null;
        Assert.Equal(SyncOutcome.Ok, await e.SyncAsync(default));
        Assert.False(e.State.Unauthorized);
    }

    [Fact]
    public async Task Not_registered_does_not_call_server()
    {
        var e = NewEngine(registered: false);
        Assert.Equal(SyncOutcome.NotRegistered, await e.SyncAsync(default));
        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task Large_backlog_is_sent_in_batches()
    {
        var e = NewEngine();
        for (var i = 0; i < GuardEngine.BatchSize + 20; i++)
        {
            e.RecordMinute(new SessionSample(HeartbeatStates.Locked));
            _time.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(SyncOutcome.Ok, await e.SyncAsync(default));
        Assert.Equal(2, _api.Requests.Count);
        Assert.Equal(GuardEngine.BatchSize, _api.Requests[0].Heartbeats.Count);
        Assert.Equal(20, _api.Requests[1].Heartbeats.Count);
        Assert.Equal(0, e.QueueLength);
    }

    [Fact]
    public async Task Spent_offline_uses_server_total_plus_unsent_minutes()
    {
        var e = NewEngine();
        _api.Respond = _ => ServerSays(spentToday: 50); // includes other PCs
        await e.SyncAsync(default);

        _api.Throw = new ApiException("offline");
        for (var i = 0; i < 7; i++)
        {
            e.RecordMinute(new SessionSample(HeartbeatStates.Active, "S-1"));
            _time.Advance(TimeSpan.FromMinutes(1));
        }
        e.RecordMinute(new SessionSample(HeartbeatStates.Locked, "S-1")); // not counted
        await e.SyncAsync(default);

        Assert.Equal(57, e.SpentMinutes(Misha, new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public async Task Spent_never_below_local_count()
    {
        var e = NewEngine();
        for (var i = 0; i < 30; i++)
        {
            e.RecordMinute(new SessionSample(HeartbeatStates.Idle, "S-1"));
            _time.Advance(TimeSpan.FromMinutes(1));
        }
        // Server lags (e.g. stale answer) and reports less than we counted locally
        _api.Respond = r => ServerSays(spentToday: 5);
        await e.SyncAsync(default);
        Assert.Equal(30, e.SpentMinutes(Misha, new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public async Task Unbound_account_minutes_do_not_count_for_child()
    {
        var e = NewEngine();
        _api.Respond = _ => ServerSays(0);
        e.RecordMinute(new SessionSample(HeartbeatStates.Active, "S-PAPA", "papa"));
        await e.SyncAsync(default);
        Assert.Equal(0, e.SpentMinutes(Misha, new DateOnly(2026, 11, 1)));
        Assert.Null(e.ChildOf("S-PAPA"));
    }

    [Fact]
    public async Task State_survives_restart()
    {
        var e = NewEngine();
        _api.Respond = _ => ServerSays(42);
        e.RecordMinute(new SessionSample(HeartbeatStates.Active, "S-1"));
        await e.SyncAsync(default);

        var restarted = NewEngine();
        Assert.Equal(Misha, restarted.ChildOf("S-1"));
        Assert.Equal(42, restarted.SpentMinutes(Misha, new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void Acks_are_sent_once()
    {
        var e = NewEngine();
        e.State.PendingAcks.Add(13);
        e.SyncAsync(default).GetAwaiter().GetResult();
        e.SyncAsync(default).GetAwaiter().GetResult();
        Assert.Equal(new long[] { 13 }, _api.Requests[0].AckedCommands);
        Assert.Empty(_api.Requests[1].AckedCommands);
    }
}
