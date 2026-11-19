using System.Diagnostics;
using System.Net.NetworkInformation;
using BtBattery;
using Microsoft.Win32;

namespace NetTools;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--status", StringComparer.OrdinalIgnoreCase))
        {
            Native.AttachConsole(-1);
            SynchronizationContext.SetSynchronizationContext(null);
            var s = new NetService();
            s.RefreshAsync(true, true).GetAwaiter().GetResult();
            s.ScanWifiAsync(false).GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine($"Connected={s.Connected} {s.Kind} '{s.Name}' link={NetService.LinkSpeed(s.LinkBps)} ip={s.LocalIp} gw={s.Gateway} dns={s.Dns}");
            Console.WriteLine($"Internet={s.Internet} ping={s.PingMs} public={s.PublicIp} {s.Location} {s.Isp}");
            foreach (var v in s.Vpns) Console.WriteLine($"  VPN {v.Name} connected={v.Connected} ras={v.RasEntry} :: {v.Detail}");
            Console.WriteLine($"Wi-Fi adapter={s.HasWifi} on={s.WifiOn} msg={s.WifiMessage} networks={s.Networks.Count}");
            Console.WriteLine($"Usage today={s.Today} month={s.Month} msg={s.UsageMessage}");
            foreach (var a in s.Apps) Console.WriteLine($"  app {a.Name}: {NetService.Bytes(a.Down)} down {NetService.Bytes(a.Up)} up");
            if (args.Contains("--speed", StringComparer.OrdinalIgnoreCase))
            {
                var r = s.SpeedTestAsync().GetAwaiter().GetResult();
                Console.WriteLine(r == null ? "Speed test failed: " + s.TestPhase : $"Speed: down {r.DownMbps:0.0} Mbps, up {r.UpMbps:0.0} Mbps, ping {r.PingMs:0} ms");
            }
            return;
        }

        using var mutex = new Mutex(true, "NetworkWidget.SingleInstance", out bool first);
        if (!first) return;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => NetService.Log("UI error: " + e.Exception);
        ApplicationConfiguration.Initialize();
        Application.Run(new NetTrayApp());
    }
}

sealed class NetTrayApp : ApplicationContext
{
    readonly NetSettings _settings = AppFiles.Load<NetSettings>(NetService.AppName);
    readonly NetService _svc = new();
    readonly NetFlyout _flyout;
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer _changeDebounce = new() { Interval = 2500 };
    NetWidget? _widget;
    IntPtr _hIcon;
    DateTime _lastFull, _lastScan;
    bool? _wasInternet;
    HashSet<string>? _wasVpns;
    bool _refreshing;
    DateTime _refreshStarted;

    public NetTrayApp()
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _flyout = new NetFlyout(_svc, this);
        _svc.Changed += OnServiceChanged;

        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true, Text = "Network" };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFlyout(null); };
        RenderIcon();

        if (_settings.ShowWidget) SetWidget(true);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _changeDebounce.Tick += (_, _) => { _changeDebounce.Stop(); Refresh(true, true); };
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        SystemEvents.UserPreferenceChanged += OnThemeChanged;
        Refresh(true, false);
    }

    public SpeedResult? LastSpeed => _settings.SpeedHistory.LastOrDefault();

    void Tick()
    {
        bool full = (DateTime.Now - _lastFull).TotalSeconds >= 60;
        Refresh(full, false);
        if (_flyout.Visible && (DateTime.Now - _lastScan).TotalSeconds >= 20) Scan();
    }

    async void Refresh(bool full, bool forceIp)
    {
        if (_refreshing)
        {
            if ((DateTime.Now - _refreshStarted).TotalSeconds < 45) return;
            NetService.Log("Refresh took over 45 s; starting a new one");
        }
        _refreshing = true;
        var started = _refreshStarted = DateTime.Now;
        try
        {
            if (full) _lastFull = DateTime.Now;
            await _svc.RefreshAsync(full, forceIp);
            NotifyChanges();
        }
        catch (Exception ex) { NetService.Log("Refresh: " + ex.Message); }
        finally
        {
            if (_refreshStarted == started) _refreshing = false;
            var took = (DateTime.Now - started).TotalSeconds;
            if (took > 8) NetService.Log($"Slow refresh ({(full ? "full" : "quick")}): {took:0.0} s");
        }
    }

    public void RefreshAll()
    {
        Refresh(true, true);
        Scan();
    }

    async void Scan()
    {
        _lastScan = DateTime.Now;
        await _svc.ScanWifiAsync();
    }

    void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (!_flyout.IsHandleCreated) return;
        _flyout.BeginInvoke(() => { _changeDebounce.Stop(); _changeDebounce.Start(); });
    }

    void OnServiceChanged()
    {
        _widget?.UpdateView();
        _flyout.RefreshLayout();
        RenderIcon();
        var t = !_svc.Connected ? "Network: offline"
              : $"{_svc.Name}{(_svc.Internet ? "" : " (no internet)")}{(_svc.PingMs is double ms ? $" · {ms:0} ms" : "")}{(_svc.VpnActive ? " · VPN" : "")}";
        _icon.Text = t.Length <= 127 ? t : t[..127];
    }

    void NotifyChanges()
    {
        var vpns = _svc.Vpns.Where(v => v.Connected).Select(v => v.Name).ToHashSet();
        if (_settings.NotifyChanges && _wasInternet != null)
        {
            if (_wasInternet == true && !_svc.Internet)
                _icon.ShowBalloonTip(5000, "Internet connection lost", _svc.Connected ? $"{_svc.Name} has no internet" : "Network disconnected", ToolTipIcon.Warning);
            else if (_wasInternet == false && _svc.Internet)
                _icon.ShowBalloonTip(4000, "Back online", _svc.Name, ToolTipIcon.Info);
            if (_wasVpns != null)
            {
                foreach (var v in vpns.Except(_wasVpns)) _icon.ShowBalloonTip(4000, "VPN connected", v, ToolTipIcon.Info);
                foreach (var v in _wasVpns.Except(vpns)) _icon.ShowBalloonTip(5000, "VPN disconnected", v, ToolTipIcon.Warning);
            }
        }
        _wasInternet = _svc.Internet;
        _wasVpns = vpns;
    }

    public void Open(string target)
    {
        _flyout.HideFlyout();
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { NetService.Log("Open: " + ex.Message); }
    }

    public async void ToggleRas(string entry, bool connect)
    {
        _flyout.SetBusy("ras:" + entry);
        _flyout.SetStatus(connect ? $"Connecting to {entry}…" : $"Disconnecting {entry}…");
        var err = await _svc.SetRasAsync(entry, connect);
        _flyout.SetBusy(null);
        if (err == null) { _flyout.SetStatus(connect ? $"Connected to {entry}" : $"Disconnected {entry}"); Refresh(true, true); }
        else if (connect) { _flyout.SetStatus("Couldn't connect — opening VPN settings"); Open("ms-settings:network-vpn"); }
        else _flyout.SetStatus(err);
    }

    public async void SetWifi(bool on)
    {
        _flyout.SetStatus(on ? "Turning Wi-Fi on…" : "Turning Wi-Fi off…");
        bool ok = await _svc.SetWifiAsync(on);
        _flyout.SetStatus(ok ? "" : "Windows didn't allow changing Wi-Fi");
        if (on && ok) { await Task.Delay(2000); Scan(); }
    }

    public async void ConnectWifi(WifiNet net)
    {
        _flyout.SetBusy(net.Ssid);
        var err = await _svc.ConnectWifiAsync(net);
        _flyout.SetBusy(null);
        if (err == null) _flyout.SetStatus($"Connected to {net.Ssid}");
        else
        {
            _flyout.SetStatus($"{net.Ssid}: enter the password in Windows");
            Open("ms-availablenetworks:");
        }
    }

    public async void DisconnectWifi()
    {
        _flyout.SetStatus("Disconnecting…");
        await _svc.DisconnectWifiAsync();
        _flyout.SetStatus("Disconnected");
    }

    public async void RunSpeedTest()
    {
        var r = await _svc.SpeedTestAsync();
        if (r == null) return;
        _settings.SpeedHistory.Add(r);
        while (_settings.SpeedHistory.Count > 30) _settings.SpeedHistory.RemoveAt(0);
        _settings.Save();
        _flyout.SetStatus($"↓ {NetService.Mb(r.DownMbps)} Mbps  ↑ {NetService.Mb(r.UpMbps)} Mbps");
        if (!_flyout.Visible)
            _icon.ShowBalloonTip(5000, "Speed test finished",
                $"Download {NetService.Mb(r.DownMbps)} Mbps · Upload {NetService.Mb(r.UpMbps)} Mbps · Ping {r.PingMs:0} ms", ToolTipIcon.Info);
    }

    void RenderIcon()
    {
        using var bmp = IconRenderer.Glyph(NetGlyphs.For(_svc), Theme.TaskbarDark, _svc.Connected && !_svc.Internet);
        var h = bmp.GetHicon();
        _icon.Icon = Icon.FromHandle(h);
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
        _hIcon = h;
    }

    void ShowFlyout(Rectangle? anchor)
    {
        if (_flyout.Visible) { _flyout.HideFlyout(); return; }
        if ((DateTime.Now - _flyout.LastHidden).TotalMilliseconds < 300) return;
        _flyout.ShowAt(anchor);
        Refresh(true, false);
        Scan();
    }

    void SetWidget(bool on)
    {
        if (on)
        {
            if (_widget == null)
            {
                _widget = new NetWidget(_svc, _settings, _menu);
                _widget.Clicked += () => ShowFlyout(_widget.Bounds);
            }
            _widget.ShowStrip();
        }
        else if (_widget != null)
        {
            _widget.Close();
            _widget.Dispose();
            _widget = null;
        }
    }

    void BuildMenu()
    {
        _menu.Items.Clear();
        var open = new ToolStripMenuItem("Open network panel", null, (_, _) => ShowFlyout(null));
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);
        _menu.Items.Add(_svc.Testing ? "Speed test running…" : "Run speed test", null, (_, _) => { if (!_svc.Testing) RunSpeedTest(); });
        _menu.Items.Add("Copy public IP", null, (_, _) => { if (_svc.PublicIp != null) Clipboard.SetText(_svc.PublicIp); }).Enabled = _svc.PublicIp != null;
        _menu.Items.Add("Network settings", null, (_, _) => Open("ms-settings:network-status"));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Toggle("Show taskbar widget", _settings.ShowWidget, v => { _settings.ShowWidget = v; SetWidget(v); }));
        _menu.Items.Add(Toggle("Show ping in widget (else network name)", _settings.ShowLatency, v => { _settings.ShowLatency = v; _widget?.UpdateView(); }));
        _menu.Items.Add(Toggle("Show VPN shield in widget", _settings.ShowVpn, v => { _settings.ShowVpn = v; _widget?.UpdateView(); }));
        _menu.Items.Add(Toggle("Notify when internet or VPN drops", _settings.NotifyChanges, v => _settings.NotifyChanges = v));
        _menu.Items.Add(Toggle("Start with Windows", AppFiles.StartupEnabled(NetService.AppName), v => AppFiles.SetStartup(NetService.AppName, v)));
        if (_widget != null) _menu.Items.Add("Reset widget position", null, (_, _) => _widget?.ResetPosition());
        _menu.Items.Add(new ToolStripSeparator());
        Credit.AddToMenu(_menu);
        _menu.Items.Add(new ToolStripSeparator());        _menu.Items.Add("Exit", null, (_, _) => ExitApp());
        AppFiles.ThemeMenu(_menu);
    }

    ToolStripMenuItem Toggle(string text, bool on, Action<bool> set) =>
        new(text, null, (_, _) => { set(!on); _settings.Save(); }) { Checked = on };

    void OnThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (!_flyout.IsHandleCreated) _ = _flyout.Handle;
        _flyout.BeginInvoke(() => { RenderIcon(); _widget?.ApplyTheme(); _flyout.ApplyTheme(); });
    }

    void ExitApp()
    {
        _timer.Stop();
        _svc.CancelSpeedTest();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
        _widget?.Close();
        _flyout.Close();
        ExitThread();
    }
}
