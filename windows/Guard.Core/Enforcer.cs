using Microsoft.Extensions.Logging;

namespace ParentControl.Core;

/// <summary>Platform actions on a Windows session.</summary>
public interface ISessionControl
{
    /// <summary>Shows a non-blocking message box in the session.</summary>
    void ShowMessage(uint sessionId, string title, string text, int timeoutSeconds);

    /// <summary>Sends the session to the sign-in screen; programs keep running.</summary>
    bool Disconnect(uint sessionId);
}

/// <summary>
/// Turns decisions into actions: a warning before the end, then a short
/// explanation and a disconnect after <see cref="Grace"/>. Called every few seconds.
/// </summary>
public sealed class Enforcer(ISessionControl control, TimeProvider time, ILogger<Enforcer> log)
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);
    public static readonly int[] WarnAt = [5, 1];
    private const string Title = "Экранное время";

    private uint? _pendingSession;
    private DateTimeOffset _disconnectAt;
    private readonly HashSet<string> _warned = [];

    public void Apply(SessionSample sample, Decision decision)
    {
        if (sample.State != HeartbeatStates.Active || sample.SessionId is not { } session || !decision.Restricted)
        {
            _pendingSession = null;
            return;
        }

        if (decision.Lock is { } reason)
        {
            var now = time.GetUtcNow();
            if (_pendingSession != session)
            {
                control.ShowMessage(session, Title, LockText(reason, decision), (int)Grace.TotalSeconds + 5);
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
                if (_warned.Add(k)) due = true; // mark every crossed threshold, show one popup
            }
            else
            {
                _warned.Remove(k); // time was added: warn again later
            }
        }
        if (!due) return;

        var text = left <= 1
            ? "Осталась 1 минута! Сохрани игру — скоро выход на экран входа."
            : $"Осталось {left} мин. Сохрани игру заранее.";
        control.ShowMessage(session, Title, text, 30);
        log.LogInformation("Warned {Child}: {Left} min left", d.ChildName, left);
    }

    internal static string LockText(LockReason reason, Decision d) => reason switch
    {
        LockReason.Limit =>
            "Время за компьютером на сегодня закончилось.\nСейчас будет выход на экран входа. Программы останутся открытыми.",
        LockReason.Window =>
            $"Сейчас компьютер недоступен.\nРазрешённое время: {d.WindowFrom}–{d.WindowTo}.",
        _ =>
            "Родитель заблокировал компьютер.\nСейчас будет выход на экран входа.",
    };
}
