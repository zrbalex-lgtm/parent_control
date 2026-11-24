using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ParentControl.Core;

namespace ParentControl.Tray;

/// <summary>
/// Closes the foreground window when it belongs to a blocked app group: a browser tab (Ctrl+W),
/// never the whole browser; any other app is asked to close, then ended if it stays.
/// What is blocked is decided by the service; this only carries it out in the user's session.
/// </summary>
internal sealed class BlockEnforcer : IDisposable
{
    private static readonly TimeSpan NoticeEvery = TimeSpan.FromSeconds(20);

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Action<TrayBlock> _notify;
    private readonly Action<IpcMessage> _report;
    private readonly int _ownPid = Environment.ProcessId;
    private List<TrayBlock> _blocks = [];
    private IntPtr _lastWindow;
    private int _attempts;
    private readonly Dictionary<string, DateTime> _lastNotice = new(StringComparer.OrdinalIgnoreCase);

    public BlockEnforcer(Action<TrayBlock> notify, Action<IpcMessage> report)
    {
        _notify = notify;
        _report = report;
        _timer.Tick += (_, _) => Check();
    }

    public void Update(List<TrayBlock>? blocks)
    {
        _blocks = blocks ?? [];
        _timer.Enabled = _blocks.Count > 0;
        if (_blocks.Count > 0) Check();
    }

    private void Check()
    {
        try
        {
            CheckCore();
        }
        catch (Exception ex)
        {
            TrayApp.Log(ex);
        }
    }

    private void CheckCore()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == _ownPid) return;

        var path = ProcessPath(pid);
        var exe = path is null ? null : Path.GetFileName(path);
        var title = WindowTitle(hwnd);
        var product = path is null ? null : Product(path);
        var block = AppBlocks.Match(_blocks, exe, product, title, DateTimeOffset.UtcNow);
        if (block is null)
        {
            _lastWindow = IntPtr.Zero;
            _attempts = 0;
            return;
        }

        // Count tries per window: the gentle way first, a firmer one if the window stays.
        _attempts = hwnd == _lastWindow ? _attempts + 1 : 1;
        _lastWindow = hwnd;

        string done;
        if (AppBlocks.ActionFor(exe) == BlockAction.CloseTab)
        {
            if (_attempts <= 3)
            {
                SendCtrlW();
                done = "tab";
            }
            else
            {
                // The tab does not close (e.g. a dialog has focus): hide the window instead, the browser stays.
                ShowWindow(hwnd, SW_MINIMIZE);
                done = "minimize";
                _attempts = 0;
            }
        }
        else if (_attempts == 1)
        {
            PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            done = "close";
        }
        else if (_attempts >= 3)
        {
            done = KillProcess(pid) ? "kill" : "minimize";
            if (done == "minimize") ShowWindow(hwnd, SW_MINIMIZE);
            _attempts = 0;
        }
        else
        {
            return; // give the app a moment to close by itself
        }

        _report(new IpcMessage { Type = IpcTypes.AppBlocked, Group = block.Group, Kind = done, Exe = exe, Title = title });
        var now = DateTime.UtcNow;
        if (!_lastNotice.TryGetValue(block.Group, out var last) || now - last > NoticeEvery)
        {
            _lastNotice[block.Group] = now;
            _notify(block);
        }
    }

    private static bool KillProcess(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            p.Kill();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static void SendCtrlW()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, false), Key(VK_W, false), Key(VK_W, true), Key(VK_CONTROL, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    private static string? WindowTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLengthW(hwnd);
        if (len <= 0) return null;
        var sb = new StringBuilder(Math.Min(len, 1024) + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? ProcessPath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static readonly Dictionary<string, string?> ProductCache = new(StringComparer.OrdinalIgnoreCase);

    private static string? Product(string path)
    {
        if (ProductCache.TryGetValue(path, out var cached)) return cached;
        string? product = null;
        try
        {
            product = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim();
            if (string.IsNullOrEmpty(product)) product = null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
        }
        if (ProductCache.Count > 500) ProductCache.Clear();
        ProductCache[path] = product;
        return product;
    }

    public void Dispose() => _timer.Dispose();

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint WM_CLOSE = 0x0010;
    private const int SW_MINIMIZE = 6;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_W = 0x57;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // Sized for the largest member (MOUSEINPUT) so Marshal.SizeOf matches what SendInput expects.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
