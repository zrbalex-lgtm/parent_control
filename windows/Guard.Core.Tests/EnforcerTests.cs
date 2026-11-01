using Microsoft.Extensions.Logging.Abstractions;
using ParentControl.Core;

namespace ParentControl.Core.Tests;

public class EnforcerTests
{
    private sealed class FakeControl : ISessionControl
    {
        public List<string> Messages { get; } = [];
        public List<uint> Disconnects { get; } = [];
        public void Warn(uint sessionId, int minutesLeft) => Messages.Add(Texts.Warn(minutesLeft));
        public void ShowLock(uint sessionId, LockReason reason, Decision d, TimeSpan grace) =>
            Messages.Add(Texts.Lock(reason, d.WindowFrom, d.WindowTo));
        public bool Disconnect(uint sessionId) { Disconnects.Add(sessionId); return true; }
    }

    private static readonly Guid Misha = Guid.NewGuid();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero));
    private readonly FakeControl _ctl = new();
    private readonly Enforcer _enf;

    public EnforcerTests() => _enf = new Enforcer(_ctl, _time, NullLogger<Enforcer>.Instance);

    private static SessionSample Active(uint session = 2) => new(HeartbeatStates.Active, "S-1", "misha", SessionId: session);

    private static Decision Locked(LockReason r = LockReason.Limit) =>
        new() { Restricted = true, ChildId = Misha, ChildName = "Миша", Lock = r, WindowFrom = "15:00", WindowTo = "20:00" };

    private static Decision Left(int? minutes) =>
        new() { Restricted = true, ChildId = Misha, ChildName = "Миша", RemainingMinutes = minutes };

    [Fact]
    public void Lock_shows_message_then_disconnects_after_grace()
    {
        _enf.Apply(Active(), Locked());
        Assert.Single(_ctl.Messages);
        Assert.Empty(_ctl.Disconnects);

        _time.Advance(TimeSpan.FromSeconds(5));
        _enf.Apply(Active(), Locked());
        Assert.Empty(_ctl.Disconnects);

        _time.Advance(Enforcer.Grace);
        _enf.Apply(Active(), Locked());
        Assert.Equal(new uint[] { 2 }, _ctl.Disconnects);
    }

    [Fact]
    public void Relogin_during_lock_gets_message_and_disconnect_again()
    {
        _enf.Apply(Active(), Locked());
        _time.Advance(Enforcer.Grace);
        _enf.Apply(Active(), Locked());
        _enf.Apply(new SessionSample(HeartbeatStates.NoUser), Decision.Unrestricted); // sign-in screen
        _enf.Apply(Active(3), Locked());
        _time.Advance(Enforcer.Grace);
        _enf.Apply(Active(3), Locked());
        Assert.Equal(2, _ctl.Messages.Count);
        Assert.Equal(new uint[] { 2, 3 }, _ctl.Disconnects);
    }

    [Fact]
    public void Unrestricted_or_locked_screen_does_nothing()
    {
        _enf.Apply(Active(), Decision.Unrestricted);
        _enf.Apply(new SessionSample(HeartbeatStates.Locked, "S-1", SessionId: 2), Locked());
        Assert.Empty(_ctl.Messages);
        Assert.Empty(_ctl.Disconnects);
    }

    [Fact]
    public void Lock_lifted_during_grace_cancels_disconnect()
    {
        _enf.Apply(Active(), Locked());
        _enf.Apply(Active(), Left(30)); // parent added time
        _time.Advance(Enforcer.Grace);
        _enf.Apply(Active(), Left(30));
        Assert.Empty(_ctl.Disconnects);
    }

    [Fact]
    public void Warns_once_at_ten_five_and_one()
    {
        _enf.Apply(Active(), Left(20));
        _enf.Apply(Active(), Left(10));
        _enf.Apply(Active(), Left(9));
        _enf.Apply(Active(), Left(5));
        _enf.Apply(Active(), Left(5));
        _enf.Apply(Active(), Left(4));
        _enf.Apply(Active(), Left(1));
        _enf.Apply(Active(), Left(1));
        Assert.Equal(3, _ctl.Messages.Count);
        Assert.Contains("10 мин", _ctl.Messages[0]);
        Assert.Contains("5 мин", _ctl.Messages[1]);
        Assert.Contains("1 минута", _ctl.Messages[2]);
    }

    [Fact]
    public void Login_with_little_time_left_gets_single_warning()
    {
        _enf.Apply(Active(), Left(1));
        _enf.Apply(Active(), Left(1));
        Assert.Single(_ctl.Messages);
    }

    [Fact]
    public void Bonus_rearms_warnings()
    {
        _enf.Apply(Active(), Left(5));
        _enf.Apply(Active(), Left(35)); // +30 from parent
        _enf.Apply(Active(), Left(5));
        Assert.Equal(2, _ctl.Messages.Count);
    }

    [Fact]
    public void Idle_session_is_still_locked()
    {
        var idle = new SessionSample(HeartbeatStates.Idle, "S-1", "misha", SessionId: 2);
        _enf.Apply(idle, Locked());
        _time.Advance(Enforcer.Grace);
        _enf.Apply(idle, Locked());
        Assert.Equal(new uint[] { 2 }, _ctl.Disconnects);
    }

    [Fact]
    public void Window_lock_text_names_the_window()
    {
        _enf.Apply(Active(), Locked(LockReason.Window));
        Assert.Contains("15:00–20:00", _ctl.Messages[0]);
    }
}
