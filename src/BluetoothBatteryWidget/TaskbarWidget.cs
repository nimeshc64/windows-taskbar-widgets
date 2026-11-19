using System.Drawing.Drawing2D;

namespace BtBattery;

sealed class TaskbarWidget : Form
{
    readonly AppSettings _settings;
    readonly ContextMenuStrip _menu;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 750 };
    readonly HoverCardController _hoverCard;
    readonly Native.WinEventProc _hookProc;
    IntPtr _hook;

    List<DeviceBattery> _items = new();
    bool _hiddenForSpace;
    bool _placed, _hiddenForFullScreen, _hover, _down, _dragging;
    Point _downCursor, _downLocation;

    public event Action? Clicked;

    public TaskbarWidget(AppSettings settings, ContextMenuStrip menu)
    {
        _settings = settings;
        _menu = menu;
        Text = "Bluetooth Battery Widget";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _timer.Tick += (_, _) => KeepOnTop();
        _hookProc = (_, _, _, _, _, _, _) => { if (IsHandleCreated) BeginInvoke(KeepOnTop); };
        _hoverCard = new HoverCardController(this, HoverInfo);
    }

    HoverContent HoverInfo()
    {
        int low = _settings.LowBatteryThreshold;
        var rows = _items.Count == 0
            ? new List<HoverRow> { new(Glyphs.Bluetooth, "No connected device reports battery", "") }
            : _items.Select(d => new HoverRow(Glyphs.For(d.Kind), d.Name, $"{d.Battery}%",
                  Theme.LevelColor(d.Battery!.Value, low), d.Battery)).ToList();
        return new HoverContent("Bluetooth battery", rows, "Click for all devices · Drag to move");
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x8 | 0x08000000;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;
    float S => DeviceDpi / 96f;
    int Px(float v) => (int)Math.Round(v * S);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle, Theme.TaskbarDark);
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _hookProc, 0, 0, 0);
        _timer.Start();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _timer.Stop();
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        base.OnHandleDestroyed(e);
    }

    public void ShowWidget()
    {
        Relayout();
        if (!_placed) { Location = SavedOrDefaultLocation(); _placed = true; }
        if (!Native.FullScreenAppRunning()) Show();
        else _hiddenForFullScreen = true;
    }

    public void ApplyTheme()
    {
        if (IsHandleCreated) Native.StyleWindow(Handle, Theme.TaskbarDark);
        Invalidate();
    }

    public void SetDevices(IEnumerable<DeviceBattery> devices)
    {
        _items = devices.Where(d => d.IsConnected && d.Battery.HasValue).ToList();
        Relayout();
        Invalidate();
        _hoverCard.Refresh();
    }

    public void ResetPosition()
    {
        _settings.WidgetX = _settings.WidgetY = null;
        _settings.Save();
        Location = DefaultLocation();
    }

    void KeepOnTop()
    {
        if (IsDisposed || !IsHandleCreated) return;
        bool fs = Native.FullScreenAppRunning();
        if (fs && Visible) { Hide(); _hiddenForFullScreen = true; return; }
        if (!fs && _hiddenForFullScreen) { _hiddenForFullScreen = false; if (!_hiddenForSpace) Show(); }
        Native.MarkWanted(Handle, !fs);
        if (!_dragging)
        {
            var nb = Array.Empty<string>();
            float sc = DeviceDpi / 96f;
            var saved = _settings.WidgetX is int sx && _settings.WidgetY is int sy ? new Point(sx, sy) : (Point?)null;
            bool useSaved = saved is Point sp && Native.InTaskbarZone(new Rectangle(sp, Size), sc);
            Point? target = useSaved ? Native.ClearOfNeighbors(Bounds, nb, sc) : Native.DockLocation(Size, nb, sc);
            bool room = useSaved || target is not Point t || Native.FitsInTaskbarZone(t.X, sc);
            if (!room && Visible) { _hoverCard.Hide(); Hide(); _hiddenForSpace = true; }
            else if (room && _hiddenForSpace) { _hiddenForSpace = false; Show(); }
            if (room && target is Point p && p != Location) Location = p;
        }
        if (Visible && !_dragging && !Native.IsAboveTaskbar(Handle))
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    Font TextFont() => new("Segoe UI Semibold", 12.5f * S, GraphicsUnit.Pixel);
    Font IconFont() => new(Theme.IconFont, 14f * S, GraphicsUnit.Pixel);
    const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

    IEnumerable<(string glyph, string text, int? pct)> Entries() => _items.Count == 0
        ? new[] { (Glyphs.Bluetooth, "—", (int?)null) }
        : _items.Select(d => (Glyphs.For(d.Kind), $"{d.Battery}%", d.Battery));

    void Relayout()
    {
        using var tf = TextFont();
        using var icf = IconFont();
        int w = Px(10);
        foreach (var (glyph, text, _) in Entries())
        {
            w += TextRenderer.MeasureText(glyph, icf, Size.Empty, Flags).Width + Px(5);
            w += TextRenderer.MeasureText(text, tf, Size.Empty, Flags).Width + Px(12);
        }
        w += Px(-2);
        var size = new Size(w, Px(34));
        if (size == Size) return;
        int right = Right;
        Size = size;
        if (_placed) Left = right - size.Width;
    }

    Point SavedOrDefaultLocation()
    {
        if (_settings.WidgetX is int x && _settings.WidgetY is int y)
        {
            var c = new Point(x + Width / 2, y + Height / 2);
            if (Screen.AllScreens.Any(s => s.Bounds.Contains(c))) return new Point(x, y);
        }
        return DefaultLocation();
    }

    Point DefaultLocation()
    {
        var screen = Screen.PrimaryScreen!;
        var b = screen.Bounds;
        var wa = screen.WorkingArea;
        if (wa.Bottom < b.Bottom)
        {
            int taskTop = wa.Bottom, taskH = b.Bottom - wa.Bottom;
            int y = taskTop + (taskH - Height) / 2;
            var tray = Native.TrayNotifyRect();
            int x = tray is Rectangle t && t.Left > b.Left + b.Width / 2 ? t.Left - Width - Px(8) : b.Right - Width - Px(320);
            return new Point(x, y);
        }
        return new Point(wa.Right - Width - Px(12), wa.Bottom - Height - Px(12));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool dark = Theme.TaskbarDark;
        var bg = dark ? (_hover ? Color.FromArgb(52, 52, 52) : Color.FromArgb(32, 32, 32))
                      : (_hover ? Color.FromArgb(222, 222, 222) : Color.FromArgb(238, 238, 238));
        var fg = dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(26, 26, 26);
        g.Clear(bg);

        using var tf = TextFont();
        using var icf = IconFont();
        int x = Px(10);
        foreach (var (glyph, text, pct) in Entries())
        {
            var gs = TextRenderer.MeasureText(glyph, icf, Size.Empty, Flags);
            TextRenderer.DrawText(g, glyph, icf, new Rectangle(x, 0, gs.Width, Height - Px(2)), fg, Flags);
            x += gs.Width + Px(5);

            var ts = TextRenderer.MeasureText(text, tf, Size.Empty, Flags);
            var color = pct is int p && p <= _settings.LowBatteryThreshold ? Theme.Red : fg;
            TextRenderer.DrawText(g, text, tf, new Rectangle(x, -Px(2), ts.Width, Height), color, Flags);

            if (pct is int level)
            {
                var bar = new RectangleF(x, Height - Px(8), ts.Width, Math.Max(2f, 2.5f * S));
                using var track = new SolidBrush(Color.FromArgb(60, fg));
                using var fill = new SolidBrush(Theme.LevelColor(level, _settings.LowBatteryThreshold));
                using var tp = Theme.Rounded(bar, bar.Height / 2);
                g.FillPath(track, tp);
                var fr = bar with { Width = Math.Max(bar.Height, bar.Width * level / 100f) };
                using var fp = Theme.Rounded(fr, bar.Height / 2);
                g.FillPath(fill, fp);
            }
            x += ts.Width + Px(12);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _down = true;
        _downCursor = Cursor.Position;
        _downLocation = Location;
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
        if (wa.Bottom < b.Bottom && c.Y >= wa.Bottom)
            loc.Y = wa.Bottom + (b.Bottom - wa.Bottom - Height) / 2;
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
        if (_dragging)
        {
            bool docked = Native.OnTaskbar(Bounds);
            _settings.WidgetX = docked ? null : Left;
            _settings.WidgetY = docked ? null : Top;
            _settings.Save();
        }
        else if (_down) Clicked?.Invoke();
        _down = _dragging = false;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _hoverCard.Dispose(); }
        base.Dispose(disposing);
    }
}
