using System.Drawing.Drawing2D;

namespace BtBattery;

sealed class FlyoutForm : Form
{
    const int W = 360, Pad = 12, HeaderH = 56, RowH = 62, FooterH = 48, EmptyH = 84;

    readonly AppSettings _settings;
    List<DeviceBattery> _devices = new();
    DateTime? _updated;
    bool _busy;
    Rectangle? _anchor;

    readonly List<Rectangle> _rows = new();
    Rectangle _refreshRect, _settingsRect;
    int _hoverRow = -1;
    bool _hoverRefresh, _hoverSettings;

    public DateTime LastHidden { get; private set; }
    public event Action? RefreshRequested;
    public event Action? SettingsRequested;

    public FlyoutForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Bluetooth Battery";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _ = Handle;
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80;  return cp; }
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

    public void ShowAt(Rectangle? anchor, List<DeviceBattery> devices, DateTime? updated)
    {
        _anchor = anchor;
        _devices = devices;
        _updated = updated;
        Reposition();
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    public void SetDevices(List<DeviceBattery> devices, DateTime? updated)
    {
        _devices = devices;
        _updated = updated;
        if (Visible) Reposition();
        Invalidate();
    }

    public void SetBusy(bool busy) { _busy = busy; if (Visible) Invalidate(); }

    void Reposition()
    {
        int h = HeaderH + (_devices.Count == 0 ? EmptyH : _devices.Count * RowH) + FooterH + Pad / 2 + Credit.Height;
        var size = new Size(Px(W), Px(h));
        var screen = _anchor is Rectangle a ? Screen.FromRectangle(a) : Screen.FromPoint(Cursor.Position);
        var wa = screen.WorkingArea;
        int gap = Px(12);
        int x, y;

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

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (Visible) Reposition();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        HideFlyout();
    }

    public void HideFlyout()
    {
        if (!Visible) return;
        Hide();
        LastHidden = DateTime.Now;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) HideFlyout();
        else if (e.KeyCode == Keys.F5) RefreshRequested?.Invoke();
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Palette.Get(Theme.AppsDark);
        g.Clear(p.Background);

        using var title = new Font("Segoe UI Semibold", 15f * S, GraphicsUnit.Pixel);
        using var name = new Font("Segoe UI Semibold", 13.5f * S, GraphicsUnit.Pixel);
        using var sub = new Font("Segoe UI", 12f * S, GraphicsUnit.Pixel);
        using var pctFont = new Font("Segoe UI Semibold", 14f * S, GraphicsUnit.Pixel);
        using var icon16 = new Font(Theme.IconFont, 16f * S, GraphicsUnit.Pixel);
        using var icon14 = new Font(Theme.IconFont, 14f * S, GraphicsUnit.Pixel);
        const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                     TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                       TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        TextRenderer.DrawText(g, "Bluetooth devices", title, new Rectangle(Px(Pad + 10), 0, Px(W - 120), Px(HeaderH)), p.Text, Left);
        _refreshRect = new Rectangle(Px(W - Pad - 36), Px((HeaderH - 34) / 2f), Px(34), Px(34));
        if (_hoverRefresh) Fill(g, p.Hover, _refreshRect, 6);
        TextRenderer.DrawText(g, Glyphs.Refresh, icon14, _refreshRect, _busy ? p.Accent : p.Text, Center);

        _rows.Clear();
        int y = Px(HeaderH);
        if (_devices.Count == 0)
        {
            var r = new Rectangle(0, y, Width, Px(EmptyH));
            TextRenderer.DrawText(g, _busy ? "Looking for devices…" : "No paired Bluetooth devices", sub, r, p.SubText, Center);
            y += Px(EmptyH);
        }
        for (int i = 0; i < _devices.Count; i++)
        {
            var r = new Rectangle(Px(Pad), y + Px(2), Px(W - 2 * Pad), Px(RowH - 4));
            _rows.Add(r);
            DrawRow(g, _devices[i], r, i == _hoverRow, p, name, sub, pctFont, icon16, Left, Center);
            y += Px(RowH);
        }

        int fy = y + Px(Pad / 2);
        using (var line = new Pen(p.Border, 1)) g.DrawLine(line, 0, fy, Width, fy);
        var footer = new Rectangle(0, fy, Width, Height - fy - Px(Credit.Height));
        var linkText = "Bluetooth settings";
        var linkSize = TextRenderer.MeasureText(g, linkText, sub, Size.Empty, TextFormatFlags.NoPadding);
        _settingsRect = new Rectangle(Px(Pad), footer.Top + (footer.Height - Px(32)) / 2, linkSize.Width + Px(40), Px(32));
        if (_hoverSettings) Fill(g, p.Hover, _settingsRect, 6);
        TextRenderer.DrawText(g, Glyphs.Settings, icon14, new Rectangle(_settingsRect.Left + Px(8), _settingsRect.Top, Px(18), _settingsRect.Height), p.Text, Center);
        TextRenderer.DrawText(g, linkText, sub, new Rectangle(_settingsRect.Left + Px(32), _settingsRect.Top, linkSize.Width + Px(4), _settingsRect.Height), p.Text, Left);

        var status = _busy ? "Updating…" : _updated is DateTime t ? $"Updated {t:t}" : "";
        TextRenderer.DrawText(g, status, sub, new Rectangle(Width / 2, footer.Top, Width / 2 - Px(Pad + 8), footer.Height), p.SubText,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        Credit.Paint(g, this, p, _hoverCredit);
    }

    bool _hoverCredit;

    void DrawRow(Graphics g, DeviceBattery d, Rectangle r, bool hover, Palette p,
                 Font name, Font sub, Font pctFont, Font icon, TextFormatFlags left, TextFormatFlags center)
    {
        if (hover) Fill(g, p.Hover, r, 6);

        int circle = Px(38);
        var cr = new Rectangle(r.Left + Px(8), r.Top + (r.Height - circle) / 2, circle, circle);
        using (var cb = new SolidBrush(d.IsConnected ? p.AccentSoft : p.Chip)) g.FillEllipse(cb, cr);
        TextRenderer.DrawText(g, Glyphs.For(d.Kind), icon, cr, d.IsConnected ? p.Accent : p.SubText, center);

        int tx = cr.Right + Px(12);
        int right = r.Right - Px(96);
        TextRenderer.DrawText(g, d.Name, name, new Rectangle(tx, r.Top + Px(10), right - tx, Px(20)), p.Text, left);
        TextRenderer.DrawText(g, Subtitle(d), sub, new Rectangle(tx, r.Top + Px(31), right - tx + Px(40), Px(18)), p.SubText, left);

        var pctRect = new Rectangle(r.Right - Px(52), r.Top, Px(44), r.Height);
        if (d.Battery is int pct)
        {
            bool stale = !d.IsConnected;
            var level = Theme.LevelColor(pct, _settings.LowBatteryThreshold);
            var textColor = stale ? p.SubText : pct <= _settings.LowBatteryThreshold ? Theme.Red : p.Text;
            TextRenderer.DrawText(g, $"{pct}%", pctFont, pctRect, textColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            var batt = new RectangleF(pctRect.Left - 30 * S, r.Top + (r.Height - 13 * S) / 2f, 27 * S, 13 * S);
            Theme.DrawBattery(g, batt, pct, stale ? Theme.Mix(level, p.Background, 0.55f) : level, p.SubText, S);
        }
        else
        {
            TextRenderer.DrawText(g, "—", pctFont, pctRect, p.SubText,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    static string Subtitle(DeviceBattery d)
    {
        string state;
        if (d.IsConnected) state = d.Battery.HasValue ? "Connected" : "Connected · no battery info";
        else if (d.LastSeen is DateTime t) state = $"Last seen {Ago(t)}";
        else state = "Not connected";
        var kind = Glyphs.Label(d.Kind);
        return kind.Length > 0 ? $"{kind} · {state}" : state;
    }

    static string Ago(DateTime t)
    {
        var s = DateTime.Now - t;
        if (s.TotalMinutes < 1) return "just now";
        if (s.TotalHours < 1) return $"{(int)s.TotalMinutes} min ago";
        if (s.TotalDays < 1) return $"{(int)s.TotalHours} h ago";
        if (s.TotalDays < 7) return $"{(int)s.TotalDays} d ago";
        return t.ToString("d");
    }

    void Fill(Graphics g, Color c, Rectangle r, float radius)
    {
        using var b = new SolidBrush(c);
        using var path = Theme.Rounded(r, radius * S);
        g.FillPath(b, path);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hc = Credit.Contains(this, e.Location);
        if (hc != _hoverCredit) { _hoverCredit = hc; Invalidate(); }
        if (hc) { Cursor = Cursors.Hand; return; }
        int row = _rows.FindIndex(r => r.Contains(e.Location));
        bool hr = _refreshRect.Contains(e.Location), hs = _settingsRect.Contains(e.Location);
        if (row != _hoverRow || hr != _hoverRefresh || hs != _hoverSettings)
        {
            _hoverRow = row; _hoverRefresh = hr; _hoverSettings = hs;
            Cursor = hr || hs ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverRow = -1; _hoverRefresh = _hoverSettings = false;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_refreshRect.Contains(e.Location)) RefreshRequested?.Invoke();
        else if (_settingsRect.Contains(e.Location)) { HideFlyout(); SettingsRequested?.Invoke(); }
        else if (Credit.Contains(this, e.Location)) { HideFlyout(); Credit.Open(); }
    }
}
