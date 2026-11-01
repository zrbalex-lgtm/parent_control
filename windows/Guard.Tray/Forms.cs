using ParentControl.Core;

namespace ParentControl.Tray;

/// <summary>"Ask for more time": optional reason + one tap on 15/30/60.</summary>
internal sealed class RequestPanel : FlowLayoutPanel
{
    private readonly TextBox _reason;
    private readonly FlowLayoutPanel _buttons;
    private readonly Label _pending;

    public event Action<int, string?>? Submitted;

    public RequestPanel(float scale = 1f)
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        BackColor = Color.Transparent;

        Controls.Add(Theme.MakeLabel("Попросить ещё времени", 12 * scale, Theme.Text, FontStyle.Bold));
        _reason = new TextBox
        {
            Width = (int)(320 * scale),
            Font = Theme.UiFont(11 * scale),
            BackColor = Theme.Surface2,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "Зачем? (необязательно)",
            MaxLength = 200,
            Margin = new Padding(0, 0, 0, 10),
        };
        Controls.Add(_reason);

        _buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        foreach (var m in new[] { 15, 30, 60 })
        {
            var b = Theme.MakeButton($"+{m} мин", primary: m == 30);
            b.Font = Theme.UiFont(11 * scale, FontStyle.Bold);
            b.Click += (_, _) => Submitted?.Invoke(m, string.IsNullOrWhiteSpace(_reason.Text) ? null : _reason.Text.Trim());
            _buttons.Controls.Add(b);
        }
        Controls.Add(_buttons);

        _pending = Theme.MakeLabel("", 12 * scale, Theme.Warn);
        _pending.Visible = false;
        Controls.Add(_pending);
    }

    /// <summary>Shows either the request controls or "waiting for an answer".</summary>
    public void SetOpenRequest(int? minutes)
    {
        var open = minutes is not null;
        _reason.Visible = !open;
        _buttons.Visible = !open;
        _pending.Visible = open;
        if (open) _pending.Text = $"Запрос на {minutes} мин отправлен — ждём ответа.";
    }
}

/// <summary>Small window above the tray icon with today's numbers.</summary>
internal sealed class StatusForm : Form
{
    private readonly Label _name = Theme.MakeLabel("", 12, Theme.Muted);
    private readonly Label _remaining = Theme.MakeLabel("", 30, Theme.Text, FontStyle.Bold);
    private readonly Label _spent = Theme.MakeLabel("", 11, Theme.Muted);
    private readonly Label _window = Theme.MakeLabel("", 11, Theme.Muted);
    private readonly Label _link = Theme.MakeLabel("", 10, Theme.Muted);
    private readonly RequestPanel _request = new();

    public event Action<int, string?>? RequestSubmitted;

    public StatusForm()
    {
        Text = Texts.Title;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.Surface;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        layout.Controls.AddRange([_name, _remaining, _spent, _window, _link]);
        _request.Margin = new Padding(0, 12, 0, 0);
        _request.Submitted += (m, r) => RequestSubmitted?.Invoke(m, r);
        layout.Controls.Add(_request);
        Controls.Add(layout);

        Deactivate += (_, _) => Hide();
    }

    public void Render(TrayStatus? s, bool connected)
    {
        if (s is null || !s.Restricted)
        {
            _name.Text = connected ? "Без ограничений" : "Нет связи со службой";
            _remaining.Text = "∞";
            _remaining.ForeColor = Theme.Muted;
            _spent.Text = _window.Text = "";
            _link.Text = "";
            _request.Visible = false;
            return;
        }

        _name.Text = s.ChildName ?? "";
        _remaining.Text = s.Lock is not null ? "Время вышло" : Texts.Remaining(s.RemainingMinutes);
        _remaining.ForeColor = s.Lock is not null ? Theme.Danger : Theme.ForRemaining(s.RemainingMinutes);
        _spent.Text = s.AllowedMinutes is { } a
            ? $"Использовано {Texts.Remaining(s.SpentMinutes)} из {Texts.Remaining(a)}"
            : $"Использовано {Texts.Remaining(s.SpentMinutes)}";
        _window.Text = s.WindowFrom is { } f && s.WindowTo is { } t && !(f == "00:00" && t == "24:00")
            ? $"Можно сегодня: {f}–{t}"
            : "Можно весь день";
        _link.Text = !connected ? "Нет связи со службой"
            : s.Online ? "Связь с сервером есть"
            : s.StaleSettings ? "Нет интернета — работают старые настройки"
            : "Нет интернета — время считается на этом компьютере";
        _link.ForeColor = connected && s.Online ? Theme.Muted : Theme.Warn;
        _request.Visible = true;
        _request.SetOpenRequest(s.RequestMinutes);
    }

    public void ShowNearTray()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        Show();
        Activate();
    }
}

/// <summary>Full-screen overlay base: dark, top-most, on the primary screen.</summary>
internal abstract class OverlayForm : Form
{
    protected readonly FlowLayoutPanel Body = new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        BackColor = Color.Transparent,
    };

    protected OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Theme.Bg;
        Opacity = 0.96;
        KeyPreview = true;
        Controls.Add(Body);
        Resize += (_, _) => Center();
        Body.SizeChanged += (_, _) => Center();
    }

    private void Center() =>
        Body.Location = new Point(Math.Max(0, (ClientSize.Width - Body.Width) / 2), Math.Max(0, (ClientSize.Height - Body.Height) / 2));

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Center();
        Activate();
    }
}

/// <summary>Last minute: "save your game".</summary>
internal sealed class FinalWarningForm : OverlayForm
{
    public FinalWarningForm()
    {
        Body.Controls.Add(Theme.MakeLabel("Осталась 1 минута", 40, Theme.Warn, FontStyle.Bold));
        Body.Controls.Add(Theme.MakeLabel("Сохрани игру и закончи то, что делаешь.", 20, Theme.Text));
        var ok = Theme.MakeButton("Понятно", primary: true);
        ok.Font = Theme.UiFont(14, FontStyle.Bold);
        ok.Margin = new Padding(0, 24, 0, 0);
        ok.Click += (_, _) => Close();
        Body.Controls.Add(ok);
        AcceptButton = ok;

        var timer = new System.Windows.Forms.Timer { Interval = 15000 };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
        FormClosed += (_, _) => timer.Dispose();
    }
}

/// <summary>Shown while the session is about to be disconnected; offers to ask for more time.</summary>
internal sealed class LockForm : OverlayForm
{
    private readonly Label _countdown = Theme.MakeLabel("", 14, Theme.Muted);
    private readonly RequestPanel _request = new(1.25f);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private int _seconds;

    public event Action<int, string?>? RequestSubmitted;

    public LockForm(string text, int seconds, int? openRequestMinutes)
    {
        _seconds = seconds;
        Body.Controls.Add(Theme.MakeLabel(text, 26, Theme.Text, FontStyle.Bold));
        Body.Controls.Add(Theme.MakeLabel(Texts.LockFooter, 14, Theme.Muted));
        Body.Controls.Add(_countdown);

        _request.Margin = new Padding(0, 24, 0, 0);
        _request.SetOpenRequest(openRequestMinutes);
        _request.Submitted += (m, r) =>
        {
            _request.SetOpenRequest(m);
            RequestSubmitted?.Invoke(m, r);
        };
        Body.Controls.Add(_request);

        Tick();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
    }

    private void Tick()
    {
        _countdown.Text = $"Выход через {_seconds} с";
        if (_seconds-- <= 0) Close();
    }

    public void SetOpenRequest(int? minutes)
    {
        if (minutes is not null) _request.SetOpenRequest(minutes);
    }
}

/// <summary>A message for the child (admin message or answer to a request).</summary>
internal sealed class NoticeForm : Form
{
    public NoticeForm(string text)
    {
        Text = Texts.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        BackColor = Theme.Surface;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24);

        var body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
        };
        var label = Theme.MakeLabel(text, 14, Theme.Text);
        label.MaximumSize = new Size(480, 0);
        body.Controls.Add(label);
        var ok = Theme.MakeButton("OK", primary: true);
        ok.Margin = new Padding(0, 16, 0, 0);
        ok.Click += (_, _) => Close();
        body.Controls.Add(ok);
        AcceptButton = ok;
        Controls.Add(body);
    }
}
