using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ParentControl.Core;

namespace ParentControl.Tray;

/// <summary>Reads the foreground window (exe, product name, title) and seconds since last input.</summary>
internal static class ActivityReader
{
    private static readonly Dictionary<string, string?> ProductCache = new(StringComparer.OrdinalIgnoreCase);

    public static IpcMessage Read()
    {
        string? exe = null, product = null, title = null;
        var hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
        {
            title = WindowTitle(hwnd);
            GetWindowThreadProcessId(hwnd, out var pid);
            var path = ProcessPath(pid);
            if (path is not null)
            {
                exe = Path.GetFileName(path);
                product = Product(path);
            }
        }
        return new IpcMessage
        {
            Type = IpcTypes.Activity,
            Exe = exe,
            Product = product,
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            IdleSeconds = IdleSeconds(),
        };
    }

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

    private static int IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return (int)(idleMs / 1000);
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
