using BtBattery;

namespace NowPlaying;

sealed class NpFlyout : FlyoutBase
{
    const int Pad = 20, HeaderH = 44, ArtSize = 104, ProgressH = 46, ControlsH = 70, OtherH = 46;

    readonly MediaService _m;
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };

    public NpFlyout(MediaService media) : base("Now Playing")
    {
        _m = media;
        _tick.Tick += (_, _) => { if (Visible && _m.IsPlaying) Invalidate(); };
        VisibleChanged += (_, _) => _tick.Enabled = Visible;
    }

    protected override int LogicalWidth => 380;

    protected override int ContentHeight()
    {
        if (!_m.HasSession) return HeaderH + 110;
        int h = HeaderH + ArtSize + 12;
        if (_m.Duration > TimeSpan.Zero) h += ProgressH;
        h += ControlsH;
        if (_m.Others.Count > 0) h += 30 + _m.Others.Count * OtherH + 8;
        return h + 8;
    }

    protected override void PaintContent(Graphics g, Palette p)
    {
        using var small = UiFont(12);
        using var icon14 = IconFont(14);
        int left = Px(Pad), right = Width - Px(Pad);

        TextRenderer.DrawText(g, "", icon14, new Rectangle(left, 0, Px(18), Px(HeaderH)), p.Accent, C);
        TextRenderer.DrawText(g, _m.HasSession ? $"{_m.App} · {(_m.IsPlaying ? "Playing" : "Paused")}" : "Now playing",
            small, new Rectangle(left + Px(26), 0, right - left - Px(26), Px(HeaderH)), p.SubText, L);

        int y = Px(HeaderH);
        if (!_m.HasSession)
        {
            using var emptyFont = UiFont(15, true);
            TextRenderer.DrawText(g, "Nothing is playing", emptyFont, new Rectangle(0, y + Px(18), Width, Px(26)), p.Text, C);
            TextRenderer.DrawText(g, "Start music or a video in any app", small, new Rectangle(0, y + Px(48), Width, Px(20)), p.SubText, C);
            return;
        }

        var art = new Rectangle(left, y, Px(ArtSize), Px(ArtSize));
        MediaService.DrawArt(g, _m.Art, art, 8 * S, p.Chip, p.Accent, 40 * S);
        int tx = art.Right + Px(16);
        using (var title = UiFont(16, true))
            TextRenderer.DrawText(g, _m.Title.Length > 0 ? _m.Title : "Unknown title", title,
                new Rectangle(tx, y + Px(6), right - tx, Px(44)), p.Text,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        using (var artist = UiFont(13))
            TextRenderer.DrawText(g, _m.Artist, artist, new Rectangle(tx, y + Px(54), right - tx, Px(20)), p.Text, L);
        TextRenderer.DrawText(g, _m.Album, small, new Rectangle(tx, y + Px(76), right - tx, Px(18)), p.SubText, L);
        y += Px(ArtSize + 12);

        if (_m.Duration > TimeSpan.Zero)
        {
            var pos = _m.Position;
            float frac = (float)Math.Clamp(pos.TotalSeconds / _m.Duration.TotalSeconds, 0, 1);
            var track = new RectangleF(left, y + Px(10), right - left, 4 * S);
            var hit = Rectangle.FromLTRB(left, y, right, y + Px(24));
            if (_m.CanSeek)
                AddHit(hit, "seek", pt =>
                    _m.Seek(TimeSpan.FromSeconds(_m.Duration.TotalSeconds * Math.Clamp((pt.X - left) / (double)(right - left), 0, 1))));
            Fill(g, Theme.AppsDark ? Color.FromArgb(90, 90, 90) : Color.FromArgb(190, 190, 190), track, 2);
            Fill(g, p.Accent, track with { Width = Math.Max(track.Height, track.Width * frac) }, 2);
            float d = (IsHover("seek") ? 14 : 10) * S;
            using (var b = new SolidBrush(p.Accent))
                g.FillEllipse(b, track.Left + track.Width * frac - d / 2, track.Top + track.Height / 2 - d / 2, d, d);
            using var tf = UiFont(11);
            TextRenderer.DrawText(g, MediaService.Time(pos), tf, new Rectangle(left, y + Px(20), Px(80), Px(18)), p.SubText, L);
            TextRenderer.DrawText(g, MediaService.Time(_m.Duration), tf, new Rectangle(right - Px(80), y + Px(20), Px(80), Px(18)), p.SubText, R);
            y += Px(ProgressH);
        }

        int cy = y + Px(ControlsH) / 2;
        int big = Px(52), sm = Px(40);
        var play = new Rectangle(Width / 2 - big / 2, cy - big / 2, big, big);
        var prev = new Rectangle(play.Left - Px(24) - sm, cy - sm / 2, sm, sm);
        var next = new Rectangle(play.Right + Px(24), cy - sm / 2, sm, sm);
        IconButton(g, p, prev, "", "prev", _ => _m.Previous(), glyphPx: 16, enabled: _m.CanPrevious);
        IconButton(g, p, next, "", "next", _ => _m.Next(), glyphPx: 16, enabled: _m.CanNext);
        if (_m.CanPlayPause) AddHit(play, "play", _ => _m.PlayPause());
        var playColor = IsHover("play") ? Theme.Mix(p.Accent, p.Text, 0.15f) : p.Accent;
        using (var pb = new SolidBrush(_m.CanPlayPause ? playColor : p.Chip)) g.FillEllipse(pb, play);
        using (var pf = IconFont(18))
            TextRenderer.DrawText(g, _m.IsPlaying ? "" : "", pf,
                Rectangle.Inflate(play, 0, 0) with { X = play.X + (_m.IsPlaying ? 0 : Px(2)) },
                Theme.AppsDark ? Color.FromArgb(20, 20, 20) : Color.White, C);
        y += Px(ControlsH);

        if (_m.Others.Count > 0)
        {
            using (var line = new Pen(p.Border, 1)) g.DrawLine(line, left, y, right, y);
            SectionLabel(g, p, "Other players", y + Px(2));
            y += Px(30);
            using var name = UiFont(13, true);
            foreach (var o in _m.Others)
            {
                var r = new Rectangle(Px(12), y, Width - Px(24), Px(OtherH - 4));
                string key = "other:" + o.Session.SourceAppUserModelId;
                AddHit(r, key, _ => _m.Select(o));
                if (IsHover(key)) Fill(g, p.Hover, r, 6);
                TextRenderer.DrawText(g, o.Playing ? "" : "", icon14, new Rectangle(r.Left + Px(10), r.Top, Px(20), r.Height), p.SubText, C);
                TextRenderer.DrawText(g, o.App, name, new Rectangle(r.Left + Px(40), r.Top + Px(4), r.Width - Px(50), Px(18)), p.Text, L);
                TextRenderer.DrawText(g, o.Title.Length > 0 ? o.Title : (o.Playing ? "Playing" : "Paused"), small,
                    new Rectangle(r.Left + Px(40), r.Top + Px(22), r.Width - Px(50), Px(16)), p.SubText, L);
                y += Px(OtherH);
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Space: _m.PlayPause(); break;
            case Keys.Right: _m.Next(); break;
            case Keys.Left: _m.Previous(); break;
        }
        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}
