using System.Collections;
using System.Drawing.Drawing2D;
using BtBattery;
using QRCoder;

namespace NetTools;

sealed class NetFlyout : FlyoutBase
{
    readonly NetService _svc;
    readonly NetTrayApp _app;
    string _status = "";
    string? _busySsid;
    (string Ssid, string? Key, List<BitArray>? Matrix)? _qr;
    const int MaxNetworks = 6;

    public NetFlyout(NetService svc, NetTrayApp app) : base("Network")
    {
        _svc = svc;
        _app = app;
    }

    protected override int LogicalWidth => 400;
    protected override int ContentHeight() => PaintedHeight(Draw);
    protected override void PaintContent(Graphics g, Palette p) => Draw(g, p);
    protected override void OnOpening() { _qr = null; _status = ""; }

    public void SetStatus(string s) { _status = s; Invalidate(); }

    static Color CardColor(Palette p) => Theme.Mix(p.Background, p.Chip, 0.55f);

    int Draw(Graphics g, Palette p) => _qr != null ? DrawQr(g, p) : DrawMain(g, p);

    int DrawMain(Graphics g, Palette p)
    {
        using var title = UiFont(15, true);
        using var name = UiFont(13, true);
        using var sub = UiFont(11.5f);
        using var icon16 = IconFont(16);
        int left = Px(12), right = Width - Px(12);
        var s = _svc;

        TextRenderer.DrawText(g, "Network", title, new Rectangle(Px(22), 0, Px(250), Px(52)), p.Text, L);
        IconButton(g, p, new Rectangle(right - Px(36), Px(9), Px(34), Px(34)), NetGlyphs.Refresh, "refresh", _ => _app.RefreshAll());
        int y = Px(52);

        var card = new Rectangle(left, y, right - left, Px(86));
        Fill(g, CardColor(p), card, 8);
        using (var big = IconFont(24))
            TextRenderer.DrawText(g, NetGlyphs.For(s), big, new Rectangle(card.Left + Px(8), card.Top + Px(6), Px(40), Px(48)),
                !s.Connected ? p.SubText : s.Internet ? p.Accent : Theme.Amber, C);
        int tx = card.Left + Px(56);
        TextRenderer.DrawText(g, s.Connected ? s.Name : "Not connected", name, new Rectangle(tx, card.Top + Px(10), card.Right - tx - Px(10), Px(20)), p.Text, L);
        string line2 = !s.Connected ? "Check the cable or turn on Wi-Fi"
            : string.Join(" · ", new[] { s.Kind, NetService.LinkSpeed(s.LinkBps), s.Internet ? "Internet" : "No internet" }.Where(t => t.Length > 0));
        TextRenderer.DrawText(g, line2, sub, new Rectangle(tx, card.Top + Px(31), card.Right - tx - Px(10), Px(17)),
            s.Connected && !s.Internet ? Theme.Amber : p.SubText, L);
        if (s.Connected)
        {
            string ip = s.PublicIp != null
                ? $"{s.PublicIp}{(string.IsNullOrEmpty(s.Location) ? "" : " · " + s.Location)}{(string.IsNullOrEmpty(s.Isp) ? "" : " · " + s.Isp)}"
                : $"Local {s.LocalIp}";
            TextRenderer.DrawText(g, ip, sub, new Rectangle(tx, card.Top + Px(54), card.Right - tx - Px(44), Px(22)), p.Text, L);
            if (s.PublicIp != null)
                Action(g, p, new Rectangle(card.Right - Px(38), card.Top + Px(50), Px(30), Px(30)), NetGlyphs.Copy, "copyip",
                    $"Copy {s.PublicIp}", () => { Clipboard.SetText(s.PublicIp); SetStatus("Public IP copied"); }, 12);
        }
        y += card.Height + Px(8);

        if (s.Connected)
        {
            var row = new Rectangle(left, y, right - left, Px(40));
            using var small = UiFont(11);
            TextRenderer.DrawText(g, "Ping", small, new Rectangle(row.Left + Px(10), row.Top, Px(40), row.Height), p.SubText, L);
            var pingText = s.PingMs is double ms ? $"{ms:0} ms" : "timeout";
            TextRenderer.DrawText(g, pingText, name, new Rectangle(row.Left + Px(42), row.Top, Px(70), row.Height),
                s.PingMs is null ? Theme.Amber : s.PingMs >= 150 ? Theme.Amber : p.Text, L);
            Sparkline(g, p, new Rectangle(row.Left + Px(116), row.Top + Px(8), row.Width - Px(126), row.Height - Px(16)), s.PingHistory);
            y += row.Height + Px(4);
        }

        SectionLabel(g, p, "VPN", y);
        TextLink(g, p, "VPN settings", new Rectangle(right - Px(130), y, Px(122), Px(28)), "vpnsettings", () => _app.Open("ms-settings:network-vpn"));
        y += Px(28);
        if (s.Vpns.Count == 0) { Message(g, p, y, 36, "No VPN found"); y += Px(36); }
        foreach (var v in s.Vpns.Take(4))
        {
            var r = new Rectangle(left, y, right - left, Px(46));
            TextRenderer.DrawText(g, NetGlyphs.Vpn, icon16, new Rectangle(r.Left + Px(8), r.Top, Px(30), r.Height), v.Connected ? Theme.Green : p.SubText, C);
            int vx = r.Left + Px(48), statusW = Px(v.RasEntry != null ? 104 : 90);
            TextRenderer.DrawText(g, v.Name, name, new Rectangle(vx, r.Top + Px(5), r.Right - vx - statusW, Px(19)), p.Text, L);
            TextRenderer.DrawText(g, v.Detail, sub, new Rectangle(vx, r.Top + Px(24), r.Right - vx - statusW, Px(17)), p.SubText, L);
            if (v.RasEntry != null)
            {
                var entry = v.RasEntry;
                bool busy = _busySsid == "ras:" + entry;
                Pill(g, p, new Rectangle(r.Right - Px(98), r.Top + Px(8), Px(94), r.Height - Px(16)),
                    busy ? "…" : v.Connected ? "Disconnect" : "Connect", "ras:" + entry,
                    () => _app.ToggleRas(entry, !v.Connected), v.Connected ? PillStyle.Neutral : PillStyle.Accent, !busy);
            }
            else
                TextRenderer.DrawText(g, v.Connected ? "Connected" : "Off", sub, new Rectangle(r.Right - statusW, r.Top, statusW - Px(8), r.Height),
                    v.Connected ? Theme.Green : p.SubText, R);
            y += r.Height;
        }

        if (s.HasWifi)
        {
            y += Px(4);
            SectionLabel(g, p, "Wi-Fi", y);
            Switch(g, p, new Rectangle(right - Px(48), y + Px(5), Px(40), Px(20)), s.WifiOn, "wifi", () => _app.SetWifi(!s.WifiOn));
            y += Px(30);
            if (!s.WifiOn) { Message(g, p, y, 36, "Wi-Fi is off"); y += Px(36); }
            else if (s.WifiMessage != null)
            {
                if (s.WifiNeedsLocation)
                    TextLink(g, p, s.WifiMessage + " ›", new Rectangle(Px(22), y, right - Px(22), Px(36)), "location",
                        () => _app.Open("ms-settings:privacy-location"), alignRight: false);
                else Message(g, p, y, 36, s.WifiMessage);
                y += Px(36);
            }
            else if (s.Networks.Count == 0) { Message(g, p, y, 36, s.Scanning ? "Searching for networks…" : "No networks found"); y += Px(36); }
            foreach (var n in s.WifiOn && s.WifiMessage == null ? s.Networks.Take(MaxNetworks) : Enumerable.Empty<WifiNet>())
            {
                var r = new Rectangle(left, y, right - left, Px(40));
                string key = "net:" + n.Ssid;
                AddHit(r, key, null);
                bool hover = HoverKey != null && HoverKey.EndsWith(":" + n.Ssid);
                if (hover) Fill(g, p.Hover, r, 6);
                TextRenderer.DrawText(g, NetGlyphs.Wifi(n.Bars), icon16, new Rectangle(r.Left + Px(8), r.Top, Px(30), r.Height),
                    n.Connected ? p.Accent : p.Text, C);
                int nx = r.Left + Px(48);
                int actionsW = n.Connected ? Px(140) : Px(100);
                var ns = TextRenderer.MeasureText(g, n.Ssid, name, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                int nameW = Math.Min(ns.Width + Px(2), r.Right - nx - actionsW - Px(20));
                TextRenderer.DrawText(g, n.Ssid, name, new Rectangle(nx, r.Top, nameW, r.Height), p.Text, L);
                if (n.Secure)
                    using (var lockFont = IconFont(10))
                        TextRenderer.DrawText(g, NetGlyphs.Lock, lockFont, new Rectangle(nx + nameW + Px(4), r.Top, Px(14), r.Height), p.SubText, C);

                var net = n;
                if (_busySsid == n.Ssid)
                    TextRenderer.DrawText(g, "Connecting…", sub, new Rectangle(r.Right - Px(110), r.Top, Px(102), r.Height), p.SubText, R);
                else if (n.Connected)
                {
                    Action(g, p, new Rectangle(r.Right - Px(132), r.Top + Px(5), Px(30), Px(30)), NetGlyphs.Qr, "qr:" + n.Ssid,
                        "Share this Wi-Fi as a QR code", () => ShowQr(net.Ssid), 13);
                    Pill(g, p, new Rectangle(r.Right - Px(98), r.Top + Px(6), Px(94), r.Height - Px(12)), "Disconnect", "disc:" + n.Ssid,
                        () => _app.DisconnectWifi(), PillStyle.Neutral);
                }
                else if (hover)
                    Pill(g, p, new Rectangle(r.Right - Px(88), r.Top + Px(6), Px(84), r.Height - Px(12)), "Connect", "conn:" + n.Ssid,
                        () => _app.ConnectWifi(net));
                y += r.Height;
            }
            if (s.WifiOn && s.WifiMessage == null && s.Networks.Count > MaxNetworks)
            {
                TextLink(g, p, $"+{s.Networks.Count - MaxNetworks} more networks ›", new Rectangle(Px(22), y, Px(250), Px(28)), "morenets",
                    () => _app.Open("ms-availablenetworks:"), alignRight: false);
                y += Px(28);
            }
        }

        y += Px(4);
        SectionLabel(g, p, "Speed test", y);
        var last = _app.LastSpeed;
        if (last != null && !s.Testing)
            TextRenderer.DrawText(g, "Last test " + Ago(last.When), sub, new Rectangle(right - Px(200), y, Px(192), Px(28)), p.SubText, R);
        y += Px(28);
        int gap = Px(8), bw = (right - left - 2 * gap) / 3;
        var phase = s.Testing ? s.TestPhase : "";
        Metric(g, p, new Rectangle(left, y, bw, Px(64)), NetGlyphs.Down, "Download",
            s.Testing || s.TestDown != null ? s.TestDown : last?.DownMbps, "Mbps", phase == "Download");
        Metric(g, p, new Rectangle(left + bw + gap, y, bw, Px(64)), NetGlyphs.Up, "Upload",
            s.Testing || s.TestUp != null ? s.TestUp : last?.UpMbps, "Mbps", phase == "Upload");
        Metric(g, p, new Rectangle(left + 2 * (bw + gap), y, bw, Px(64)), NetGlyphs.Speed, "Ping",
            s.Testing || s.TestPing != null ? s.TestPing : last?.PingMs, "ms", phase.StartsWith("Measuring"));
        y += Px(64) + Px(6);
        var testRow = new Rectangle(left, y, right - left, Px(34));
        string note = s.Testing ? (s.TestPhase.StartsWith("Measuring") ? s.TestPhase : $"Testing {s.TestPhase.ToLowerInvariant()}…")
                    : s.TestPhase.Length > 0 ? s.TestPhase : "Uses Cloudflare · about 50–500 MB of data";
        TextRenderer.DrawText(g, note, sub, new Rectangle(testRow.Left + Px(10), testRow.Top, testRow.Width - Px(120), testRow.Height), p.SubText, L);
        Pill(g, p, new Rectangle(testRow.Right - Px(100), testRow.Top + Px(2), Px(96), testRow.Height - Px(4)),
            s.Testing ? "Cancel" : "Run test", "speed", () => { if (s.Testing) s.CancelSpeedTest(); else _app.RunSpeedTest(); },
            s.Testing ? PillStyle.Neutral : PillStyle.Accent, s.Connected || s.Testing);
        y += testRow.Height + Px(6);

        SectionLabel(g, p, s.Connected ? $"Data usage · {s.Name}" : "Data usage", y);
        y += Px(28);
        if (s.Today is null) { Message(g, p, y, 32, s.UsageMessage ?? "Loading…"); y += Px(32); }
        else
        {
            UsageLine(g, p, sub, y, "Today", s.Today.Value); y += Px(24);
            if (s.Month is (ulong, ulong) m) { UsageLine(g, p, sub, y, DateTime.Now.ToString("MMMM"), m); y += Px(24); }
            if (s.Apps.Count > 0)
            {
                y += Px(4);
                ulong max = Math.Max(1, s.Apps.Max(a => a.Down + a.Up));
                foreach (var a in s.Apps)
                {
                    var r = new Rectangle(left, y, right - left, Px(24));
                    int nameW = Px(130), valW = Px(70);
                    TextRenderer.DrawText(g, a.Name, sub, new Rectangle(r.Left + Px(10), r.Top, nameW, r.Height), p.Text, L);
                    var bar = new RectangleF(r.Left + Px(10) + nameW + Px(8), r.Top + r.Height / 2f - Px(3), r.Width - nameW - valW - Px(36), Px(6));
                    Fill(g, p.Chip, bar, 3);
                    Fill(g, p.Accent, bar with { Width = Math.Max(bar.Height, bar.Width * (float)((double)(a.Down + a.Up) / max)) }, 3);
                    TextRenderer.DrawText(g, NetService.Bytes(a.Down + a.Up), sub, new Rectangle(r.Right - valW - Px(8), r.Top, valW, r.Height), p.SubText, R);
                    y += r.Height;
                }
            }
        }

        y += Px(10);
        using (var line = new Pen(p.Border, 1)) g.DrawLine(line, 0, y, Width, y);
        int ly = y + Px(9);
        var link = LinkButton(g, p, NetGlyphs.Settings, "Network settings", Px(12), ly, false, "netsettings", _ => _app.Open("ms-settings:network-status"));
        TextRenderer.DrawText(g, HoverLabel ?? _status, sub, Rectangle.FromLTRB(link.Right + Px(10), ly, right - Px(8), ly + Px(32)), p.SubText,
            R | TextFormatFlags.EndEllipsis);
        return ly + Px(32) + Px(9);
    }

    void UsageLine(Graphics g, Palette p, Font f, int y, string label, (ulong Down, ulong Up) v)
    {
        int left = Px(22), right = Width - Px(20);
        TextRenderer.DrawText(g, label, f, new Rectangle(left, y, Px(120), Px(24)), p.Text, L);
        TextRenderer.DrawText(g, $"↓ {NetService.Bytes(v.Down)}     ↑ {NetService.Bytes(v.Up)}", f,
            new Rectangle(left + Px(120), y, right - left - Px(120), Px(24)), p.Text, R);
    }

    void Metric(Graphics g, Palette p, Rectangle r, string glyph, string caption, double? value, string unit, bool active)
    {
        Fill(g, CardColor(p), r, 8);
        if (active)
        {
            using var pen = new Pen(p.Accent, Math.Max(1.5f, 1.5f * S));
            using var path = Theme.Rounded(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 8 * S);
            g.DrawPath(pen, path);
        }
        using var cap = UiFont(11);
        using var ic = IconFont(11);
        TextRenderer.DrawText(g, glyph, ic, new Rectangle(r.Left + Px(10), r.Top + Px(6), Px(14), Px(18)), p.SubText, C);
        TextRenderer.DrawText(g, caption, cap, new Rectangle(r.Left + Px(28), r.Top + Px(6), r.Width - Px(30), Px(18)), p.SubText, L);
        using var big = UiFont(21, true);
        using var unitFont = UiFont(11);
        string text = unit == "ms" ? (value is double v ? v.ToString("0") : "—") : NetService.Mb(value);
        var ts = TextRenderer.MeasureText(g, text, big, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, text, big, new Rectangle(r.Left + Px(10), r.Top + Px(26), ts.Width + Px(2), Px(30)), p.Text, L);
        if (value != null)
            TextRenderer.DrawText(g, unit, unitFont, new Rectangle(r.Left + Px(14) + ts.Width, r.Top + Px(33), Px(40), Px(20)), p.SubText, L);
    }

    void Sparkline(Graphics g, Palette p, Rectangle r, List<double?> values)
    {
        if (values.Count < 2) return;
        double max = Math.Max(50, values.Where(v => v != null).Select(v => v!.Value).DefaultIfEmpty(0).Max() * 1.15);
        int cap = 60;
        float step = r.Width / (float)(cap - 1);
        float x0 = r.Right - step * (values.Count - 1);
        using var pen = new Pen(p.Accent, Math.Max(1.5f, 1.5f * S)) { LineJoin = LineJoin.Round };
        using var fail = new SolidBrush(Theme.Amber);
        var pts = new List<PointF>();
        void Flush()
        {
            if (pts.Count >= 2) g.DrawLines(pen, pts.ToArray());
            else if (pts.Count == 1) g.FillEllipse(pen.Brush, pts[0].X - S, pts[0].Y - S, 2 * S, 2 * S);
            pts.Clear();
        }
        for (int i = 0; i < values.Count; i++)
        {
            float x = x0 + i * step;
            if (values[i] is double v) pts.Add(new PointF(x, r.Bottom - (float)(Math.Min(v, max) / max) * r.Height));
            else { Flush(); g.FillRectangle(fail, x - S, r.Bottom - Px(4), 2 * S, Px(4)); }
        }
        Flush();
    }

    void ShowQr(string ssid)
    {
        var (key, auth) = NetService.SavedWifiKey(ssid);
        bool open = auth.Equals("open", StringComparison.OrdinalIgnoreCase);
        List<BitArray>? matrix = null;
        if (key != null || open)
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(NetService.WifiQrText(ssid, key, auth), QRCodeGenerator.ECCLevel.M);
            matrix = data.ModuleMatrix.Select(b => new BitArray(b)).ToList();
        }
        _qr = (ssid, key, matrix);
        RefreshLayout();
    }

    int DrawQr(Graphics g, Palette p)
    {
        var (ssid, key, matrix) = _qr!.Value;
        using var title = UiFont(15, true);
        using var name = UiFont(14, true);
        using var sub = UiFont(12);
        IconButton(g, p, new Rectangle(Px(10), Px(9), Px(34), Px(34)), NetGlyphs.Back, "back", _ => { _qr = null; RefreshLayout(); });
        TextRenderer.DrawText(g, "Share Wi-Fi", title, new Rectangle(Px(52), 0, Px(250), Px(52)), p.Text, L);
        int y = Px(56);
        TextRenderer.DrawText(g, ssid, name, new Rectangle(0, y, Width, Px(24)), p.Text, C);
        y += Px(32);
        if (matrix != null)
        {
            int size = Px(232);
            var box = new Rectangle((Width - size) / 2, y, size, size);
            Fill(g, Color.White, box, 10);
            int n = matrix.Count;
            float cell = (size - Px(8)) / (float)n;
            float ox = box.Left + (size - cell * n) / 2, oy = box.Top + (size - cell * n) / 2;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            using var black = new SolidBrush(Color.Black);
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                    if (matrix[r][c]) g.FillRectangle(black, ox + c * cell, oy + r * cell, cell + 0.5f, cell + 0.5f);
            g.SmoothingMode = old;
            y += size + Px(14);
            TextRenderer.DrawText(g, "Point your phone's camera at the code to join", sub, new Rectangle(0, y, Width, Px(20)), p.SubText, C);
            y += Px(24);
            if (key != null)
            {
                TextRenderer.DrawText(g, "Password: " + key, sub, new Rectangle(Px(20), y, Width - Px(40), Px(20)), p.Text, C | TextFormatFlags.NoPrefix);
                y += Px(24);
                int bw = Px(140);
                Pill(g, p, new Rectangle((Width - bw) / 2, y + Px(4), bw, Px(30)), "Copy password", "copypw",
                    () => { Clipboard.SetText(key); SetStatus("Password copied"); _qr = null; RefreshLayout(); }, PillStyle.Neutral);
                y += Px(40);
            }
        }
        else
        {
            using var f = UiFont(12);
            TextRenderer.DrawText(g, "Windows only reveals saved Wi-Fi passwords to administrators.", f,
                new Rectangle(Px(20), y, Width - Px(40), Px(20)), p.SubText, C);
            y += Px(22);
            TextRenderer.DrawText(g, "Run the widget as administrator, or use Settings → Wi-Fi.", f,
                new Rectangle(Px(20), y, Width - Px(40), Px(20)), p.SubText, C);
            y += Px(30);
            int bw = Px(160);
            Pill(g, p, new Rectangle((Width - bw) / 2, y, bw, Px(32)), "Open Wi-Fi settings", "wifisettings",
                () => _app.Open("ms-settings:network-wifi"), PillStyle.Neutral);
            y += Px(44);
        }
        return y + Px(12);
    }

    readonly Dictionary<string, string> _labels = new();
    string? HoverLabel => HoverKey != null && _labels.TryGetValue(HoverKey, out var l) ? l : null;
    protected override void OnHoverChanged() => Invalidate();

    void Action(Graphics g, Palette p, Rectangle r, string glyph, string key, string label, System.Action click, float glyphPx = 14)
    {
        _labels[key] = label;
        IconButton(g, p, r, glyph, key, _ => click(), glyphPx: glyphPx);
    }

    void TextLink(Graphics g, Palette p, string text, Rectangle r, string key, System.Action click, bool alignRight = true)
    {
        AddHit(r, key, _ => click());
        using var f = UiFont(12, IsHover(key));
        TextRenderer.DrawText(g, text, f, r, p.Accent, (alignRight ? R : L) | TextFormatFlags.EndEllipsis);
    }

    public void SetBusy(string? key) { _busySsid = key; Invalidate(); }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (t.Date == DateTime.Today) return "today " + t.ToString("HH:mm");
        if (t.Date == DateTime.Today.AddDays(-1)) return "yesterday " + t.ToString("HH:mm");
        return t.ToString("MMM d, HH:mm");
    }
}
