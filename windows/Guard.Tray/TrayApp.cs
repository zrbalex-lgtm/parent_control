using System.Runtime.InteropServices;
using ParentControl.Core;

namespace ParentControl.Tray;

/// <summary>Entry point for "ParentControl.Guard.exe tray": runs in the user's session, shows status, reports activity.</summary>
public static class TrayApp
{
    public static int Run()
    {
        FreeConsole(); // started from a console exe; the tray needs no console window

        using var single = new Mutex(true, @"Local\ParentControlTray", out var first);
        if (!first) return 0;

        var exit = 0;
        var thread = new Thread(() =>
        {
            // Never show the .NET crash dialog to the child: log and keep running.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Log(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new TrayContext());
            }
            catch (Exception ex)
            {
                Log(ex);
                exit = 1;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }

    /// <summary>%LOCALAPPDATA%\ParentControl\tray.log, trimmed when it grows past 1 MB.</summary>
    internal static void Log(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParentControl");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "tray.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\n\n");
        }
        catch (Exception)
        {
            // nothing sensible left to do
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
}

internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan ActivityEvery = TimeSpan.FromSeconds(15);

    private readonly NotifyIcon _icon;
    private readonly PipeClient _pipe;
    private StatusForm _statusForm = new();
    private readonly System.Windows.Forms.Timer _activityTimer = new();
    private TrayStatus? _status;
    private bool _connected;
    private LockForm? _lockForm;
    private FinalWarningForm? _finalForm;
    private readonly Dictionary<string, NoticeForm> _notices = [];
    private Icon? _currentIcon;

    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Подробнее", null, (_, _) => ShowStatus());
        _icon = new NotifyIcon { Text = Texts.Title, ContextMenuStrip = menu, Visible = false };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowStatus(); };
        _statusForm.RequestSubmitted += RequestTime;

        // Needed for the pipe callbacks to land on this (UI) thread.
        _ = _statusForm.Handle;
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _pipe = new PipeClient(ui, OnMessage, OnConnected);
        _pipe.Start();

        _activityTimer.Interval = (int)ActivityEvery.TotalMilliseconds;
        _activityTimer.Tick += (_, _) => { if (_pipe.Connected) _pipe.Send(ActivityReader.Read()); };
        _activityTimer.Start();

        Refresh();
    }

    private void OnConnected(bool connected)
    {
        _connected = connected;
        if (connected) _pipe.Send(ActivityReader.Read());
        Refresh();
    }

    private void OnMessage(IpcMessage m)
    {
        switch (m.Type)
        {
            case IpcTypes.Status:
                _status = m.Status;
                if (_status?.Lock is null) CloseLock(); // time was added or lock lifted
                _lockForm?.SetOpenRequest(_status?.RequestMinutes);
                Refresh();
                break;

            case IpcTypes.Warn when m.MinutesLeft is { } left:
                if (left <= 1)
                {
                    if (_finalForm is null || _finalForm.IsDisposed)
                    {
                        _finalForm = new FinalWarningForm();
                        _finalForm.Show();
                    }
                }
                else
                {
                    _icon.ShowBalloonTip(10000, Texts.Title, Texts.Warn(left), ToolTipIcon.Warning);
                }
                break;

            case IpcTypes.Lock:
                _finalForm?.Close();
                CloseLock();
                _lockForm = new LockForm(m.Text ?? Texts.Title, m.Seconds ?? 20, _status?.RequestMinutes);
                _lockForm.RequestSubmitted += RequestTime;
                _lockForm.Show();
                break;

            case IpcTypes.Notice when m.Text is not null:
                if (m.NoticeId is null or "info")
                {
                    _icon.ShowBalloonTip(5000, Texts.Title, m.Text, ToolTipIcon.Info);
                }
                else
                {
                    ShowNotice(m.NoticeId, m.Text);
                }
                break;
        }
    }

    private void ShowNotice(string id, string text)
    {
        if (_notices.TryGetValue(id, out var open) && !open.IsDisposed) return;
        var form = new NoticeForm(text);
        form.FormClosed += (_, _) =>
        {
            _notices.Remove(id);
            if (id != "info") _pipe.Send(new IpcMessage { Type = IpcTypes.AckNotice, NoticeId = id });
        };
        _notices[id] = form;
        form.Show();
    }

    private void RequestTime(int minutes, string? reason)
    {
        if (!_pipe.Connected)
        {
            _icon.ShowBalloonTip(5000, Texts.Title, "Нет связи со службой — попробуй через минуту.", ToolTipIcon.Warning);
            return;
        }
        _pipe.Send(new IpcMessage { Type = IpcTypes.RequestTime, Minutes = minutes, Reason = reason });
    }

    private void CloseLock()
    {
        if (_lockForm is { IsDisposed: false }) _lockForm.Close();
        _lockForm = null;
    }

    private void ShowStatus()
    {
        if (_statusForm.IsDisposed)
        {
            _statusForm = new StatusForm();
            _statusForm.RequestSubmitted += RequestTime;
        }
        _statusForm.Render(_status, _connected);
        _statusForm.ShowNearTray();
    }

    private void Refresh()
    {
        var restricted = _status?.Restricted == true;
        _icon.Visible = restricted || !_connected; // parents see nothing once connected
        var old = _currentIcon;
        _currentIcon = IconRenderer.Render(_status?.RemainingMinutes, _status?.Lock is not null, restricted);
        _icon.Icon = _currentIcon;
        old?.Dispose();

        var tip = !_connected ? "Нет связи со службой"
            : !restricted ? "Без ограничений"
            : _status!.Lock is not null ? "Время вышло"
            : $"Осталось {Texts.Remaining(_status.RemainingMinutes)}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;

        if (!_statusForm.IsDisposed && _statusForm.Visible) _statusForm.Render(_status, _connected);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _activityTimer.Dispose();
            _pipe.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _currentIcon?.Dispose();
            _statusForm.Dispose();
        }
        base.Dispose(disposing);
    }
}
