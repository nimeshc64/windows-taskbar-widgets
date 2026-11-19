using AudioCtl.Interop;
using BtBattery;
using Microsoft.Win32;

namespace AudioCtl;

sealed class SoundTrayApp : ApplicationContext
{
    readonly SoundSettings _settings = SoundSettings.Load();
    readonly SoundService _svc;
    readonly SoundFlyout _flyout;
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu = new();
    SoundTaskbarWidget? _widget;
    IntPtr _hIcon;
    string _iconKey = "";

    public SoundTrayApp()
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _svc = new SoundService();
        _flyout = new SoundFlyout(_svc, _settings);

        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();

        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFlyout(null); };

        _svc.VolumeChanged += OnVolumeChanged;
        _svc.DevicesChanged += () => { _flyout.ReloadDevices(); OnVolumeChanged(); };

        if (_settings.ShowWidget) SetWidget(true);
        OnVolumeChanged();

        SystemEvents.UserPreferenceChanged += OnThemeChanged;
    }

    void OnVolumeChanged()
    {
        if (_flyout.Visible) _flyout.Invalidate();
        _widget?.UpdateView();
        UpdateTrayIcon();
    }

    void UpdateTrayIcon()
    {
        var o = _svc.Output;
        string glyph = o.Available ? SoundGlyphs.Volume(o.Volume, o.Muted) : "";
        bool dark = Theme.TaskbarDark;
        string key = $"{glyph}|{dark}|{IconRenderer.TraySize}";
        if (key != _iconKey)
        {
            using var bmp = IconRenderer.Glyph(glyph, dark, false);
            var h = bmp.GetHicon();
            _icon.Icon = Icon.FromHandle(h);
            if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
            _hIcon = h;
            _iconKey = key;
        }
        var lines = new List<string> { "Audio Control" };
        if (o.Available) lines.Add($"{o.DeviceName}: {(o.Muted ? "muted" : o.Percent + "%")}");
        var i = _svc.Input;
        if (i.Available) lines.Add($"{i.DeviceName}: {(i.Muted ? "muted" : i.Percent + "%")}");
        var text = string.Join("\n", lines);
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
                _widget = new SoundTaskbarWidget(_svc, _settings, _menu);
                _widget.Clicked += () => ShowFlyout(_widget.Bounds);
            }
            _widget.UpdateView();
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

        var open = new ToolStripMenuItem("Open sound panel", null, (_, _) => ShowFlyout(null));
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);
        _menu.Items.Add(DeviceMenu("Output device", EDataFlow.Render));
        _menu.Items.Add(DeviceMenu("Input device", EDataFlow.Capture));
        _menu.Items.Add(Toggle("Mute speakers", _svc.Output.Muted, v => _svc.Output.Muted = v));
        _menu.Items.Add(Toggle("Mute microphone", _svc.Input.Muted, v => _svc.Input.Muted = v));
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(Toggle("Show taskbar widget", _settings.ShowWidget, v => { _settings.ShowWidget = v; _settings.Save(); SetWidget(v); }));
        _menu.Items.Add(Toggle("Show microphone in widget", _settings.ShowMicInWidget,
            v => { _settings.ShowMicInWidget = v; _settings.Save(); _widget?.UpdateView(); }));
        var step = new ToolStripMenuItem("Scroll step");
        foreach (var s in new[] { 1, 2, 5, 10 })
            step.DropDownItems.Add(new ToolStripMenuItem($"{s}%", null, (_, _) => { _settings.ScrollStep = s; _settings.Save(); })
                { Checked = _settings.ScrollStep == s });
        _menu.Items.Add(step);
        _menu.Items.Add(Toggle("Start with Windows", SoundStartup.IsEnabled, SoundStartup.Set));
        if (_widget != null) _menu.Items.Add("Reset widget position", null, (_, _) => _widget?.ResetPosition());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Sound settings…", null, (_, _) => SoundFlyout.Open("ms-settings:sound"));
        _menu.Items.Add("Volume mixer…", null, (_, _) => SoundFlyout.Open("ms-settings:apps-volume"));
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

    ToolStripMenuItem DeviceMenu(string text, EDataFlow flow)
    {
        var menu = new ToolStripMenuItem(text);
        foreach (var d in _svc.GetDevices(flow))
        {
            var id = d.Id;
            menu.DropDownItems.Add(new ToolStripMenuItem($"{d.Name} ({d.Interface})", null, (_, _) => _svc.SetDefault(id))
                { Checked = d.IsDefault });
        }
        if (menu.DropDownItems.Count == 0) menu.DropDownItems.Add(new ToolStripMenuItem("No devices") { Enabled = false });
        return menu;
    }

    static ToolStripMenuItem Toggle(string text, bool on, Action<bool> set) =>
        new(text, null, (_, _) => set(!on)) { Checked = on };

    void OnThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (!_flyout.IsHandleCreated) _ = _flyout.Handle;
        _flyout.BeginInvoke(() => { _iconKey = ""; UpdateTrayIcon(); _widget?.ApplyTheme(); _flyout.ApplyTheme(); });
    }

    void ExitApp()
    {
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
        _widget?.Close();
        _flyout.Close();
        _svc.Dispose();
        ExitThread();
    }
}
