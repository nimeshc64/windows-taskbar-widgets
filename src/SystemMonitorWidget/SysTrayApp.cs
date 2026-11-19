using BtBattery;
using Microsoft.Win32;

namespace SysMon;

sealed class SysTrayApp : ApplicationContext
{
    readonly SmSettings _settings = SmSettings.Load();
    readonly SystemSampler _sampler = new();
    readonly SysFlyout _flyout;
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    SysWidget? _widget;
    IntPtr _hIcon;

    public SysTrayApp()
    {
        _sampler.Sample();
        _flyout = new SysFlyout(_sampler, _settings);

        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();

        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true, Text = "System Monitor" };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFlyout(null); };
        RenderIcon();

        if (_settings.ShowWidget) SetWidget(true);

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        SystemEvents.UserPreferenceChanged += OnThemeChanged;
    }

    void Tick()
    {
        try { _sampler.Sample(); }
        catch (Exception ex) { SmLog.Write("Sample: " + ex.Message); }
        _widget?.UpdateView();
        _flyout.UpdateView();

        var text = $"System Monitor\nCPU {Math.Round(_sampler.Cpu)}% · Memory {Math.Round(_sampler.MemoryLoad)}%\n" +
                   $"↓ {SystemSampler.Speed(_sampler.Down, _settings.Bits)}  ↑ {SystemSampler.Speed(_sampler.Up, _settings.Bits)}";
        _icon.Text = text.Length <= 127 ? text : text[..127];
    }

    void RenderIcon()
    {
        using var bmp = IconRenderer.Glyph(SmGlyphs.Cpu, Theme.TaskbarDark, false);
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
    }

    void SetWidget(bool on)
    {
        if (on)
        {
            if (_widget == null)
            {
                _widget = new SysWidget(_sampler, _settings, _menu);
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
        bool dark = Theme.AppsDark;
        var renderer = new MenuRenderer(dark);
        ToolStripManager.Renderer = renderer;
        _menu.Renderer = renderer;
        _menu.Items.Clear();

        var open = new ToolStripMenuItem("Open system panel", null, (_, _) => ShowFlyout(null));
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Toggle("Show taskbar widget", _settings.ShowWidget, v => { _settings.ShowWidget = v; SetWidget(v); }));

        var parts = new ToolStripMenuItem("Show in widget");
        parts.DropDownItems.Add(Toggle("CPU", _settings.ShowCpu, v => { _settings.ShowCpu = v; _widget?.UpdateView(); }));
        parts.DropDownItems.Add(Toggle("Memory", _settings.ShowMemory, v => { _settings.ShowMemory = v; _widget?.UpdateView(); }));
        parts.DropDownItems.Add(Toggle("Network speed", _settings.ShowNetwork, v => { _settings.ShowNetwork = v; _widget?.UpdateView(); }));
        _menu.Items.Add(parts);

        var units = new ToolStripMenuItem("Network units");
        units.DropDownItems.Add(Toggle("Bytes (KB/s, MB/s)", !_settings.Bits, _ => { _settings.Bits = false; _widget?.UpdateView(); }));
        units.DropDownItems.Add(Toggle("Bits (Kbps, Mbps)", _settings.Bits, _ => { _settings.Bits = true; _widget?.UpdateView(); }));
        _menu.Items.Add(units);

        _menu.Items.Add(Toggle("Start with Windows", SmStartup.IsEnabled, SmStartup.Set, save: false));
        if (_widget != null) _menu.Items.Add("Reset widget position", null, (_, _) => _widget?.ResetPosition());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Task Manager…", null, (_, _) => SysFlyout.Open("taskmgr.exe"));
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

    ToolStripMenuItem Toggle(string text, bool on, Action<bool> set, bool save = true) =>
        new(text, null, (_, _) => { set(!on); if (save) _settings.Save(); }) { Checked = on };

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
