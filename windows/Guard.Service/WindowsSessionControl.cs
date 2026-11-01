using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ParentControl.Core;

namespace ParentControl.Service;

/// <summary>WTS actions on a user session (requires LocalSystem or an elevated admin).</summary>
public sealed class WindowsSessionControl(ILogger<WindowsSessionControl> log) : ISessionControl
{
    private const uint MB_OK = 0x0;
    private const uint MB_ICONWARNING = 0x30;
    private const uint MB_SYSTEMMODAL = 0x1000;
    private const uint MB_SETFOREGROUND = 0x10000;
    private const uint MB_TOPMOST = 0x40000;

    public void ShowMessage(uint sessionId, string title, string text, int timeoutSeconds)
    {
        // bWait=false: returns immediately, the box closes itself after the timeout.
        var ok = WTSSendMessageW(IntPtr.Zero, sessionId,
            title, (uint)(title.Length * 2), text, (uint)(text.Length * 2),
            MB_OK | MB_ICONWARNING | MB_SYSTEMMODAL | MB_SETFOREGROUND | MB_TOPMOST,
            (uint)timeoutSeconds, out _, false);
        if (!ok) log.LogWarning("WTSSendMessage to session {Session} failed: {Error}", sessionId, Marshal.GetLastPInvokeError());
    }

    public bool Disconnect(uint sessionId)
    {
        var ok = WTSDisconnectSession(IntPtr.Zero, sessionId, false);
        if (!ok) log.LogError("WTSDisconnectSession({Session}) failed: {Error}", sessionId, Marshal.GetLastPInvokeError());
        return ok;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSDisconnectSession(IntPtr hServer, uint sessionId, bool bWait);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSSendMessageW(
        IntPtr hServer, uint sessionId,
        string pTitle, uint titleLength,
        string pMessage, uint messageLength,
        uint style, uint timeout, out uint response, bool bWait);
}
