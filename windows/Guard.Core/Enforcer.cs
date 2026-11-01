using Microsoft.Extensions.Logging;

namespace ParentControl.Core;

/// <summary>Platform actions on a Windows session (tray UI when connected, system dialogs otherwise).</summary>
public interface ISessionControl
{
    /// <summary>"N minutes left" warning; <paramref name="minutesLeft"/> ≤ 1 is the final one.</summary>
    void Warn(uint sessionId, int minutesLeft);

    /// <summary>Explains the lock; the session is disconnected after <paramref name="grace"/>.</summary>
    void ShowLock(uint sessionId, LockReason reason, Decision decision, TimeSpan grace);

    /// <summary>Sends the session to the sign-in screen; programs keep running.</summary>
    bool Disconnect(uint sessionId);
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
                control.ShowLock(session, reason, decision, Grace);
                _pendingSession = session;
                _disconnectAt = now + Grace;
                log.LogInformation("Locking {Child} ({Reason}) in {Grace}s", decision.ChildName, reason, Grace.TotalSeconds);
            }
            else if (now >= _disconnectAt)
            {
                var ok = control.Disconnect(session);
                log.LogInformation("Disconnected session {Session} of {Child} ({Reason}): {Result}",
                    session, decision.ChildName, reason, ok ? "ok" : "FAILED");
                _pendingSession = null;
            }
            return;
        }

        _pendingSession = null;
        Warn(session, decision);
    }

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
