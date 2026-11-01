using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ParentControl.Service;

/// <summary>
/// Starts "ParentControl.Guard.exe tray" inside a user's session as that user.
/// Works only when the service runs as LocalSystem (WTSQueryUserToken needs SeTcbPrivilege).
/// </summary>
public sealed class TrayLauncher(ILogger<TrayLauncher> log)
{
    private bool _warnedNoPrivilege;

    public bool TryLaunch(uint sessionId)
    {
        if (!WTSQueryUserToken(sessionId, out var token))
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == ERROR_PRIVILEGE_NOT_HELD)
            {
                if (!_warnedNoPrivilege)
                {
                    log.LogWarning("Cannot start the tray app: not running as a service (LocalSystem). Start it manually: ParentControl.Guard.exe tray");
                    _warnedNoPrivilege = true;
                }
            }
            else
            {
                log.LogWarning("WTSQueryUserToken({Session}) failed: {Error}", sessionId, err);
            }
            return false;
        }

        var env = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out env, token, false)) env = IntPtr.Zero;

            var exe = Environment.ProcessPath!;
            var cmd = new StringBuilder($"\"{exe}\" tray");
            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
            };
            var flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
            if (!CreateProcessAsUserW(token, null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, env,
                    Path.GetDirectoryName(exe), ref si, out var pi))
            {
                log.LogWarning("CreateProcessAsUser failed: {Error}", Marshal.GetLastPInvokeError());
                return false;
            }
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            log.LogInformation("Tray started in session {Session} (pid {Pid})", sessionId, pi.dwProcessId);
            return true;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            CloseHandle(token);
        }
    }

    private const int ERROR_PRIVILEGE_NOT_HELD = 1314;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUserW(
        IntPtr token, string? applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
