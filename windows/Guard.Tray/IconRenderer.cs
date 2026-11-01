using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ParentControl.Tray;

/// <summary>Draws the tray icon: a colored disc with minutes left (or a status glyph).</summary>
internal static class IconRenderer
{
    public static Icon Render(int? remaining, bool locked, bool restricted)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var color = !restricted ? Theme.Muted : locked ? Theme.Danger : Theme.ForRemaining(remaining);
            using (var brush = new SolidBrush(color)) g.FillEllipse(brush, 1, 1, size - 2, size - 2);

            var text = !restricted ? "∞"
                : locked ? "×"
                : remaining is null ? "∞"
                : remaining >= 100 ? $"{remaining / 60}ч"
                : remaining.Value.ToString();
            var fontSize = text.Length >= 3 ? 11f : text.Length == 2 ? 14f : 17f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Color.FromArgb(0x0b, 0x0d, 0x12));
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, ink, new RectangleF(0, 1, size, size), fmt);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            // Clone so the GDI handle can be released right away.
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
