using ParentControl.Core;

namespace ParentControl.Core.Tests;

public class HeartbeatQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Enqueue_persists_and_reloads_in_order()
    {
        using var dir = new TempDir();
        var q = new HeartbeatQueue(dir.File("q.jsonl"));
        q.Enqueue(Hb.At(T0.AddMinutes(2)));
        q.Enqueue(Hb.At(T0));
        q.Enqueue(Hb.At(T0.AddMinutes(1)));

        var reloaded = new HeartbeatQueue(dir.File("q.jsonl"));
        Assert.Equal(3, reloaded.Count);
        Assert.Equal(new[] { T0, T0.AddMinutes(1), T0.AddMinutes(2) }, reloaded.Snapshot().Select(h => h.Ts));
    }

    [Fact]
    public void Same_minute_is_queued_once()
    {
        using var dir = new TempDir();
        var q = new HeartbeatQueue(dir.File("q.jsonl"));
        Assert.True(q.Enqueue(Hb.At(T0)));
        Assert.False(q.Enqueue(Hb.At(T0, HeartbeatStates.Locked)));
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public void Remove_drops_sent_items_and_survives_reload()
    {
        using var dir = new TempDir();
        var q = new HeartbeatQueue(dir.File("q.jsonl"));
        for (var i = 0; i < 5; i++) q.Enqueue(Hb.At(T0.AddMinutes(i)));
        q.Remove(q.Peek(3));
        Assert.Equal(2, q.Count);
        Assert.Equal(2, new HeartbeatQueue(dir.File("q.jsonl")).Count);
        // Removed minute can be queued again (e.g. never happens, but keys must be freed)
        Assert.True(q.Enqueue(Hb.At(T0)));
    }

    [Fact]
    public void Cap_drops_oldest()
    {
        using var dir = new TempDir();
        var q = new HeartbeatQueue(dir.File("q.jsonl"), maxItems: 3);
        for (var i = 0; i < 5; i++) q.Enqueue(Hb.At(T0.AddMinutes(i)));
        Assert.Equal(new[] { T0.AddMinutes(2), T0.AddMinutes(3), T0.AddMinutes(4) }, q.Snapshot().Select(h => h.Ts));
        Assert.Equal(3, new HeartbeatQueue(dir.File("q.jsonl"), maxItems: 3).Count);
    }

    [Fact]
    public void Torn_last_line_is_ignored()
    {
        using var dir = new TempDir();
        var path = dir.File("q.jsonl");
        var q = new HeartbeatQueue(path);
        q.Enqueue(Hb.At(T0));
        File.AppendAllText(path, "{\"ts\":\"2026-11-01T14:01");
        Assert.Equal(1, new HeartbeatQueue(path).Count);
    }
}
