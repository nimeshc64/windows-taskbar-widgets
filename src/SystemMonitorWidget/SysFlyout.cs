using System.Diagnostics;
using System.Drawing.Drawing2D;
using BtBattery;

namespace SysMon;

sealed class SysFlyout : Form
{
    const int W = 380, Pad = 12, HeaderH = 52, SectionH = 128, GraphH = 46, FooterH = 50;

    enum Link { TaskManager, Network }

    readonly SystemSampler _s;
    readonly SmSettings _settings;
    readonly Dictionary<Link, Rectangle> _links = new();
    Link? _hover;
    bool _hoverCredit;
    Rectangle? _anchor;

    public DateTime LastHidden { get; private set; }

    public SysFlyout(SystemSampler sampler, SmSettings settings)
    {
        _s = sampler;
        _settings = settings;
        Text = "System Monitor";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
    }

    float S => DeviceDpi / 96f;
    int Px(float v) => (int)Math.Round(v * S);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle, Theme.AppsDark);
    }

    public void ApplyTheme()
    {
        if (IsHandleCreated) Native.StyleWindow(Handle, Theme.AppsDark);
        Invalidate();
    }

    public void ShowAt(Rectangle? anchor)
    {
        _anchor = anchor;
        Reposition();
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    public void UpdateView() { if (Visible) Invalidate(); }

    void Reposition()
    {
        var size = new Size(Px(W), Px(HeaderH + 3 * SectionH + FooterH + Credit.Height));
        var screen = _anchor is Rectangle a ? Screen.FromRectangle(a) : Screen.FromPoint(Cursor.Position);
        var wa = screen.WorkingArea;
        int gap = Px(12), x, y;
        if (_anchor is Rectangle r)
        {
            x = r.Left + r.Width / 2 - size.Width / 2;
            y = r.Top - size.Height - Px(8);
            if (y < wa.Top) y = r.Bottom + Px(8);
        }
        else
        {
            x = wa.Right - size.Width - gap;
            y = wa.Top > screen.Bounds.Top ? wa.Top + gap : wa.Bottom - size.Height - gap;
            if (wa.Left > screen.Bounds.Left) x = wa.Left + gap;
        }
        x = Math.Clamp(x, wa.Left + gap, Math.Max(wa.Left + gap, wa.Right - size.Width - gap));
        y = Math.Clamp(y, wa.Top + gap, Math.Max(wa.Top + gap, wa.Bottom - size.Height - gap));
        Bounds = new Rectangle(new Point(x, y), size);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); if (Visible) Reposition(); }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); HideFlyout(); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) HideFlyout(); base.OnKeyDown(e); }

    public void HideFlyout()
    {
        if (!Visible) return;
        Hide();
        LastHidden = DateTime.Now;
    }

    const TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                              TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    const TextFormatFlags R = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
    const TextFormatFlags C = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Palette.Get(Theme.AppsDark);
        g.Clear(p.Background);

        using var title = new Font("Segoe UI Semibold", 15f * S, GraphicsUnit.Pixel);
        using var sub = new Font("Segoe UI", 12f * S, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, "System", title, new Rectangle(Px(Pad + 10), 0, Px(150), Px(HeaderH)), p.Text, L);
        TextRenderer.DrawText(g, $"Up {SystemSampler.Uptime()}", sub, new Rectangle(Width / 2, 0, Width / 2 - Px(Pad + 10), Px(HeaderH)), p.SubText, R);

        bool bits = _settings.Bits;
        int y = Px(HeaderH);

        Section(g, p, y, SmGlyphs.Cpu, "CPU", $"{Math.Round(_s.Cpu)}%", SystemSampler.LoadColor(_s.Cpu),
            _s.CpuName, $"{_s.LogicalProcessors} logical processors");
        Graph(g, p, GraphRect(y), new[] { (_s.CpuHistory, Accent(p)) }, 100, null);
        y += Px(SectionH);

        Section(g, p, y, SmGlyphs.Memory, "Memory", $"{Math.Round(_s.MemoryLoad)}%", SystemSampler.LoadColor(_s.MemoryLoad),
            $"{SystemSampler.Gb(_s.MemoryUsed)} used of {SystemSampler.Gb(_s.MemoryTotal)}",
            $"{SystemSampler.Gb(_s.MemoryAvailable)} available");
        Graph(g, p, GraphRect(y), new[] { (_s.MemoryHistory, Color.FromArgb(160, 110, 230)) }, 100, null);
        y += Px(SectionH);

        string status = !_s.HasNetwork ? "Offline" : _s.Internet switch { true => "Connected", false => "No internet", _ => "…" };
        var statusColor = _s.Internet == true && _s.HasNetwork ? Theme.Green : Theme.Red;
        Section(g, p, y, SmGlyphs.Network(_s), "Network", status, statusColor,
            _s.HasNetwork ? string.Join(" · ", new[] { _s.AdapterKind, _s.AdapterName, _s.LocalIp }
                                .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                          : "No active network adapter",
            $"↓ {SystemSampler.Speed(_s.Down, bits)}     ↑ {SystemSampler.Speed(_s.Up, bits)}");
        double max = Math.Max(Math.Max(_s.DownHistory.Max, _s.UpHistory.Max), 64 * 1024) * 1.15;
        Graph(g, p, GraphRect(y), new[] { (_s.DownHistory, Accent(p)), (_s.UpHistory, Theme.Amber) }, max,
            SystemSampler.Speed(max / 1.15, bits));
        y += Px(SectionH);

        using (var line = new Pen(p.Border, 1)) g.DrawLine(line, 0, y, Width, y);
        using var icon = new Font(Theme.IconFont, 14f * S, GraphicsUnit.Pixel);
        int ly = y + (Height - Px(Credit.Height) - y - Px(32)) / 2;
        Credit.Paint(g, this, p, _hoverCredit);
        LinkButton(g, p, sub, icon, Link.TaskManager, SmGlyphs.TaskManager, "Task Manager", Px(Pad), ly, false);
        LinkButton(g, p, sub, icon, Link.Network, SmGlyphs.Globe, "Network settings", Width - Px(Pad), ly, true);
    }

    static Color Accent(Palette p) => p.Accent;

    Rectangle GraphRect(int sectionTop) =>
        new(Px(Pad + 8), sectionTop + Px(SectionH - GraphH - 12), Width - Px(2 * (Pad + 8)), Px(GraphH));

    void Section(Graphics g, Palette p, int y, string glyph, string title, string value, Color valueColor, string line1, string line2)
    {
        using var titleFont = new Font("Segoe UI Semibold", 13.5f * S, GraphicsUnit.Pixel);
        using var valueFont = new Font("Segoe UI Semibold", 15f * S, GraphicsUnit.Pixel);
        using var sub = new Font("Segoe UI", 11.5f * S, GraphicsUnit.Pixel);
        using var icon = new Font(Theme.IconFont, 16f * S, GraphicsUnit.Pixel);
        int left = Px(Pad + 8), right = Width - Px(Pad + 8);
        TextRenderer.DrawText(g, glyph, icon, new Rectangle(left, y + Px(4), Px(20), Px(26)), p.Accent, C);
        TextRenderer.DrawText(g, title, titleFont, new Rectangle(left + Px(28), y + Px(4), Px(160), Px(26)), p.Text, L);
        TextRenderer.DrawText(g, value, valueFont, new Rectangle(right - Px(160), y + Px(4), Px(160), Px(26)), valueColor, R);
        TextRenderer.DrawText(g, line1, sub, new Rectangle(left, y + Px(32), right - left, Px(17)), p.SubText, L);
        TextRenderer.DrawText(g, line2, sub, new Rectangle(left, y + Px(49), right - left, Px(17)), p.SubText, L);
    }

    void Graph(Graphics g, Palette p, Rectangle r, (History h, Color c)[] series, double max, string? scale)
    {
        using (var bg = new SolidBrush(p.Chip))
        using (var path = Theme.Rounded(r, 6 * S)) g.FillPath(bg, path);
        using (var grid = new Pen(Color.FromArgb(40, p.Text), 1) { DashStyle = DashStyle.Dot })
            g.DrawLine(grid, r.Left + Px(4), r.Top + r.Height / 2, r.Right - Px(4), r.Top + r.Height / 2);

        var plot = Rectangle.Inflate(r, -Px(3), -Px(4));
        var state = g.Save();
        using (var clip = Theme.Rounded(r, 6 * S)) g.SetClip(clip);
        foreach (var (h, color) in series)
        {
            var vals = h.Values;
            if (vals.Count < 2) continue;
            float step = plot.Width / (float)(h.Capacity - 1);
            var pts = new PointF[vals.Count];
            for (int i = 0; i < vals.Count; i++)
                pts[i] = new PointF(plot.Right - (vals.Count - 1 - i) * step,
                                    plot.Bottom - (float)(Math.Clamp(vals[i] / max, 0, 1) * plot.Height));
            using var area = new GraphicsPath();
            area.AddLines(pts);
            area.AddLine(pts[^1], new PointF(pts[^1].X, r.Bottom));
            area.AddLine(new PointF(pts[^1].X, r.Bottom), new PointF(pts[0].X, r.Bottom));
            area.CloseFigure();
            using (var fill = new SolidBrush(Color.FromArgb(55, color))) g.FillPath(fill, area);
            using (var pen = new Pen(color, 1.6f * S) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts);
        }
        g.Restore(state);

        if (scale != null)
        {
            using var f = new Font("Segoe UI", 10f * S, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, scale, f, new Rectangle(r.Right - Px(110), r.Top + Px(2), Px(104), Px(14)), p.SubText, R);
        }
    }

    void LinkButton(Graphics g, Palette p, Font font, Font icon, Link link, string glyph, string text, int x, int y, bool alignRight)
    {
        var ts = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
        int w = ts.Width + Px(42);
        var r = new Rectangle(alignRight ? x - w : x, y, w, Px(32));
        _links[link] = r;
        if (_hover == link)
        {
            using var b = new SolidBrush(p.Hover);
            using var path = Theme.Rounded(r, 6 * S);
            g.FillPath(b, path);
        }
        TextRenderer.DrawText(g, glyph, icon, new Rectangle(r.Left + Px(8), r.Top, Px(18), r.Height), p.Text, C);
        TextRenderer.DrawText(g, text, font, new Rectangle(r.Left + Px(32), r.Top, ts.Width + Px(4), r.Height), p.Text, L);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hc = Credit.Contains(this, e.Location);
        if (hc != _hoverCredit) { _hoverCredit = hc; Invalidate(); }
        if (hc) { Cursor = Cursors.Hand; return; }
        Link? hit = null;
        foreach (var (k, r) in _links) if (r.Contains(e.Location)) hit = k;
        if (hit != _hover) { _hover = hit; Cursor = hit != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != null) { _hover = null; Invalidate(); } }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && Credit.Contains(this, e.Location)) { HideFlyout(); Credit.Open(); return; }
        if (e.Button != MouseButtons.Left || _hover is not Link link) return;
        HideFlyout();
        Open(link == Link.TaskManager ? "taskmgr.exe" : "ms-settings:network-status");
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { SmLog.Write("Open " + target + ": " + ex.Message); }
    }
}
