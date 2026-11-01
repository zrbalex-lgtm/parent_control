namespace ParentControl.Core;

public enum LockReason { Window, Limit, Command }

public static class RemainingReasons
{
    public const string Limit = "limit";
    public const string Window = "window";
    public const string Lock = "lock";
}

/// <summary>What the guard thinks about the current console user right now.</summary>
public sealed record Decision
{
    public static readonly Decision Unrestricted = new();

    /// <summary>The account is bound to a child and the device is enforcing.</summary>
    public bool Restricted { get; init; }
    public Guid? ChildId { get; init; }
    public string? ChildName { get; init; }
    public LockReason? Lock { get; init; }

    /// <summary>Minutes until the next lock (limit, window end or scheduled command); null = no end.</summary>
    public int? RemainingMinutes { get; init; }
    /// <summary>What ends the time first: "limit", "window" or "lock"; null if nothing.</summary>
    public string? RemainingBy { get; init; }
    public int SpentMinutes { get; init; }
    /// <summary>Limit + bonus for today; null = no limit.</summary>
    public int? AllowedMinutes { get; init; }
    public string? WindowFrom { get; init; }
    public string? WindowTo { get; init; }
    /// <summary>Settings came from an older day because the PC has been offline for long.</summary>
    public bool StaleSettings { get; init; }
}

/// <summary>Pure decision logic over the engine's cached server state.</summary>
public sealed class Policy(GuardEngine engine, TimeProvider time)
{
    public Decision Evaluate(string? sid)
    {
        var sync = engine.State.LastSync;
        if (sync is null || !sync.DeviceEnabled) return Decision.Unrestricted;

        var childId = engine.ChildOf(sid);
        if (childId is null) return Decision.Unrestricted;
        var child = sync.Children.FirstOrDefault(c => c.ChildId == childId);

        var nowUtc = time.GetUtcNow();
        var nowLocal = time.GetLocalNow();
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var minuteOfDay = nowLocal.TimeOfDay.TotalMinutes;

        // Today's settings; if offline past the cached days, reuse the latest known day without its bonus.
        var day = child?.Days.FirstOrDefault(d => d.LocalDate == today);
        var stale = false;
        if (day is null && child is not null)
        {
            day = child.Days.Where(d => d.LocalDate < today).MaxBy(d => d.LocalDate);
            if (day is not null)
            {
                day = day with { BonusMinutes = 0 };
                stale = true;
            }
        }

        var spent = engine.SpentMinutes(childId.Value, today);
        int? allowed = day?.LimitMinutes is { } limit ? Math.Max(0, limit + day.BonusMinutes) : null;

        var from = ParseTime(day?.WindowFrom) ?? 0;
        var to = ParseTime(day?.WindowTo) ?? 24 * 60;
        var inWindow = minuteOfDay >= from && minuteOfDay < to;

        // Locks from the admin: active now, or scheduled ("lock in 5 minutes").
        var locks = sync.Locks.Where(l => l.ChildId == childId && (l.LockUntil is null || l.LockUntil > nowUtc)).ToList();
        var activeLock = locks.Any(l => l.LockFrom <= nowUtc);
        var nextLockIn = locks.Where(l => l.LockFrom > nowUtc)
            .Select(l => (int?)Math.Ceiling((l.LockFrom - nowUtc).TotalMinutes))
            .Min();

        LockReason? reason =
            activeLock ? LockReason.Command
            : !inWindow ? LockReason.Window
            : allowed is { } a && spent >= a ? LockReason.Limit
            : null;

        int? remaining = null;
        string? remainingBy = null;
        void Consider(int? minutes, string by)
        {
            if (minutes is null || (remaining is not null && minutes >= remaining)) return;
            remaining = minutes;
            remainingBy = by;
        }
        if (reason is null)
        {
            Consider(allowed - spent, RemainingReasons.Limit);
            if (to < 24 * 60) Consider((int)Math.Ceiling(to - minuteOfDay), RemainingReasons.Window);
            Consider(nextLockIn, RemainingReasons.Lock);
        }

        return new Decision
        {
            Restricted = true,
            ChildId = childId,
            ChildName = child?.Name,
            Lock = reason,
            RemainingMinutes = remaining,
            RemainingBy = remainingBy,
            SpentMinutes = spent,
            AllowedMinutes = allowed,
            WindowFrom = day?.WindowFrom,
            WindowTo = day?.WindowTo,
            StaleSettings = stale,
        };
    }

    private static int? Min(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    /// <summary>"15:00" -> 900, "24:00" -> 1440.</summary>
    internal static int? ParseTime(string? hhmm)
    {
        if (string.IsNullOrEmpty(hhmm)) return null;
        var parts = hhmm.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        return h * 60 + m;
    }
}
