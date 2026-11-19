using System.Drawing.Drawing2D;

namespace BtBattery;

abstract class FlyoutBase : Form
{
    Rectangle? _anchor;
    readonly List<(Rectangle R, string Key, Action<Point>? Click)> _hits = new();

    public DateTime LastHidden { get; private set; }
    protected string? HoverKey { get; private set; }

    protected FlyoutBase(string title)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected abstract int ContentHeight();
    protected abstract void PaintContent(Graphics g, Palette p);
    protected virtual int LogicalWidth => 380;
    protected virtual void OnOpening() { }

    protected float S => DeviceDpi / 96f;
    protected int Px(float v) => (int)Math.Round(v * S);
    protected Font UiFont(float px, bool semibold = false) =>
        new(semibold ? "Segoe UI Semibold" : "Segoe UI", px * S, GraphicsUnit.Pixel);
    protected Font IconFont(float px) => new(Theme.IconFont, px * S, GraphicsUnit.Pixel);

    protected const TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    protected const TextFormatFlags R = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    protected const TextFormatFlags C = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
    }

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
        OnOpening();
        Reposition();
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    public void RefreshLayout()
    {
        if (!Visible) return;
        Reposition();
        Invalidate();
    }

    void Reposition()
    {
        var size = new Size(Px(LogicalWidth), Px(ContentHeight() + Credit.Height));
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
        size.Height = Math.Min(size.Height, wa.Height - 2 * gap);
        x = Math.Clamp(x, wa.Left + gap, Math.Max(wa.Left + gap, wa.Right - size.Width - gap));
        y = Math.Clamp(y, wa.Top + gap, Math.Max(wa.Top + gap, wa.Bottom - size.Height - gap));
        Bounds = new Rectangle(new Point(x, y), size);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); RefreshLayout(); }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); HideFlyout(); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) HideFlyout(); base.OnKeyDown(e); }

    public void HideFlyout()
    {
        if (!Visible) return;
        Hide();
        LastHidden = DateTime.Now;
    }

    protected sealed override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Palette.Get(Theme.AppsDark);
        g.Clear(p.Background);
        _hits.Clear();
        PaintContent(g, p);
        Credit.Paint(g, this, p, IsHover("credit"));
        AddHit(Credit.Area(this), "credit", _ => Credit.Open());
    }

    protected void AddHit(Rectangle r, string key, Action<Point>? click) => _hits.Add((r, key, click));
    protected bool IsHover(string key) => HoverKey == key;

    (Rectangle R, string Key, Action<Point>? Click)? HitAt(Point pt)
    {
        for (int i = _hits.Count - 1; i >= 0; i--) if (_hits[i].R.Contains(pt)) return _hits[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitAt(e.Location);
        if (hit?.Key != HoverKey)
        {
            HoverKey = hit?.Key;
            Cursor = hit?.Click != null ? Cursors.Hand : Cursors.Default;
            OnHoverChanged();
            Invalidate();
        }
    }

    protected virtual void OnHoverChanged() { }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (HoverKey != null) { HoverKey = null; OnHoverChanged(); Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && HitAt(e.Location) is { Click: not null } hit) hit.Click(e.Location);
    }

    protected void Fill(Graphics g, Color c, RectangleF r, float radius)
    {
        using var b = new SolidBrush(c);
        using var path = Theme.Rounded(r, radius * S);
        g.FillPath(b, path);
    }

    protected void IconButton(Graphics g, Palette p, Rectangle r, string glyph, string key, Action<Point>? click,
                              Color? color = null, float glyphPx = 14, bool enabled = true)
    {
        if (enabled) AddHit(r, key, click);
        if (enabled && IsHover(key)) Fill(g, p.Hover, r, 6);
        using var f = IconFont(glyphPx);
        TextRenderer.DrawText(g, glyph, f, r, enabled ? color ?? p.Text : Theme.Mix(p.SubText, p.Background, 0.4f), C);
    }

    protected Rectangle LinkButton(Graphics g, Palette p, string glyph, string text, int x, int y, bool alignRight,
                                   string key, Action<Point> click)
    {
        using var font = UiFont(12);
        using var icon = IconFont(14);
        var ts = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
        int w = ts.Width + Px(42);
        var r = new Rectangle(alignRight ? x - w : x, y, w, Px(32));
        AddHit(r, key, click);
        if (IsHover(key)) Fill(g, p.Hover, r, 6);
        TextRenderer.DrawText(g, glyph, icon, new Rectangle(r.Left + Px(8), r.Top, Px(18), r.Height), p.Text, C);
        TextRenderer.DrawText(g, text, font, new Rectangle(r.Left + Px(32), r.Top, ts.Width + Px(4), r.Height), p.Text, L);
        return r;
    }

    protected int PaintedHeight(Func<Graphics, Palette, int> draw)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        int before = _hits.Count;
        int y = draw(g, Palette.Get(Theme.AppsDark));
        if (_hits.Count > before) _hits.RemoveRange(before, _hits.Count - before);
        return (int)Math.Ceiling(y / S);
    }

    protected enum PillStyle { Accent, Neutral, Danger }

    protected void Pill(Graphics g, Palette p, Rectangle r, string text, string key, Action click,
                        PillStyle style = PillStyle.Accent, bool enabled = true)
    {
        if (enabled) AddHit(r, key, _ => click());
        var bg = style switch { PillStyle.Danger => Theme.Red, PillStyle.Neutral => p.Chip, _ => p.Accent };
        var fg = style == PillStyle.Neutral ? p.Text
               : style == PillStyle.Danger || !Theme.AppsDark ? Color.White : Color.FromArgb(20, 20, 20);
        if (!enabled) { bg = Theme.Mix(bg, p.Background, 0.5f); fg = Theme.Mix(fg, bg, 0.4f); }
        else if (IsHover(key)) bg = Theme.Mix(bg, p.Text, style == PillStyle.Neutral ? 0.08f : 0.15f);
        Fill(g, bg, r, 4);
        using var f = UiFont(12, true);
        TextRenderer.DrawText(g, text, f, r, fg, C | TextFormatFlags.EndEllipsis);
    }

    protected void Switch(Graphics g, Palette p, Rectangle track, bool on, string key, Action click, Rectangle? hit = null)
    {
        AddHit(hit ?? track, key, _ => click());
        bool hover = IsHover(key);
        float rad = track.Height / 2f;
        using (var path = Theme.Rounded(track, rad))
        {
            if (on)
            {
                using var b = new SolidBrush(hover ? Theme.Mix(p.Accent, p.Text, 0.12f) : p.Accent);
                g.FillPath(b, path);
            }
            else
            {
                if (hover) { using var hb = new SolidBrush(p.Hover); g.FillPath(hb, path); }
                using var pen = new Pen(p.SubText, Math.Max(1f, S));
                g.DrawPath(pen, path);
            }
        }
        float d = track.Height - Px(on ? 8 : 10);
        float kx = on ? track.Right - Px(4) - d : track.Left + Px(5);
        using var kb = new SolidBrush(on ? (Theme.AppsDark ? Color.FromArgb(20, 20, 20) : Color.White) : p.SubText);
        g.FillEllipse(kb, kx, track.Top + (track.Height - d) / 2f, d, d);
    }

    protected void Message(Graphics g, Palette p, int y, int h, string text)
    {
        using var f = UiFont(12);
        TextRenderer.DrawText(g, text, f, new Rectangle(Px(22), y, Width - Px(44), Px(h)), p.SubText, L);
    }

    protected void SectionLabel(Graphics g, Palette p, string text, int y, int h = 28)
    {
        using var f = UiFont(12, true);
        TextRenderer.DrawText(g, text, f, new Rectangle(Px(22), y, Width - Px(44), Px(h)), p.SubText, L);
    }
}
