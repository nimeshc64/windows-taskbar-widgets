using System.Diagnostics;
using System.Media;
using BtBattery;
using Microsoft.Win32;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace MacNotify;

public sealed class NotifySettings
{
    public bool Enabled { get; set; } = true;
    public int Seconds { get; set; } = 6;
    public bool Sound { get; set; } = true;
    public string Position { get; set; } = "TopRight";
    public bool ShownDndTip { get; set; }
    public List<string> MutedApps { get; set; } = new();
    public Dictionary<string, string> AppSounds { get; set; } = new();
    public void Save() => AppFiles.Save(NotifyApp.AppName, this);
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--sessions"))
        {
            BtBattery.Native.AttachConsole(-1);
            Console.WriteLine();
            foreach (var pid in AppSounds.ActiveAudioProcesses())
            {
                string n; try { n = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { n = "?"; }
                Console.WriteLine($"  active audio: {n} ({pid})");
            }
            Console.WriteLine("done");
            return;
        }
        using var mutex = new Mutex(true, "NotificationsWidget.SingleInstance", out bool first);
        if (!first) return;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => NotifyApp.Log("UI error: " + e.Exception);
        ApplicationConfiguration.Initialize();
        Application.Run(new NotifyApp(args.Contains("--test")));
    }
}

sealed class NotifyApp : ApplicationContext
{
    public const string AppName = "NotificationsWidget";
    public static void Log(string m) => AppFiles.Log(AppName, m);
    const int MaxVisible = 4;

    readonly NotifySettings _settings = AppFiles.Load<NotifySettings>(AppName);
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _poll = new() { Interval = 1000 };
    readonly List<Banner> _banners = new();
    readonly Dictionary<string, Image?> _icons = new();
    readonly Control _ui = new();
    HashSet<uint>? _seen;
    UserNotificationListener? _listener;
    bool _polling;
    IntPtr _hIcon;

    public NotifyApp(bool test)
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ = _ui.Handle;
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true, Text = "Mac-style notifications" };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowTest(); };
        RenderIcon();
        SystemEvents.UserPreferenceChanged += (_, _) => _ui.BeginInvoke(() => { RenderIcon(); foreach (var b in _banners) b.Invalidate(); });
        _poll.Tick += async (_, _) => await PollAsync();
        StartListener(test);
    }

    async void StartListener(bool test)
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Show(new NoteInfo(0, "Notifications", null, "Notification access is off",
                    "Allow apps to access notifications in Settings → Privacy → Notifications, then restart this app.", null, DateTime.Now));
                return;
            }
            await PollAsync();
            _poll.Start();
            if (!_settings.ShownDndTip)
            {
                _settings.ShownDndTip = true;
                _settings.Save();
                Show(new NoteInfo(0, "Notifications", "dnd", "Mac-style notifications are on",
                    "Turn on Do not disturb so Windows' own pop-ups stay hidden — click to open notification settings.", null, DateTime.Now));
            }
            if (test) ShowTest();
        }
        catch (Exception ex) { Log("Listener: " + ex); }
    }

    async Task PollAsync()
    {
        if (_polling || _listener == null) return;
        _polling = true;
        try
        {
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var ids = list.Select(n => n.Id).ToHashSet();
            if (_seen == null) { _seen = ids; return; }
            var fresh = list.Where(n => !_seen.Contains(n.Id)).OrderBy(n => n.CreationTime).ToList();
            _seen = ids;
            if (!_settings.Enabled) return;
            foreach (var n in fresh.TakeLast(MaxVisible))
            {
                var info = await ToInfoAsync(n);
                if (info == null) continue;
                _seenApps.Add(info.App);
                if (_settings.MutedApps.Contains(info.App)) continue;
                Show(info);
            }
        }
        catch (Exception ex) { Log("Poll: " + ex.Message); }
        finally { _polling = false; }
    }

    async Task<NoteInfo?> ToInfoAsync(UserNotification n)
    {
        string app = "Notification", aumid = "";
        try { app = n.AppInfo.DisplayInfo.DisplayName; aumid = n.AppInfo.AppUserModelId; } catch { }
        var texts = new List<string>();
        try
        {
            var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding != null) texts = binding.GetTextElements().Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList();
        }
        catch { }
        if (texts.Count == 0) return null;
        return new NoteInfo(n.Id, app, aumid, texts[0], string.Join("\n", texts.Skip(1)), await IconAsync(n, aumid),
            n.CreationTime.LocalDateTime);
    }

    async Task<Image?> IconAsync(UserNotification n, string key)
    {
        if (_icons.TryGetValue(key, out var cached)) return cached;
        Image? img = null;
        try
        {
            var logo = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            using var stream = await logo.OpenReadAsync();
            using var s = stream.AsStream();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            ms.Position = 0;
            img = new Bitmap(Image.FromStream(ms));
        }
        catch { }
        _icons[key] = img;
        return img;
    }

    void Show(NoteInfo note)
    {
        var b = new Banner(note, Math.Max(3, _settings.Seconds) * 1000);
        b.Dismissed += x => { _banners.Remove(x); Restack(); };
        b.Activated2 += x => Open(x.Note);
        _banners.Insert(0, b);
        while (_banners.Count > MaxVisible) _banners[^1].Dismiss();
        Restack(b);
        if (_settings.Sound && note.Id > 1)
        {
            var app = note.App;
            var choice = _settings.AppSounds.GetValueOrDefault(app);
            Task.Delay(600).ContinueWith(_ => AppSounds.Play(app, choice));
        }
        else if (_settings.Sound && note.Id == 1) AppSounds.Preview(null, "Notifications");
    }

    void Restack(Banner? fresh = null)
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        var pos = _settings.Position;
        bool bottom = pos.StartsWith("Bottom");
        int side = pos.EndsWith("Right") ? 1 : pos.EndsWith("Left") ? -1 : 0;
        int gap = Scale(8), margin = Scale(10);
        int y = bottom ? wa.Bottom - margin : wa.Top + margin;
        foreach (var b in _banners.ToList())
        {
            int x = side > 0 ? wa.Right - b.Width - margin : side < 0 ? wa.Left + margin : wa.Left + (wa.Width - b.Width) / 2;
            int top = bottom ? y - b.Height : y;
            b.Side = side;
            b.FromBottom = bottom;
            b.MoveTo(x, top, b == fresh);
            y = bottom ? top - gap : top + b.Height + gap;
        }
    }

    int Scale(int v) => (int)(v * (_ui.DeviceDpi / 96f));

    void Open(NoteInfo note)
    {
        try
        {
            if (note.Aumid == "dnd") { Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }); return; }
            if (!string.IsNullOrEmpty(note.Aumid))
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + note.Aumid) { UseShellExecute = false });
        }
        catch (Exception ex) { Log("Open: " + ex.Message); }
    }

    void ShowTest() => Show(new NoteInfo(1, "Notifications", null, "This is how notifications look",
        "New notifications from any app appear here. Hover to keep one on screen, click × to close.", null, DateTime.Now));

    void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem("Show Mac-style banners", null, (_, _) => { _settings.Enabled = !_settings.Enabled; _settings.Save(); })
            { Checked = _settings.Enabled });
        _menu.Items.Add("Show a test notification", null, (_, _) => ShowTest());
        var dur = new ToolStripMenuItem("Keep on screen");
        foreach (var s in new[] { 4, 6, 10, 15 })
        {
            var sec = s;
            dur.DropDownItems.Add(new ToolStripMenuItem($"{s} seconds", null, (_, _) => { _settings.Seconds = sec; _settings.Save(); }) { Checked = _settings.Seconds == s });
        }
        _menu.Items.Add(dur);
        var place = new ToolStripMenuItem("Position");
        foreach (var (key, label) in new[] { ("TopRight", "Top right (like Mac)"), ("TopCenter", "Top centre"), ("TopLeft", "Top left"),
                                             ("BottomRight", "Bottom right (like Windows)"), ("BottomLeft", "Bottom left") })
        {
            var k = key;
            place.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) =>
            {
                _settings.Position = k; _settings.Save();
                Restack();
                ShowTest();
            }) { Checked = _settings.Position == k });
        }
        _menu.Items.Add(place);
        _menu.Items.Add(SoundMenu());
        var apps = _seenApps.Count > 0 || _settings.MutedApps.Count > 0 ? new ToolStripMenuItem("Don't show banners for") : null;
        if (apps != null)
        {
            var names = _banners.Select(b => b.Note.App).Concat(_settings.MutedApps).Concat(_seenApps).Distinct().OrderBy(n => n);
            foreach (var n in names)
            {
                var name = n;
                apps.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
                {
                    if (!_settings.MutedApps.Remove(name)) _settings.MutedApps.Add(name);
                    _settings.Save();
                }) { Checked = _settings.MutedApps.Contains(name) });
            }
            if (apps.DropDownItems.Count > 0) _menu.Items.Add(apps);
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Hide Windows' own pop-ups (Do not disturb)…", null, (_, _) =>
            Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }));
        _menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => AppFiles.SetStartup(AppName, !AppFiles.StartupEnabled(AppName)))
            { Checked = AppFiles.StartupEnabled(AppName) });
        _menu.Items.Add(new ToolStripSeparator());
        Credit.AddToMenu(_menu);
        _menu.Items.Add(new ToolStripSeparator());        _menu.Items.Add("Exit", null, (_, _) => { _icon.Visible = false; _icon.Dispose(); ExitThread(); });
        AppFiles.ThemeMenu(_menu);
    }

    readonly HashSet<string> _seenApps = new();

    ToolStripMenuItem SoundMenu()
    {
        var m = new ToolStripMenuItem("Sounds");
        m.DropDownItems.Add(new ToolStripMenuItem("Play sounds", null, (_, _) => { _settings.Sound = !_settings.Sound; _settings.Save(); })
            { Checked = _settings.Sound });
        var apps = _seenApps.Concat(_settings.AppSounds.Keys).Distinct().OrderBy(a => a).ToList();
        if (apps.Count > 0) m.DropDownItems.Add(new ToolStripSeparator());
        foreach (var app in apps)
        {
            var current = _settings.AppSounds.GetValueOrDefault(app, AppSounds.Auto);
            string label = current == AppSounds.Auto ? "Automatic" : current == AppSounds.None ? "None"
                : AppSounds.Builtin.FirstOrDefault(b => b.File == current).Name ?? Path.GetFileNameWithoutExtension(current);
            var item = new ToolStripMenuItem($"{app}:  {label}");
            void Choice(string text, string value)
            {
                item.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) =>
                {
                    _settings.AppSounds[app] = value;
                    _settings.Save();
                    AppSounds.Preview(value, app);
                }) { Checked = current == value });
            }
            Choice("Automatic — the app's own sound if it has one", AppSounds.Auto);
            Choice("None (silent)", AppSounds.None);
            item.DropDownItems.Add(new ToolStripSeparator());
            foreach (var (name, file) in AppSounds.Builtin) Choice(name, file);
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add("Choose a sound file…", null, (_, _) =>
            {
                using var dlg = new System.Windows.Forms.OpenFileDialog { Title = $"Sound for {app}", Filter = "Sound (*.wav)|*.wav",
                    InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media") };
                if (dlg.ShowDialog() != DialogResult.OK) return;
                _settings.AppSounds[app] = dlg.FileName;
                _settings.Save();
                AppSounds.Preview(dlg.FileName, app);
            });
            m.DropDownItems.Add(item);
        }
        if (apps.Count == 0)
            m.DropDownItems.Add(new ToolStripMenuItem("Apps appear here after they send a notification") { Enabled = false });
        return m;
    }

    void RenderIcon()
    {
        using var bmp = IconRenderer.Glyph("", Theme.TaskbarDark, false);
        var h = bmp.GetHicon();
        _icon.Icon = Icon.FromHandle(h);
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
        _hIcon = h;
    }
}
