namespace ParentControl.Core;

/// <summary>A child account whose Windows sign-in is temporarily denied.</summary>
public sealed record LogonBlock(string Sid, string? User, Guid? ChildId, DateTimeOffset Until);

/// <summary>
/// Windows logon hours: 168 bits (one per hour of the week, starting Sunday 00:00 UTC).
/// Blocks are whole hours, so Windows lets the child in again by itself when the block ends,
/// even if the guard is not running.
/// </summary>
public static class LogonHours
{
    public const int UnitsPerWeek = 168;
    public static readonly TimeSpan MaxBlock = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinBlock = TimeSpan.FromHours(1);

    public static byte[] AllAllowed() => Enumerable.Repeat((byte)0xFF, UnitsPerWeek / 8).ToArray();

    /// <summary>Bitmap allowing every hour except [from, until) (UTC, whole hours).</summary>
    public static byte[] DenyBetween(DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        var bits = AllAllowed();
        for (var t = FloorHour(fromUtc); t < untilUtc; t = t.AddHours(1))
        {
            var index = HourOfWeek(t);
            bits[index / 8] &= (byte)~(1 << (index % 8));
        }
        return bits;
    }

    /// <summary>0 = Sunday 00:00–01:00 UTC.</summary>
    public static int HourOfWeek(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return (int)u.DayOfWeek * 24 + u.Hour;
    }

    public static DateTimeOffset FloorHour(DateTimeOffset t)
    {
        var u = t.ToUniversalTime();
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }

    public static DateTimeOffset CeilHour(DateTimeOffset t)
    {
        var f = FloorHour(t);
        return f == t.ToUniversalTime() ? f : f.AddHours(1);
    }

    /// <summary>
    /// How long to deny sign-in: at least an hour; if it is before today's window, until the window opens;
    /// after today's window, until the next day's window. Rounded up to a full hour, at most 24 hours.
    /// </summary>
    public static DateTimeOffset BlockUntil(DateTimeOffset nowUtc, TimeZoneInfo zone, LockReason? reason,
        string? todayFrom, string? tomorrowFrom)
    {
        var until = nowUtc + MinBlock;
        if (reason == LockReason.Window)
        {
            var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
            var today = local.Date;
            DateTimeOffset? next = null;
            if (Minutes(todayFrom) is { } f && local.TimeOfDay.TotalMinutes < f)
            {
                next = At(today.AddMinutes(f), zone);
            }
            else if (Minutes(tomorrowFrom) is { } t)
            {
                next = At(today.AddDays(1).AddMinutes(t), zone);
            }
            if (next is { } n && n > until) until = n;
        }
        until = CeilHour(until);
        var cap = CeilHour(nowUtc + MaxBlock);
        return until > cap ? cap : until;
    }

    private static DateTimeOffset At(DateTime localUnspecified, TimeZoneInfo zone) =>
        new(localUnspecified, zone.GetUtcOffset(localUnspecified));

    private static int? Minutes(string? hhmm)
    {
        if (string.IsNullOrEmpty(hhmm)) return null;
        var p = hhmm.Split(':');
        return p.Length >= 2 && int.TryParse(p[0], out var h) && int.TryParse(p[1], out var m) ? h * 60 + m : null;
    }
}
