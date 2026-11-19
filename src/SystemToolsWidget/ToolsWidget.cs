using System.Drawing.Drawing2D;
using BtBattery;

namespace SysTools;

public sealed class ToolsSettings
{
    public bool ShowWidget { get; set; } = true;
    public bool ShowBinInWidget { get; set; } = true;
    public bool ShowUpdatesInWidget { get; set; } = true;
    public string? LastCapture { get; set; }
    public List<WindowLayout> Layouts { get; set; } = new();
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }
    public void Save() => AppFiles.Save(ToolsService.AppName, this);
}

static class ToolGlyphs
{
    public const string Snip = "";
    public const string Crop = "", Monitor = "", Video = "", Bin = "", Update = "";
    public const string Layout = "", Eye = "", FileExt = "", Moon = "", Folder = "";
    public const string Copy = "", Open = "", Restart = "", Settings = "", Refresh = "";
    public const string Close = "", Save = "", Tools = "";
}

sealed class ToolsWidget : TaskbarStrip
{
    readonly ToolsService _svc;
    readonly ToolsSettings _settings;
    readonly Action _capture;
    Rectangle _shotBtn;
    bool _hoverShot;

    public ToolsWidget(ToolsService svc, ToolsSettings settings, ContextMenuStrip menu, Action capture) : base("System Tools Widget", menu)
    {
        _svc = svc;
        _settings = settings;
        _capture = capture;
    }

    protected override string[] NeighborTitles => new[]
    {
        "Network Widget", "Developer Tools Widget", "Now Playing Widget", "System Monitor Widget", "Audio Control Widget", "Bluetooth Battery Widget",
    };
    protected override Point? SavedLocation =>
        _settings.WidgetX is int x && _settings.WidgetY is int y ? new Point(x, y) : null;
    protected override void SaveLocation(Point? p) { _settings.WidgetX = p?.X; _settings.WidgetY = p?.Y; _settings.Save(); }

    protected override string CompactGlyph => ToolGlyphs.Tools;
    protected override Color CompactColor => _svc.RebootPending ? Theme.Amber : Fg;

    public void UpdateView() { Relayout(); Invalidate(); Hover.Refresh(); }

    Font TextFont() => new("Segoe UI Semibold", 12f * S, GraphicsUnit.Pixel);
    Font Icons() => new(Theme.IconFont, 14f * S, GraphicsUnit.Pixel);
    const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

    bool UpdateBadge => _settings.ShowUpdatesInWidget && (_svc.RebootPending || _svc.PendingUpdates > 0);

    IEnumerable<(string Glyph, string? Text, Color Color)> Info()
    {
        if (_settings.ShowBinInWidget)
            yield return (ToolGlyphs.Bin, _svc.BinItems > 0 ? ToolsService.Size(_svc.BinBytes) : null, _svc.BinItems > 0 ? Fg : Dim);
        if (UpdateBadge)
            yield return (_svc.RebootPending ? ToolGlyphs.Restart : ToolGlyphs.Update,
                          _svc.RebootPending ? "Restart" : _svc.PendingUpdates.ToString(), _svc.RebootPending ? Theme.Amber : AccentColor);
    }

    protected override int ContentWidth()
    {
        using var tf = TextFont(); using var ic = Icons();
        int x = Px(3);
        _shotBtn = new Rectangle(x, Px(4), Px(30), Px(26));
        x = _shotBtn.Right + Px(2);
        foreach (var (glyph, text, _) in Info())
        {
            x += Px(6) + TextRenderer.MeasureText(glyph, ic, Size.Empty, Flags).Width;
            if (text != null) x += Px(5) + TextRenderer.MeasureText(text, tf, Size.Empty, Flags).Width;
            x += Px(6);
        }
        return x + Px(3);
    }

    protected override void PaintStrip(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Bg);
        using var tf = TextFont(); using var ic = Icons();
        if (_hoverShot) FillRounded(g, HoverBg, _shotBtn, 4 * S);
        TextRenderer.DrawText(g, ToolGlyphs.Snip, ic, _shotBtn, Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        int x = _shotBtn.Right + Px(2);
        foreach (var (glyph, text, color) in Info())
        {
            x += Px(6);
            var gs = TextRenderer.MeasureText(glyph, ic, Size.Empty, Flags);
            TextRenderer.DrawText(g, glyph, ic, new Rectangle(x, 0, gs.Width, Height), color, Flags);
            x += gs.Width;
            if (text != null)
            {
                x += Px(5);
                var ts = TextRenderer.MeasureText(text, tf, Size.Empty, Flags);
                TextRenderer.DrawText(g, text, tf, new Rectangle(x, 0, ts.Width, Height), color == Dim ? Dim : color == Fg ? Fg : color, Flags);
                x += ts.Width;
            }
            x += Px(6);
        }
    }

    protected override void OnStripClick(Point p)
    {
        if (_shotBtn.Contains(p)) { Hover.Hide(); _capture(); }
        else base.OnStripClick(p);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool h = _shotBtn.Contains(e.Location);
        if (h != _hoverShot) { _hoverShot = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverShot) { _hoverShot = false; Invalidate(); }
    }

    protected override HoverContent GetHoverContent()
    {
        var rows = new List<HoverRow>
        {
            new(ToolGlyphs.Snip, "Screenshot", "Click the scissors"),
            new(ToolGlyphs.Bin, "Recycle bin", _svc.BinItems > 0 ? $"{ToolsService.Size(_svc.BinBytes)} · {_svc.BinItems:N0} items" : "Empty"),
        };
        if (_svc.RebootPending) rows.Add(new(ToolGlyphs.Restart, "Windows Update", "Restart required", Theme.Amber));
        else if (_svc.PendingUpdates > 0) rows.Add(new(ToolGlyphs.Update, "Windows Update", $"{_svc.PendingUpdates} available", AccentColor));
        else if (_svc.PendingUpdates == 0) rows.Add(new(ToolGlyphs.Update, "Windows Update", "Up to date", Theme.Green));
        if (_settings.Layouts.Count > 0) rows.Add(new(ToolGlyphs.Layout, "Saved window layouts", _settings.Layouts.Count.ToString()));
        return new HoverContent("System tools", rows, "Click for layouts, quick settings and more");
    }
}
