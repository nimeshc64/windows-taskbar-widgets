using System.Drawing.Drawing2D;

namespace BtBattery;

public sealed record HoverRow(string Glyph, string Title, string Value, Color? ValueColor = null, int? Battery = null);
public sealed record HoverContent(string? Header, IReadOnlyList<HoverRow> Rows, string? Hint);

sealed class HoverCard : Form
{
    const int Pad = 12, HeaderH = 22, RowH = 28, HintH = 26, GlyphW = 24, MaxTitle = 300;
    HoverContent? _c;

    public HoverCard()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x8 | 0x08000000 | 0x20;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; }
        base.WndProc(ref m);
    }

    float S => DeviceDpi / 96f;
    int Px(float v) => (int)Math.Round(v * S);

    Font HeaderFont() => new("Segoe UI Semibold", 12f * S, GraphicsUnit.Pixel);
    Font TitleFont() => new("Segoe UI", 12.5f * S, GraphicsUnit.Pixel);
    Font ValueFont() => new("Segoe UI Semibold", 12.5f * S, GraphicsUnit.Pixel);
    Font HintFont() => new("Segoe UI", 11.5f * S, GraphicsUnit.Pixel);
    Font IconFont() => new(Theme.IconFont, 14f * S, GraphicsUnit.Pixel);
    const TextFormatFlags Measure = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    public void ShowAbove(Rectangle anchor, HoverContent content)
    {
        _c = content;
        Native.StyleWindow(Handle, Theme.TaskbarDark);

        using var hf = HeaderFont(); using var tf = TitleFont(); using var vf = ValueFont(); using var nf = HintFont();
        int titleW = content.Rows.Select(r => TextRenderer.MeasureText(r.Title, tf, Size.Empty, Measure).Width).DefaultIfEmpty(0).Max();
        int valueW = content.Rows.Select(r => TextRenderer.MeasureText(r.Value, vf, Size.Empty, Measure).Width).DefaultIfEmpty(0).Max();
        int battW = content.Rows.Any(r => r.Battery.HasValue) ? Px(34) : 0;
        int rowsW = Px(GlyphW) + Math.Min(titleW, Px(MaxTitle)) + Px(20) + battW + valueW;
        int headW = content.Header == null ? 0 : TextRenderer.MeasureText(content.Header, hf, Size.Empty, Measure).Width;
        int hintW = content.Hint == null ? 0 : TextRenderer.MeasureText(content.Hint, nf, Size.Empty, Measure).Width;
        int w = Math.Max(Px(180), Math.Max(rowsW, Math.Max(headW, hintW)) + Px(2 * Pad));
        int h = Px(Pad) + (content.Header != null ? Px(HeaderH) : 0) + content.Rows.Count * Px(RowH)
                + (content.Hint != null ? Px(HintH + 6) : 0) + Px(Pad - 4);

        var wa = Screen.FromRectangle(anchor).WorkingArea;
        int x = anchor.Left + anchor.Width / 2 - w / 2;
        int y = anchor.Top - h - Px(10);
        if (y < wa.Top) y = anchor.Bottom + Px(10);
        x = Math.Clamp(x, wa.Left + Px(8), Math.Max(wa.Left + Px(8), wa.Right - w - Px(8)));
        Bounds = new Rectangle(x, y, w, h);

        if (!Visible) Show();
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_c == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool dark = Theme.TaskbarDark;
        var p = Palette.Get(dark);
        g.Clear(dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249));

        using var hf = HeaderFont(); using var tf = TitleFont(); using var vf = ValueFont(); using var nf = HintFont();
        using var icf = IconFont();
        const TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                  TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        const TextFormatFlags R = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                  TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        const TextFormatFlags C = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

        int left = Px(Pad), right = Width - Px(Pad);
        int y = Px(Pad) - Px(2);
        if (_c.Header != null)
        {
            TextRenderer.DrawText(g, _c.Header, hf, new Rectangle(left, y, right - left, Px(HeaderH)), p.SubText, L);
            y += Px(HeaderH);
        }

        int valueW = _c.Rows.Select(r => TextRenderer.MeasureText(r.Value, vf, Size.Empty, Measure).Width).DefaultIfEmpty(0).Max();
        foreach (var row in _c.Rows)
        {
            var rr = new Rectangle(left, y, right - left, Px(RowH));
            TextRenderer.DrawText(g, row.Glyph, icf, new Rectangle(rr.Left, rr.Top, Px(18), rr.Height), p.Text, C);
            var valueRect = new Rectangle(rr.Right - valueW, rr.Top, valueW, rr.Height);
            TextRenderer.DrawText(g, row.Value, vf, valueRect, row.ValueColor ?? p.Text, R);
            int titleRight = valueRect.Left - Px(16);
            if (row.Battery is int pct)
            {
                var b = new RectangleF(valueRect.Left - 34 * S, rr.Top + (rr.Height - 12 * S) / 2f, 25 * S, 12 * S);
                Theme.DrawBattery(g, b, pct, row.ValueColor ?? Theme.Green, p.SubText, S);
                titleRight = (int)b.Left - Px(10);
            }
            TextRenderer.DrawText(g, row.Title, tf, Rectangle.FromLTRB(rr.Left + Px(GlyphW), rr.Top, titleRight, rr.Bottom), p.Text, L);
            y += Px(RowH);
        }

        if (_c.Hint != null)
        {
            y += Px(4);
            using (var line = new Pen(p.Border, 1)) g.DrawLine(line, left, y, right, y);
            TextRenderer.DrawText(g, _c.Hint, nf, new Rectangle(left, y + Px(2), right - left, Px(HintH)), p.SubText, L);
        }
    }
}

sealed class HoverCardController : IDisposable
{
    readonly Control _owner;
    readonly Func<HoverContent> _content;
    readonly HoverCard _card = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 450 };

    public HoverCardController(Control owner, Func<HoverContent> content)
    {
        _owner = owner;
        _content = content;
        owner.MouseEnter += (_, _) => { if (!_card.Visible) _timer.Start(); };
        owner.MouseLeave += (_, _) => Hide();
        owner.MouseDown += (_, e) => { if (e.Button != MouseButtons.Middle) Hide(); };
        owner.VisibleChanged += (_, _) => { if (!owner.Visible) Hide(); };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (owner.IsDisposed || !owner.Visible || Control.MouseButtons != MouseButtons.None) return;
            if (!owner.ClientRectangle.Contains(owner.PointToClient(Cursor.Position))) return;
            Show();
        };
    }

    void Show() => _card.ShowAbove(_owner.RectangleToScreen(_owner.ClientRectangle), _content());

    public void Refresh() { if (_card.Visible) Show(); }

    public void Hide()
    {
        _timer.Stop();
        if (_card.Visible) _card.Hide();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _card.Dispose();
    }
}
