namespace ParentControl.Tray;

/// <summary>Dark palette shared by all tray windows (matches the admin app).</summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x0f, 0x11, 0x15);
    public static readonly Color Surface = Color.FromArgb(0x18, 0x1b, 0x22);
    public static readonly Color Surface2 = Color.FromArgb(0x20, 0x24, 0x2d);
    public static readonly Color Line = Color.FromArgb(0x2a, 0x2f, 0x3a);
    public static readonly Color Text = Color.FromArgb(0xe8, 0xea, 0xf0);
    public static readonly Color Muted = Color.FromArgb(0x9a, 0xa3, 0xb2);
    public static readonly Color Accent = Color.FromArgb(0x4f, 0x8c, 0xff);
    public static readonly Color Ok = Color.FromArgb(0x3e, 0xcf, 0x8e);
    public static readonly Color Warn = Color.FromArgb(0xf5, 0xa5, 0x24);
    public static readonly Color Danger = Color.FromArgb(0xff, 0x64, 0x64);

    public static Font UiFont(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    public static Button MakeButton(string text, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(72, 40),
            Padding = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Surface2,
            ForeColor = primary ? Color.FromArgb(0x0b, 0x12, 0x20) : Text,
            Font = UiFont(11, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 8, 8),
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Line;
        return b;
    }

    public static Label MakeLabel(string text, float size = 11, Color? color = null, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color ?? Text,
        Font = UiFont(size, style),
        Margin = new Padding(0, 0, 0, 8),
        BackColor = Color.Transparent,
    };

    public static Color ForRemaining(int? minutes) => minutes switch
    {
        null => Muted,
        <= 5 => Danger,
        <= 15 => Warn,
        _ => Ok,
    };
}
