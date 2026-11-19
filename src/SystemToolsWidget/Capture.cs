using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using BtBattery;

namespace SysTools;

static class ScreenCapture
{
    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    public static Bitmap Grab(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Point.Empty, r.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static string SaveAndCopy(Bitmap bmp)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        bmp.Save(path, ImageFormat.Png);
        try { Clipboard.SetImage(bmp); } catch (Exception ex) { ToolsService.Log("Clipboard: " + ex.Message); }
        return path;
    }
}

sealed class RegionPicker : Form
{
    readonly Bitmap _shot;
    readonly Rectangle _virtual;
    Point? _start;
    Rectangle _sel;
    public Bitmap? Result { get; private set; }

    public RegionPicker()
    {
        _virtual = SystemInformation.VirtualScreen;
        _shot = ScreenCapture.Grab(_virtual);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        Cursor = Cursors.Cross;
        Bounds = _virtual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x02E0) return;
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _virtual;
        Activate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; Close(); return; }
        if (e.Button == MouseButtons.Left) { _start = e.Location; _sel = Rectangle.Empty; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_start is Point s)
        {
            var old = _sel;
            _sel = Rectangle.FromLTRB(Math.Min(s.X, e.X), Math.Min(s.Y, e.Y), Math.Max(s.X, e.X), Math.Max(s.Y, e.Y));
            var dirty = Rectangle.Union(old, _sel);
            dirty.Inflate(160, 60);
            Invalidate(dirty);
        }
        else Invalidate(new Rectangle(e.X - 200, e.Y - 200, 400, 400));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _start == null) return;
        Rectangle crop = _sel;
        if (crop.Width < 6 || crop.Height < 6)
        {
            var mon = Screen.FromPoint(PointToScreen(e.Location)).Bounds;
            crop = new Rectangle(mon.X - _virtual.X, mon.Y - _virtual.Y, mon.Width, mon.Height);
        }
        Result = _shot.Clone(crop, _shot.PixelFormat);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImageUnscaled(_shot, 0, 0);
        using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
        using (var region = new Region(ClientRectangle))
        {
            if (!_sel.IsEmpty) region.Exclude(_sel);
            g.FillRegion(dim, region);
        }

        float s = DeviceDpi / 96f;
        var accent = Theme.Mix(Theme.Accent, Color.White, 0.25f);
        using var font = new Font("Segoe UI Semibold", 12 * s, GraphicsUnit.Pixel);
        if (!_sel.IsEmpty)
        {
            using var pen = new Pen(accent, Math.Max(2f, 2 * s));
            g.DrawRectangle(pen, _sel);
            Label(g, font, $"{_sel.Width} × {_sel.Height}", new Point(_sel.Left, _sel.Top - (int)(30 * s) < 0 ? _sel.Bottom + (int)(6 * s) : _sel.Top - (int)(30 * s)), s);
        }
        else
        {
            var c = PointToClient(Cursor.Position);
            Label(g, font, "Drag to capture an area · Click for the whole screen · Esc to cancel", new Point(c.X + (int)(18 * s), c.Y + (int)(18 * s)), s);
        }
    }

    static void Label(Graphics g, Font f, string text, Point at, float s)
    {
        var size = TextRenderer.MeasureText(text, f);
        var r = new Rectangle(at.X, at.Y, size.Width + (int)(10 * s), size.Height + (int)(8 * s));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(r, 5 * s);
        using var b = new SolidBrush(Color.FromArgb(220, 32, 32, 32));
        g.FillPath(b, path);
        TextRenderer.DrawText(g, text, f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        g.SmoothingMode = SmoothingMode.None;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _shot.Dispose();
        base.Dispose(disposing);
    }
}
