using System.Diagnostics;
using Microsoft.Win32;

namespace BtBattery;

sealed class TrayApp : ApplicationContext
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly BatteryService _service = new();
    readonly NotifyIcon _mainIcon;
    readonly Dictionary<string, NotifyIcon> _deviceIcons = new();
    readonly Dictionary<NotifyIcon, (IntPtr handle, string key)> _iconState = new();
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _timer = new();
    readonly HashSet<string> _alerted = new();
    readonly FlyoutForm _flyout;
    TaskbarWidget? _widget;

    List<DeviceBattery> _devices = new();
    DateTime? _lastScan;
    bool _busy;

    public TrayApp()
    {
        _flyout = new FlyoutForm(_settings);
        _flyout.RefreshRequested += () => _ = RefreshAsync();
        _flyout.SettingsRequested += OpenBluetoothSettings;

        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();

        _mainIcon = new NotifyIcon { ContextMenuStrip = _menu, Text = "Bluetooth Battery", Visible = true };
        _mainIcon.MouseClick += OnTrayClick;
        _mainIcon.BalloonTipClicked += (_, _) => ShowFlyout(null);
        RenderMainIcon();

        if (_settings.ShowWidget) SetWidget(true);

        _timer.Interval = Math.Max(10, _settings.RefreshSeconds) * 1000;
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();

        SystemEvents.UserPreferenceChanged += OnThemeChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _ = RefreshAsync();
    }

    async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        _flyout.SetBusy(true);
        try
        {
            var list = await _service.ScanAsync();
            var now = DateTime.Now;
            foreach (var d in list)
            {
                if (d.IsConnected)
                {
                    d.LastSeen = now;
                    if (d.Battery is int b) _settings.LastKnown[d.Address] = new LastKnown { Battery = b, Time = now };
                }
                else if (_settings.LastKnown.TryGetValue(d.Address, out var lk))
                {
                    d.Battery = lk.Battery;
                    d.LastSeen = lk.Time;
                    d.BatteryIsLastKnown = true;
                }
                else d.BatteryIsLastKnown = d.Battery.HasValue;
            }
            _devices = list;
            _lastScan = now;
            _settings.Save();

            UpdateDeviceIcons();
            RenderMainIcon();
            _widget?.SetDevices(_devices);
            _flyout.SetDevices(VisibleDevices(), _lastScan);
            CheckAlerts();
        }
        catch (Exception ex) { Log.Write("Scan failed: " + ex); }
        finally
        {
            _busy = false;
            _flyout.SetBusy(false);
        }
    }

    List<DeviceBattery> VisibleDevices() => _devices
        .Where(d => _settings.ShowDisconnected || d.IsConnected)
        .OrderByDescending(d => d.IsConnected)
        .ThenByDescending(d => d.Battery.HasValue)
        .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    IEnumerable<DeviceBattery> LiveDevices() => _devices
        .Where(d => d.IsConnected && d.Battery.HasValue)
        .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase);

    void CheckAlerts()
    {
        foreach (var d in _devices.Where(d => d.IsConnected && d.Battery.HasValue))
        {
            int pct = d.Battery!.Value;
            if (pct > _settings.LowBatteryThreshold + 5) { _alerted.Remove(d.Address); continue; }
            if (_settings.LowBatteryAlerts && pct <= _settings.LowBatteryThreshold && _alerted.Add(d.Address))
                _mainIcon.ShowBalloonTip(8000, $"{d.Name} battery is low",
                    $"{pct}% remaining. Charge it soon.", ToolTipIcon.Warning);
        }
    }

    void RenderMainIcon()
    {
        bool anyLow = LiveDevices().Any(d => d.Battery <= _settings.LowBatteryThreshold);
        SetIcon(_mainIcon, $"main|{Theme.TaskbarDark}|{anyLow}|{IconRenderer.TraySize}",
            () => IconRenderer.Glyph(Glyphs.Bluetooth, Theme.TaskbarDark, anyLow));
        var lines = LiveDevices().Select(d => $"{d.Name}: {d.Battery}%").ToList();
        _mainIcon.Text = Trim(lines.Count == 0 ? "Bluetooth Battery\nNo battery info from connected devices"
                                               : "Bluetooth Battery\n" + string.Join("\n", lines));
    }

    void UpdateDeviceIcons()
    {
        var want = _settings.ShowDeviceIcons ? LiveDevices().ToList() : new List<DeviceBattery>();
        foreach (var key in _deviceIcons.Keys.Except(want.Select(d => d.Address)).ToList())
        {
            var ni = _deviceIcons[key];
            ni.Visible = false;
            ReleaseIcon(ni);
            ni.Dispose();
            _deviceIcons.Remove(key);
        }

        bool dark = Theme.TaskbarDark;
        int low = _settings.LowBatteryThreshold;
        foreach (var d in want)
        {
            if (!_deviceIcons.TryGetValue(d.Address, out var ni))
            {
                ni = new NotifyIcon { ContextMenuStrip = _menu };
                ni.MouseClick += OnTrayClick;
                _deviceIcons[d.Address] = ni;
            }
            int pct = d.Battery!.Value;
            SetIcon(ni, $"{pct}|{dark}|{low}|{IconRenderer.TraySize}", () => IconRenderer.Battery(pct, dark, low));
            ni.Text = Trim($"{d.Name}\n{pct}% battery");
            ni.Visible = true;
        }
    }

    void SetIcon(NotifyIcon ni, string key, Func<Bitmap> render)
    {
        if (_iconState.TryGetValue(ni, out var cur) && cur.key == key) return;
        using var bmp = render();
        var h = bmp.GetHicon();
        ni.Icon = Icon.FromHandle(h);
        if (cur.handle != IntPtr.Zero) Native.DestroyIcon(cur.handle);
        _iconState[ni] = (h, key);
    }

    void ReleaseIcon(NotifyIcon ni)
    {
        if (_iconState.Remove(ni, out var cur) && cur.handle != IntPtr.Zero) Native.DestroyIcon(cur.handle);
    }

    static string Trim(string s) => s.Length <= 127 ? s : s[..124] + "...";

    void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) ShowFlyout(null);
    }

    void ShowFlyout(Rectangle? anchor)
    {
        if (_flyout.Visible) { _flyout.HideFlyout(); return; }
        if ((DateTime.Now - _flyout.LastHidden).TotalMilliseconds < 300) return;
        _flyout.ShowAt(anchor, VisibleDevices(), _lastScan);
        if (_lastScan == null || (DateTime.Now - _lastScan.Value).TotalSeconds > 5) _ = RefreshAsync();
    }

    void SetWidget(bool on)
    {
        if (on)
        {
            if (_widget == null)
            {
                _widget = new TaskbarWidget(_settings, _menu);
                _widget.Clicked += () => ShowFlyout(_widget.Bounds);
            }
            _widget.SetDevices(_devices);
            _widget.ShowWidget();
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
        bool dark = Theme.AppsDark;
        var renderer = new MenuRenderer(dark);
        ToolStripManager.Renderer = renderer;
        _menu.Renderer = renderer;
        _menu.Items.Clear();

        var open = new ToolStripMenuItem("Open battery panel", null, (_, _) => ShowFlyout(null));
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);
        _menu.Items.Add("Refresh now", null, (_, _) => _ = RefreshAsync());
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(Toggle("Show taskbar widget", _settings.ShowWidget, v => { _settings.ShowWidget = v; SetWidget(v); }));
        _menu.Items.Add(Toggle("Show a battery icon per device", _settings.ShowDeviceIcons,
            v => { _settings.ShowDeviceIcons = v; UpdateDeviceIcons(); }));
        _menu.Items.Add(Toggle("List disconnected devices", _settings.ShowDisconnected,
            v => { _settings.ShowDisconnected = v; _flyout.SetDevices(VisibleDevices(), _lastScan); }));

        var alerts = new ToolStripMenuItem("Low battery alert");
        alerts.DropDownItems.Add(Choice("Off", !_settings.LowBatteryAlerts, () => _settings.LowBatteryAlerts = false));
        foreach (var t in new[] { 10, 15, 20, 25, 30 })
            alerts.DropDownItems.Add(Choice($"At {t}%", _settings.LowBatteryAlerts && _settings.LowBatteryThreshold == t, () =>
            {
                _settings.LowBatteryAlerts = true;
                _settings.LowBatteryThreshold = t;
                _alerted.Clear();
                RedrawAll();
                CheckAlerts();
            }));
        _menu.Items.Add(alerts);

        var every = new ToolStripMenuItem("Refresh every");
        foreach (var (label, secs) in new[] { ("15 seconds", 15), ("30 seconds", 30), ("1 minute", 60), ("2 minutes", 120), ("5 minutes", 300) })
            every.DropDownItems.Add(Choice(label, _settings.RefreshSeconds == secs, () =>
            {
                _settings.RefreshSeconds = secs;
                _timer.Interval = secs * 1000;
            }));
        _menu.Items.Add(every);

        _menu.Items.Add(Toggle("Start with Windows", SafeStartupEnabled(), v =>
        {
            try { Startup.Set(v); } catch (Exception ex) { Log.Write("Startup: " + ex.Message); }
        }));
        if (_widget != null) _menu.Items.Add("Reset widget position", null, (_, _) => _widget?.ResetPosition());

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Bluetooth settings…", null, (_, _) => OpenBluetoothSettings());
        Credit.AddToMenu(_menu);
        _menu.Items.Add(new ToolStripSeparator());        _menu.Items.Add("Exit", null, (_, _) => ExitApp());

        var fg = dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(26, 26, 26);
        _menu.BackColor = dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
        foreach (ToolStripItem item in _menu.Items)
        {
            item.ForeColor = fg;
            if (item is ToolStripMenuItem mi)
                foreach (ToolStripItem sub in mi.DropDownItems) sub.ForeColor = fg;
        }
    }

    ToolStripMenuItem Toggle(string text, bool on, Action<bool> set) =>
        new(text, null, (_, _) => { set(!on); _settings.Save(); }) { Checked = on };

    ToolStripMenuItem Choice(string text, bool on, Action pick) =>
        new(text, null, (_, _) => { pick(); _settings.Save(); }) { Checked = on };

    static bool SafeStartupEnabled()
    {
        try { return Startup.IsEnabled; } catch { return false; }
    }

    static void OpenBluetoothSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Open settings: " + ex.Message); }
    }

    void RedrawAll()
    {
        RenderMainIcon();
        UpdateDeviceIcons();
        _widget?.ApplyTheme();
        _flyout.ApplyTheme();
    }

    void OnThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_flyout.IsHandleCreated) _flyout.BeginInvoke(RedrawAll);
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && _flyout.IsHandleCreated)
            _flyout.BeginInvoke(async () => { await Task.Delay(8000); await RefreshAsync(); });
    }

    void ExitApp()
    {
        _timer.Stop();
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _settings.Save();
        foreach (var ni in _deviceIcons.Values.Append(_mainIcon))
        {
            ni.Visible = false;
            ReleaseIcon(ni);
            ni.Dispose();
        }
        _widget?.Close();
        _flyout.Close();
        ExitThread();
    }
}
