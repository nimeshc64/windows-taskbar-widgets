using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace BtBattery;

static class IconRenderer
{
    public static int TraySize
    {
        get
        {
            try { return Math.Max(16, Native.GetSystemMetricsForDpi(49 , Native.GetDpiForSystem())); }
            catch { return 16; }
        }
    }

    public static Bitmap Battery(int pct, bool darkTaskbar, int low)
    {
        int n = TraySize;
        var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var fg = darkTaskbar ? Color.White : Color.Black;
        var level = Theme.LevelColor(pct, low);
        float barH = Math.Max(2f, MathF.Round(n / 7f));
        float textArea = n - barH - Math.Max(1f, n / 16f);

        using (var path = new GraphicsPath())
        {
            path.AddString(pct.ToString(), new FontFamily("Segoe UI"), (int)FontStyle.Bold, 100f, PointF.Empty,
                StringFormat.GenericTypographic);
            var b = path.GetBounds();
            float scale = Math.Min(n / b.Width, textArea / b.Height);
            float w = b.Width * scale, h = b.Height * scale;
            using var m = new Matrix();
            m.Translate((n - w) / 2f, (textArea - h) / 2f);
            m.Scale(scale, scale);
            m.Translate(-b.X, -b.Y);
            path.Transform(m);
            using var brush = new SolidBrush(pct <= low ? Theme.Red : fg);
            g.FillPath(brush, path);
        }

        using (var track = new SolidBrush(Color.FromArgb(70, fg)))
            g.FillRectangle(track, 0, n - barH, n, barH);
        using (var fill = new SolidBrush(level))
            g.FillRectangle(fill, 0, n - barH, Math.Max(1f, n * pct / 100f), barH);
        return bmp;
    }

    public static Bitmap Glyph(string glyph, bool darkTaskbar, bool warningDot)
    {
        int n = TraySize;
        var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(Theme.IconFont, n * 0.82f, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var brush = new SolidBrush(darkTaskbar ? Color.White : Color.Black);
        g.DrawString(glyph, font, brush, new RectangleF(0, 1, n, n), sf);
        if (warningDot)
        {
            float d = n * 0.42f;
            using var red = new SolidBrush(Theme.Red);
            g.FillEllipse(red, n - d, n - d, d - 0.5f, d - 0.5f);
        }
        return bmp;
    }
}
