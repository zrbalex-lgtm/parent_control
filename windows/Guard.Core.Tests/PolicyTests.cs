using Microsoft.Extensions.Logging.Abstractions;
using ParentControl.Core;

namespace ParentControl.Core.Tests;

public class PolicyTests : IDisposable
{
    private static readonly Guid Misha = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateOnly Nov1 = new(2026, 11, 1);
    private static readonly DateOnly Nov2 = new(2026, 11, 2);

    private readonly TempDir _dir = new();
    // 16:00 local (UTC+1)
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 1, 15, 0, 10, TimeSpan.Zero));
    private readonly FakeApi _api = new();
    private readonly GuardEngine _engine;
    private readonly Policy _policy;

    public PolicyTests()
    {
        var creds = new CredentialStore(_dir.File("device.json"));
        creds.Save(new DeviceCredentials(Guid.NewGuid(), "s"));
        _engine = new GuardEngine(_api, new HeartbeatQueue(_dir.File("q.jsonl")), new StateStore(_dir.File("state.json")),
            creds, _time, NullLogger<GuardEngine>.Instance, "test");
        _policy = new Policy(_engine, _time);
    }

    public void Dispose() => _dir.Dispose();

    private void Server(int? limit = 120, int bonus = 0, string from = "15:00", string to = "20:00", int spent = 0,
        bool enabled = true, List<LockInfo>? locks = null, DayInfo? tomorrow = null)
    {
        _api.Respond = _ => new SyncResponse
        {
            DeviceEnabled = enabled,
            Accounts = [new AccountInfo("S-1", "misha", Misha), new AccountInfo("S-PAPA", "papa", null)],
            Children =
            [
                new ChildInfo(Misha, "Миша",
                [
                    new DayInfo(Nov1, limit, bonus, from, to, spent),
                    tomorrow ?? new DayInfo(Nov2, 60, 0, "10:00", "20:00", 0),
                ]),
            ],
            Locks = locks ?? [],
        };
        _engine.SyncAsync(default).GetAwaiter().GetResult();
    }

    [Fact]
    public void Never_synced_is_unrestricted() =>
        Assert.False(_policy.Evaluate("S-1").Restricted);

    [Fact]
    public void Unbound_account_is_unrestricted()
    {
        Server(limit: 0);
        Assert.False(_policy.Evaluate("S-PAPA").Restricted);
    }

    [Fact]
    public void Disabled_device_is_unrestricted()
    {
        Server(limit: 0, enabled: false);
        Assert.False(_policy.Evaluate("S-1").Restricted);
    }

    [Fact]
    public void Inside_window_under_limit_reports_remaining()
    {
        Server(limit: 120, spent: 100);
        var d = _policy.Evaluate("S-1");
        Assert.True(d.Restricted);
        Assert.Null(d.Lock);
        Assert.Equal(20, d.RemainingMinutes);
        Assert.Equal(RemainingReasons.Limit, d.RemainingBy);
        Assert.Equal(120, d.AllowedMinutes);
    }

    [Fact]
    public void Window_end_can_bind_before_limit()
    {
        _time.UtcNow = new DateTimeOffset(2026, 11, 1, 16, 22, 0, TimeSpan.Zero); // 17:22 local
        Server(limit: 225, spent: 13, to: "20:00");
        var d = _policy.Evaluate("S-1");
        Assert.Equal(158, d.RemainingMinutes); // 2:38 to 20:00, not 3:32 by limit
        Assert.Equal(RemainingReasons.Window, d.RemainingBy);
    }

    [Fact]
    public void Remaining_is_capped_by_window_end()
    {
        _time.UtcNow = new DateTimeOffset(2026, 11, 1, 18, 56, 30, TimeSpan.Zero); // 19:56:30 local
        Server(limit: 120, spent: 10);
        Assert.Equal(4, _policy.Evaluate("S-1").RemainingMinutes);
    }

    [Fact]
    public void Limit_reached_locks()
    {
        Server(limit: 120, spent: 120);
        Assert.Equal(LockReason.Limit, _policy.Evaluate("S-1").Lock);
    }

    [Fact]
    public void Bonus_extends_limit()
    {
        Server(limit: 120, bonus: 30, spent: 130);
        var d = _policy.Evaluate("S-1");
        Assert.Null(d.Lock);
        Assert.Equal(20, d.RemainingMinutes);
    }

    [Fact]
    public void Negative_bonus_can_lock_early()
    {
        Server(limit: 60, bonus: -30, spent: 30);
        Assert.Equal(LockReason.Limit, _policy.Evaluate("S-1").Lock);
    }

    [Fact]
    public void Before_window_locks()
    {
        _time.UtcNow = new DateTimeOffset(2026, 11, 1, 13, 59, 0, TimeSpan.Zero); // 14:59 local
        Server(from: "15:00");
        Assert.Equal(LockReason.Window, _policy.Evaluate("S-1").Lock);
    }

    [Fact]
    public void Window_end_is_exclusive()
    {
        _time.UtcNow = new DateTimeOffset(2026, 11, 1, 19, 0, 0, TimeSpan.Zero); // 20:00 local
        Server(to: "20:00");
        Assert.Equal(LockReason.Window, _policy.Evaluate("S-1").Lock);
    }

    [Fact]
    public void No_limit_full_day_has_no_remaining()
    {
        Server(limit: null, from: "00:00", to: "24:00");
        var d = _policy.Evaluate("S-1");
        Assert.Null(d.Lock);
        Assert.Null(d.RemainingMinutes);
    }

    [Fact]
    public void Active_command_lock_wins_and_expires()
    {
        var now = _time.UtcNow;
        Server(locks: [new LockInfo(1, Misha, now.AddMinutes(-1), now.AddMinutes(30))]);
        Assert.Equal(LockReason.Command, _policy.Evaluate("S-1").Lock);

        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(_policy.Evaluate("S-1").Lock);
    }

    [Fact]
    public void Scheduled_command_lock_counts_down()
    {
        var now = _time.UtcNow;
        Server(limit: null, locks: [new LockInfo(1, Misha, now.AddMinutes(5), null)]);
        var d = _policy.Evaluate("S-1");
        Assert.Null(d.Lock);
        Assert.Equal(5, d.RemainingMinutes);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(LockReason.Command, _policy.Evaluate("S-1").Lock);
    }

    [Fact]
    public void Midnight_switches_to_tomorrow_settings_offline()
    {
        Server(limit: 120, spent: 120); // used up today
        _api.Throw = new ApiException("offline");
        _time.UtcNow = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero); // 10:30 local next day
        var d = _policy.Evaluate("S-1");
        Assert.Null(d.Lock);
        Assert.Equal(60, d.AllowedMinutes);
        Assert.Equal(0, d.SpentMinutes);
        Assert.False(d.StaleSettings);
    }

    [Fact]
    public void Long_offline_reuses_latest_day_without_bonus()
    {
        Server(tomorrow: new DayInfo(Nov2, 60, 45, "10:00", "20:00", 0));
        _time.UtcNow = new DateTimeOffset(2026, 11, 5, 11, 0, 0, TimeSpan.Zero); // Nov 5, 12:00 local
        var d = _policy.Evaluate("S-1");
        Assert.True(d.StaleSettings);
        Assert.Equal(60, d.AllowedMinutes);
        Assert.Equal("10:00", d.WindowFrom);
    }

    [Fact]
    public void Local_minutes_count_while_offline()
    {
        Server(limit: 3, spent: 0);
        _api.Throw = new ApiException("offline");
        for (var i = 0; i < 3; i++)
        {
            _engine.RecordMinute(new SessionSample(HeartbeatStates.Active, "S-1"));
            _time.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(LockReason.Limit, _policy.Evaluate("S-1").Lock);
    }

    [Fact]
    public void Locked_by_default_until_access_is_opened()
    {
        Server(tomorrow: null);
        _api.Respond = _ => new SyncResponse
        {
            Accounts = [new AccountInfo("S-1", "misha", Misha)],
            Children = [new ChildInfo(Misha, "Миша",
            [
                new DayInfo(Nov1, 120, 0, "15:00", "20:00", 0, RequiresUnlock: true, Unlocked: false),
                new DayInfo(Nov2, 120, 0, "15:00", "20:00", 0, RequiresUnlock: true, Unlocked: false),
            ])],
        };
        _engine.SyncAsync(default).GetAwaiter().GetResult();
        var d = _policy.Evaluate("S-1");
        Assert.Equal(LockReason.NotUnlocked, d.Lock);
        Assert.Null(d.RemainingMinutes);
    }

    [Fact]
    public void Opened_access_falls_back_to_schedule()
    {
        _api.Respond = _ => new SyncResponse
        {
            Accounts = [new AccountInfo("S-1", "misha", Misha)],
            Children = [new ChildInfo(Misha, "Миша",
                [new DayInfo(Nov1, 120, 0, "15:00", "20:00", 120, RequiresUnlock: true, Unlocked: true)])],
        };
        _engine.SyncAsync(default).GetAwaiter().GetResult();
        Assert.Equal(LockReason.Limit, _policy.Evaluate("S-1").Lock); // opened, but the limit still applies
    }

    [Fact]
    public void Window_wins_over_not_unlocked()
    {
        _time.UtcNow = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero); // 09:00 local, before 15:00
        _api.Respond = _ => new SyncResponse
        {
            Accounts = [new AccountInfo("S-1", "misha", Misha)],
            Children = [new ChildInfo(Misha, "Миша",
                [new DayInfo(Nov1, 120, 0, "15:00", "20:00", 0, RequiresUnlock: true, Unlocked: false)])],
        };
        _engine.SyncAsync(default).GetAwaiter().GetResult();
        Assert.Equal(LockReason.Window, _policy.Evaluate("S-1").Lock);
    }

    [Fact]
    public void Offline_stale_day_does_not_carry_yesterdays_unlock()
    {
        _api.Respond = _ => new SyncResponse
        {
            Accounts = [new AccountInfo("S-1", "misha", Misha)],
            Children = [new ChildInfo(Misha, "Миша",
                [new DayInfo(Nov2, null, 0, "00:00", "24:00", 0, RequiresUnlock: true, Unlocked: true)])],
        };
        _engine.SyncAsync(default).GetAwaiter().GetResult();
        _time.UtcNow = new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.Zero);
        Assert.Equal(LockReason.NotUnlocked, _policy.Evaluate("S-1").Lock);
    }

    [Theory]
    [InlineData("15:00", 900)]
    [InlineData("24:00", 1440)]
    [InlineData("00:30", 30)]
    [InlineData("", null)]
    [InlineData("bad", null)]
    public void ParseTime(string s, int? expected) => Assert.Equal(expected, Policy.ParseTime(s));
}
