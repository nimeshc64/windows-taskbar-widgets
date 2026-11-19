using System.Drawing.Drawing2D;
using BtBattery;

namespace NetTools;

public sealed class NetSettings
{
    public bool ShowWidget { get; set; } = true;
    public bool ShowLatency { get; set; } = true;
    public bool ShowVpn { get; set; } = true;
    public bool NotifyChanges { get; set; } = true;
    public List<SpeedResult> SpeedHistory { get; set; } = new();
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }
    public void Save() => AppFiles.Save(NetService.AppName, this);
}

static class NetGlyphs
{
    public const string Ethernet = "", Wifi1 = "", Wifi2 = "", Wifi3 = "", Wifi4 = "";
    public const string Globe = "", Vpn = "", Speed = "", Copy = "", Qr = "", Chart = "";
    public const string Settings = "", Refresh = "", Lock = "", Back = "", Down = "", Up = "";
    public const string Cellular = "", Location = "";

    public static string Wifi(int? bars) => bars switch { >= 4 => Wifi4, 3 => Wifi3, 2 => Wifi2, _ => Wifi1 };
    public static string For(NetService s) =>
        !s.Connected ? Globe : s.Kind == "Wi-Fi" ? Wifi(s.Bars) : s.Kind == "Cellular" ? Cellular : Ethernet;
}

sealed class NetWidget : TaskbarStrip
{
    readonly NetService _svc;
    readonly NetSettings _settings;

    public NetWidget(NetService svc, NetSettings settings, ContextMenuStrip menu) : base("Network Widget", menu)
    {
        _svc = svc;
        _settings = settings;
    }

    protected override string[] NeighborTitles => new[]
    {
        "Developer Tools Widget", "Now Playing Widget", "System Monitor Widget", "Audio Control Widget", "Bluetooth Battery Widget",
    };
    protected override Point? SavedLocation =>
        _settings.WidgetX is int x && _settings.WidgetY is int y ? new Point(x, y) : null;
    protected override void SaveLocation(Point? p) { _settings.WidgetX = p?.X; _settings.WidgetY = p?.Y; _settings.Save(); }

    protected override string CompactGlyph => NetGlyphs.For(_svc);
    protected override Color CompactColor => !_svc.Connected ? Dim : !_svc.Internet ? Theme.Amber : _svc.VpnActive ? Theme.Green : Fg;

    public void UpdateView() { Relayout(); Invalidate(); Hover.Refresh(); }

    Font TextFont() => new("Segoe UI Semibold", 12f * S, GraphicsUnit.Pixel);
    Font Icons() => new(Theme.IconFont, 14f * S, GraphicsUnit.Pixel);
    const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

    static string Short(string s) => s.Length > 14 ? s[..13] + "…" : s;

    IEnumerable<(string Glyph, string? Text, Color GlyphColor, Color TextColor)> Parts()
    {
        var s = _svc;
        Color warn = Theme.Amber;
        if (!s.Connected) { yield return (NetGlyphs.Globe, "Offline", Dim, Dim); }
        else if (!s.Internet) { yield return (NetGlyphs.For(s), "No internet", warn, warn); }
        else
        {
            string text = _settings.ShowLatency && s.PingMs is double ms ? $"{ms:0} ms" : Short(s.Name);
            Color tc = _settings.ShowLatency && s.PingMs is double p && p >= 150 ? warn : Fg;
            yield return (NetGlyphs.For(s), text, Fg, tc);
        }
        if (_settings.ShowVpn && s.VpnActive) yield return (NetGlyphs.Vpn, null, Theme.Green, Fg);
    }

    protected override int ContentWidth()
    {
        using var tf = TextFont(); using var ic = Icons();
        int x = Px(4);
        foreach (var (glyph, text, _, _) in Parts())
        {
            x += Px(6) + TextRenderer.MeasureText(glyph, ic, Size.Empty, Flags).Width;
            if (text != null) x += Px(5) + TextRenderer.MeasureText(text, tf, Size.Empty, Flags).Width;
            x += Px(6);
        }
        return x + Px(4);
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
            var gs = TextRenderer.MeasureText(glyph, ic, Size.Empty, Flags);
            TextRenderer.DrawText(g, glyph, ic, new Rectangle(x, 0, gs.Width, Height), gc, Flags);
            x += gs.Width;
            if (text != null)
            {
                x += Px(5);
                var ts = TextRenderer.MeasureText(text, tf, Size.Empty, Flags);
                TextRenderer.DrawText(g, text, tf, new Rectangle(x, 0, ts.Width, Height), tc, Flags);
                x += ts.Width;
            }
            x += Px(6);
        }
    }

    protected override HoverContent GetHoverContent()
    {
        var s = _svc;
        var rows = new List<HoverRow>();
        if (!s.Connected) rows.Add(new(NetGlyphs.Globe, "Not connected", "Offline", Theme.Red));
        else
        {
            rows.Add(new(NetGlyphs.For(s), s.Name, string.Join(" · ", new[] { s.Kind, NetService.LinkSpeed(s.LinkBps) }.Where(t => t.Length > 0))));
            rows.Add(new(NetGlyphs.Globe, "Internet", s.Internet ? "Connected" : "No internet", s.Internet ? Theme.Green : Theme.Amber));
            if (s.PingMs is double ms) rows.Add(new(NetGlyphs.Speed, "Latency (1.1.1.1)", $"{ms:0} ms"));
            if (s.PublicIp != null) rows.Add(new(NetGlyphs.Location, s.PublicIp, s.Location ?? ""));
        }
        foreach (var v in s.Vpns.Where(v => v.Connected)) rows.Add(new(NetGlyphs.Vpn, v.Name, "Connected", Theme.Green));
        if (s.Today is var (d, u)) rows.Add(new(NetGlyphs.Chart, "Data today", $"↓ {NetService.Bytes(d)}  ↑ {NetService.Bytes(u)}"));
        return new HoverContent("Network", rows, "Click for Wi-Fi, VPN, speed test and data usage");
    }
}
