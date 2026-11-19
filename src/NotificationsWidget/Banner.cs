using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using BtBattery;

namespace MacNotify;

public sealed record NoteInfo(uint Id, string App, string? Aumid, string Title, string Body, Image? Icon, DateTime Time);

sealed class Banner : Form
{
    public NoteInfo Note { get; }
    public event Action<Banner>? Dismissed;
    public event Action<Banner>? Activated2;

    readonly System.Windows.Forms.Timer _life = new();
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 12 };
    bool _hover, _hoverClose, _closing;
    int _bodyH;
    int _targetX, _targetY;
    float _opacity;

    public Banner(NoteInfo note, int lifeMs)
    {
        Note = note;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        Opacity = 0;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        int w = Px(356);
        int bodyH = 0;
        if (Note.Body.Length > 0)
        {
            using var body = new Font("Segoe UI", 12.5f * S, GraphicsUnit.Pixel);
            int line = TextRenderer.MeasureText("Ag", body, Size.Empty, TextFormatFlags.NoPadding).Height;
            int textW = w - Px(12 + 36 + 10 + 12);
            int full = TextRenderer.MeasureText(Note.Body, body, new Size(textW, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height;
            _bodyH = Math.Min(full, line * 2);
            bodyH = _bodyH + Px(2);
        }
        Size = new Size(w, Px(10 + 17 + 19) + bodyH + Px(12));
        _life.Interval = lifeMs;
        _life.Tick += (_, _) => { if (!_hover) Dismiss(); };
        _anim.Tick += (_, _) => Animate();
    }

    float S => DeviceDpi / 96f;
    int Px(float v) => (int)Math.Round(v * S);

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }
    }
    protected override bool ShowWithoutActivation => true;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
        int dark = Theme.AppsDark ? 1 : 0; DwmSetWindowAttribute(Handle, 20, ref dark, 4);
        int border = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(Handle, 34, ref border, 4);
    }

    public int Side { get; set; } = 1;
    public bool FromBottom { get; set; }

    public void MoveTo(int x, int y, bool slideIn)
    {
        _targetX = x;
        _targetY = y;
        if (slideIn)
        {
            Location = Side != 0 ? new Point(x + Side * Px(40), y) : new Point(x, y + (FromBottom ? Px(30) : -Px(30)));
            Show();
            _life.Start();
        }
        _anim.Start();
    }

    void Animate()
    {
        int dx = _targetX - Left, dy = _targetY - Top;
        Location = new Point(Left + (Math.Abs(dx) <= 1 ? dx : (int)(dx * 0.3)), Top + (Math.Abs(dy) <= 1 ? dy : (int)(dy * 0.3)));
        float goal = _closing ? 0f : 0.985f;
        _opacity += (goal - _opacity) * 0.3f;
        if (Math.Abs(goal - _opacity) < 0.02f) _opacity = goal;
        Opacity = _opacity;
        if (_closing && _opacity <= 0.01f) { _anim.Stop(); Close(); return; }
        if (dx == 0 && dy == 0 && _opacity == goal) _anim.Stop();
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _life.Stop();
        if (Side != 0) _targetX = Left + Side * Px(60);
        else _targetY = Top + (FromBottom ? Px(40) : -Px(40));
        _anim.Start();
        Dismissed?.Invoke(this);
    }

    Rectangle CloseRect => new(Px(4), Px(4), Px(18), Px(18));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        bool dark = Theme.AppsDark;
        var bg = dark ? Color.FromArgb(44, 44, 46) : Color.FromArgb(246, 246, 246);
        var fg = dark ? Color.FromArgb(245, 245, 247) : Color.FromArgb(29, 29, 31);
        var dim = dark ? Color.FromArgb(160, 160, 166) : Color.FromArgb(110, 110, 115);
        g.Clear(_hover ? Theme.Mix(bg, fg, 0.04f) : bg);

        int pad = Px(12), icon = Px(36);
        var ir = new Rectangle(pad, (Height - icon) / 2, icon, icon);
        if (Note.Icon != null)
        {
            using var clip = Theme.Rounded(ir, Px(8));
            g.SetClip(clip);
            g.DrawImage(Note.Icon, ir);
            g.ResetClip();
        }
        else
        {
            using var b = new SolidBrush(Theme.Accent);
            using var p = Theme.Rounded(ir, Px(8));
            g.FillPath(b, p);
            using var f = new Font("Segoe UI Semibold", 16 * S, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, Note.App.Length > 0 ? Note.App[..1].ToUpperInvariant() : "•", f, ir, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int tx = ir.Right + Px(10), tw = Width - tx - Px(12);
        const TextFormatFlags One = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        using var small = new Font("Segoe UI", 11 * S, GraphicsUnit.Pixel);
        using var title = new Font("Segoe UI Semibold", 13 * S, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 12.5f * S, GraphicsUnit.Pixel);

        int y = Px(10);
        string when = TimeLabel();
        int whenW = Px(60);
        TextRenderer.DrawText(g, Note.App.ToUpperInvariant(), small, new Rectangle(tx, y, tw - whenW - Px(4), Px(15)), dim, One);
        TextRenderer.DrawText(g, when, small, new Rectangle(Width - Px(12) - whenW, y, whenW, Px(15)), dim,
            TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        y += Px(17);
        TextRenderer.DrawText(g, Note.Title, title, new Rectangle(tx, y, tw, Px(18)), fg, One);
        y += Px(19);
        if (Note.Body.Length > 0)
            TextRenderer.DrawText(g, Note.Body, body, new Rectangle(tx, y, tw, _bodyH), dark ? Color.FromArgb(220, 220, 225) : Color.FromArgb(60, 60, 65),
                TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        if (_hover)
        {
            var cr = CloseRect;
            using var cb = new SolidBrush(_hoverClose ? Theme.Mix(bg, fg, 0.35f) : Theme.Mix(bg, fg, 0.15f));
            g.FillEllipse(cb, cr);
            using var pen = new Pen(dark ? Color.White : Color.FromArgb(60, 60, 60), Math.Max(1.3f, 1.3f * S));
            int m = Px(6);
            g.DrawLine(pen, cr.Left + m, cr.Top + m, cr.Right - m, cr.Bottom - m);
            g.DrawLine(pen, cr.Right - m, cr.Top + m, cr.Left + m, cr.Bottom - m);
        }
    }

    string TimeLabel()
    {
        var d = DateTime.Now - Note.Time;
        return d.TotalMinutes < 1 ? "now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}m ago" : Note.Time.ToString("HH:mm");
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = _hoverClose = false;
        Invalidate();
        _life.Stop(); _life.Start();
        base.OnMouseLeave(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool c = CloseRect.Contains(e.Location);
        if (c != _hoverClose) { _hoverClose = c; Invalidate(); }
        Cursor = Cursors.Hand;
        base.OnMouseMove(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right || CloseRect.Contains(e.Location)) { Dismiss(); return; }
        if (e.Button == MouseButtons.Left) { Activated2?.Invoke(this); Dismiss(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _life.Dispose(); _anim.Dispose(); }
        base.Dispose(disposing);
    }
}
