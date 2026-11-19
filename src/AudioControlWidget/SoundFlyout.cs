using System.Diagnostics;
using System.Drawing.Drawing2D;
using AudioCtl.Interop;
using BtBattery;

namespace AudioCtl;

sealed class SoundFlyout : Form
{
    const int W = 380, Pad = 12, HeaderH = 52, LabelH = 26, VolH = 52, DevH = 50, GapH = 16, FooterH = 50;

    enum HitKind { Mute, Slider, Device, Settings, Mixer }
    sealed record Hit(Rectangle R, HitKind Kind, EDataFlow Flow, string? Id = null);

    readonly SoundService _svc;
    readonly SoundSettings _settings;
    List<AudioDevice> _outs = new(), _ins = new();
    readonly List<Hit> _hits = new();
    Hit? _hover;
    bool _hoverCredit;
    Hit? _drag;
    int _inputTop;
    Rectangle? _anchor;

    public DateTime LastHidden { get; private set; }

    public SoundFlyout(SoundService svc, SoundSettings settings)
    {
        _svc = svc;
        _settings = settings;
        Text = "Audio Control";
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
        LoadDevices();
        Reposition();
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    public void ReloadDevices()
    {
        if (!Visible) return;
        LoadDevices();
        Reposition();
        Invalidate();
    }

    void LoadDevices()
    {
        _outs = _svc.GetDevices(EDataFlow.Render);
        _ins = _svc.GetDevices(EDataFlow.Capture);
    }

    static int Section(int n) => LabelH + VolH + Math.Max(1, n) * DevH;

    void Reposition()
    {
        int h = HeaderH + Section(_outs.Count) + GapH + Section(_ins.Count) + FooterH + Pad / 2 + Credit.Height;
        var size = new Size(Px(W), Px(h));
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

    public void HideFlyout()
    {
        if (!Visible) return;
        _drag = null;
        Hide();
        LastHidden = DateTime.Now;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape: HideFlyout(); break;
            case Keys.Up: case Keys.Right: _svc.Output.Step(_settings.ScrollStep); break;
            case Keys.Down: case Keys.Left: _svc.Output.Step(-_settings.ScrollStep); break;
            case Keys.M: _svc.Output.Muted = !_svc.Output.Muted; break;
        }
        base.OnKeyDown(e);
    }

    const TextFormatFlags LeftFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                      TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    const TextFormatFlags CenterFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
    const TextFormatFlags RightFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                                       TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Palette.Get(Theme.AppsDark);
        g.Clear(p.Background);
        _hits.Clear();

        using var title = new Font("Segoe UI Semibold", 15f * S, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, "Sound", title, new Rectangle(Px(Pad + 10), 0, Px(200), Px(HeaderH)), p.Text, LeftFlags);

        int y = Px(HeaderH);
        y = DrawSection(g, p, "Output", _svc.Output, _outs, y);
        using (var line = new Pen(p.Border, 1)) g.DrawLine(line, Px(Pad + 8), y + Px(GapH / 2f), Width - Px(Pad + 8), y + Px(GapH / 2f));
        y += Px(GapH);
        _inputTop = y;
        y = DrawSection(g, p, "Input", _svc.Input, _ins, y);

        int fy = y + Px(Pad / 2f);
        using (var line = new Pen(p.Border, 1)) g.DrawLine(line, 0, fy, Width, fy);
        using var sub = new Font("Segoe UI", 12f * S, GraphicsUnit.Pixel);
        using var icon = new Font(Theme.IconFont, 14f * S, GraphicsUnit.Pixel);
        int linkY = fy + (Height - Px(Credit.Height) - fy - Px(32)) / 2;
        Credit.Paint(g, this, p, _hoverCredit);
        var r1 = Link(g, p, sub, icon, Glyphs.Settings, "Sound settings", Px(Pad), linkY, HitKind.Settings, alignRight: false);
        Link(g, p, sub, icon, SoundGlyphs.Mixer, "Volume mixer", Width - Px(Pad), linkY, HitKind.Mixer, alignRight: true);
        _ = r1;
    }

    Rectangle Link(Graphics g, Palette p, Font font, Font icon, string glyph, string text, int x, int y, HitKind kind, bool alignRight)
    {
        var ts = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
        int w = ts.Width + Px(42);
        var r = new Rectangle(alignRight ? x - w : x, y, w, Px(32));
        _hits.Add(new Hit(r, kind, EDataFlow.Render));
        if (_hover?.Kind == kind) Fill(g, p.Hover, r, 6);
        TextRenderer.DrawText(g, glyph, icon, new Rectangle(r.Left + Px(8), r.Top, Px(18), r.Height), p.Text, CenterFlags);
        TextRenderer.DrawText(g, text, font, new Rectangle(r.Left + Px(32), r.Top, ts.Width + Px(4), r.Height), p.Text, LeftFlags);
        return r;
    }

    int DrawSection(Graphics g, Palette p, string label, SoundService.Endpoint ep, List<AudioDevice> devices, int y)
    {
        using var labelFont = new Font("Segoe UI Semibold", 12f * S, GraphicsUnit.Pixel);
        using var name = new Font("Segoe UI", 13f * S, GraphicsUnit.Pixel);
        using var nameBold = new Font("Segoe UI Semibold", 13f * S, GraphicsUnit.Pixel);
        using var sub = new Font("Segoe UI", 11.5f * S, GraphicsUnit.Pixel);
        using var pctFont = new Font("Segoe UI Semibold", 13.5f * S, GraphicsUnit.Pixel);
        using var icon16 = new Font(Theme.IconFont, 16f * S, GraphicsUnit.Pixel);
        using var icon12 = new Font(Theme.IconFont, 12f * S, GraphicsUnit.Pixel);

        TextRenderer.DrawText(g, label, labelFont, new Rectangle(Px(Pad + 10), y, Px(200), Px(LabelH)), p.SubText, LeftFlags);
        y += Px(LabelH);

        var row = new Rectangle(Px(Pad), y, Width - Px(2 * Pad), Px(VolH));
        var mute = new Rectangle(row.Left + Px(2), row.Top + (row.Height - Px(38)) / 2, Px(38), Px(38));
        var track = Rectangle.FromLTRB(mute.Right + Px(14), row.Top, row.Right - Px(58), row.Bottom);
        var pctRect = Rectangle.FromLTRB(track.Right + Px(8), row.Top, row.Right - Px(6), row.Bottom);
        if (ep.Available)
        {
            bool muted = ep.Muted;
            float v = ep.Volume;
            _hits.Add(new Hit(mute, HitKind.Mute, ep.Flow));
            _hits.Add(new Hit(track, HitKind.Slider, ep.Flow));
            if (_hover?.Kind == HitKind.Mute && _hover.Flow == ep.Flow) Fill(g, p.Hover, mute, 6);
            TextRenderer.DrawText(g, SoundGlyphs.Level(ep), icon16, mute, muted ? Theme.Red : p.Text, CenterFlags);
            bool hot = (_hover?.Kind == HitKind.Slider && _hover.Flow == ep.Flow) || _drag?.Flow == ep.Flow;
            DrawSlider(g, p, track, v, muted, hot);
            TextRenderer.DrawText(g, muted ? "Muted" : $"{(int)Math.Round(v * 100)}%", pctFont, pctRect,
                muted ? p.SubText : p.Text, RightFlags);
        }
        else
        {
            TextRenderer.DrawText(g, "No device available", sub, row, p.SubText, CenterFlags);
        }
        y += Px(VolH);

        if (devices.Count == 0)
        {
            TextRenderer.DrawText(g, "No active devices", sub, new Rectangle(row.Left, y, row.Width, Px(DevH)), p.SubText, CenterFlags);
            return y + Px(DevH);
        }
        foreach (var d in devices)
        {
            var r = new Rectangle(Px(Pad), y + Px(2), Width - Px(2 * Pad), Px(DevH - 4));
            _hits.Add(new Hit(r, HitKind.Device, ep.Flow, d.Id));
            bool hover = _hover?.Kind == HitKind.Device && _hover.Id == d.Id;
            if (d.IsDefault) Fill(g, hover ? Theme.Mix(p.AccentSoft, p.Text, 0.06f) : p.AccentSoft, r, 6);
            else if (hover) Fill(g, p.Hover, r, 6);
            if (d.IsDefault)
            {
                using var pill = new SolidBrush(p.Accent);
                using var pp = Theme.Rounded(new RectangleF(r.Left + 1.5f * S, r.Top + r.Height / 2f - 8 * S, 3 * S, 16 * S), 1.5f * S);
                g.FillPath(pill, pp);
            }
            var ir = new Rectangle(r.Left + Px(12), r.Top, Px(24), r.Height);
            TextRenderer.DrawText(g, SoundGlyphs.ForDevice(d), icon16, ir, d.IsDefault ? p.Accent : p.SubText, CenterFlags);
            int tx = ir.Right + Px(12), tr = r.Right - Px(36);
            TextRenderer.DrawText(g, d.Name, d.IsDefault ? nameBold : name, new Rectangle(tx, r.Top + Px(6), tr - tx, Px(19)), p.Text, LeftFlags);
            TextRenderer.DrawText(g, d.Interface, sub, new Rectangle(tx, r.Top + Px(25), tr - tx, Px(16)), p.SubText, LeftFlags);
            if (d.IsDefault)
                TextRenderer.DrawText(g, Glyphs.Check, icon12, new Rectangle(r.Right - Px(32), r.Top, Px(20), r.Height), p.Accent, CenterFlags);
            y += Px(DevH);
        }
        return y;
    }

    void DrawSlider(Graphics g, Palette p, Rectangle area, float v, bool muted, bool hot)
    {
        float cy = area.Top + area.Height / 2f;
        float h = 4 * S;
        var track = new RectangleF(area.Left, cy - h / 2, area.Width, h);
        var fillColor = muted ? p.SubText : p.Accent;
        using (var tb = new SolidBrush(Theme.AppsDark ? Color.FromArgb(110, 110, 110) : Color.FromArgb(160, 160, 160)))
        using (var tp = Theme.Rounded(track, h / 2)) g.FillPath(tb, tp);
        var filled = track with { Width = Math.Max(h, track.Width * v) };
        using (var fb = new SolidBrush(fillColor))
        using (var fp = Theme.Rounded(filled, h / 2)) g.FillPath(fb, fp);

        float tx = track.Left + track.Width * v;
        float outer = 20 * S, inner = (hot ? 12 : 10) * S;
        var thumbBg = Theme.AppsDark ? Color.FromArgb(69, 69, 69) : Color.White;
        using (var ob = new SolidBrush(thumbBg)) g.FillEllipse(ob, tx - outer / 2, cy - outer / 2, outer, outer);
        using (var op = new Pen(p.Border, 1)) g.DrawEllipse(op, tx - outer / 2, cy - outer / 2, outer, outer);
        using (var ib = new SolidBrush(fillColor)) g.FillEllipse(ib, tx - inner / 2, cy - inner / 2, inner, inner);
    }

    void Fill(Graphics g, Color c, Rectangle r, float radius)
    {
        using var b = new SolidBrush(c);
        using var path = Theme.Rounded(r, radius * S);
        g.FillPath(b, path);
    }

    Hit? HitAt(Point pt) => _hits.LastOrDefault(h => h.R.Contains(pt));

    void SetFromX(Hit slider, int x)
    {
        float v = Math.Clamp((x - slider.R.Left) / (float)slider.R.Width, 0f, 1f);
        var ep = _svc[slider.Flow];
        ep.Volume = MathF.Round(v * 100) / 100f;
        if (ep.Muted && v > 0) ep.Muted = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var hit = HitAt(e.Location);
        if (hit?.Kind == HitKind.Slider) { _drag = hit; SetFromX(hit, e.X); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag != null) { SetFromX(_drag, e.X); return; }
        bool hc = Credit.Contains(this, e.Location);
        if (hc != _hoverCredit) { _hoverCredit = hc; Invalidate(); }
        if (hc) { Cursor = Cursors.Hand; return; }
        var hit = HitAt(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit?.Kind is HitKind.Settings or HitKind.Mixer or HitKind.Device or HitKind.Mute ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag != null) { _drag = null; Invalidate(); return; }
        if (e.Button != MouseButtons.Left) return;
        if (Credit.Contains(this, e.Location)) { HideFlyout(); Credit.Open(); return; }
        var hit = HitAt(e.Location);
        switch (hit?.Kind)
        {
            case HitKind.Mute:
                var ep = _svc[hit.Flow];
                ep.Muted = !ep.Muted;
                Invalidate();
                break;
            case HitKind.Device when hit.Id != null:
                if (hit.Id != _svc[hit.Flow].DeviceId) _svc.SetDefault(hit.Id);
                break;
            case HitKind.Settings:
                HideFlyout(); Open("ms-settings:sound");
                break;
            case HitKind.Mixer:
                HideFlyout(); Open("ms-settings:apps-volume");
                break;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null) { _hover = null; Invalidate(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var flow = e.Y >= _inputTop ? EDataFlow.Capture : EDataFlow.Render;
        _svc[flow].Step(Math.Sign(e.Delta) * _settings.ScrollStep);
    }

    public static void Open(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { SLog.Write("Open " + uri + ": " + ex.Message); }
    }
}
