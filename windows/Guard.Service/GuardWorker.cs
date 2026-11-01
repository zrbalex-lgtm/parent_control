using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParentControl.Core;

namespace ParentControl.Service;

/// <summary>
/// Main loop. Every few seconds: sample the console session and enforce.
/// Once per minute: record a heartbeat and sync with the server.
/// </summary>
public sealed class GuardWorker(
    GuardEngine engine,
    Policy policy,
    Enforcer enforcer,
    ISessionProbe probe,
    DataDirectory data,
    TimeProvider time,
    ILogger<GuardWorker> log) : BackgroundService
{
    // Fast enough that a re-login during a lock is caught within seconds.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Guard {Version} started. Data: {Dir}. Queue: {Queue}. Registered: {Registered}",
            Program.Version, data.Root, engine.QueueLength, File.Exists(data.Credentials));

        string? lastStateKey = null;
        string? lastDecisionKey = null;
        var notRegisteredTicks = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sample = probe.Sample();

                // Once per minute: heartbeat + sync (RecordMinute returns null within the same minute).
                if (engine.RecordMinute(sample) is not null)
                {
                    var outcome = await engine.SyncAsync(ct);
                    if (outcome == SyncOutcome.NotRegistered && notRegisteredTicks++ % 60 == 0)
                    {
                        log.LogWarning("Device is not registered; run: ParentControl.Guard.exe register <CODE>");
                    }
                }

                var decision = policy.Evaluate(sample.Sid);
                enforcer.Apply(sample, decision);

                var stateKey = $"{sample.State}/{sample.User}";
                if (stateKey != lastStateKey)
                {
                    log.LogInformation("Console session: {State} {User}", sample.State, sample.User ?? "-");
                    lastStateKey = stateKey;
                }
                var decisionKey = $"{decision.ChildName}/{decision.Lock}/{decision.StaleSettings}";
                if (decision.Restricted && decisionKey != lastDecisionKey)
                {
                    log.LogInformation("{Child}: {Lock}, spent {Spent} of {Allowed}, window {From}-{To}{Stale}",
                        decision.ChildName, decision.Lock?.ToString() ?? "allowed", decision.SpentMinutes,
                        decision.AllowedMinutes?.ToString() ?? "unlimited", decision.WindowFrom, decision.WindowTo,
                        decision.StaleSettings ? " (offline, old settings)" : "");
                }
                lastDecisionKey = decisionKey;
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
}
