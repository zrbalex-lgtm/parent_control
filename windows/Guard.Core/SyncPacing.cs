namespace ParentControl.Core;

/// <summary>
/// How often to ask the server between the regular once-a-minute syncs, so changes made
/// by an adult (bonus time, unlock, lock) reach the PC within seconds while they matter.
/// </summary>
public static class SyncPacing
{
    /// <summary>While a child waits for an answer to a request.</summary>
    public static readonly TimeSpan AnswerEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan AnswerFor = TimeSpan.FromMinutes(15);

    /// <summary>While a child is at the PC or was recently sent to the sign-in screen.</summary>
    public static readonly TimeSpan ChildEvery = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AfterDisconnect = TimeSpan.FromMinutes(15);

    /// <summary>True when an extra sync is due now.</summary>
    public static bool ShouldSync(DateTimeOffset now, DateTimeOffset lastSyncAt, bool online,
        bool awaitingAnswer, bool childPresent, DateTimeOffset? lastDisconnectAt)
    {
        if (!online) return false;
        var since = now - lastSyncAt;
        if (awaitingAnswer && since >= AnswerEvery) return true;
        var childRelevant = childPresent || (lastDisconnectAt is { } d && now - d < AfterDisconnect);
        return childRelevant && since >= ChildEvery;
    }
}
