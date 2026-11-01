using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ParentControl.Core;

namespace ParentControl.Service;

/// <summary>
/// Session actions. UI goes to the tray app when it is connected in that session,
/// otherwise to a system message box (WTSSendMessage). Disconnect needs LocalSystem or admin.
/// </summary>
public sealed class WindowsSessionControl(TrayHub hub, GuardEngine engine, ILogger<WindowsSessionControl> log) : ISessionControl
{
    private const uint MB_OK = 0x0;
    private const uint MB_ICONWARNING = 0x30;
    private const uint MB_SYSTEMMODAL = 0x1000;
    private const uint MB_SETFOREGROUND = 0x10000;
    private const uint MB_TOPMOST = 0x40000;

    public void Warn(uint sessionId, int minutesLeft)
    {
        if (hub.Send(sessionId, new IpcMessage { Type = IpcTypes.Warn, MinutesLeft = minutesLeft })) return;
        ShowMessageBox(sessionId, Texts.Warn(minutesLeft), 30);
    }

    public void ShowLock(uint sessionId, LockReason reason, Decision decision, TimeSpan grace)
    {
        var text = Texts.Lock(reason, decision.WindowFrom, decision.WindowTo);
        var canRequest = decision.ChildId is { } child && engine.OpenRequestFor(child) is null;
        var sent = hub.Send(sessionId, new IpcMessage
        {
            Type = IpcTypes.Lock,
            Text = text,
            Seconds = (int)grace.TotalSeconds,
            CanRequest = canRequest,
        });
        if (!sent) ShowMessageBox(sessionId, $"{text}\n{Texts.LockFooter}", (int)grace.TotalSeconds + 5);
    }

    public bool Disconnect(uint sessionId)
    {
        var ok = WTSDisconnectSession(IntPtr.Zero, sessionId, false);
        if (!ok) log.LogError("WTSDisconnectSession({Session}) failed: {Error}", sessionId, Marshal.GetLastPInvokeError());
        return ok;
    }

    /// <summary>System message box in the session; returns immediately, closes itself after the timeout.</summary>
    public void ShowMessageBox(uint sessionId, string text, int timeoutSeconds)
    {
        var title = Texts.Title;
        var ok = WTSSendMessageW(IntPtr.Zero, sessionId,
            title, (uint)(title.Length * 2), text, (uint)(text.Length * 2),
            MB_OK | MB_ICONWARNING | MB_SYSTEMMODAL | MB_SETFOREGROUND | MB_TOPMOST,
            (uint)timeoutSeconds, out _, false);
        if (!ok) log.LogWarning("WTSSendMessage to session {Session} failed: {Error}", sessionId, Marshal.GetLastPInvokeError());
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
