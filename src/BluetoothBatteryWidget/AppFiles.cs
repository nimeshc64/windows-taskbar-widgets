using System.Text.Json;
using Microsoft.Win32;

namespace BtBattery;

static class AppFiles
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string Folder(string app) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), app);

    public static T Load<T>(string app) where T : new()
    {
        try
        {
            var f = Path.Combine(Folder(app), "settings.json");
            if (File.Exists(f)) return JsonSerializer.Deserialize<T>(File.ReadAllText(f), Json) ?? new T();
        }
        catch (Exception ex) { Log(app, "Load settings: " + ex.Message); }
        return new T();
    }

    public static void Save<T>(string app, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder(app));
            File.WriteAllText(Path.Combine(Folder(app), "settings.json"), JsonSerializer.Serialize(value, Json));
        }
        catch (Exception ex) { Log(app, "Save settings: " + ex.Message); }
    }

    public static void Log(string app, string msg)
    {
        try
        {
            Directory.CreateDirectory(Folder(app));
            var path = Path.Combine(Folder(app), "log.txt");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public static bool StartupEnabled(string app)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(app) is string; }
        catch { return false; }
    }

    public static void SetStartup(string app, bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(app, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(app, false);
        }
        catch (Exception ex) { Log(app, "Startup: " + ex.Message); }
    }

    public static void ThemeMenu(ContextMenuStrip menu)
    {
        bool dark = Theme.AppsDark;
        var renderer = new MenuRenderer(dark);
        ToolStripManager.Renderer = renderer;
        menu.Renderer = renderer;
        var fg = dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(26, 26, 26);
        menu.BackColor = dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = fg;
            if (item is ToolStripMenuItem mi)
                foreach (ToolStripItem sub in mi.DropDownItems) sub.ForeColor = fg;
        }
    }
}
