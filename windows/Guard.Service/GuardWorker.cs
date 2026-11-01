using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParentControl.Core;

namespace ParentControl.Service;

/// <summary>
/// Main loop. Every few seconds: sample the console session, enforce, talk to the tray.
/// Once per minute (or right after a time request): record a heartbeat and sync.
/// </summary>
public sealed class GuardWorker(
    GuardEngine engine,
    Policy policy,
    Enforcer enforcer,
    ISessionProbe probe,
    TrayHub hub,
    TrayLauncher launcher,
    WindowsSessionControl control,
    DataDirectory data,
    TimeProvider time,
    ILogger<GuardWorker> log) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActivityMaxAge = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TrayGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RelaunchEvery = TimeSpan.FromSeconds(60);

    private string? _lastStateKey;
    private string? _lastDecisionKey;
    private int _notRegisteredTicks;
    private bool _syncNow;

    // per console session
    private uint? _session;
    private DateTimeOffset _sessionSince;
    private DateTimeOffset _lastLaunch = DateTimeOffset.MinValue;
    private string? _lastStatusJson;
    private DateTimeOffset _lastStatusAt;
    private readonly HashSet<string> _noticesSent = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Guard {Version} started. Data: {Dir}. Queue: {Queue}. Registered: {Registered}",
            Program.Version, data.Root, engine.QueueLength, File.Exists(data.Credentials));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad check stop the guard.
                log.LogError(ex, "Check failed");
            }

            try
            {
                await Task.Delay(CheckInterval, time, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        log.LogInformation("Guard stopping");
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var sample = WithActivity(probe.Sample());
        TrackSession(sample, now);

        HandleInbox(sample);

        if (engine.RecordMinute(sample) is not null || _syncNow)
        {
            _syncNow = false;
            var outcome = await engine.SyncAsync(ct);
            if (outcome == SyncOutcome.NotRegistered && _notRegisteredTicks++ % 60 == 0)
            {
                log.LogWarning("Device is not registered; run: ParentControl.Guard.exe register <CODE>");
            }
        }

        var decision = policy.Evaluate(sample.Sid);
        enforcer.Apply(sample, decision);

        if (sample.SessionId is { } session && HeartbeatStates.IsCounted(sample.State))
        {
            EnsureTray(session, decision, now);
            PushStatus(session, decision, now);
            PushNotices(session, decision, now);
        }

        LogChanges(sample, decision);
    }

    /// <summary>Adds the tray's foreground-window report; marks the minute idle after 5 min without input.</summary>
    private SessionSample WithActivity(SessionSample s)
    {
        if (s.SessionId is not { } session || hub.FreshActivity(session, ActivityMaxAge) is not { } a) return s;
        var state = s.State == HeartbeatStates.Active && a.IdleSeconds >= IdleAfter.TotalSeconds
            ? HeartbeatStates.Idle
            : s.State;
        return s with { State = state, Exe = a.Exe, Product = a.Product, Title = a.Title };
    }

    private void TrackSession(SessionSample s, DateTimeOffset now)
    {
        var active = HeartbeatStates.IsCounted(s.State) ? s.SessionId : null;
        if (active == _session) return;
        _session = active;
        _sessionSince = now;
        _lastStatusJson = null;
        _noticesSent.Clear();
    }

    private void HandleInbox(SessionSample sample)
    {
        while (hub.Inbox.TryDequeue(out var item))
        {
            var (session, m) = item;
            // Only the user in front of the console can ask for time.
            var sid = sample.SessionId == session ? sample.Sid : null;
            switch (m.Type)
            {
                case IpcTypes.RequestTime:
                {
                    var result = engine.RequestTime(sid, m.Minutes ?? 30, m.Reason);
                    var text = result switch
                    {
                        RequestTimeResult.Queued => Texts.RequestSent,
                        RequestTimeResult.AlreadyPending => Texts.RequestAlreadyPending,
                        _ => null,
                    };
                    if (text is not null) hub.Send(session, new IpcMessage { Type = IpcTypes.Notice, Text = text, NoticeId = "info" });
                    if (result == RequestTimeResult.Queued) _syncNow = true;
                    _lastStatusJson = null;
                    break;
                }
                case IpcTypes.AckNotice:
                    Ack(m.NoticeId);
                    break;
            }
        }
    }

    private void Ack(string? noticeId)
    {
        if (noticeId is null) return;
        var parts = noticeId.Split(':');
        if (parts.Length != 2 || !long.TryParse(parts[1], out var id)) return;
        if (parts[0] == "message") engine.MarkMessageShown(id);
        else if (parts[0] == "request") engine.MarkRequestNotified(id);
    }

    private void EnsureTray(uint session, Decision decision, DateTimeOffset now)
    {
        if (!decision.Restricted || hub.IsConnected(session)) return;
        if (now - _sessionSince < TrayGrace || now - _lastLaunch < RelaunchEvery) return;
        _lastLaunch = now;
        launcher.TryLaunch(session);
    }

    private void PushStatus(uint session, Decision d, DateTimeOffset now)
    {
        if (!hub.IsConnected(session)) return;
        var open = d.ChildId is { } child ? engine.OpenRequestFor(child) : null;
        var status = new TrayStatus
        {
            Restricted = d.Restricted,
            ChildName = d.ChildName,
            Lock = d.Lock?.ToString(),
            RemainingMinutes = d.RemainingMinutes,
            SpentMinutes = d.SpentMinutes,
            AllowedMinutes = d.AllowedMinutes,
            WindowFrom = d.WindowFrom,
            WindowTo = d.WindowTo,
            Online = engine.State.LastSyncError is null && engine.State.LastSyncAt is { } at && now - at < TimeSpan.FromMinutes(3),
            StaleSettings = d.StaleSettings,
            RequestMinutes = open?.Minutes,
            RequestUploaded = open?.Uploaded ?? false,
        };
        var msg = new IpcMessage { Type = IpcTypes.Status, Status = status };
        var json = Ipc.Serialize(msg);
        if (json == _lastStatusJson && now - _lastStatusAt < TimeSpan.FromSeconds(30)) return;
        if (hub.Send(session, msg))
        {
            _lastStatusJson = json;
            _lastStatusAt = now;
        }
    }

    /// <summary>Admin messages and answers to time requests; via tray, or a system box if no tray shows up.</summary>
    private void PushNotices(uint session, Decision d, DateTimeOffset now)
    {
        if (d.ChildId is not { } child) return;

        var notices = engine.UnshownMessages(child).Select(m => ($"message:{m.Id}", m.Text))
            .Concat(engine.UnnotifiedResults(child).Select(r => ($"request:{r.Id}", Texts.RequestResult(r))))
            .ToList();
        if (notices.Count == 0) return;

        if (hub.IsConnected(session))
        {
            foreach (var (id, text) in notices.Where(n => !_noticesSent.Contains(n.Item1)))
            {
                if (hub.Send(session, new IpcMessage { Type = IpcTypes.Notice, Text = text, NoticeId = id })) _noticesSent.Add(id);
            }
        }
        else if (now - _sessionSince > TrayGrace + RelaunchEvery)
        {
            // No tray for a while (e.g. console mode): fall back to a system box and consider it shown.
            foreach (var (id, text) in notices)
            {
                control.ShowMessageBox(session, text, 60);
                Ack(id);
            }
        }
    }

    private void LogChanges(SessionSample sample, Decision decision)
    {
        var stateKey = $"{sample.State}/{sample.User}";
        if (stateKey != _lastStateKey)
        {
            log.LogInformation("Console session: {State} {User}", sample.State, sample.User ?? "-");
            _lastStateKey = stateKey;
        }
        var decisionKey = $"{decision.ChildName}/{decision.Lock}/{decision.StaleSettings}";
        if (decision.Restricted && decisionKey != _lastDecisionKey)
        {
            log.LogInformation("{Child}: {Lock}, spent {Spent} of {Allowed}, window {From}-{To}{Stale}",
                decision.ChildName, decision.Lock?.ToString() ?? "allowed", decision.SpentMinutes,
                decision.AllowedMinutes?.ToString() ?? "unlimited", decision.WindowFrom, decision.WindowTo,
                decision.StaleSettings ? " (offline, old settings)" : "");
        }
        _lastDecisionKey = decisionKey;
    }
}
