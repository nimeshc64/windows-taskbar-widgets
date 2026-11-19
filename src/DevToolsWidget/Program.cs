using System.Diagnostics;
using BtBattery;
using Microsoft.Win32;

namespace DevTools;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--status", StringComparer.OrdinalIgnoreCase))
        {
            Native.AttachConsole(-1);
            SynchronizationContext.SetSynchronizationContext(null);
            var svc = new DevService();
            svc.RefreshAsync(true).GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine($"SDK: {svc.Sdk}  adb: {svc.Adb}  emulator: {svc.Emulator}  adb server: {svc.AdbServerRunning}");
            foreach (var d in svc.Devices)
                Console.WriteLine($"  device {d.Serial} [{d.State}] {d.DisplayName} Android {d.Version} {d.Transport} battery={d.Battery}");
            Console.WriteLine("  AVDs: " + string.Join(", ", svc.Avds));
            foreach (var p in svc.Ports) Console.WriteLine($"  :{p.Port,-6} {p.Process} (PID {p.Pid}) {p.Address} {p.Label}");
            return;
        }

        using var mutex = new Mutex(true, "DevToolsWidget.SingleInstance", out bool first);
        if (!first) return;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppFiles.Log(DevService.AppName, "UI error: " + e.Exception);
        ApplicationConfiguration.Initialize();
        Application.Run(new DevTrayApp());
    }
}

sealed class DevTrayApp : ApplicationContext
{
    readonly DevSettings _settings = AppFiles.Load<DevSettings>(DevService.AppName);
    readonly DevService _svc = new();
    readonly DevFlyout _flyout;
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 4000 };
    DevWidget? _widget;
    IntPtr _hIcon;
    HashSet<string>? _knownOnline;
    string? _balloonPath;
    DateTime _lastFull;

    public DevTrayApp()
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _flyout = new DevFlyout(_svc, this);

        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true, Text = "Developer tools" };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFlyout(null); };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonPath != null) Open(_balloonPath);
            else ShowFlyout(null);
        };
        RenderIcon();

        if (_settings.ShowWidget) SetWidget(true);
        _timer.Tick += (_, _) => RefreshNow((DateTime.Now - _lastFull).TotalSeconds > 60);
        _timer.Start();
        SystemEvents.UserPreferenceChanged += OnThemeChanged;
        RefreshNow(true);
    }

    public List<PortInfo> VisiblePorts =>
        _settings.ShowHighPorts ? _svc.Ports : _svc.Ports.Where(p => p.Port < 49152).ToList();

    public async void RefreshNow(bool full)
    {
        if (full) _lastFull = DateTime.Now;
        await _svc.RefreshAsync(full);
        NotifyDeviceChanges();
        _widget?.UpdateView();
        _flyout.RefreshLayout();
        var online = _svc.Devices.Count(d => d.Online);
        var text = $"Developer tools\n{online} Android device(s) · {VisiblePorts.Count} listening ports";
        _icon.Text = text.Length <= 127 ? text : text[..127];
    }

    void NotifyDeviceChanges()
    {
        var now = _svc.Devices.Where(d => d.Online).ToDictionary(d => d.Serial, d => d.DisplayName);
        if (_knownOnline != null && _settings.NotifyDevices)
        {
            foreach (var (serial, name) in now.Where(kv => !_knownOnline.Contains(kv.Key)))
                Balloon($"{name} connected", serial, null);
            foreach (var serial in _knownOnline.Where(s => !now.ContainsKey(s)))
                Balloon("Android device disconnected", serial, null);
        }
        _knownOnline = now.Keys.ToHashSet();
    }

    void Balloon(string title, string text, string? openPath, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _balloonPath = openPath;
        _icon.ShowBalloonTip(5000, title, text, icon);
    }

    public async void Screenshot(AndroidDevice d)
    {
        _flyout.SetStatus($"Capturing {d.DisplayName}…");
        var path = await _svc.ScreenshotAsync(d);
        if (path == null)
        {
            _flyout.SetStatus("Screenshot failed");
            Balloon("Screenshot failed", d.DisplayName, null, ToolTipIcon.Warning);
            return;
        }
        try
        {
            using var ms = new MemoryStream(await File.ReadAllBytesAsync(path));
            using var img = Image.FromStream(ms);
            Clipboard.SetImage(img);
        }
        catch (Exception ex) { AppFiles.Log(DevService.AppName, "Clipboard: " + ex.Message); }
        _flyout.SetStatus("Screenshot saved and copied");
        Balloon("Screenshot saved and copied", Path.GetFileName(path) + " — click to open", path);
    }

    public async void InstallApk(AndroidDevice d)
    {
        _flyout.HideFlyout();
        using var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title = $"Install APK on {d.DisplayName}",
            Filter = "Android packages (*.apk)|*.apk",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var file = Path.GetFileName(dlg.FileName);
        Balloon($"Installing {file}…", d.DisplayName, null);
        var result = await _svc.InstallAsync(d, dlg.FileName);
        bool ok = result == "Success";
        Balloon(ok ? $"Installed {file}" : "Install failed", ok ? d.DisplayName : result, null, ok ? ToolTipIcon.Info : ToolTipIcon.Error);
        _flyout.SetStatus(ok ? $"Installed {file}" : "Install failed: " + result);
    }

    public void OpenScreenshots()
    {
        Directory.CreateDirectory(DevService.ScreenshotFolder);
        Open(DevService.ScreenshotFolder);
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { AppFiles.Log(DevService.AppName, "Open: " + ex.Message); }
    }

    void RenderIcon()
    {
        using var bmp = IconRenderer.Glyph(DevGlyphs.Tools, Theme.TaskbarDark, false);
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
        RefreshNow(true);
    }

    void SetWidget(bool on)
    {
        if (on)
        {
            if (_widget == null)
            {
                _widget = new DevWidget(_svc, _settings, _menu);
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
        var open = new ToolStripMenuItem("Open developer tools", null, (_, _) => ShowFlyout(null));
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);
        _menu.Items.Add("Refresh", null, (_, _) => RefreshNow(true));
        if (_svc.Adb != null && !_svc.AdbServerRunning)
            _menu.Items.Add("Start ADB server", null, async (_, _) => { await _svc.StartAdbServerAsync(); RefreshNow(false); });
        _menu.Items.Add("Open screenshots folder", null, (_, _) => OpenScreenshots());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Toggle("Show taskbar widget", _settings.ShowWidget, v => { _settings.ShowWidget = v; SetWidget(v); }));
        _menu.Items.Add(Toggle("Show port count in widget", _settings.ShowPortsInWidget, v => { _settings.ShowPortsInWidget = v; _widget?.UpdateView(); }));
        _menu.Items.Add(Toggle("Show high ports (49152+)", _settings.ShowHighPorts, v => { _settings.ShowHighPorts = v; _widget?.UpdateView(); _flyout.RefreshLayout(); }));
        _menu.Items.Add(Toggle("Notify when devices connect", _settings.NotifyDevices, v => _settings.NotifyDevices = v));
        _menu.Items.Add(Toggle("Start with Windows", AppFiles.StartupEnabled(DevService.AppName), v => AppFiles.SetStartup(DevService.AppName, v)));
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
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
        _widget?.Close();
        _flyout.Close();
        ExitThread();
    }
}
