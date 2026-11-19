using System.Diagnostics;

namespace BtBattery;

static class Credit
{
    public const string Author = "Nimesh Jayaweera";
    public const string Url = "https://nimeshjayaweera.com/";
    public const string RepoUrl = "https://github.com/nimeshc64/windows-taskbar-widgets";
    public const int Height = 28;

    public static void Open(string url = Url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public static Rectangle Area(Control c) => new(0, c.Height - Scale(c, Height), c.Width, Scale(c, Height));

    public static bool Contains(Control c, Point pt) => Area(c).Contains(pt);

    public static void Paint(Graphics g, Control c, Palette p, bool hover)
    {
        var r = Area(c);
        using (var line = new Pen(p.Border, 1)) g.DrawLine(line, 0, r.Top, c.Width, r.Top);
        using var f = new Font("Segoe UI", 11f * c.DeviceDpi / 96f, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, $"Created by {Author}  ·  nimeshjayaweera.com", f, r, hover ? p.Accent : p.SubText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    public static void AddToMenu(ContextMenuStrip menu)
    {
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add($"Created by {Author}", null, (_, _) => Open());
        menu.Items.Add("Source code on GitHub", null, (_, _) => Open(RepoUrl));
    }

    static int Scale(Control c, int v) => (int)Math.Round(v * c.DeviceDpi / 96f);
}
