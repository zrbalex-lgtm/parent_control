using Microsoft.Extensions.Logging;

namespace ParentControl.Core;

/// <summary>Platform actions on a Windows session (tray UI when connected, system dialogs otherwise).</summary>
public interface ISessionControl
{
    /// <summary>"N minutes left" warning; <paramref name="minutesLeft"/> ≤ 1 is the final one.</summary>
    void Warn(uint sessionId, int minutesLeft);

    /// <summary>
    /// Explains the lock; the session is disconnected after <paramref name="grace"/>,
    /// or signed out when <paramref name="signInBlockedUntil"/> is set.
    /// </summary>
    void ShowLock(uint sessionId, LockReason reason, Decision decision, TimeSpan grace, DateTimeOffset? signInBlockedUntil = null);

    /// <summary>Sends the session to the sign-in screen; programs keep running.</summary>
    bool Disconnect(uint sessionId);

    /// <summary>Signs the session out (programs are closed), so coming back needs a new sign-in.</summary>
    bool LogOff(uint sessionId);
}

/// <summary>
/// Turns decisions into actions: warnings before the end, then an explanation
/// and a disconnect after <see cref="Grace"/>. Called every few seconds.
/// </summary>
public sealed class Enforcer(ISessionControl control, TimeProvider time, ILogger<Enforcer> log)
{
    /// <summary>Long enough to read the reason and tap "ask for more time".</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);
    public static readonly int[] WarnAt = [10, 5, 1];

    /// <summary>Signing in again right after a lock only shows the reason briefly: re-login gives no free minutes.</summary>
    public static readonly TimeSpan RepeatGrace = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);

    private readonly Dictionary<Guid, DateTimeOffset> _lastDisconnect = [];

    /// <summary>When a child was last sent to the sign-in screen for a lock (any child).</summary>
    public DateTimeOffset? LastDisconnectAt { get; private set; }

    /// <summary>This many lock disconnects of one child within <see cref="AbuseWindow"/> = deliberate re-login.</summary>
    public const int AbuseThreshold = 10;
    public static readonly TimeSpan AbuseWindow = TimeSpan.FromHours(1);
    private readonly Dictionary<Guid, List<DateTimeOffset>> _disconnects = [];

    /// <summary>Raised once when a child reaches <see cref="AbuseThreshold"/> lock disconnects: (sample, decision, count).</summary>
    public event Action<SessionSample, Decision, int>? RepeatedRelogin;

    /// <summary>
    /// Until when Windows sign-in of an account (SID) is denied, if it is. A disconnected session can be
    /// reconnected despite denied logon hours, so a blocked child is signed out instead of disconnected.
    /// </summary>
    public Func<string?, DateTimeOffset?>? SignInBlockedUntil { get; set; }

    private uint? _pendingSession;
    private DateTimeOffset _disconnectAt;
    private readonly HashSet<string> _warned = [];

    public void Apply(SessionSample sample, Decision decision)
    {
        // Idle still counts as screen time, so it is enforced the same way.
        if (!HeartbeatStates.IsCounted(sample.State) || sample.SessionId is not { } session || !decision.Restricted)
        {
            _pendingSession = null;
            return;
        }

        if (decision.Lock is { } reason)
        {
            var now = time.GetUtcNow();
            if (_pendingSession != session)
            {
                var blockedUntil = SignInBlockedUntil?.Invoke(sample.Sid);
                var grace = blockedUntil is null ? GraceFor(decision.ChildId, now) : RepeatGrace;
                control.ShowLock(session, reason, decision, grace, blockedUntil);
                _pendingSession = session;
                _disconnectAt = now + grace;
                log.LogInformation("Locking {Child} ({Reason}) in {Grace}s", decision.ChildName, reason, grace.TotalSeconds);
            }
            else if (now >= _disconnectAt)
            {
                if (decision.ChildId is { } child)
                {
                    _lastDisconnect[child] = now;
                    LastDisconnectAt = now;
                    CountDisconnect(child, sample, decision, now);
                }
                // Checked after counting: reaching the threshold may have just blocked sign-in.
                if (SignInBlockedUntil?.Invoke(sample.Sid) is not null)
                {
                    var off = control.LogOff(session);
                    log.LogInformation("Signed out session {Session} of {Child} ({Reason}, sign-in blocked): {Result}",
                        session, decision.ChildName, reason, off ? "ok" : "FAILED");
                    if (!off) control.Disconnect(session);
                }
                else
                {
                    var ok = control.Disconnect(session);
                    log.LogInformation("Disconnected session {Session} of {Child} ({Reason}): {Result}",
                        session, decision.ChildName, reason, ok ? "ok" : "FAILED");
                }
                _pendingSession = null;
            }
            return;
        }

        _pendingSession = null;
        Warn(session, decision);
    }

    private void CountDisconnect(Guid child, SessionSample sample, Decision decision, DateTimeOffset now)
    {
        if (!_disconnects.TryGetValue(child, out var list)) _disconnects[child] = list = [];
        list.RemoveAll(t => now - t > AbuseWindow);
        list.Add(now);
        if (list.Count < AbuseThreshold) return;
        list.Clear(); // count again from zero after acting
        log.LogWarning("{Child}: {Count} lock disconnects within {Window} min", decision.ChildName, AbuseThreshold, AbuseWindow.TotalMinutes);
        RepeatedRelogin?.Invoke(sample, decision, AbuseThreshold);
    }

    public TimeSpan GraceFor(Guid? child, DateTimeOffset now) =>
        child is { } c && _lastDisconnect.TryGetValue(c, out var at) && now - at < RepeatWindow ? RepeatGrace : Grace;

    private void Warn(uint session, Decision d)
    {
        var key = $"{time.GetLocalNow():yyyy-MM-dd}|{d.ChildId}";
        if (d.RemainingMinutes is not { } left)
        {
            _warned.RemoveWhere(k => k.StartsWith(key, StringComparison.Ordinal));
            return;
        }

        var due = false;
        foreach (var t in WarnAt)
        {
            var k = $"{key}|{t}";
            if (left <= t)
            {
                if (_warned.Add(k)) due = true; // mark every crossed threshold, show one warning
            }
            else
            {
                _warned.Remove(k); // time was added: warn again later
            }
        }
        if (!due) return;

        control.Warn(session, left);
        log.LogInformation("Warned {Child}: {Left} min left", d.ChildName, left);
    }
}
