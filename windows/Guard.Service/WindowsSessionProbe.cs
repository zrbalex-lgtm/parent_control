using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using ParentControl.Core;

namespace ParentControl.Service;

/// <summary>Reads the physical console session via WTS: who is logged on and whether it is locked.</summary>
public sealed class WindowsSessionProbe(ILogger<WindowsSessionProbe> log) : ISessionProbe
{
    private readonly ConcurrentDictionary<string, string?> _sidCache = new(StringComparer.OrdinalIgnoreCase);

    public SessionSample Sample()
    {
        var sessionId = Native.WTSGetActiveConsoleSessionId();
        if (sessionId == Native.NoSession) return new SessionSample(HeartbeatStates.NoUser);

        var user = Native.QueryString(sessionId, Native.WTS_INFO_CLASS.WTSUserName);
        if (string.IsNullOrEmpty(user)) return new SessionSample(HeartbeatStates.NoUser); // logon screen
        var domain = Native.QueryString(sessionId, Native.WTS_INFO_CLASS.WTSDomainName);

        var (connectState, flags) = Native.QuerySessionStateAndFlags(sessionId);
        var locked = connectState != Native.WTSActive || flags == Native.WTS_SESSIONSTATE_LOCK;

        return new SessionSample(
            locked ? HeartbeatStates.Locked : HeartbeatStates.Active,
            ResolveSid(domain, user),
            user);
    }

    public uint ConsoleSessionId => Native.WTSGetActiveConsoleSessionId();

    private string? ResolveSid(string? domain, string user)
    {
        var key = $"{domain}\\{user}";
        return _sidCache.GetOrAdd(key, _ =>
        {
            try
            {
                var account = string.IsNullOrEmpty(domain) ? new NTAccount(user) : new NTAccount(domain, user);
                return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
            }
            catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
            {
                log.LogWarning("Cannot resolve SID for {Account}: {Message}", key, ex.Message);
                return null;
            }
        });
    }

    private static class Native
    {
        public const uint NoSession = 0xFFFFFFFF;
        public const int WTSActive = 0;
        public const int WTS_SESSIONSTATE_LOCK = 0;

        public enum WTS_INFO_CLASS
        {
            WTSUserName = 5,
            WTSDomainName = 7,
            WTSSessionInfoEx = 25,
        }

        [DllImport("kernel32.dll")]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WTSQuerySessionInformationW(
            IntPtr hServer, uint sessionId, WTS_INFO_CLASS infoClass, out IntPtr buffer, out uint bytesReturned);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);

        public static string? QueryString(uint sessionId, WTS_INFO_CLASS cls)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, cls, out var buf, out _)) return null;
            try
            {
                return Marshal.PtrToStringUni(buf);
            }
            finally
            {
                WTSFreeMemory(buf);
            }
        }

        // WTSINFOEXW: DWORD Level; then (8-byte aligned) WTSINFOEX_LEVEL1_W:
        // SessionId @+0, SessionState @+4, SessionFlags @+8.
        public static (int ConnectState, int Flags) QuerySessionStateAndFlags(uint sessionId)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTS_INFO_CLASS.WTSSessionInfoEx, out var buf, out _))
            {
                return (WTSActive, -1); // unknown: treat as unlocked so time is not under-counted
            }
            try
            {
                var level = Marshal.ReadInt32(buf, 0);
                if (level != 1) return (WTSActive, -1);
                return (Marshal.ReadInt32(buf, 8 + 4), Marshal.ReadInt32(buf, 8 + 8));
            }
            finally
            {
                WTSFreeMemory(buf);
            }
        }
    }
}
