using System.Drawing.Drawing2D;
using BtBattery;

namespace SysMon;

sealed class SysWidget : TaskbarStrip
{
    enum Part { Cpu, Memory, Network }

    readonly SystemSampler _s;
    readonly SmSettings _settings;
    readonly List<(Rectangle rect, Part part)> _parts = new();
    Part? _hoverPart;

    public SysWidget(SystemSampler sampler, SmSettings settings, ContextMenuStrip menu) : base("System Monitor Widget", menu)
    {
        _s = sampler;
        _settings = settings;
    }

    protected override string CompactGlyph => SmGlyphs.Cpu;
    protected override Color CompactColor => _s.Internet == false ? Theme.Red : Math.Max(_s.Cpu, _s.MemoryLoad) >= 70 ? SystemSampler.LoadColor(Math.Max(_s.Cpu, _s.MemoryLoad)) : Fg;

    protected override string[] NeighborTitles => new[] { "Audio Control Widget", "Bluetooth Battery Widget" };
    protected override Point? SavedLocation =>
        _settings.WidgetX is int x && _settings.WidgetY is int y ? new Point(x, y) : null;
    protected override void SaveLocation(Point? p)
    {
        _settings.WidgetX = p?.X;
        _settings.WidgetY = p?.Y;
        _settings.Save();
    }

    public void UpdateView()
    {
        Relayout();
        Invalidate();
        Hover.Refresh();
    }

    Font PctFont() => new("Segoe UI Semibold", 12.5f * S, GraphicsUnit.Pixel);
    Font SpeedFont() => new("Segoe UI Semibold", 10.5f * S, GraphicsUnit.Pixel);
    Font IconFont() => new(Theme.IconFont, 14f * S, GraphicsUnit.Pixel);
    Font ArrowFont() => new(Theme.IconFont, 9.5f * S, GraphicsUnit.Pixel);
    const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

    IEnumerable<Part> Parts()
    {
        if (_settings.ShowCpu) yield return Part.Cpu;
        if (_settings.ShowMemory) yield return Part.Memory;
        if (_settings.ShowNetwork) yield return Part.Network;
    }

    (int glyph, int pct, int arrow, int speed) Metrics()
    {
        using var pf = PctFont(); using var sf = SpeedFont(); using var icf = IconFont(); using var af = ArrowFont();
        int speedW = Math.Max(TextRenderer.MeasureText(_settings.Bits ? "99.9 Mbps" : "999 KB/s", sf, Size.Empty, Flags).Width,
                              TextRenderer.MeasureText("Offline", sf, Size.Empty, Flags).Width);
        return (TextRenderer.MeasureText(SmGlyphs.Cpu, icf, Size.Empty, Flags).Width,
                TextRenderer.MeasureText("100%", pf, Size.Empty, Flags).Width,
                TextRenderer.MeasureText(SmGlyphs.Down, af, Size.Empty, Flags).Width,
                speedW);
    }

    protected override int ContentWidth()
    {
        var m = Metrics();
        _parts.Clear();
        int x = Px(4);
        foreach (var part in Parts())
        {
            int w = part == Part.Network
                ? Px(6) + m.arrow + Px(4) + m.speed + Px(6)
                : Px(6) + m.glyph + Px(5) + m.pct + Px(6);
            _parts.Add((new Rectangle(x, Px(2), w, Px(30)), part));
            x += w + Px(2);
        }
        if (_parts.Count == 0) { _parts.Add((new Rectangle(x, Px(2), Px(30), Px(30)), Part.Cpu)); x += Px(32); }
        return x + Px(2);
    }

    protected override void PaintStrip(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Bg);
        var m = Metrics();
        using var pf = PctFont(); using var sf = SpeedFont(); using var icf = IconFont(); using var af = ArrowFont();

        foreach (var (rect, part) in _parts)
        {
            if (_hoverPart == part) FillRounded(g, HoverBg, rect, 4 * S);
            int x = rect.Left + Px(6);
            if (part == Part.Network)
            {
                DrawNetwork(g, rect, x, m, sf, af);
                continue;
            }

            double pct = part == Part.Cpu ? _s.Cpu : _s.MemoryLoad;
            string glyph = part == Part.Cpu ? SmGlyphs.Cpu : SmGlyphs.Memory;
            TextRenderer.DrawText(g, glyph, icf, new Rectangle(x, 0, m.glyph, Height - Px(2)), Fg, Flags);
            x += m.glyph + Px(5);
            var text = $"{Math.Round(pct)}%";
            var ts = TextRenderer.MeasureText(text, pf, Size.Empty, Flags);
            TextRenderer.DrawText(g, text, pf, new Rectangle(x, -Px(2), ts.Width, Height), pct >= 90 ? Theme.Red : Fg, Flags);
            DrawBar(g, x, ts.Width, (float)(pct / 100), SystemSampler.LoadColor(pct));
        }
    }

    void DrawNetwork(Graphics g, Rectangle rect, int x, (int glyph, int pct, int arrow, int speed) m, Font sf, Font af)
    {
        int top = rect.Top + Px(1), half = Px(14);
        if (!_s.HasNetwork)
        {
            TextRenderer.DrawText(g, SmGlyphs.Globe, af, new Rectangle(x, 0, m.arrow, Height), Theme.Red, Flags);
            TextRenderer.DrawText(g, "Offline", sf, new Rectangle(x + m.arrow + Px(4), 0, m.speed, Height), Theme.Red, Flags);
            return;
        }
        var arrowColor = _s.Internet == false ? Theme.Red : AccentColor;
        TextRenderer.DrawText(g, SmGlyphs.Down, af, new Rectangle(x, top, m.arrow, half), arrowColor, Flags);
        TextRenderer.DrawText(g, SmGlyphs.Up, af, new Rectangle(x, top + half, m.arrow, half), _s.Internet == false ? Theme.Red : Theme.Amber, Flags);
        int tx = x + m.arrow + Px(4);
        TextRenderer.DrawText(g, SystemSampler.Speed(_s.Down, _settings.Bits), sf, new Rectangle(tx, top, m.speed, half), Fg, Flags);
        TextRenderer.DrawText(g, SystemSampler.Speed(_s.Up, _settings.Bits), sf, new Rectangle(tx, top + half, m.speed, half), Fg, Flags);
    }

    protected override HoverContent GetHoverContent()
    {
        bool bits = _settings.Bits;
        var rows = new List<HoverRow>
        {
            new(SmGlyphs.Cpu, "CPU", $"{Math.Round(_s.Cpu)}%", SystemSampler.LoadColor(_s.Cpu)),
            new(SmGlyphs.Memory, $"Memory · {SystemSampler.Gb(_s.MemoryUsed)} of {SystemSampler.Gb(_s.MemoryTotal)}",
                $"{Math.Round(_s.MemoryLoad)}%", SystemSampler.LoadColor(_s.MemoryLoad)),
        };
        if (_s.HasNetwork)
        {
            rows.Add(new(SmGlyphs.Down, $"Download · {_s.AdapterKind}", SystemSampler.Speed(_s.Down, bits)));
            rows.Add(new(SmGlyphs.Up, "Upload", SystemSampler.Speed(_s.Up, bits)));
        }
        rows.Add(new(SmGlyphs.Globe, "Internet",
            _s.Internet switch { true => "Connected", false => "No internet", _ => "Checking…" },
            _s.Internet switch { true => Theme.Green, false => Theme.Red, _ => null }));
        return new HoverContent("System", rows, "Click for details · Drag to move");
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Part? part = null;
        foreach (var (rect, p) in _parts) if (rect.Contains(e.Location)) part = p;
        if (part != _hoverPart) { _hoverPart = part; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverPart = null;
        Invalidate();
    }
}
