using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using ParentControl.Core;

namespace ParentControl.Service;

/// <summary>
/// After repeated re-logins while locked, denies Windows sign-in for the child account
/// through its logon hours (whole hours, so Windows lifts it by itself when the time is over).
/// Lifted early when access opens again (approved request, schedule, admin), or by
/// "ParentControl.Guard.exe unblock-logon" / reset-logon.cmd.
/// </summary>
public sealed class LogonBlocker(
    GuardEngine engine, Policy policy, IAdminCheck admins, DataDirectory data, TimeProvider time, ILogger<LogonBlocker> log)
{
    private bool _reconciled;

    /// <summary>Called by the enforcer when a child reached the re-login threshold.</summary>
    public void Block(SessionSample sample, Decision decision, int attempts)
    {
        if (sample.Sid is not { } sid || decision.ChildId is not { } child) return;
        if (SafeIsAdmin(sid))
        {
            log.LogInformation("Not blocking sign-in of {User}: administrator", sample.User);
            return;
        }
        var user = LocalUserName(sid);
        if (user is null)
        {
            log.LogWarning("Cannot block sign-in: {Sid} is not a local account", sid);
            return;
        }

        var now = time.GetUtcNow();
        var today = engine.LocalToday;
        var until = LogonHours.BlockUntil(now, time.LocalTimeZone, decision.Lock,
            engine.DayOf(child, today)?.WindowFrom, engine.DayOf(child, today.AddDays(1))?.WindowFrom);

        if (!SetHours(user, LogonHours.DenyBetween(now, until))) return;

        var blocks = engine.State.LogonBlocks;
        blocks.RemoveAll(b => b.Sid == sid);
        blocks.Add(new LogonBlock(sid, user, child, until));
        var untilLocal = TimeZoneInfo.ConvertTime(until, time.LocalTimeZone);
        log.LogWarning("Sign-in of {User} denied until {Until:HH:mm} after {Attempts} re-logins", user, untilLocal, attempts);
        engine.ReportTamper(TamperTypes.LoginBlocked, new()
        {
            ["user"] = user,
            ["until"] = until.ToString("O"),
            ["attempts"] = attempts.ToString(),
        }, user);
    }

    /// <summary>Every loop: restore sign-in when the block is over or access is open again.</summary>
    public void Tick()
    {
        var blocks = engine.State.LogonBlocks;
        var now = time.GetUtcNow();

        if (File.Exists(data.UnblockLogonFlag))
        {
            try { File.Delete(data.UnblockLogonFlag); } catch (IOException) { }
            foreach (var b in blocks.ToList()) Lift(b, "unblocked by an administrator");
            ResetKnownChildren();
            return;
        }

        if (!_reconciled)
        {
            // After a restart (or an installer that reset logon hours) put active blocks back in place.
            _reconciled = true;
            foreach (var b in blocks.Where(b => b.Until > now && b.User is not null).ToList())
            {
                SetHours(b.User!, LogonHours.DenyBetween(now, b.Until));
            }
        }

        foreach (var b in blocks.ToList())
        {
            if (b.Until <= now) Lift(b, "block time is over");
            else if (policy.Evaluate(b.Sid).Lock is null) Lift(b, "access is open again");
        }
    }

    private void Lift(LogonBlock b, string why)
    {
        if (b.User is not null) SetHours(b.User, LogonHours.AllAllowed());
        engine.State.LogonBlocks.Remove(b);
        log.LogInformation("Sign-in of {User} allowed again: {Why}", b.User, why);
    }

    /// <summary>Clears logon hours of every account bound to a child (manual unblock).</summary>
    private void ResetKnownChildren()
    {
        foreach (var a in engine.State.LastSync?.Accounts.Where(a => a.ChildId is not null) ?? [])
        {
            if (LocalUserName(a.Sid) is { } user) SetHours(user, LogonHours.AllAllowed());
        }
    }

    private bool SafeIsAdmin(string sid)
    {
        try
        {
            return admins.IsAdmin(sid);
        }
        catch (Exception)
        {
            return true; // unknown: never block a possible administrator
        }
    }

    /// <summary>"MACHINE\user" -> "user" for local accounts (Microsoft accounts are local too); null otherwise.</summary>
    internal static string? LocalUserName(string sid)
    {
        try
        {
            var name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            var parts = name.Split('\\');
            return parts.Length == 2 && parts[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool SetHours(string user, byte[] hours)
    {
        var buffer = Marshal.AllocHGlobal(hours.Length);
        try
        {
            Marshal.Copy(hours, 0, buffer, hours.Length);
            var info = new USER_INFO_1020 { usri1020_units_per_week = LogonHours.UnitsPerWeek, usri1020_logon_hours = buffer };
            var rc = NetUserSetInfo(null, user, 1020, ref info, out _);
            if (rc != 0) log.LogError("NetUserSetInfo({User}, logon hours) failed: {Code}", user, rc);
            return rc == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USER_INFO_1020
    {
        public uint usri1020_units_per_week;
        public IntPtr usri1020_logon_hours;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(string? serverName, string userName, int level, ref USER_INFO_1020 buf, out int parmErr);
}
