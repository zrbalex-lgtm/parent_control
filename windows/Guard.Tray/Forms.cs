using System.ComponentModel;
using ParentControl.Core;

namespace ParentControl.Tray;

/// <summary>"Ask for more time": optional reason + one tap on 15/30/60.</summary>
internal sealed class RequestPanel : FlowLayoutPanel
{
    private readonly float _scale;
    private readonly Label _title;
    private readonly TextBox _reason;
    private readonly FlowLayoutPanel _buttons;
    private readonly Label _pending;
    private bool _unlockMode;

    /// <summary>(minutes, reason, kind)</summary>
    public event Action<int, string?, string>? Submitted;

    public RequestPanel(float scale = 1f)
    {
        _scale = scale;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        BackColor = Color.Transparent;

        _title = Theme.MakeLabel("Попросить ещё времени", 12 * scale, Theme.Text, FontStyle.Bold);
        Controls.Add(_title);
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

        _buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 4) };
        Controls.Add(_buttons);
        BuildButtons();

        _pending = Theme.MakeLabel("", 12 * scale, Theme.Warn);
        _pending.Visible = false;
        Controls.Add(_pending);
    }

    /// <summary>Unlock mode asks to open today's access instead of asking for minutes.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool UnlockMode
    {
        get => _unlockMode;
        set
        {
            if (_unlockMode == value) return;
            _unlockMode = value;
            BuildButtons();
        }
    }

    private void BuildButtons()
    {
        _title.Text = _unlockMode ? "Попросить открыть доступ" : "Попросить ещё времени";
        _reason.PlaceholderText = _unlockMode ? "Например: уроки сделал (необязательно)" : "Зачем? (необязательно)";
        _buttons.Controls.Clear();
        string? Reason() => string.IsNullOrWhiteSpace(_reason.Text) ? null : _reason.Text.Trim();
        if (_unlockMode)
        {
            var b = Theme.MakeButton("Попросить открыть доступ", primary: true);
            b.Font = Theme.UiFont(11 * _scale, FontStyle.Bold);
            b.Click += (_, _) => Submitted?.Invoke(0, Reason(), RequestKinds.Unlock);
            _buttons.Controls.Add(b);
            return;
        }
        foreach (var m in new[] { 15, 30, 60 })
        {
            var b = Theme.MakeButton($"+{m} мин", primary: m == 30);
            b.Font = Theme.UiFont(11 * _scale, FontStyle.Bold);
            b.Click += (_, _) => Submitted?.Invoke(m, Reason(), RequestKinds.Time);
            _buttons.Controls.Add(b);
        }
    }

    /// <summary>Shows either the request controls or "waiting for an answer".</summary>
    public void SetOpenRequest(int? minutes, string? kind)
    {
        var open = minutes is not null;
        _reason.Visible = !open;
        _buttons.Visible = !open;
        _pending.Visible = open;
        if (open) _pending.Text = Texts.OpenRequest(minutes!.Value, kind ?? RequestKinds.Time);
    }
}

/// <summary>Small window above the tray icon with today's numbers.</summary>
internal sealed class StatusForm : Form
{
    private readonly Label _name = Theme.MakeLabel("", 12, Theme.Muted);
    private readonly Label _remaining = Theme.MakeLabel("", 30, Theme.Text, FontStyle.Bold);
    private readonly Label _remainingBy = Theme.MakeLabel("", 10, Theme.Muted);
    private readonly Label _spent = Theme.MakeLabel("", 11, Theme.Muted);
    private readonly Label _window = Theme.MakeLabel("", 11, Theme.Muted);
    private readonly Label _link = Theme.MakeLabel("", 10, Theme.Muted);
    private readonly RequestPanel _request = new();

    public event Action<int, string?, string>? RequestSubmitted;

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

        // Padding on the auto-sized panel (form padding is ignored by AutoSize forms).
        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(20, 16, 20, 12),
        };
        _remaining.Margin = new Padding(0, 0, 0, 0);
        layout.Controls.AddRange([_name, _remaining, _remainingBy, _spent, _window, _link]);
        _request.Margin = new Padding(0, 12, 0, 0);
        _request.Submitted += (m, r, k) => RequestSubmitted?.Invoke(m, r, k);
        layout.Controls.Add(_request);
        Controls.Add(layout);

        Deactivate += (_, _) => Hide();
        // The close box only hides the window; it is reused for every tray click.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    public void Render(TrayStatus? s, bool connected)
    {
        if (s is null || !s.Restricted)
        {
            _name.Text = connected ? "Без ограничений" : "Нет связи со службой";
            _remaining.Text = "∞";
            _remaining.ForeColor = Theme.Muted;
            _remainingBy.Text = _spent.Text = _window.Text = "";
            _link.Text = "";
            _request.Visible = false;
            return;
        }

        _name.Text = s.ChildName ?? "";
        _remaining.Text = s.Lock is null ? Texts.Remaining(s.RemainingMinutes) : Texts.LockShort(s.Lock);
        _remaining.ForeColor = s.Lock is not null ? Theme.Danger : Theme.ForRemaining(s.RemainingMinutes);
        _remainingBy.Text = s.Lock is not null ? "" : Texts.RemainingBy(s.RemainingBy, s.WindowTo);
        _remainingBy.Visible = _remainingBy.Text.Length > 0;
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
        _request.Visible = LockReasons.AllowsRequest(s.Lock);
        _request.UnlockMode = s.Lock == nameof(LockReason.NotUnlocked);
        _request.SetOpenRequest(s.RequestMinutes, s.RequestKind);
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

    public event Action<int, string?, string>? RequestSubmitted;

    public LockForm(string text, int seconds, bool unlockMode, int? openRequestMinutes, string? openRequestKind, bool allowRequest = true)
    {
        _seconds = seconds;
        Body.Controls.Add(Theme.MakeLabel(text, 26, Theme.Text, FontStyle.Bold));
        Body.Controls.Add(Theme.MakeLabel(Texts.LockFooter, 14, Theme.Muted));
        Body.Controls.Add(_countdown);

        _request.Margin = new Padding(0, 24, 0, 0);
        _request.UnlockMode = unlockMode;
        _request.SetOpenRequest(openRequestMinutes, openRequestKind);
        _request.Submitted += (m, r, k) =>
        {
            _request.SetOpenRequest(m, k);
            RequestSubmitted?.Invoke(m, r, k);
        };
        if (allowRequest) Body.Controls.Add(_request);

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

    public void SetOpenRequest(int? minutes, string? kind)
    {
        if (minutes is not null) _request.SetOpenRequest(minutes, kind);
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

        var body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
            Padding = new Padding(28, 22, 28, 20),
        };
        var label = Theme.MakeLabel(text, 14, Theme.Text);
        label.MaximumSize = new Size(480, 0);
        label.MinimumSize = new Size(300, 0);
        body.Controls.Add(label);
        var ok = Theme.MakeButton("OK", primary: true);
        ok.MinimumSize = new Size(96, 40);
        ok.Margin = new Padding(0, 16, 0, 0);
        ok.Anchor = AnchorStyles.Right; // right-aligned within the column
        ok.Click += (_, _) => Close();
        body.Controls.Add(ok);
        AcceptButton = ok;
        Controls.Add(body);
    }
}
