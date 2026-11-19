namespace BtBattery;

abstract class TaskbarStrip : Form
{
    readonly System.Windows.Forms.Timer _topTimer = new() { Interval = 750 };
    readonly Native.WinEventProc _hookProc;
    readonly ContextMenuStrip _menu;
    IntPtr _hook;
    bool _placed, _hiddenForFullScreen, _down, _dragging;
    Point _downCursor, _downLocation;

    protected readonly HoverCardController Hover;
    public event Action? Clicked;

    protected TaskbarStrip(string title, ContextMenuStrip menu)
    {
        _menu = menu;
        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _topTimer.Tick += (_, _) => KeepOnTop();
        _hookProc = (_, _, _, _, _, _, _) => { if (IsHandleCreated) BeginInvoke(KeepOnTop); };
        Hover = new HoverCardController(this, GetHoverContent);
    }

    protected abstract HoverContent GetHoverContent();
    protected abstract int ContentWidth();
    protected abstract Point? SavedLocation { get; }
    protected abstract void SaveLocation(Point? location);
    protected virtual string[] NeighborTitles => Array.Empty<string>();

    protected float S => DeviceDpi / 96f;
    protected int Px(float v) => (int)Math.Round(v * S);
    protected static bool Dark => Theme.TaskbarDark;
    protected static Color Fg => Dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(26, 26, 26);
    protected static Color Dim => Dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);
    protected static Color Bg => Dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(238, 238, 238);
    protected static Color HoverBg => Dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(222, 222, 222);
    protected static Color AccentColor => Dark ? Theme.Mix(Theme.Accent, Color.White, 0.35f) : Theme.Accent;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x8 | 0x08000000; return cp; }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle, Theme.TaskbarDark);
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc, 0, 0, 0);
        _topTimer.Start();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _topTimer.Stop();
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        base.OnHandleDestroyed(e);
    }

    public void ShowStrip()
    {
        _wanted = true;
        Relayout();
        if (!_placed) { Location = SavedOrDefaultLocation(); _placed = true; }
        _hiddenForFullScreen = Native.FullScreenAppRunning();
        DockToNeighbors();
        ApplyVisibility();
    }

    public void HideStrip()
    {
        _wanted = false;
        ApplyVisibility();
    }

    protected enum StripMode { Full, Icon, Hidden }
    protected StripMode Mode { get; private set; } = StripMode.Full;
    bool _wanted;

    protected virtual string CompactGlyph => "";
    protected virtual Color CompactColor => Fg;
    const int CompactWidth = 38;

    void ApplyVisibility()
    {
        bool show = _wanted && !_hiddenForFullScreen && Mode != StripMode.Hidden;
        if (show && !Visible) Show();
        else if (!show && Visible) { Hover.Hide(); Hide(); }
    }

    public void ApplyTheme()
    {
        if (IsHandleCreated) Native.StyleWindow(Handle, Theme.TaskbarDark);
        Invalidate();
    }

    public void ResetPosition()
    {
        SaveLocation(null);
        Location = DefaultLocation();
    }

    protected void Relayout()
    {
        var size = new Size(Mode == StripMode.Icon ? Px(CompactWidth) : ContentWidth(), Px(34));
        if (size == Size) return;
        int right = Right;
        Size = size;
        if (_placed) Left = right - size.Width;
    }

    void KeepOnTop()
    {
        if (IsDisposed || !IsHandleCreated) return;
        bool fs = Native.FullScreenAppRunning();
        if (fs != _hiddenForFullScreen) { _hiddenForFullScreen = fs; ApplyVisibility(); }
        Native.MarkWanted(Handle, _wanted && !fs);
        if (fs) return;
        if (_wanted && !_dragging) DockToNeighbors();
        if (Visible && !_dragging && !Native.IsAboveTaskbar(Handle))
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void DockToNeighbors()
    {
        if (SavedLocation is Point saved && Native.InTaskbarZone(new Rectangle(saved, Size), S))
        {
            if (Mode != StripMode.Full) SetMode(StripMode.Full);
            if (Native.ClearOfNeighbors(Bounds, NeighborTitles, S) is Point moved && moved != Location) Location = moved;
            return;
        }
        int reserve = Native.ReserveForLeft(Text, Px(CompactWidth), S);
        var mode = StripMode.Full;
        var target = Native.DockLocation(new Size(ContentWidth(), Px(34)), NeighborTitles, S);
        if (target is Point full && !Native.FitsInTaskbarZone(full.X - reserve, S))
        {
            target = Native.DockLocation(new Size(Px(CompactWidth), Px(34)), NeighborTitles, S);
            mode = target is Point icon && Native.FitsInTaskbarZone(icon.X, S) ? StripMode.Icon : StripMode.Hidden;
        }
        SetMode(mode);
        if (mode != StripMode.Hidden && target is Point p && p != Location) Location = p;
    }

    void SetMode(StripMode mode)
    {
        if (mode == Mode) return;
        Mode = mode;
        Relayout();
        Invalidate();
        ApplyVisibility();
    }

    protected sealed override void OnPaint(PaintEventArgs e)
    {
        if (Mode != StripMode.Icon) { PaintStrip(e); return; }
        var g = e.Graphics;
        g.Clear(Bg);
        if (_hoverCompact) FillRounded(g, HoverBg, new RectangleF(Px(3), Px(4), Width - Px(6), Height - Px(8)), 4 * S);
        using var f = new Font(Theme.IconFont, 15f * S, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, CompactGlyph, f, ClientRectangle, CompactColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    bool _hoverCompact;

    protected abstract void PaintStrip(PaintEventArgs e);

    Point SavedOrDefaultLocation()
    {
        if (SavedLocation is Point p && Screen.AllScreens.Any(s => s.Bounds.Contains(new Point(p.X + Width / 2, p.Y + Height / 2))))
            return p;
        return DefaultLocation();
    }

    Point DefaultLocation()
    {
        var screen = Screen.PrimaryScreen!;
        var b = screen.Bounds;
        var wa = screen.WorkingArea;
        if (wa.Bottom >= b.Bottom) return new Point(wa.Right - Width - Px(12), wa.Bottom - Height - Px(12));

        int y = wa.Bottom + (b.Bottom - wa.Bottom - Height) / 2;
        var neighbors = NeighborTitles.Select(Native.WindowRect).OfType<Rectangle>()
            .Where(r => r.Top >= wa.Bottom - 4).ToList();
        if (neighbors.Count > 0) return new Point(neighbors.Min(r => r.Left) - Width - Px(6), y);
        if (Native.TrayNotifyRect() is Rectangle t && t.Left > b.Left + b.Width / 2) return new Point(t.Left - Width - Px(8), y);
        return new Point(b.Right - Width - Px(320), y);
    }

    protected void FillRounded(Graphics g, Color c, RectangleF r, float radius)
    {
        using var b = new SolidBrush(c);
        using var p = Theme.Rounded(r, radius);
        g.FillPath(b, p);
    }

    protected void DrawBar(Graphics g, float x, float width, float fraction, Color color)
    {
        var bar = new RectangleF(x, Height - Px(8), width, Math.Max(2f, 2.5f * S));
        FillRounded(g, Color.FromArgb(60, Fg), bar, bar.Height / 2);
        FillRounded(g, color, bar with { Width = Math.Max(bar.Height, bar.Width * Math.Clamp(fraction, 0f, 1f)) }, bar.Height / 2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _down = true;
        _downCursor = Cursor.Position;
        _downLocation = Location;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (Mode == StripMode.Icon) { _hoverCompact = true; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverCompact) { _hoverCompact = false; Invalidate(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_down) return;
        var c = Cursor.Position;
        if (!_dragging && Math.Abs(c.X - _downCursor.X) + Math.Abs(c.Y - _downCursor.Y) < Px(5)) return;
        _dragging = true;
        var loc = new Point(_downLocation.X + c.X - _downCursor.X, _downLocation.Y + c.Y - _downCursor.Y);
        var screen = Screen.FromPoint(c);
        var b = screen.Bounds; var wa = screen.WorkingArea;
        if (wa.Bottom < b.Bottom && c.Y >= wa.Bottom) loc.Y = wa.Bottom + (b.Bottom - wa.Bottom - Height) / 2;
        Location = loc;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            Native.SetForegroundWindow(Handle);
            _menu.Show(Cursor.Position);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (_dragging) SaveLocation(Native.OnTaskbar(Bounds) ? null : Location);
        else if (_down) { if (Mode == StripMode.Icon) Clicked?.Invoke(); else OnStripClick(e.Location); }
        _down = _dragging = false;
    }

    protected virtual void OnStripClick(Point location) => Clicked?.Invoke();

    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Relayout(); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _topTimer.Dispose(); Hover.Dispose(); }
        base.Dispose(disposing);
    }
}
