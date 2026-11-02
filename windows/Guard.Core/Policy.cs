namespace ParentControl.Core;

public enum LockReason
{
    Window,
    Limit,
    Command,
    NotUnlocked,
    /// <summary>Someone interfered with the guard; until the admin clears it.</summary>
    Tamper,
    /// <summary>Registration is gone; only accounts known as children are blocked.</summary>
    NotConfigured,
}

public static class LockReasons
{
    /// <summary>Locks the child cannot ask more time for.</summary>
    public static bool AllowsRequest(LockReason r) => r is not (LockReason.Tamper or LockReason.NotConfigured);

    public static bool AllowsRequest(string? name) =>
        !Enum.TryParse<LockReason>(name, out var r) || AllowsRequest(r);
}

public static class RemainingReasons
{
    public const string Limit = "limit";
    public const string Window = "window";
    public const string Lock = "lock";
    /// <summary>Access granted by an approved request runs out.</summary>
    public const string Grant = "grant";
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
/// <remarks>
/// Fail-closed reasons (tamper lock, lost registration) never apply to local administrators:
/// whoever can repair the PC must always be able to sign in.
/// </remarks>
public sealed class Policy(GuardEngine engine, TimeProvider time, IAdminCheck? admins = null)
{
    private readonly IAdminCheck _admins = admins ?? NoAdmins.Instance;

    public Decision Evaluate(string? sid)
    {
        var sync = engine.State.LastSync;
        if (sync is null || !sync.DeviceEnabled) return Decision.Unrestricted;

        var childId = engine.ChildOf(sid);
        if (childId is null) return Decision.Unrestricted;
        var child = sync.Children.FirstOrDefault(c => c.ChildId == childId);

        // Fail-closed checks come first and do not depend on the schedule.
        var failClosed = engine.ConfigLost ? LockReason.NotConfigured
            : engine.State.TamperLocked ? LockReason.Tamper
            : (LockReason?)null;
        if (failClosed is not null && sid is not null && IsAdmin(sid)) failClosed = null;

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
                day = day with { BonusMinutes = 0, Unlocked = false };
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

        // An approved "more time" request opens access for that many minutes from the approval,
        // even outside the window, past the limit or on a day that is locked by default.
        var grantEnd = (sync.Requests ?? [])
            .Where(r => r.ChildId == childId && r.Kind == RequestKinds.Time && r.Status == RequestStatuses.Approved
                        && r.DecidedAt is { } at && at <= nowUtc)
            .Select(r => r.DecidedAt!.Value.AddMinutes(r.GrantedMinutes ?? r.Minutes))
            .Where(end => end > nowUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        int? grantLeft = grantEnd > nowUtc ? (int)Math.Ceiling((grantEnd - nowUtc).TotalMinutes) : null;

        LockReason? reason =
            failClosed is not null ? failClosed
            : activeLock ? LockReason.Command
            : !inWindow ? LockReason.Window
            : day is { RequiresUnlock: true, Unlocked: false } ? LockReason.NotUnlocked
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

        if (grantLeft is not null && reason is LockReason.Window or LockReason.NotUnlocked or LockReason.Limit)
        {
            // Schedule says no, the approved request says yes until it runs out.
            reason = null;
            Consider(grantLeft, RemainingReasons.Grant);
            Consider(nextLockIn, RemainingReasons.Lock);
        }
        else if (reason is null)
        {
            Consider(allowed - spent, RemainingReasons.Limit);
            if (to < 24 * 60) Consider((int)Math.Ceiling(to - minuteOfDay), RemainingReasons.Window);
            // A grant that outlasts the schedule keeps access open longer.
            if (grantLeft is not null && remaining is not null && grantLeft > remaining)
            {
                remaining = grantLeft;
                remainingBy = RemainingReasons.Grant;
            }
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

    private bool IsAdmin(string sid)
    {
        try
        {
            return _admins.IsAdmin(sid);
        }
        catch (Exception)
        {
            // Unknown: treat as admin so a failing lookup can never lock out the repair account.
            return true;
        }
    }

    /// <summary>"15:00" -> 900, "24:00" -> 1440.</summary>
    internal static int? ParseTime(string? hhmm)
    {
        if (string.IsNullOrEmpty(hhmm)) return null;
        var parts = hhmm.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        return h * 60 + m;
    }
}
