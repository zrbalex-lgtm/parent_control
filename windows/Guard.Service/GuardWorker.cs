using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParentControl.Core;

namespace ParentControl.Service;

/// <summary>Minute loop: sample the console session, record a heartbeat, sync.</summary>
public sealed class GuardWorker(
    GuardEngine engine,
    ISessionProbe probe,
    DataDirectory data,
    ILogger<GuardWorker> log) : BackgroundService
{
    // Tick a couple of seconds after the minute boundary so each minute is sampled once.
    private static readonly TimeSpan TickOffset = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Guard {Version} started. Data: {Dir}. Queue: {Queue}. Registered: {Registered}",
            Program.Version, data.Root, engine.QueueLength, File.Exists(data.Credentials));

        string? lastState = null;
        var notRegisteredTicks = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sample = probe.Sample();
                engine.RecordMinute(sample);

                var stateKey = $"{sample.State}/{sample.User}";
                if (stateKey != lastState)
                {
                    log.LogInformation("Console session: {State} {User}", sample.State, sample.User ?? "-");
                    lastState = stateKey;
                }

                var outcome = await engine.SyncAsync(ct);
                if (outcome == SyncOutcome.NotRegistered && notRegisteredTicks++ % 60 == 0)
                {
                    log.LogWarning("Device is not registered; run: ParentControl.Guard.exe register <CODE>");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad minute stop the guard.
                log.LogError(ex, "Tick failed");
            }

            try
            {
                await Task.Delay(DelayToNextTick(DateTimeOffset.UtcNow), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        log.LogInformation("Guard stopping");
    }

    internal static TimeSpan DelayToNextTick(DateTimeOffset now)
    {
        var next = GuardEngine.TruncateToMinute(now).AddMinutes(1) + TickOffset;
        var delay = next - now;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
    }
}
