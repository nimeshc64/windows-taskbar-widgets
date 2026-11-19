using System.Drawing.Drawing2D;
using BtBattery;

namespace NowPlaying;

public sealed class NpSettings
{
    public bool ShowWidget { get; set; } = true;
    public bool ShowControls { get; set; } = true;
    public bool HideWhenIdle { get; set; }
    public int MaxTextWidth { get; set; } = 170;
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }
    public void Save() => AppFiles.Save(MediaService.AppName, this);
}

sealed class NpWidget : TaskbarStrip
{
    const string Prev = "", Play = "", Pause = "", NextGlyph = "";

    readonly MediaService _m;
    readonly NpSettings _settings;
    Rectangle _art, _text, _prev, _play, _next;
    string? _hoverBtn;

    public NpWidget(MediaService media, NpSettings settings, ContextMenuStrip menu) : base("Now Playing Widget", menu)
    {
        _m = media;
        _settings = settings;
    }

    protected override string[] NeighborTitles =>
        new[] { "System Monitor Widget", "Audio Control Widget", "Bluetooth Battery Widget" };
    protected override Point? SavedLocation =>
        _settings.WidgetX is int x && _settings.WidgetY is int y ? new Point(x, y) : null;
    protected override void SaveLocation(Point? p) { _settings.WidgetX = p?.X; _settings.WidgetY = p?.Y; _settings.Save(); }

    protected override string CompactGlyph => _m.IsPlaying ? "" : "";
    protected override Color CompactColor => _m.IsPlaying ? AccentColor : Fg;

    public void UpdateView()
    {
        Relayout();
        Invalidate();
        Hover.Refresh();
    }

    Font TitleFont() => new("Segoe UI Semibold", 11.5f * S, GraphicsUnit.Pixel);
    Font ArtistFont() => new("Segoe UI", 10.5f * S, GraphicsUnit.Pixel);
    Font Icons() => new(Theme.IconFont, 12f * S, GraphicsUnit.Pixel);
    const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
                                  TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
    const TextFormatFlags MeasureFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    string Line1 => _m.HasSession ? (_m.Title.Length > 0 ? _m.Title : _m.App) : "Nothing playing";
    string Line2 => _m.HasSession ? (_m.Artist.Length > 0 ? _m.Artist : _m.App) : "";

    protected override int ContentWidth()
    {
        using var tf = TitleFont();
        using var af = ArtistFont();
        int maxText = Px(_settings.MaxTextWidth);
        int textW = Math.Min(maxText, Math.Max(
            TextRenderer.MeasureText(Line1, tf, Size.Empty, MeasureFlags).Width,
            TextRenderer.MeasureText(Line2, af, Size.Empty, MeasureFlags).Width));
        textW = Math.Max(textW, Px(40));

        int x = Px(5);
        int art = Px(24);
        _art = new Rectangle(x, (Px(34) - art) / 2, art, art);
        x += art + Px(8);
        _text = new Rectangle(x, 0, textW, Px(34));
        x += textW + Px(6);
        if (_m.HasSession && _settings.ShowControls)
        {
            int b = Px(26), by = (Px(34) - b) / 2;
            _prev = new Rectangle(x, by, b, b); x += b;
            _play = new Rectangle(x, by, b, b); x += b;
            _next = new Rectangle(x, by, b, b); x += b;
        }
        else _prev = _play = _next = Rectangle.Empty;
        return x + Px(4);
    }

    protected override void PaintStrip(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Bg);

        MediaService.DrawArt(g, _m.Art, _art, 4 * S, Dark ? Color.FromArgb(55, 55, 55) : Color.FromArgb(215, 215, 215),
            _m.HasSession ? AccentColor : Dim, 13 * S);

        using var tf = TitleFont();
        using var af = ArtistFont();
        if (Line2.Length > 0)
        {
            TextRenderer.DrawText(g, Line1, tf, new Rectangle(_text.Left, Px(3), _text.Width, Px(15)), Fg, Flags);
            TextRenderer.DrawText(g, Line2, af, new Rectangle(_text.Left, Px(17), _text.Width, Px(14)), Dim, Flags);
        }
        else TextRenderer.DrawText(g, Line1, af, _text, Dim, Flags);

        if (_m.HasSession && _m.Duration > TimeSpan.Zero)
        {
            float frac = (float)(_m.Position.TotalSeconds / Math.Max(1, _m.Duration.TotalSeconds));
            var bar = new RectangleF(_text.Left, Height - Px(3), _text.Width, Math.Max(1.5f, 1.5f * S));
            FillRounded(g, Color.FromArgb(50, Fg), bar, bar.Height / 2);
            FillRounded(g, AccentColor, bar with { Width = Math.Max(bar.Height, bar.Width * Math.Clamp(frac, 0, 1)) }, bar.Height / 2);
        }

        if (_play.IsEmpty) return;
        using var ic = Icons();
        Button(g, ic, _prev, Prev, "prev", _m.CanPrevious);
        Button(g, ic, _play, _m.IsPlaying ? Pause : Play, "play", _m.CanPlayPause);
        Button(g, ic, _next, NextGlyph, "next", _m.CanNext);
    }

    void Button(Graphics g, Font f, Rectangle r, string glyph, string key, bool enabled)
    {
        if (enabled && _hoverBtn == key) FillRounded(g, HoverBg, r, 4 * S);
        TextRenderer.DrawText(g, glyph, f, r, enabled ? Fg : Theme.Mix(Dim, Bg, 0.4f),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    string? ButtonAt(Point p) =>
        _prev.Contains(p) ? "prev" : _play.Contains(p) ? "play" : _next.Contains(p) ? "next" : null;

    protected override void OnStripClick(Point p)
    {
        switch (ButtonAt(p))
        {
            case "prev": if (_m.CanPrevious) _m.Previous(); break;
            case "play": if (_m.CanPlayPause) _m.PlayPause(); break;
            case "next": if (_m.CanNext) _m.Next(); break;
            default: base.OnStripClick(p); break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Middle && _m.CanPlayPause) _m.PlayPause();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var b = ButtonAt(e.Location);
        if (b != _hoverBtn) { _hoverBtn = b; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverBtn != null) { _hoverBtn = null; Invalidate(); }
    }

    protected override HoverContent GetHoverContent()
    {
        if (!_m.HasSession)
            return new HoverContent("Now playing", new[] { new HoverRow("", "Nothing is playing", "") },
                "Start music or a video in any app");
        var rows = new List<HoverRow>
        {
            new("", _m.Title.Length > 0 ? _m.Title : "Unknown title",
                _m.Duration > TimeSpan.Zero ? $"{MediaService.Time(_m.Position)} / {MediaService.Time(_m.Duration)}" : (_m.IsPlaying ? "Playing" : "Paused")),
        };
        if (_m.Artist.Length > 0) rows.Add(new("", _m.Artist, ""));
        if (_m.Album.Length > 0) rows.Add(new("", _m.Album, ""));
        return new HoverContent($"{_m.App} · {(_m.IsPlaying ? "Playing" : "Paused")}", rows,
            "Click for player · Middle-click: play/pause");
    }
}
