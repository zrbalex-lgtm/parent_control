using Microsoft.Extensions.Logging.Abstractions;
using ParentControl.Core;

namespace ParentControl.Core.Tests;

public class ResilienceTests : IDisposable
{
    private static readonly Guid Misha = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DeviceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateOnly Nov1 = new(2026, 11, 1);

    private readonly TempDir _dir = new();
    // 16:00 local (UTC+1)
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 1, 15, 0, 10, TimeSpan.Zero));
    private readonly FakeApi _api = new();
    private readonly MemoryBackupStore _backup = new();
    private readonly FakeAdmins _admins = new();

    public ResilienceTests()
    {
        _api.Respond = r => new SyncResponse
        {
            Accounts = [new AccountInfo("S-1", "misha", Misha), new AccountInfo("S-PAPA", "papa", null)],
            Children = [new ChildInfo(Misha, "Миша", [new DayInfo(Nov1, 120, 0, "00:00", "24:00", 0)])],
            TamperLocked = _api.ServerTamperLocked,
            TamperAction = TamperActions.Lock,
        };
    }

    public void Dispose() => _dir.Dispose();

    private sealed class FakeAdmins : IAdminCheck
    {
        public HashSet<string> Sids { get; } = [];
        public bool Fail { get; set; }
        public bool IsAdmin(string sid) => Fail ? throw new InvalidOperationException("lookup failed") : Sids.Contains(sid);
    }

    private GuardEngine Engine(bool register = false)
    {
        var creds = new CredentialStore(_dir.File("device.json"));
        if (register) creds.Save(new DeviceCredentials(DeviceId, "s3cret"));
        return new GuardEngine(_api, new HeartbeatQueue(_dir.File("queue.jsonl")), new StateStore(_dir.File("state.json")),
            creds, _time, NullLogger<GuardEngine>.Instance, "test", _backup, new TamperQueue(_dir.File("tamper.jsonl")));
    }

    private Policy PolicyFor(GuardEngine e) => new(e, _time, _admins);

    private static SessionSample Misha1 => new(HeartbeatStates.Active, "S-1", "misha", SessionId: 1);

    private void WipeDataFolder()
    {
        foreach (var f in Directory.GetFiles(_dir.Path)) File.Delete(f);
    }

    [Fact]
    public async Task Fresh_install_writes_a_backup_and_has_no_tamper_events()
    {
        var e = Engine(register: true);
        await e.SyncAsync(default);
        Assert.Equal(DeviceId, _backup.Value?.Credentials?.DeviceId);
        Assert.NotNull(_backup.Value?.LastSync);
        Assert.Empty(_api.Reports);
        Assert.False(e.State.TamperLocked);
    }

    [Fact]
    public async Task Wiped_data_folder_is_restored_reported_and_locks_the_child()
    {
        var e = Engine(register: true);
        await e.SyncAsync(default);
        e.RecordMinute(Misha1);
        _time.Advance(TimeSpan.FromMinutes(1));
        e.RecordMinute(Misha1);

        WipeDataFolder();
        var restarted = Engine();

        Assert.True(restarted.HasCredentials);
        Assert.True(File.Exists(_dir.File("device.json")));
        Assert.Equal(2, restarted.SpentMinutes(Misha, Nov1)); // minutes survive via the backup
        Assert.True(restarted.State.TamperLocked);
        Assert.Equal(LockReason.Tamper, PolicyFor(restarted).Evaluate("S-1").Lock);
        Assert.False(PolicyFor(restarted).Evaluate("S-PAPA").Restricted);

        _api.ServerTamperLocked = true;
        await restarted.SyncAsync(default);
        var reported = Assert.Single(_api.Reports);
        Assert.Equal(TamperTypes.ConfigRestored, Assert.Single(reported).Type);
        Assert.Equal(0, restarted.TamperQueueLength);
        Assert.True(restarted.State.TamperLocked);
    }

    [Fact]
    public async Task Server_clear_lifts_the_local_lock_only_after_events_are_uploaded()
    {
        var e = Engine(register: true);
        await e.SyncAsync(default);
        e.ReportTamper(TamperTypes.AclFixed);
        Assert.True(e.State.TamperLocked);

        // Offline: lock stays.
        _api.Throw = new ApiException("network");
        await e.SyncAsync(default);
        Assert.True(e.State.TamperLocked);
        Assert.Equal(1, e.TamperQueueLength);

        // Admin cleared it in the app.
        _api.Throw = null;
        _api.ServerTamperLocked = false;
        await e.SyncAsync(default);
        Assert.False(e.State.TamperLocked);
        Assert.False(_backup.Value!.TamperLocked);
    }

    [Fact]
    public async Task Notify_mode_does_not_lock_locally()
    {
        _api.Respond = _ => new SyncResponse
        {
            Accounts = [new AccountInfo("S-1", "misha", Misha)],
            Children = [new ChildInfo(Misha, "Миша", [new DayInfo(Nov1, null, 0, "00:00", "24:00", 0)])],
            TamperAction = TamperActions.Notify,
        };
        var e = Engine(register: true);
        await e.SyncAsync(default);
        e.ReportTamper(TamperTypes.ConfigRestored);
        Assert.False(e.State.TamperLocked);
        Assert.Equal(1, e.TamperQueueLength);
    }

    [Fact]
    public async Task Non_lock_events_never_lock()
    {
        var e = Engine(register: true);
        await e.SyncAsync(default);
        e.ReportTamper(TamperTypes.TrayUnresponsive);
        Assert.False(e.State.TamperLocked);
    }

    [Fact]
    public async Task Admins_are_never_blocked_by_tamper_or_lost_config()
    {
        var e = Engine(register: true);
        await e.SyncAsync(default);
        e.ReportTamper(TamperTypes.DataTampered);
        _admins.Sids.Add("S-1");
        Assert.Null(PolicyFor(e).Evaluate("S-1").Lock);

        // A failing lookup counts as admin: never lock out the repair account.
        _admins.Sids.Clear();
        _admins.Fail = true;
        Assert.Null(PolicyFor(e).Evaluate("S-1").Lock);
    }

    [Fact]
    public async Task Local_clear_tamper_unlocks_offline_and_tells_the_server()
    {
        var e = Engine(register: true);
        await e.SyncAsync(default);
        e.ReportTamper(TamperTypes.SafeBootFixed);
        _api.Throw = new ApiException("network");
        e.ClearTamperLocally("papa");
        Assert.False(e.State.TamperLocked);
        Assert.Null(PolicyFor(e).Evaluate("S-1").Lock);

        _api.Throw = null;
        await e.SyncAsync(default);
        var sent = Assert.Single(_api.Reports);
        Assert.Equal(new[] { TamperTypes.SafeBootFixed, TamperTypes.TamperClearedLocally }, sent.Select(x => x.Type).ToArray());
        Assert.False(e.State.TamperLocked);
    }

    [Fact]
    public async Task Deleted_device_json_is_rewritten_while_running()
    {
        var e = Engine(register: true);
        await e.SyncAsync(default);
        File.Delete(_dir.File("device.json"));

        await e.SyncAsync(default);
        Assert.True(File.Exists(_dir.File("device.json")));
        Assert.Contains(_api.Reports.SelectMany(r => r), x => x.Type == TamperTypes.ConfigRestored);
    }

    [Fact]
    public async Task Deleted_state_json_is_reported_on_the_next_minute()
    {
        var e = Engine(register: true);
        await e.SyncAsync(default);
        File.Delete(_dir.File("state.json"));
        e.RecordMinute(Misha1);
        Assert.True(File.Exists(_dir.File("state.json")));
        Assert.Equal(1, e.TamperQueueLength);
        Assert.Equal(1, e.SpentMinutes(Misha, Nov1)); // the minute still counts
    }

    [Fact]
    public async Task Minutes_still_count_when_the_whole_folder_vanishes_while_running()
    {
        var e = Engine(register: true);
        await e.SyncAsync(default);
        Directory.Delete(_dir.Path, recursive: true);
        e.RecordMinute(Misha1);
        Assert.Equal(1, e.SpentMinutes(Misha, Nov1));
        Assert.Equal(1, e.QueueLength);
    }

    [Fact]
    public async Task Lost_registration_blocks_only_known_child_accounts_and_sends_a_beacon()
    {
        var e = Engine(register: true);
        await e.SyncAsync(default);
        WipeDataFolder();
        _backup.Value = _backup.Value! with { Credentials = null, TamperLocked = false };

        var lost = Engine();
        Assert.True(lost.ConfigLost);
        Assert.Equal(LockReason.NotConfigured, PolicyFor(lost).Evaluate("S-1").Lock);
        Assert.False(PolicyFor(lost).Evaluate("S-PAPA").Restricted);
        Assert.False(PolicyFor(lost).Evaluate("S-UNKNOWN").Restricted);

        Assert.Equal(SyncOutcome.NotRegistered, await lost.SyncAsync(default));
        await lost.SyncAsync(default); // rate limited
        var beacon = Assert.Single(_api.Beacons);
        Assert.Equal(DeviceId, beacon.DeviceId);
        _time.Advance(GuardEngine.BeaconEvery);
        await lost.SyncAsync(default);
        Assert.Equal(2, _api.Beacons.Count);

        _admins.Sids.Add("S-1");
        Assert.Null(PolicyFor(lost).Evaluate("S-1").Lock);
    }

    [Fact]
    public async Task Never_registered_pc_blocks_nothing_and_sends_no_beacon()
    {
        var e = Engine();
        Assert.False(e.ConfigLost);
        Assert.Equal(SyncOutcome.NotRegistered, await e.SyncAsync(default));
        Assert.Empty(_api.Beacons);
        Assert.False(PolicyFor(e).Evaluate("S-1").Restricted);
    }

    [Fact]
    public void Fail_closed_locks_do_not_offer_requests()
    {
        Assert.False(LockReasons.AllowsRequest(LockReason.Tamper));
        Assert.False(LockReasons.AllowsRequest(nameof(LockReason.NotConfigured)));
        Assert.True(LockReasons.AllowsRequest(LockReason.Limit));
        Assert.True(LockReasons.AllowsRequest((string?)null));
    }

    [Fact]
    public async Task Time_zone_change_is_reported()
    {
        var e = Engine(register: true);
        await e.SyncAsync(default);
        e.NoteTimeZone("W. Europe Standard Time", "misha");
        Assert.Equal(0, e.TamperQueueLength); // first sighting
        e.NoteTimeZone("W. Europe Standard Time", "misha");
        e.NoteTimeZone("Hawaiian Standard Time", "misha");
        Assert.Equal(1, e.TamperQueueLength);
        Assert.True(e.State.TamperLocked);
    }

    [Fact]
    public void Tamper_queue_survives_restart()
    {
        var q = new TamperQueue(_dir.File("t.jsonl"));
        q.Enqueue(new TamperEvent { ClientId = Guid.NewGuid(), OccurredAt = _time.GetUtcNow(), Type = TamperTypes.AclFixed });
        Assert.Equal(1, new TamperQueue(_dir.File("t.jsonl")).Count);
    }

    [Fact]
    public void Clock_watch_flags_jumps_only()
    {
        long tick = 0;
        var watch = new ClockWatch(_time, () => tick, TimeSpan.FromMinutes(2));
        Assert.Null(watch.Check());
        _time.Advance(TimeSpan.FromSeconds(5)); tick += 5000;
        Assert.Null(watch.Check());
        _time.Advance(TimeSpan.FromHours(-3)); tick += 5000;
        Assert.True(watch.Check() is { } jump && jump < TimeSpan.FromHours(-2.9));
        _time.Advance(TimeSpan.FromSeconds(30)); tick += 5000; // small drift
        Assert.Null(watch.Check());
    }

    [Fact]
    public void Usage_ranges_round_trip()
    {
        long[] minutes = [1, 2, 3, 7, 9, 10];
        var ranges = UsageRanges.ToRanges(minutes);
        Assert.Equal(3, ranges.Count);
        Assert.Equal(minutes, UsageRanges.FromRanges(ranges).ToArray());
    }

    [Fact]
    public void Built_in_config_fills_missing_settings()
    {
        var o = BuiltInConfig.ApplyTo(new GuardOptions());
        Assert.StartsWith("https://", o.SupabaseUrl);
        Assert.NotEmpty(o.AnonKey);
        Assert.Equal("parent_control", o.Schema);
    }
}
