using Microsoft.Extensions.Logging.Abstractions;
using ParentControl.Core;

namespace ParentControl.Core.Tests;

public class UpdateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly string Sha = new('a', 64);
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static UpdateManifest M(string v) => new(v, "parent-control-guard-win-x64.zip", Sha, 1000);

    [Fact]
    public void Only_newer_versions_are_wanted()
    {
        Assert.True(UpdatePlanner.IsWanted("1.2.0", M("1.3.0"), null, null, Now));
        Assert.True(UpdatePlanner.IsWanted("1.2.0+abc", M("1.2.1"), null, null, Now));
        Assert.False(UpdatePlanner.IsWanted("1.3.0", M("1.3.0"), null, null, Now));
        Assert.False(UpdatePlanner.IsWanted("1.3.0", M("1.2.9"), null, null, Now)); // never downgrade
        Assert.False(UpdatePlanner.IsWanted("1.2.0", M("garbage"), null, null, Now));
        Assert.False(UpdatePlanner.IsWanted("1.2.0", M("1.3.0") with { Sha256 = "short" }, null, null, Now));
    }

    [Fact]
    public void Failed_version_is_retried_after_a_day()
    {
        Assert.False(UpdatePlanner.IsWanted("1.2.0", M("1.3.0"), "1.3.0", Now.AddHours(-2), Now));
        Assert.True(UpdatePlanner.IsWanted("1.2.0", M("1.3.0"), "1.3.0", Now.AddHours(-25), Now));
        Assert.True(UpdatePlanner.IsWanted("1.2.0", M("1.3.1"), "1.3.0", Now.AddHours(-2), Now)); // a fixed version goes ahead
    }

    [Fact]
    public void Waits_for_the_child_to_leave_but_not_forever()
    {
        Assert.True(UpdatePlanner.CanInstallNow(childActive: false, Now, Now));
        Assert.False(UpdatePlanner.CanInstallNow(childActive: true, Now.AddHours(-1), Now));
        Assert.True(UpdatePlanner.CanInstallNow(childActive: true, Now.AddHours(-13), Now));
    }

    [Fact]
    public async Task Outcome_of_an_attempt_is_recorded_and_failures_reported()
    {
        var time = new FakeTime(Now);
        var api = new FakeApi();
        var creds = new CredentialStore(_dir.File("device.json"));
        creds.Save(new DeviceCredentials(Guid.NewGuid(), "s"));
        GuardEngine Engine() => new(api, new HeartbeatQueue(_dir.File("q.jsonl")), new StateStore(_dir.File("state.json")),
            creds, time, NullLogger<GuardEngine>.Instance, "test", null, new TamperQueue(_dir.File("t.jsonl")));

        var e = Engine();
        Assert.True(await e.UpdateAllowedAsync(default));
        api.AutoUpdate = false;
        Assert.False(await e.UpdateAllowedAsync(default));

        e.NoteUpdateAttempt("1.3.0");
        var after = Engine(); // restarted by the updater, but the old version came back
        after.CheckUpdateOutcome("1.2.0");
        Assert.Equal("1.3.0", after.State.FailedUpdateVersion);
        Assert.Equal(1, after.TamperQueueLength);
        Assert.False(after.State.TamperLocked); // not a lock event

        after.NoteUpdateAttempt("1.3.1");
        var ok = Engine();
        ok.CheckUpdateOutcome("1.3.1");
        Assert.Null(ok.State.FailedUpdateVersion);
        Assert.Null(ok.State.UpdateAttemptVersion);
    }
}
