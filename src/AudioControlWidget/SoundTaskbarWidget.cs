using System.Drawing.Drawing2D;
using AudioCtl.Interop;
using BtBattery;

namespace AudioCtl;

sealed class SoundTaskbarWidget : Form
{
    readonly SoundService _svc;
    readonly SoundSettings _settings;
    readonly ContextMenuStrip _menu;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 750 };
    readonly HoverCardController _hoverCard;
    readonly Native.WinEventProc _hookProc;
    IntPtr _hook;

    readonly List<(Rectangle rect, EDataFlow flow)> _parts = new();
    bool _hiddenForSpace;
    bool _placed, _hiddenForFullScreen, _down, _dragging;
    EDataFlow? _hoverPart;
    MicStatus.State _mic = new(false, false, null);
    Point _downCursor, _downLocation;

    public event Action? Clicked;

    public SoundTaskbarWidget(SoundService svc, SoundSettings settings, ContextMenuStrip menu)
    {
        _svc = svc;
        _settings = settings;
        _menu = menu;
        Text = "Audio Control Widget";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _timer.Tick += (_, _) => { KeepOnTop(); PollMic(); };
        _hookProc = (_, _, _, _, _, _, _) => { if (IsHandleCreated) BeginInvoke(KeepOnTop); };
        _hoverCard = new HoverCardController(this, HoverInfo);
    }

    void PollMic()
    {
        if (!_settings.ShowMicInWidget) return;
        var now = MicStatus.Current;
        if (now == _mic) return;
        _mic = now;
        Invalidate();
        _hoverCard.Refresh();
    }

    HoverContent HoverInfo()
    {
        var rows = new List<HoverRow>();
        foreach (var ep in new[] { _svc.Output, _svc.Input })
        {
            if (!ep.Available) continue;
            if (ep.Flow == EDataFlow.Capture)
            {
                var (value, color) = ep.Muted ? ("Muted", (Color?)Theme.Red)
                    : _mic.AppMuted ? ($"Muted in {_mic.App ?? "app"}", (Color?)Theme.Amber)
                    : _mic.InUse ? ($"Live · {_mic.App ?? "in use"} · {ep.Percent}%", (Color?)Theme.Red)
                    : ($"{ep.Percent}%", (Color?)null);
                rows.Add(new HoverRow(ep.Muted || _mic.AppMuted ? SoundGlyphs.MicOff : SoundGlyphs.Mic, ep.DeviceName, value, color));
                continue;
            }
            rows.Add(new HoverRow(SoundGlyphs.Level(ep), ep.DeviceName,
                ep.Muted ? "Muted" : $"{ep.Percent}%", ep.Muted ? Theme.Red : null));
        }
        if (rows.Count == 0) rows.Add(new HoverRow("", "No audio devices", ""));
        return new HoverContent("Sound", rows, _mic.CallMute ? "Middle-click the mic to mute/unmute " + (_mic.App ?? "the call") : "Scroll to adjust · Middle-click to mute");
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x8 | 0x08000000; return cp; }
    }

    protected override bool ShowWithoutActivation => true;
    float S => DeviceDpi / 96f;
    int Px(float v) => (int)Math.Round(v * S);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.StyleWindow(Handle, Theme.TaskbarDark);
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc, 0, 0, 0);
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
        if (!Native.FullScreenAppRunning()) Show(); else _hiddenForFullScreen = true;
    }

    public void ApplyTheme()
    {
        if (IsHandleCreated) Native.StyleWindow(Handle, Theme.TaskbarDark);
        Invalidate();
    }

    public void UpdateView()
    {
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
            var nb = new[] { "Bluetooth Battery Widget" };
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

    IEnumerable<SoundService.Endpoint> Endpoints()
    {
        yield return _svc.Output;
        if (_settings.ShowMicInWidget && _svc.Input.Available) yield return _svc.Input;
    }

    void Relayout()
    {
        using var tf = TextFont();
        using var icf = IconFont();
        int glyphW = TextRenderer.MeasureText("", icf, Size.Empty, Flags).Width;
        int textW = Math.Max(TextRenderer.MeasureText("100%", tf, Size.Empty, Flags).Width,
                             TextRenderer.MeasureText("Muted", tf, Size.Empty, Flags).Width);
        _parts.Clear();
        int x = Px(4);
        foreach (var ep in Endpoints())
        {
            int w = Px(6) + glyphW + Px(5) + textW + Px(6);
            _parts.Add((new Rectangle(x, Px(2), w, Px(30)), ep.Flow));
            x += w + Px(2);
        }
        var size = new Size(x + Px(2), Px(34));
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
            int y = wa.Bottom + (b.Bottom - wa.Bottom - Height) / 2;
            if (Native.WindowRect("Bluetooth Battery Widget") is Rectangle bt && bt.Top >= wa.Bottom - 4)
                return new Point(bt.Left - Width - Px(6), y);
            if (Native.TrayNotifyRect() is Rectangle t && t.Left > b.Left + b.Width / 2)
                return new Point(t.Left - Width - Px(8), y);
            return new Point(b.Right - Width - Px(320), y);
        }
        return new Point(wa.Right - Width - Px(12), wa.Bottom - Height - Px(12));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool dark = Theme.TaskbarDark;
        var bg = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(238, 238, 238);
        var hover = dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(222, 222, 222);
        var fg = dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(26, 26, 26);
        var dim = dark ? Color.FromArgb(140, 140, 140) : Color.FromArgb(120, 120, 120);
        var accent = dark ? Theme.Mix(Theme.Accent, Color.White, 0.35f) : Theme.Accent;
        g.Clear(bg);

        using var tf = TextFont();
        using var icf = IconFont();
        foreach (var (rect, flow) in _parts)
        {
            var ep = _svc[flow];
            if (_hoverPart == flow)
            {
                using var hb = new SolidBrush(hover);
                using var hp = Theme.Rounded(rect, 4 * S);
                g.FillPath(hb, hp);
            }
            bool isMic = flow == EDataFlow.Capture;
            bool appMuted = isMic && _mic.AppMuted && !ep.Muted;
            bool muted = ep.Muted || appMuted, avail = ep.Available;
            bool live = isMic && _mic.InUse && !muted;
            var muteColor = ep.Muted ? Theme.Red : Theme.Amber;
            int pct = ep.Percent;
            string glyph = !avail ? (isMic ? SoundGlyphs.MicOff : "")
                         : isMic ? (muted ? SoundGlyphs.MicOff : SoundGlyphs.Mic)
                         : SoundGlyphs.Level(ep);
            var gs = TextRenderer.MeasureText(glyph, icf, Size.Empty, Flags);
            int x = rect.Left + Px(6);
            TextRenderer.DrawText(g, glyph, icf, new Rectangle(x, 0, gs.Width, Height - Px(2)), muted ? muteColor : fg, Flags);
            if (live)
            {
                using var dot = new SolidBrush(Theme.Red);
                g.FillEllipse(dot, x + gs.Width - Px(5), Px(7), Px(6), Px(6));
            }
            x += gs.Width + Px(5);

            string text = !avail ? "—" : muted ? "Muted" : $"{pct}%";
            var ts = TextRenderer.MeasureText(text, tf, Size.Empty, Flags);
            TextRenderer.DrawText(g, text, tf, new Rectangle(x, muted ? 0 : -Px(2), ts.Width, Height),
                !avail ? dim : muted ? muteColor : fg, Flags);

            if (avail && !muted)
            {
                var bar = new RectangleF(x, Height - Px(8), ts.Width, Math.Max(2f, 2.5f * S));
                using var track = new SolidBrush(Color.FromArgb(60, fg));
                using var fill = new SolidBrush(muted ? dim : accent);
                using var tp = Theme.Rounded(bar, bar.Height / 2);
                g.FillPath(track, tp);
                var fr = bar with { Width = Math.Max(bar.Height, bar.Width * pct / 100f) };
                using var fp = Theme.Rounded(fr, bar.Height / 2);
                g.FillPath(fill, fp);
            }
        }
    }

    EDataFlow? PartAt(Point p)
    {
        foreach (var (rect, flow) in _parts) if (rect.Contains(p)) return flow;
        return _parts.Count > 0 ? _parts[0].flow : null;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverPart = null; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (PartAt(e.Location) is EDataFlow flow) _svc[flow].Step(Math.Sign(e.Delta) * _settings.ScrollStep);
    }

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
        var part = PartAt(e.Location);
        if (part != _hoverPart) { _hoverPart = part; Invalidate(); }
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
        switch (e.Button)
        {
            case MouseButtons.Right:
                Native.SetForegroundWindow(Handle);
                _menu.Show(Cursor.Position);
                return;
            case MouseButtons.Middle:
                if (PartAt(e.Location) is EDataFlow flow)
                {
                    if (flow == EDataFlow.Capture && _mic.CallMute && !_svc[flow].Muted) MicStatus.ToggleCallMute();
                    else { var ep = _svc[flow]; ep.Muted = !ep.Muted; }
                }
                return;
            case MouseButtons.Left:
                if (_dragging)
                {
                    bool docked = Native.OnTaskbar(Bounds);
                    _settings.WidgetX = docked ? null : Left;
                    _settings.WidgetY = docked ? null : Top;
                    _settings.Save();
                }
                else if (_down) Clicked?.Invoke();
                _down = _dragging = false;
                return;
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Relayout(); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _hoverCard.Dispose(); }
        base.Dispose(disposing);
    }
}
