using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace BtBattery;

static class Theme
{
    public static readonly string IconFont = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    static bool HasFont(string name)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    static int ReadPersonalize(string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue(name) is int i ? i : 1;
        }
        catch { return 1; }
    }

    public static bool AppsDark => ReadPersonalize("AppsUseLightTheme") == 0;
    public static bool TaskbarDark => ReadPersonalize("SystemUsesLightTheme") == 0;

    public static Color Accent
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (k?.GetValue("AccentColor") is int abgr)
                    return Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            }
            catch { }
            return Color.FromArgb(0, 120, 212);
        }
    }

    public static readonly Color Green = Color.FromArgb(76, 194, 96);
    public static readonly Color Amber = Color.FromArgb(240, 170, 40);
    public static readonly Color Red = Color.FromArgb(232, 72, 72);

    public static Color LevelColor(int pct, int low) =>
        pct <= low ? Red : pct <= Math.Max(low + 20, 40) ? Amber : Green;

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void DrawBattery(Graphics g, RectangleF r, int pct, Color fill, Color outline, float s)
    {
        var body = new RectangleF(r.X, r.Y, r.Width - 2.5f * s, r.Height);
        using (var pen = new Pen(outline, Math.Max(1f, 1.2f * s)))
        using (var path = Rounded(body, 3 * s))
            g.DrawPath(pen, path);
        using (var nub = new SolidBrush(outline))
            g.FillRectangle(nub, body.Right + 0.6f * s, r.Y + r.Height * 0.3f, 1.9f * s, r.Height * 0.4f);

        var inner = RectangleF.Inflate(body, -2.3f * s, -2.3f * s);
        inner.Width = Math.Max(1.5f * s, inner.Width * Math.Clamp(pct, 0, 100) / 100f);
        using var fb = new SolidBrush(fill);
        using var ip = Rounded(inner, 1.4f * s);
        g.FillPath(fb, ip);
    }
}

sealed record Palette(Color Background, Color Hover, Color Text, Color SubText, Color Border,
                      Color Accent, Color AccentSoft, Color Chip)
{
    public static Palette Get(bool dark)
    {
        var accent = Theme.Accent;
        if (dark)
        {
            var bg = Color.FromArgb(32, 32, 32);
            var acc = Theme.Mix(accent, Color.White, 0.35f);
            return new Palette(bg, Color.FromArgb(45, 45, 45), Color.FromArgb(245, 245, 245), Color.FromArgb(160, 160, 160),
                Color.FromArgb(58, 58, 58), acc, Theme.Mix(bg, accent, 0.28f), Color.FromArgb(52, 52, 52));
        }
        else
        {
            var bg = Color.FromArgb(243, 243, 243);
            return new Palette(bg, Color.FromArgb(232, 232, 232), Color.FromArgb(26, 26, 26), Color.FromArgb(96, 96, 96),
                Color.FromArgb(218, 218, 218), accent, Theme.Mix(bg, accent, 0.16f), Color.FromArgb(226, 226, 226));
        }
    }
}

static class Glyphs
{
    public const string Bluetooth = "";
    public const string Refresh = "";
    public const string Settings = "";
    public const string Check = "";

    public static string For(DeviceKind k) => k switch
    {
        DeviceKind.Headphones => "",
        DeviceKind.Speaker => "",
        DeviceKind.Keyboard => "",
        DeviceKind.Mouse => "",
        DeviceKind.Gamepad => "",
        DeviceKind.Phone => "",
        DeviceKind.Computer => "",
        DeviceKind.Pen => "",
        DeviceKind.Watch => "",
        _ => Bluetooth,
    };

    public static string Label(DeviceKind k) => k switch
    {
        DeviceKind.Headphones => "Audio",
        DeviceKind.Speaker => "Speaker",
        DeviceKind.Keyboard => "Keyboard",
        DeviceKind.Mouse => "Mouse",
        DeviceKind.Gamepad => "Controller",
        DeviceKind.Phone => "Phone",
        DeviceKind.Computer => "Computer",
        DeviceKind.Pen => "Pen",
        DeviceKind.Watch => "Wearable",
        _ => "",
    };
}

sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    readonly bool _dark;
    Color Fg => _dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(26, 26, 26);

    public MenuRenderer(bool dark) : base(new MenuColors(dark)) { _dark = dark; RoundedEdges = false; }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Fg; base.OnRenderArrow(e); }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Fg : Color.Gray;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        using var font = new Font(Theme.IconFont, r.Height * 0.75f, GraphicsUnit.Pixel);
        TextRenderer.DrawText(e.Graphics, Glyphs.Check, font, r, Fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    sealed class MenuColors(bool dark) : ProfessionalColorTable
    {
        readonly Color _bg = dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
        readonly Color _hi = dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(229, 229, 229);
        readonly Color _line = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(215, 215, 215);
        public override Color ToolStripDropDownBackground => _bg;
        public override Color ImageMarginGradientBegin => _bg;
        public override Color ImageMarginGradientMiddle => _bg;
        public override Color ImageMarginGradientEnd => _bg;
        public override Color MenuBorder => _line;
        public override Color MenuItemBorder => _hi;
        public override Color MenuItemSelected => _hi;
        public override Color MenuItemSelectedGradientBegin => _hi;
        public override Color MenuItemSelectedGradientEnd => _hi;
        public override Color MenuItemPressedGradientBegin => _hi;
        public override Color MenuItemPressedGradientEnd => _hi;
        public override Color SeparatorDark => _line;
        public override Color SeparatorLight => _line;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }
}
