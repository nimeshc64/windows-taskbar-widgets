using System.Drawing.Drawing2D;
using BtBattery;

namespace SysTools;

sealed class ToolsFlyout : FlyoutBase
{
    readonly ToolsService _svc;
    readonly ToolsSettings _settings;
    readonly ToolsTrayApp _app;
    string _status = "";
    string? _confirm;
    Image? _thumb;
    string? _thumbPath;

    public ToolsFlyout(ToolsService svc, ToolsSettings settings, ToolsTrayApp app) : base("System Tools")
    {
        _svc = svc;
        _settings = settings;
        _app = app;
    }

    protected override int LogicalWidth => 400;
    protected override int ContentHeight() => PaintedHeight(Draw);
    protected override void PaintContent(Graphics g, Palette p) => Draw(g, p);
    protected override void OnOpening() { _confirm = null; _status = ""; }

    public void SetStatus(string s) { _status = s; Invalidate(); }

    static Color CardColor(Palette p) => Theme.Mix(p.Background, p.Chip, 0.55f);

    int Draw(Graphics g, Palette p)
    {
        using var title = UiFont(15, true);
        using var name = UiFont(13, true);
        using var sub = UiFont(11.5f);
        using var icon16 = IconFont(16);
        int left = Px(12), right = Width - Px(12);

        TextRenderer.DrawText(g, "System tools", title, new Rectangle(Px(22), 0, Px(250), Px(52)), p.Text, L);
        IconButton(g, p, new Rectangle(right - Px(36), Px(9), Px(34), Px(34)), ToolGlyphs.Refresh, "refresh", _ => _app.RefreshAll(true));
        int y = Px(52);

        SectionLabel(g, p, "Screenshot", y);
        y += Px(28);
        int gap = Px(8), tw = (right - left - 2 * gap) / 3;
        Tile(g, p, new Rectangle(left, y, tw, Px(66)), ToolGlyphs.Crop, "Area", "shot:area", "Drag to select · copies to clipboard", () => _app.CaptureRegion());
        Tile(g, p, new Rectangle(left + tw + gap, y, tw, Px(66)), ToolGlyphs.Monitor, "Full screen", "shot:full", "Screen under the cursor", () => _app.CaptureFullScreen());
        Tile(g, p, new Rectangle(left + 2 * (tw + gap), y, tw, Px(66)), ToolGlyphs.Video, "Record", "shot:rec", "Opens Snipping Tool to record a video", () => _app.Record());
        y += Px(66) + Px(8);

        var lastPath = _settings.LastCapture;
        if (lastPath != null && File.Exists(lastPath))
        {
            var r = new Rectangle(left, y, right - left, Px(54));
            AddHit(r, "last", _ => ToolsService.Open(lastPath));
            if (IsHover("last")) Fill(g, p.Hover, r, 6);
            var thumbR = new Rectangle(r.Left + Px(6), r.Top + Px(6), Px(72), r.Height - Px(12));
            DrawThumb(g, p, lastPath, thumbR);
            int tx = thumbR.Right + Px(10);
            TextRenderer.DrawText(g, "Last capture", name, new Rectangle(tx, r.Top + Px(8), r.Right - tx - Px(80), Px(19)), p.Text, L);
            TextRenderer.DrawText(g, $"{File.GetLastWriteTime(lastPath):MMM d, HH:mm} · {ImageSize(lastPath)}", sub,
                new Rectangle(tx, r.Top + Px(27), r.Right - tx - Px(80), Px(17)), p.SubText, L);
            Action(g, p, new Rectangle(r.Right - Px(72), r.Top + Px(11), Px(32), Px(32)), ToolGlyphs.Copy, "lastcopy", "Copy image",
                () => _app.CopyImage(lastPath));
            Action(g, p, new Rectangle(r.Right - Px(38), r.Top + Px(11), Px(32), Px(32)), ToolGlyphs.Folder, "lastfolder", "Show in folder",
                () => ToolsService.Open("explorer.exe", $"/select,\"{lastPath}\""));
            y += r.Height + Px(4);
        }

        SectionLabel(g, p, "Window layouts", y);
        TextLink(g, p, "+ Save current", new Rectangle(right - Px(140), y, Px(132), Px(28)), "savelayout", () => _app.SaveLayout());
        y += Px(28);
        if (_settings.Layouts.Count == 0)
        {
            Message(g, p, y, 40, "Save where your windows are, restore them in one click");
            y += Px(40);
        }
        foreach (var layout in _settings.Layouts.ToList())
        {
            var r = new Rectangle(left, y, right - left, Px(48));
            var key = "layout:" + layout.Saved.Ticks;
            AddHit(r, key, null);
            bool hover = HoverKey != null && HoverKey.EndsWith(":" + layout.Saved.Ticks);
            if (hover) Fill(g, p.Hover, r, 6);
            TextRenderer.DrawText(g, ToolGlyphs.Layout, icon16, new Rectangle(r.Left + Px(8), r.Top, Px(30), r.Height), p.Accent, C);
            int tx = r.Left + Px(48), actionsW = Px(130);
            TextRenderer.DrawText(g, layout.Name, name, new Rectangle(tx, r.Top + Px(6), r.Right - tx - actionsW, Px(19)), p.Text, L);
            var mons = layout.Monitors == 1 ? "1 monitor" : $"{layout.Monitors} monitors";
            TextRenderer.DrawText(g, $"{layout.Windows.Count} windows · {mons} · {layout.Saved:MMM d, HH:mm}", sub,
                new Rectangle(tx, r.Top + Px(25), r.Right - tx - actionsW, Px(17)), p.SubText, L);
            var l = layout;
            Pill(g, p, new Rectangle(r.Right - Px(126), r.Top + Px(9), Px(86), r.Height - Px(18)), "Restore", "restore:" + layout.Saved.Ticks,
                () => _app.RestoreLayout(l));
            Action(g, p, new Rectangle(r.Right - Px(36), r.Top + Px(9), Px(30), Px(30)), ToolGlyphs.Close, "del:" + layout.Saved.Ticks,
                "Delete this layout", () => _app.DeleteLayout(l), 11);
            y += r.Height;
        }

        y += Px(4);
        SectionLabel(g, p, "Quick settings", y);
        y += Px(28);
        y = SwitchRow(g, p, y, ToolGlyphs.Eye, "Show hidden files", _svc.HiddenFiles, "hidden", v => _app.Apply(() => _svc.SetHiddenFiles(v),
            v ? "Hidden files are visible" : "Hidden files are hidden"));
        y = SwitchRow(g, p, y, ToolGlyphs.FileExt, "Show file extensions", _svc.FileExtensions, "ext", v => _app.Apply(() => _svc.SetFileExtensions(v),
            v ? "File extensions shown" : "File extensions hidden"));
        y = SwitchRow(g, p, y, ToolGlyphs.Moon, "Dark mode", _svc.DarkMode, "dark", v => _app.Apply(() => _svc.SetDarkMode(v),
            v ? "Dark mode on" : "Light mode on"));

        y += Px(4);
        {
            var r = new Rectangle(left, y, right - left, Px(52));
            Fill(g, CardColor(p), r, 8);
            TextRenderer.DrawText(g, ToolGlyphs.Bin, icon16, new Rectangle(r.Left + Px(8), r.Top, Px(30), r.Height), p.Text, C);
            int tx = r.Left + Px(48);
            TextRenderer.DrawText(g, "Recycle bin", name, new Rectangle(tx, r.Top + Px(8), Px(160), Px(19)), p.Text, L);
            TextRenderer.DrawText(g, _svc.BinItems > 0 ? $"{ToolsService.Size(_svc.BinBytes)} · {_svc.BinItems:N0} items" : "Empty", sub,
                new Rectangle(tx, r.Top + Px(27), Px(180), Px(17)), p.SubText, L);
            Action(g, p, new Rectangle(r.Right - Px(132), r.Top + Px(10), Px(32), Px(32)), ToolGlyphs.Open, "binopen", "Open recycle bin",
                () => _app.Open("shell:RecycleBinFolder"));
            bool confirm = _confirm == "bin";
            Pill(g, p, new Rectangle(r.Right - Px(96), r.Top + Px(11), Px(88), r.Height - Px(22)), confirm ? "Confirm" : "Empty", "binempty",
                () => { if (confirm) { _confirm = null; _app.EmptyBin(); } else { _confirm = "bin"; Invalidate(); } },
                confirm ? PillStyle.Danger : PillStyle.Neutral, _svc.BinItems > 0);
            y += r.Height + Px(8);
        }

        {
            var r = new Rectangle(left, y, right - left, Px(56));
            Fill(g, CardColor(p), r, 8);
            var (glyph, color, head) = _svc.RebootPending ? (ToolGlyphs.Restart, Theme.Amber, "Restart to finish updates")
                : _svc.PendingUpdates > 0 ? (ToolGlyphs.Update, p.Accent, _svc.PendingUpdates == 1 ? "1 update available" : $"{_svc.PendingUpdates} updates available")
                : _svc.PendingUpdates == 0 ? (ToolGlyphs.Update, Theme.Green, "Windows is up to date")
                : (ToolGlyphs.Update, p.SubText, _svc.CheckingUpdates ? "Checking Windows Update…" : "Windows Update");
            TextRenderer.DrawText(g, glyph, icon16, new Rectangle(r.Left + Px(8), r.Top, Px(30), r.Height), color, C);
            int tx = r.Left + Px(48), btnW = Px(104);
            TextRenderer.DrawText(g, head, name, new Rectangle(tx, r.Top + Px(9), r.Right - tx - btnW, Px(19)), p.Text, L);
            string detail = _svc.PausedUntil is DateTime pu ? $"Paused until {pu:MMM d}"
                : _svc.PendingTitles.Count > 0 && !_svc.RebootPending ? _svc.PendingTitles[0]
                : _svc.LastChecked is DateTime lc ? $"Last checked {Friendly(lc)}"
                : "";
            TextRenderer.DrawText(g, detail, sub, new Rectangle(tx, r.Top + Px(29), r.Right - tx - btnW, Px(17)), p.SubText, L);
            var br = new Rectangle(r.Right - Px(96), r.Top + Px(12), Px(88), r.Height - Px(24));
            if (_svc.RebootPending)
            {
                bool confirm = _confirm == "restart";
                Pill(g, p, br, confirm ? "Restart now" : "Restart", "restart",
                    () => { if (confirm) { _confirm = null; _app.RestartPc(); } else { _confirm = "restart"; SetStatus("Click again to restart now — save your work first"); } },
                    confirm ? PillStyle.Danger : PillStyle.Accent);
            }
            else
                Pill(g, p, br, "Open", "wu", () => _app.Open("ms-settings:windowsupdate"), PillStyle.Neutral);
            y += r.Height;
        }

        y += Px(10);
        using (var line = new Pen(p.Border, 1)) g.DrawLine(line, 0, y, Width, y);
        int ly = y + Px(9);
        var link = LinkButton(g, p, ToolGlyphs.Folder, "Screenshots", Px(12), ly, false, "folder", _ => _app.OpenScreenshots());
        TextRenderer.DrawText(g, HoverLabel ?? _status, sub, Rectangle.FromLTRB(link.Right + Px(10), ly, right - Px(8), ly + Px(32)), p.SubText,
            R | TextFormatFlags.EndEllipsis);
        return ly + Px(32) + Px(9);
    }

    int SwitchRow(Graphics g, Palette p, int y, string glyph, string text, bool on, string key, Action<bool> set)
    {
        var r = new Rectangle(Px(12), y, Width - Px(24), Px(40));
        if (IsHover(key)) Fill(g, p.Hover, r, 6);
        using var icon = IconFont(15);
        using var f = UiFont(13);
        TextRenderer.DrawText(g, glyph, icon, new Rectangle(r.Left + Px(8), r.Top, Px(30), r.Height), p.Text, C);
        TextRenderer.DrawText(g, text, f, new Rectangle(r.Left + Px(48), r.Top, r.Width - Px(110), r.Height), p.Text, L);
        Switch(g, p, new Rectangle(r.Right - Px(52), r.Top + Px(10), Px(40), Px(20)), on, key, () => set(!on), r);
        return y + r.Height;
    }

    void Tile(Graphics g, Palette p, Rectangle r, string glyph, string text, string key, string label, System.Action click)
    {
        _labels[key] = label;
        AddHit(r, key, _ => click());
        Fill(g, IsHover(key) ? p.Hover : CardColor(p), r, 8);
        using var icon = IconFont(20);
        using var f = UiFont(12);
        TextRenderer.DrawText(g, glyph, icon, new Rectangle(r.Left, r.Top + Px(8), r.Width, Px(30)), p.Accent, C);
        TextRenderer.DrawText(g, text, f, new Rectangle(r.Left, r.Top + Px(40), r.Width, Px(18)), p.Text, C);
    }

    void DrawThumb(Graphics g, Palette p, string path, Rectangle r)
    {
        if (_thumbPath != path)
        {
            _thumb?.Dispose();
            _thumb = null;
            _thumbPath = path;
            try
            {
                using var ms = new MemoryStream(File.ReadAllBytes(path));
                using var img = Image.FromStream(ms);
                float scale = Math.Min(160f / img.Width, 100f / img.Height);
                var bmp = new Bitmap(Math.Max(1, (int)(img.Width * scale)), Math.Max(1, (int)(img.Height * scale)));
                using (var tg = Graphics.FromImage(bmp)) { tg.InterpolationMode = InterpolationMode.HighQualityBicubic; tg.DrawImage(img, 0, 0, bmp.Width, bmp.Height); }
                _thumb = bmp;
            }
            catch { }
        }
        Fill(g, p.Chip, r, 4);
        if (_thumb == null) return;
        float s = Math.Min((float)r.Width / _thumb.Width, (float)r.Height / _thumb.Height);
        var dst = new RectangleF(r.Left + (r.Width - _thumb.Width * s) / 2, r.Top + (r.Height - _thumb.Height * s) / 2, _thumb.Width * s, _thumb.Height * s);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(_thumb, dst);
    }

    readonly Dictionary<string, (int W, int H)> _sizes = new();
    string ImageSize(string path)
    {
        if (!_sizes.TryGetValue(path, out var s))
        {
            try { using var fs = File.OpenRead(path); using var img = Image.FromStream(fs, false, false); s = (img.Width, img.Height); }
            catch { s = (0, 0); }
            _sizes[path] = s;
        }
        return s.W > 0 ? $"{s.W} × {s.H}" : "";
    }

    static string Friendly(DateTime t) =>
        t.Date == DateTime.Today ? "today " + t.ToString("HH:mm")
        : t.Date == DateTime.Today.AddDays(-1) ? "yesterday " + t.ToString("HH:mm")
        : t.ToString("MMM d");

    readonly Dictionary<string, string> _labels = new();
    string? HoverLabel => HoverKey != null && _labels.TryGetValue(HoverKey, out var l) ? l : null;

    protected override void OnHoverChanged()
    {
        if (_confirm == "bin" && HoverKey != "binempty") _confirm = null;
        if (_confirm == "restart" && HoverKey != "restart") { _confirm = null; _status = ""; }
    }

    void Action(Graphics g, Palette p, Rectangle r, string glyph, string key, string label, System.Action click, float glyphPx = 14)
    {
        _labels[key] = label;
        IconButton(g, p, r, glyph, key, _ => click(), glyphPx: glyphPx);
    }

    void TextLink(Graphics g, Palette p, string text, Rectangle r, string key, System.Action click)
    {
        AddHit(r, key, _ => click());
        using var f = UiFont(12, IsHover(key));
        TextRenderer.DrawText(g, text, f, r, p.Accent, R);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _thumb?.Dispose();
        base.Dispose(disposing);
    }
}
