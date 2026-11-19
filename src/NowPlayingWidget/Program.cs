using BtBattery;
using Microsoft.Win32;

namespace NowPlaying;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "NowPlayingWidget.SingleInstance", out bool first);
        if (!first) return;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppFiles.Log(MediaService.AppName, "UI error: " + e.Exception);
        ApplicationConfiguration.Initialize();
        Application.Run(new NpTrayApp());
    }
}

sealed class NpTrayApp : ApplicationContext
{
    readonly NpSettings _settings = AppFiles.Load<NpSettings>(MediaService.AppName);
    readonly MediaService _media;
    readonly NpFlyout _flyout;
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _progress = new() { Interval = 1000 };
    NpWidget? _widget;
    IntPtr _hIcon;
    string _iconKey = "";

    public NpTrayApp()
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _media = new MediaService();
        _flyout = new NpFlyout(_media);

        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true, Text = "Now Playing" };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowFlyout(null);
            else if (e.Button == MouseButtons.Middle) _media.PlayPause();
        };

        _media.Changed += OnMediaChanged;
        if (_settings.ShowWidget) SetWidget(true);
        UpdateIcon();

        _progress.Tick += (_, _) => { if (_media.IsPlaying) _widget?.Invalidate(); };
        _progress.Start();
        SystemEvents.UserPreferenceChanged += OnThemeChanged;

        _ = StartAsync();
    }

    async Task StartAsync()
    {
        try { await _media.StartAsync(); }
        catch (Exception ex) { AppFiles.Log(MediaService.AppName, "Media start: " + ex); }
    }

    void OnMediaChanged()
    {
        ApplyIdleVisibility();
        _widget?.UpdateView();
        _flyout.RefreshLayout();
        UpdateIcon();
    }

    void ApplyIdleVisibility()
    {
        if (_widget == null) return;
        bool show = !_settings.HideWhenIdle || _media.HasSession;
        if (show && !_widget.Visible) _widget.ShowStrip();
        else if (!show && _widget.Visible) _widget.HideStrip();
    }

    void UpdateIcon()
    {
        string glyph = _media.IsPlaying ? "" : "";
        string key = glyph + Theme.TaskbarDark + IconRenderer.TraySize;
        if (key != _iconKey)
        {
            using var bmp = IconRenderer.Glyph(glyph, Theme.TaskbarDark, false);
            var h = bmp.GetHicon();
            _icon.Icon = Icon.FromHandle(h);
            if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
            _hIcon = h;
            _iconKey = key;
        }
        var text = _media.HasSession
            ? $"{(_media.IsPlaying ? "▶" : "❚❚")} {_media.Title}\n{_media.Artist}\n{_media.App}"
            : "Now Playing\nNothing is playing";
        _icon.Text = text.Length <= 127 ? text : text[..124] + "...";
    }

    void ShowFlyout(Rectangle? anchor)
    {
        if (_flyout.Visible) { _flyout.HideFlyout(); return; }
        if ((DateTime.Now - _flyout.LastHidden).TotalMilliseconds < 300) return;
        _flyout.ShowAt(anchor);
    }

    void SetWidget(bool on)
    {
        if (on)
        {
            if (_widget == null)
            {
                _widget = new NpWidget(_media, _settings, _menu);
                _widget.Clicked += () => ShowFlyout(_widget.Bounds);
            }
            _widget.ShowStrip();
            ApplyIdleVisibility();
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
        var open = new ToolStripMenuItem("Open player", null, (_, _) => ShowFlyout(null));
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);
        _menu.Items.Add(new ToolStripMenuItem(_media.IsPlaying ? "Pause" : "Play", null, (_, _) => _media.PlayPause()) { Enabled = _media.CanPlayPause });
        _menu.Items.Add(new ToolStripMenuItem("Next track", null, (_, _) => _media.Next()) { Enabled = _media.CanNext });
        _menu.Items.Add(new ToolStripMenuItem("Previous track", null, (_, _) => _media.Previous()) { Enabled = _media.CanPrevious });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Toggle("Show taskbar widget", _settings.ShowWidget, v => { _settings.ShowWidget = v; SetWidget(v); }));
        _menu.Items.Add(Toggle("Show playback buttons", _settings.ShowControls, v => { _settings.ShowControls = v; _widget?.UpdateView(); }));
        _menu.Items.Add(Toggle("Hide widget when nothing plays", _settings.HideWhenIdle, v => { _settings.HideWhenIdle = v; ApplyIdleVisibility(); }));
        var width = new ToolStripMenuItem("Title width");
        foreach (var (label, w) in new[] { ("Narrow", 120), ("Normal", 170), ("Wide", 240) })
            width.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => { _settings.MaxTextWidth = w; _settings.Save(); _widget?.UpdateView(); })
                { Checked = _settings.MaxTextWidth == w });
        _menu.Items.Add(width);
        _menu.Items.Add(Toggle("Start with Windows", AppFiles.StartupEnabled(MediaService.AppName),
            v => AppFiles.SetStartup(MediaService.AppName, v)));
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
        _flyout.BeginInvoke(() => { _iconKey = ""; UpdateIcon(); _widget?.ApplyTheme(); _flyout.ApplyTheme(); });
    }

    void ExitApp()
    {
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        _progress.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
        _widget?.Close();
        _flyout.Close();
        ExitThread();
    }
}
