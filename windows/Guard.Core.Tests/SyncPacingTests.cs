using ParentControl.Core;

namespace ParentControl.Core.Tests;

public class SyncPacingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 18, 0, 0, TimeSpan.Zero);

    private static bool Due(int secondsSinceSync, bool online = true, bool awaiting = false, bool child = false, int? disconnectedMinAgo = null) =>
        SyncPacing.ShouldSync(Now, Now.AddSeconds(-secondsSinceSync), online, awaiting, child,
            disconnectedMinAgo is { } m ? Now.AddMinutes(-m) : null);

    [Fact]
    public void Nobody_around_keeps_the_regular_minute()
    {
        Assert.False(Due(50));
    }

    [Fact]
    public void Child_at_the_pc_syncs_every_ten_seconds()
    {
        Assert.False(Due(9, child: true));
        Assert.True(Due(10, child: true));
    }

    [Fact]
    public void Recently_disconnected_child_keeps_fast_sync_for_a_while()
    {
        Assert.True(Due(10, disconnectedMinAgo: 5));
        Assert.False(Due(30, disconnectedMinAgo: 20));
    }

    [Fact]
    public void Waiting_for_an_answer_is_faster_still()
    {
        Assert.True(Due(5, awaiting: true));
        Assert.False(Due(4, awaiting: true, child: true));
    }

    [Fact]
    public void Offline_never_adds_syncs()
    {
        Assert.False(Due(60, online: false, awaiting: true, child: true));
    }
}
