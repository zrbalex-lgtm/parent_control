using Microsoft.Extensions.Logging.Abstractions;
using ParentControl.Core;

namespace ParentControl.Core.Tests;

public class LogonHoursTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");

    private static bool Allowed(byte[] bits, DateTimeOffset t)
    {
        var i = LogonHours.HourOfWeek(t);
        return (bits[i / 8] & (1 << (i % 8))) != 0;
    }

    [Fact]
    public void Bitmap_denies_only_the_blocked_whole_hours()
    {
        // Tuesday 3 Nov 2026, 13:20 UTC -> deny 13:00..15:00 UTC
        var from = new DateTimeOffset(2026, 11, 3, 13, 20, 0, TimeSpan.Zero);
        var bits = LogonHours.DenyBetween(from, new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.Zero));
        Assert.Equal(21, bits.Length);
        Assert.False(Allowed(bits, from));
        Assert.False(Allowed(bits, from.AddHours(1)));
        Assert.True(Allowed(bits, from.AddHours(2)));
        Assert.True(Allowed(bits, from.AddHours(-1)));
        Assert.Equal(2 * 24 + 13, LogonHours.HourOfWeek(from)); // Tuesday = day 2
        Assert.All(LogonHours.AllAllowed(), b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void Block_lasts_an_hour_rounded_up_for_limit_locks()
    {
        var now = new DateTimeOffset(2026, 11, 3, 13, 20, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.Zero),
            LogonHours.BlockUntil(now, Zone, LockReason.Limit, "15:00", "15:00"));
    }

    [Fact]
    public void Before_the_window_blocks_until_it_opens()
    {
        // 12:20 local (11:20 UTC), window from 15:00 local = 14:00 UTC
        var now = new DateTimeOffset(2026, 11, 3, 11, 20, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 14, 0, 0, TimeSpan.Zero),
            LogonHours.BlockUntil(now, Zone, LockReason.Window, "15:00", "15:00"));
    }

    [Fact]
    public void After_the_window_blocks_until_tomorrow_but_at_most_a_day()
    {
        // 21:10 local (20:10 UTC); tomorrow from 10:00 local = 09:00 UTC next day
        var now = new DateTimeOffset(2026, 11, 3, 20, 10, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero),
            LogonHours.BlockUntil(now, Zone, LockReason.Window, "15:00", "10:00"));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 22, 0, 0, TimeSpan.Zero),
            LogonHours.BlockUntil(now, Zone, LockReason.Window, "15:00", null)); // unknown tomorrow: an hour
    }

    [Fact]
    public void Enforcer_reports_ten_lock_disconnects_within_an_hour_once()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 11, 3, 18, 0, 0, TimeSpan.Zero));
        var ctl = new NullControl();
        var enf = new Enforcer(ctl, time, NullLogger<Enforcer>.Instance);
        var child = Guid.NewGuid();
        var locked = new Decision { Restricted = true, ChildId = child, ChildName = "Миша", Lock = LockReason.Window };
        var raised = 0;
        enf.RepeatedRelogin += (_, _, n) => { raised++; Assert.Equal(Enforcer.AbuseThreshold, n); };

        for (uint i = 0; i < 12; i++)
        {
            var s = new SessionSample(HeartbeatStates.Active, "S-1", "misha", SessionId: 10 + i);
            enf.Apply(s, locked);
            time.Advance(TimeSpan.FromSeconds(30));
            enf.Apply(s, locked); // disconnect
            enf.Apply(new SessionSample(HeartbeatStates.NoUser), Decision.Unrestricted);
            time.Advance(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Slow_relogins_over_hours_are_not_abuse()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 11, 3, 18, 0, 0, TimeSpan.Zero));
        var enf = new Enforcer(new NullControl(), time, NullLogger<Enforcer>.Instance);
        var locked = new Decision { Restricted = true, ChildId = Guid.NewGuid(), Lock = LockReason.Limit };
        var raised = 0;
        enf.RepeatedRelogin += (_, _, _) => raised++;
        for (uint i = 0; i < 12; i++)
        {
            var s = new SessionSample(HeartbeatStates.Active, "S-1", "misha", SessionId: 10 + i);
            enf.Apply(s, locked);
            time.Advance(TimeSpan.FromSeconds(30));
            enf.Apply(s, locked);
            enf.Apply(new SessionSample(HeartbeatStates.NoUser), Decision.Unrestricted);
            time.Advance(TimeSpan.FromMinutes(10));
        }
        Assert.Equal(0, raised);
    }

    private sealed class NullControl : ISessionControl
    {
        public void Warn(uint sessionId, int minutesLeft) { }
        public void ShowLock(uint sessionId, LockReason reason, Decision decision, TimeSpan grace) { }
        public bool Disconnect(uint sessionId) => true;
    }
}
