using BtBattery;

namespace DevTools;

sealed class DevFlyout : FlyoutBase
{
    const int HeaderH = 52, LabelH = 28, DeviceH = 60, AvdH = 44, PortH = 40, MsgH = 48, FooterH = 50, MaxPorts = 10;

    readonly DevService _svc;
    readonly DevTrayApp _app;
    string _status = "";
    int? _confirmPid;

    public DevFlyout(DevService svc, DevTrayApp app) : base("Developer Tools")
    {
        _svc = svc;
        _app = app;
    }

    protected override int LogicalWidth => 420;

    public void SetStatus(string text) { _status = text; Invalidate(); }

    List<PortInfo> Ports => _app.VisiblePorts;

    List<string> IdleAvds => _svc.Avds.Where(a => _svc.Devices.All(d => d.AvdName != a)).ToList();

    protected override void OnOpening() { _confirmPid = null; }

    protected override int ContentHeight()
    {
        int h = HeaderH + LabelH;
        h += _svc.Adb == null || !_svc.AdbServerRunning || _svc.Devices.Count == 0 ? MsgH : _svc.Devices.Count * DeviceH;
        var avds = IdleAvds;
        if (avds.Count > 0) h += LabelH + avds.Count * AvdH;
        int ports = Math.Min(MaxPorts, Ports.Count);
        h += LabelH + (ports == 0 ? MsgH : ports * PortH) + (Ports.Count > MaxPorts ? 24 : 0);
        return h + FooterH + 6;
    }

    protected override void PaintContent(Graphics g, Palette p)
    {
        using var title = UiFont(15, true);
        using var name = UiFont(13, true);
        using var sub = UiFont(11.5f);
        using var icon16 = IconFont(16);
        int left = Px(12), right = Width - Px(12);

        TextRenderer.DrawText(g, "Developer tools", title, new Rectangle(Px(22), 0, Px(250), Px(HeaderH)), p.Text, L);
        IconButton(g, p, new Rectangle(right - Px(36), Px(9), Px(34), Px(34)), DevGlyphs.Refresh, "refresh",
            _ => _app.RefreshNow(true));

        int y = Px(HeaderH);
        SectionLabel(g, p, "Android devices", y);
        y += Px(LabelH);
        if (_svc.Adb == null)
        {
            Message(g, p, sub, y, "Android SDK not found (set ANDROID_HOME)");
            y += Px(MsgH);
        }
        else if (!_svc.AdbServerRunning)
        {
            var r = new Rectangle(left, y + Px(4), right - left, Px(MsgH - 8));
            TextRenderer.DrawText(g, "ADB server isn't running", sub, new Rectangle(r.Left + Px(10), r.Top, r.Width - Px(120), r.Height), p.SubText, L);
            PillButton(g, p, new Rectangle(r.Right - Px(100), r.Top + Px(6), Px(92), r.Height - Px(12)), "Start ADB", "adbstart",
                async () => { SetStatus("Starting ADB server…"); await _svc.StartAdbServerAsync(); _app.RefreshNow(false); });
            y += Px(MsgH);
        }
        else if (_svc.Devices.Count == 0)
        {
            Message(g, p, sub, y, "No devices — connect a phone or start an emulator");
            y += Px(MsgH);
        }
        foreach (var d in _svc.Devices)
        {
            var r = new Rectangle(left, y + Px(2), right - left, Px(DeviceH - 4));
            bool unauth = d.State == "unauthorized";
            TextRenderer.DrawText(g, d.IsEmulator ? DevGlyphs.Emulator : DevGlyphs.Phone, icon16,
                new Rectangle(r.Left + Px(8), r.Top, Px(26), r.Height), d.Online ? p.Accent : unauth ? Theme.Amber : p.SubText, C);
            int tx = r.Left + Px(44);
            int actionsW = d.Online ? Px(4 * 32 + 4) : 0;
            TextRenderer.DrawText(g, d.DisplayName, name, new Rectangle(tx, r.Top + Px(9), r.Right - actionsW - tx, Px(19)), p.Text, L);
            string detail = d.Online
                ? string.Join(" · ", new[] { d.Transport, d.Version != null ? "Android " + d.Version : null,
                                             d.Battery != null ? $"Battery {d.Battery}%" : null, d.Serial }.Where(s => s != null))
                : unauth ? "Unauthorized — allow USB debugging on the device" : $"{d.State} · {d.Serial}";
            TextRenderer.DrawText(g, detail, sub, new Rectangle(tx, r.Top + Px(29), r.Right - actionsW - tx, Px(17)),
                unauth ? Theme.Amber : p.SubText, L);

            if (d.Online)
            {
                int bx = r.Right - Px(4 * 32);
                int by = r.Top + (r.Height - Px(32)) / 2;
                var dev = d;
                Action(g, p, new Rectangle(bx, by, Px(32), Px(32)), DevGlyphs.Camera, $"shot:{d.Serial}", "Take screenshot", () => _app.Screenshot(dev));
                Action(g, p, new Rectangle(bx + Px(32), by, Px(32), Px(32)), DevGlyphs.Install, $"apk:{d.Serial}", "Install APK…", () => _app.InstallApk(dev));
                Action(g, p, new Rectangle(bx + Px(64), by, Px(32), Px(32)), DevGlyphs.Shell, $"shell:{d.Serial}", "Open adb shell", () => _svc.OpenShell(dev));
                Action(g, p, new Rectangle(bx + Px(96), by, Px(32), Px(32)), DevGlyphs.Logcat, $"log:{d.Serial}", "Open logcat", () => _svc.OpenLogcat(dev));
            }
            y += Px(DeviceH);
        }

        var avds = IdleAvds;
        if (avds.Count > 0)
        {
            SectionLabel(g, p, "Emulators", y);
            y += Px(LabelH);
            foreach (var avd in avds)
            {
                var r = new Rectangle(left, y + Px(2), right - left, Px(AvdH - 4));
                TextRenderer.DrawText(g, DevGlyphs.Emulator, icon16, new Rectangle(r.Left + Px(8), r.Top, Px(26), r.Height), p.SubText, C);
                TextRenderer.DrawText(g, avd.Replace('_', ' '), name, new Rectangle(r.Left + Px(44), r.Top, r.Width - Px(140), r.Height), p.Text, L);
                if (_svc.StartingAvds.Contains(avd))
                    TextRenderer.DrawText(g, "Starting…", sub, new Rectangle(r.Right - Px(110), r.Top, Px(100), r.Height), p.SubText, R);
                else
                {
                    var a = avd;
                    PillButton(g, p, new Rectangle(r.Right - Px(84), r.Top + Px(5), Px(80), r.Height - Px(10)), "▶  Start", $"avd:{avd}",
                        () => { _svc.StartAvd(a); SetStatus($"Starting {a.Replace('_', ' ')}…"); Invalidate(); return Task.CompletedTask; });
                }
                y += Px(AvdH);
            }
        }

        SectionLabel(g, p, $"Listening ports ({Ports.Count})", y);
        y += Px(LabelH);
        if (Ports.Count == 0) { Message(g, p, sub, y, "No listening ports"); y += Px(MsgH); }
        using var portFont = new Font("Cascadia Mono", 12.5f * S, FontStyle.Bold, GraphicsUnit.Pixel);
        foreach (var port in Ports.Take(MaxPorts))
        {
            var r = new Rectangle(left, y + Px(2), right - left, Px(PortH - 4));
            string rowKey = $"port:{port.Port}:{port.Pid}";
            AddHit(r, rowKey, null);
            if (IsHover(rowKey) || _confirmPid == port.Pid) Fill(g, p.Hover, r, 6);
            TextRenderer.DrawText(g, $":{port.Port}", portFont, new Rectangle(r.Left + Px(10), r.Top, Px(70), r.Height), p.Accent, L);
            var info = $"{port.Process} · PID {port.Pid}{(port.Label != null ? " · " + port.Label : "")}";
            TextRenderer.DrawText(g, info, sub, new Rectangle(r.Left + Px(84), r.Top, r.Width - Px(84 + 100), r.Height), p.SubText, L);

            var pid = port.Pid;
            if (_confirmPid == pid)
                PillButton(g, p, new Rectangle(r.Right - Px(92), r.Top + Px(4), Px(88), r.Height - Px(8)), "End task", $"kill:{pid}",
                    () =>
                    {
                        var err = DevService.KillProcess(pid);
                        _confirmPid = null;
                        SetStatus(err == null ? $"Ended {port.Process} (port {port.Port})" : $"Couldn't end {port.Process}: {err}");
                        _app.RefreshNow(false);
                        return Task.CompletedTask;
                    }, danger: true);
            else
                Action(g, p, new Rectangle(r.Right - Px(34), r.Top + (r.Height - Px(30)) / 2, Px(30), Px(30)), DevGlyphs.Close,
                    $"x:{pid}:{port.Port}", $"End {port.Process} (PID {pid})…", () => { _confirmPid = pid; Invalidate(); }, glyphPx: 11);
            y += Px(PortH);
        }
        if (Ports.Count > MaxPorts)
        {
            TextRenderer.DrawText(g, $"+{Ports.Count - MaxPorts} more", sub, new Rectangle(Px(22), y, Px(200), Px(22)), p.SubText, L);
            y += Px(24);
        }

        y += Px(6);
        using (var line = new Pen(p.Border, 1)) g.DrawLine(line, 0, y, Width, y);
        int ly = y + (Px(FooterH) - Px(32)) / 2;
        var link = LinkButton(g, p, DevGlyphs.Folder, "Screenshots", Px(12), ly, false, "folder", _ => _app.OpenScreenshots());
        var status = HoverLabel ?? _status;
        TextRenderer.DrawText(g, status, sub, Rectangle.FromLTRB(link.Right + Px(10), ly, right - Px(8), ly + Px(32)), p.SubText,
            R | TextFormatFlags.EndEllipsis);
    }

    readonly Dictionary<string, string> _labels = new();
    string? HoverLabel => HoverKey != null && _labels.TryGetValue(HoverKey, out var l) ? l : null;

    void Action(Graphics g, Palette p, Rectangle r, string glyph, string key, string label, System.Action click, float glyphPx = 14)
    {
        _labels[key] = label;
        IconButton(g, p, r, glyph, key, _ => click(), glyphPx: glyphPx);
    }

    void PillButton(Graphics g, Palette p, Rectangle r, string text, string key, Func<Task> click, bool danger = false)
    {
        AddHit(r, key, async _ => await click());
        var bg = danger ? Theme.Red : p.Accent;
        if (IsHover(key)) bg = Theme.Mix(bg, p.Text, 0.15f);
        Fill(g, bg, r, 4);
        using var f = UiFont(12, true);
        TextRenderer.DrawText(g, text, f, r, danger || !Theme.AppsDark ? Color.White : Color.FromArgb(20, 20, 20), C);
    }

    void Message(Graphics g, Palette p, Font f, int y, string text) =>
        TextRenderer.DrawText(g, text, f, new Rectangle(Px(22), y, Width - Px(44), Px(MsgH)), p.SubText, L);
}
