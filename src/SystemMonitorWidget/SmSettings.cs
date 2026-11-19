using System.Text.Json;
using Microsoft.Win32;

namespace SysMon;

public sealed class SmSettings
{
    public bool ShowWidget { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public bool Bits { get; set; }
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SystemMonitorWidget");
    static string FilePath => Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static SmSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SmSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex) { SmLog.Write("Load settings: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { SmLog.Write("Save settings: " + ex.Message); }
    }
}

static class SmStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "SystemMonitorWidget";

    public static bool IsEnabled
    {
        get
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(Name) is string; }
            catch { return false; }
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(Name, false);
        }
        catch (Exception ex) { SmLog.Write("Startup: " + ex.Message); }
    }
}

static class SmLog
{
    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(SmSettings.Folder);
            var path = Path.Combine(SmSettings.Folder, "log.txt");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }
}

static class SmGlyphs
{
    public const string Cpu = "";
    public const string Memory = "";
    public const string Wifi = "";
    public const string Ethernet = "";
    public const string Globe = "";
    public const string Down = "";
    public const string Up = "";
    public const string TaskManager = "";

    public static string Network(SystemSampler s) => s.AdapterKind == "Wi-Fi" ? Wifi : s.HasNetwork ? Ethernet : Globe;
}
