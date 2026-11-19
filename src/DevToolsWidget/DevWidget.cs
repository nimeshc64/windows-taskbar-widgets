using System.Drawing.Drawing2D;
using BtBattery;

namespace DevTools;

public sealed class DevSettings
{
    public bool ShowWidget { get; set; } = true;
    public bool ShowPortsInWidget { get; set; } = true;
    public bool NotifyDevices { get; set; } = true;
    public bool ShowHighPorts { get; set; }
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }
    public void Save() => AppFiles.Save(DevService.AppName, this);
}

static class DevGlyphs
{
    public const string Phone = "", Emulator = "", Ports = "", Tools = "";
    public const string Camera = "", Install = "", Shell = "", Logcat = "";
    public const string Play = "", Close = "", Folder = "", Refresh = "";
}

sealed class DevWidget : TaskbarStrip
{
    readonly DevService _svc;
    readonly DevSettings _settings;

    public DevWidget(DevService svc, DevSettings settings, ContextMenuStrip menu) : base("Developer Tools Widget", menu)
    {
        _svc = svc;
        _settings = settings;
    }

    protected override string[] NeighborTitles =>
        new[] { "Now Playing Widget", "System Monitor Widget", "Audio Control Widget", "Bluetooth Battery Widget" };
    protected override Point? SavedLocation =>
        _settings.WidgetX is int x && _settings.WidgetY is int y ? new Point(x, y) : null;
    protected override void SaveLocation(Point? p) { _settings.WidgetX = p?.X; _settings.WidgetY = p?.Y; _settings.Save(); }

    List<PortInfo> Ports => _settings.ShowHighPorts ? _svc.Ports : _svc.Ports.Where(p => p.Port < 49152).ToList();

    protected override string CompactGlyph => _svc.Devices.Any(d => d.Online) ? DevGlyphs.Phone : DevGlyphs.Tools;
    protected override Color CompactColor =>
        _svc.Devices.Any(d => d.State == "unauthorized") ? Theme.Amber : _svc.Devices.Any(d => d.Online) ? AccentColor : Fg;

    public void UpdateView() { Relayout(); Invalidate(); Hover.Refresh(); }

    Font TextFont() => new("Segoe UI Semibold", 12f * S, GraphicsUnit.Pixel);
    Font Icons() => new(Theme.IconFont, 14f * S, GraphicsUnit.Pixel);
    const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
                                  TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
    const TextFormatFlags MeasureFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    string DeviceText
    {
        get
        {
            if (_svc.Adb == null) return "No SDK";
            if (!_svc.AdbServerRunning) return "adb off";
            var online = _svc.Devices.Where(d => d.Online).ToList();
            if (_svc.Devices.Count == 0) return "0";
            if (_svc.Devices.Count == 1)
            {
                var n = _svc.Devices[0].DisplayName;
                return n.Length > 16 ? n[..15] + "…" : n;
            }
            return $"{_svc.Devices.Count} devices";
        }
    }

    IEnumerable<(string glyph, string text, Color glyphColor, Color textColor)> Parts()
    {
        bool anyOnline = _svc.Devices.Any(d => d.Online);
        bool anyUnauth = _svc.Devices.Any(d => d.State == "unauthorized");
        yield return (DevGlyphs.Phone, DeviceText,
            anyUnauth ? Theme.Amber : anyOnline ? AccentColor : Dim,
            anyUnauth ? Theme.Amber : anyOnline ? Fg : Dim);
        if (_settings.ShowPortsInWidget)
            yield return (DevGlyphs.Ports, Ports.Count.ToString(), Fg, Fg);
    }

    protected override int ContentWidth()
    {
        using var tf = TextFont(); using var ic = Icons();
        int x = Px(4);
        foreach (var (glyph, text, _, _) in Parts())
            x += Px(6) + TextRenderer.MeasureText(glyph, ic, Size.Empty, MeasureFlags).Width + Px(5)
                 + TextRenderer.MeasureText(text, tf, Size.Empty, MeasureFlags).Width + Px(8);
        return x + Px(2);
    }

    protected override void PaintStrip(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Bg);
        using var tf = TextFont(); using var ic = Icons();
        int x = Px(4);
        foreach (var (glyph, text, gc, tc) in Parts())
        {
            x += Px(6);
            var gs = TextRenderer.MeasureText(glyph, ic, Size.Empty, MeasureFlags);
            TextRenderer.DrawText(g, glyph, ic, new Rectangle(x, 0, gs.Width, Height), gc, Flags);
            x += gs.Width + Px(5);
            var ts = TextRenderer.MeasureText(text, tf, Size.Empty, MeasureFlags);
            TextRenderer.DrawText(g, text, tf, new Rectangle(x, 0, ts.Width, Height), tc, Flags);
            x += ts.Width + Px(8);
        }
    }

    protected override HoverContent GetHoverContent()
    {
        var rows = new List<HoverRow>();
        if (_svc.Adb == null) rows.Add(new(DevGlyphs.Phone, "Android SDK not found", ""));
        else if (!_svc.AdbServerRunning) rows.Add(new(DevGlyphs.Phone, "ADB server is not running", "Off", Theme.Amber));
        else if (_svc.Devices.Count == 0) rows.Add(new(DevGlyphs.Phone, "No Android devices connected", ""));
        foreach (var d in _svc.Devices)
            rows.Add(new(d.IsEmulator ? DevGlyphs.Emulator : DevGlyphs.Phone,
                $"{d.DisplayName}{(d.Version != null ? " · Android " + d.Version : "")}",
                d.Online ? "Online" : d.State == "unauthorized" ? "Unauthorized" : d.State,
                d.Online ? Theme.Green : Theme.Amber));
        foreach (var p in Ports.Take(5))
            rows.Add(new(DevGlyphs.Ports, $":{p.Port}  {p.Process}", p.Label ?? ""));
        if (Ports.Count > 5) rows.Add(new("", $"+{Ports.Count - 5} more listening ports", ""));
        return new HoverContent("Developer tools", rows, "Click for screenshot, install APK, shell, logcat");
    }
}
