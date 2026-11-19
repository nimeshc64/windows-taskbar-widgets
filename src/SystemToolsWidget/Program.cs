using System.Diagnostics;
using BtBattery;
using Microsoft.Win32;

namespace SysTools;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--status", StringComparer.OrdinalIgnoreCase))
        {
            Native.AttachConsole(-1);
            SynchronizationContext.SetSynchronizationContext(null);
            var s = new ToolsService();
            s.ReadRecycleBinAsync().GetAwaiter().GetResult();
            s.ReadUpdatesAsync().GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine($"Recycle bin: {ToolsService.Size(s.BinBytes)} in {s.BinItems} items");
            Console.WriteLine($"Hidden files={s.HiddenFiles} extensions={s.FileExtensions} dark={s.DarkMode}");
            Console.WriteLine($"Updates: reboot={s.RebootPending} pending={s.PendingUpdates} lastChecked={s.LastChecked} lastInstalled={s.LastInstalled} paused={s.PausedUntil}");
            foreach (var t in s.PendingTitles) Console.WriteLine("  " + t);
            var layout = ToolsService.CaptureLayout();
            Console.WriteLine($"Layout now: {layout.Name} — {layout.Windows.Count} windows on {layout.Monitors} monitors");
            foreach (var w in layout.Windows) Console.WriteLine($"  {w.Process,-20} show={w.ShowCmd} [{w.L},{w.T},{w.R},{w.B}] {(w.Title.Length > 50 ? w.Title[..50] : w.Title)}");
            return;
        }

        using var mutex = new Mutex(true, "SystemToolsWidget.SingleInstance", out bool first);
        if (!first) return;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ToolsService.Log("UI error: " + e.Exception);
        ApplicationConfiguration.Initialize();
        Application.Run(new ToolsTrayApp());
    }
}

sealed class ToolsTrayApp : ApplicationContext
{
    readonly ToolsSettings _settings = AppFiles.Load<ToolsSettings>(ToolsService.AppName);
    readonly ToolsService _svc = new();
    readonly ToolsFlyout _flyout;
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 30_000 };
    ToolsWidget? _widget;
    IntPtr _hIcon;
    DateTime _lastUpdateCheck;
    string? _balloonPath;
    bool _capturing;

    public ToolsTrayApp()
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _flyout = new ToolsFlyout(_svc, _settings, this);

        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true, Text = "System tools" };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFlyout(null); };
        _icon.BalloonTipClicked += (_, _) => { if (_balloonPath != null) ToolsService.Open(_balloonPath); };
        RenderIcon();

        if (_settings.ShowWidget) SetWidget(true);
        _timer.Tick += (_, _) => RefreshAll(false);
        _timer.Start();
        SystemEvents.UserPreferenceChanged += OnThemeChanged;
        RefreshAll(true);
    }

    public async void RefreshAll(bool updates)
    {
        await _svc.ReadRecycleBinAsync();
        _svc.ReadUpdateRegistry();
        if (updates || (DateTime.Now - _lastUpdateCheck).TotalHours >= 2)
        {
            _lastUpdateCheck = DateTime.Now;
            Changed();
            await _svc.ReadUpdatesAsync();
        }
        Changed();
    }

    void Changed()
    {
        _widget?.UpdateView();
        _flyout.RefreshLayout();
        RenderIcon();
        var t = "System tools" + (_svc.RebootPending ? " · restart required" : _svc.PendingUpdates > 0 ? $" · {_svc.PendingUpdates} updates" : "")
              + (_svc.BinItems > 0 ? $" · bin {ToolsService.Size(_svc.BinBytes)}" : "");
        _icon.Text = t.Length <= 127 ? t : t[..127];
    }

    public async void CaptureRegion()
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            _flyout.HideFlyout();
            await Task.Delay(250);
            using var picker = new RegionPicker();
            if (picker.ShowDialog() != DialogResult.OK || picker.Result == null) return;
            using var bmp = picker.Result;
            Saved(ScreenCapture.SaveAndCopy(bmp), bmp.Size);
        }
        catch (Exception ex) { ToolsService.Log("Capture: " + ex); }
        finally { _capturing = false; }
    }

    public async void CaptureFullScreen()
    {
        if (_capturing) return;
        _capturing = true;
        try
        {
            var screen = Screen.FromPoint(Cursor.Position);
            _flyout.HideFlyout();
            await Task.Delay(300);
            using var bmp = ScreenCapture.Grab(screen.Bounds);
            Saved(ScreenCapture.SaveAndCopy(bmp), bmp.Size);
        }
        catch (Exception ex) { ToolsService.Log("Capture: " + ex); }
        finally { _capturing = false; }
    }

    void Saved(string path, Size size)
    {
        _settings.LastCapture = path;
        _settings.Save();
        _balloonPath = path;
        _icon.ShowBalloonTip(4000, "Screenshot copied", $"{size.Width} × {size.Height} saved to Pictures\\Screenshots — click to open", ToolTipIcon.Info);
        _flyout.SetStatus("Screenshot saved and copied");
    }

    public void Record()
    {
        _flyout.HideFlyout();
        try { Process.Start(new ProcessStartInfo("ms-screenclip://capture/video") { UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("ms-screensketch:") { UseShellExecute = true }); }
            catch (Exception ex) { ToolsService.Log("Record: " + ex.Message); }
        }
    }

    public void CopyImage(string path)
    {
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var img = Image.FromStream(ms);
            Clipboard.SetImage(img);
            _flyout.SetStatus("Image copied");
        }
        catch (Exception ex) { _flyout.SetStatus("Couldn't copy: " + ex.Message); }
    }

    public void OpenScreenshots()
    {
        Directory.CreateDirectory(ScreenCapture.Folder);
        Open(ScreenCapture.Folder);
    }

    public void SaveLayout()
    {
        var layout = ToolsService.CaptureLayout();
        if (layout.Windows.Count == 0) { _flyout.SetStatus("No windows to save"); return; }
        _settings.Layouts.Insert(0, layout);
        while (_settings.Layouts.Count > 6) _settings.Layouts.RemoveAt(_settings.Layouts.Count - 1);
        _settings.Save();
        _flyout.SetStatus($"Saved {layout.Windows.Count} windows");
        _flyout.RefreshLayout();
        _widget?.UpdateView();
    }

    public void RestoreLayout(WindowLayout layout)
    {
        _flyout.HideFlyout();
        var (ok, total) = ToolsService.Restore(layout);
        _flyout.SetStatus(ok == total ? $"Restored {ok} windows" : $"Restored {ok} of {total} windows (others aren't open)");
        if (ok < total)
            _icon.ShowBalloonTip(4000, "Layout restored", $"{ok} of {total} windows moved — open the missing apps and restore again", ToolTipIcon.Info);
    }

    public void DeleteLayout(WindowLayout layout)
    {
        _settings.Layouts.Remove(layout);
        _settings.Save();
        _flyout.RefreshLayout();
    }

    public void Apply(Action action, string message)
    {
        try { action(); _flyout.SetStatus(message); }
        catch (Exception ex) { _flyout.SetStatus("Couldn't change setting"); ToolsService.Log("Setting: " + ex.Message); }
        _flyout.RefreshLayout();
    }

    public async void EmptyBin()
    {
        _flyout.SetStatus("Emptying recycle bin…");
        long freed = _svc.BinBytes;
        var err = await _svc.EmptyRecycleBinAsync();
        _flyout.SetStatus(err == null ? $"Freed {ToolsService.Size(freed)}" : "Couldn't empty: " + err);
        Changed();
    }

    public void RestartPc()
    {
        try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true }); }
        catch (Exception ex) { _flyout.SetStatus("Couldn't restart: " + ex.Message); }
    }

    public void Open(string target)
    {
        _flyout.HideFlyout();
        ToolsService.Open(target);
    }

    void RenderIcon()
    {
        using var bmp = IconRenderer.Glyph(ToolGlyphs.Tools, Theme.TaskbarDark, _svc.RebootPending);
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
        RefreshAll((DateTime.Now - _lastUpdateCheck).TotalMinutes >= 10);
    }

    void SetWidget(bool on)
    {
        if (on)
        {
            if (_widget == null)
            {
                _widget = new ToolsWidget(_svc, _settings, _menu, CaptureRegion);
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
        var open = new ToolStripMenuItem("Open system tools", null, (_, _) => ShowFlyout(null));
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);
        _menu.Items.Add("Screenshot an area", null, (_, _) => CaptureRegion());
        _menu.Items.Add("Screenshot full screen", null, (_, _) => CaptureFullScreen());
        _menu.Items.Add("Save window layout", null, (_, _) => SaveLayout());
        if (_settings.Layouts.Count > 0)
        {
            var restore = new ToolStripMenuItem("Restore window layout");
            foreach (var l in _settings.Layouts) restore.DropDownItems.Add(l.Name, null, (_, _) => RestoreLayout(l));
            _menu.Items.Add(restore);
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Toggle("Show taskbar widget", _settings.ShowWidget, v => { _settings.ShowWidget = v; SetWidget(v); }));
        _menu.Items.Add(Toggle("Show recycle bin in widget", _settings.ShowBinInWidget, v => { _settings.ShowBinInWidget = v; _widget?.UpdateView(); }));
        _menu.Items.Add(Toggle("Show update badge in widget", _settings.ShowUpdatesInWidget, v => { _settings.ShowUpdatesInWidget = v; _widget?.UpdateView(); }));
        _menu.Items.Add(Toggle("Start with Windows", AppFiles.StartupEnabled(ToolsService.AppName), v => AppFiles.SetStartup(ToolsService.AppName, v)));
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
