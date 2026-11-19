using System.Text.Json;
using Microsoft.Win32;

namespace BtBattery;

public enum DeviceKind { Other, Headphones, Speaker, Keyboard, Mouse, Gamepad, Phone, Computer, Pen, Watch }

public sealed class DeviceBattery
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Address { get; init; }
    public bool IsLowEnergy { get; init; }
    public DeviceKind Kind { get; init; }
    public bool IsConnected { get; set; }
    public int? Battery { get; set; }
    public bool BatteryIsLastKnown { get; set; }
    public DateTime? LastSeen { get; set; }
}

public sealed class LastKnown
{
    public int Battery { get; set; }
    public DateTime Time { get; set; }
}

public sealed class AppSettings
{
    public int RefreshSeconds { get; set; } = 60;
    public bool LowBatteryAlerts { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 20;
    public bool ShowDeviceIcons { get; set; } = true;
    public bool ShowWidget { get; set; } = true;
    public bool ShowDisconnected { get; set; } = true;
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }
    public Dictionary<string, LastKnown> LastKnown { get; set; } = new();

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BluetoothBatteryWidget");
    static string FilePath => Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    string? _lastSaved;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(text, Json);
                if (s != null) { s._lastSaved = text; return s; }
            }
        }
        catch (Exception ex) { Log.Write("Load settings: " + ex.Message); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var text = JsonSerializer.Serialize(this, Json);
            if (text == _lastSaved) return;
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, text);
            _lastSaved = text;
        }
        catch (Exception ex) { Log.Write("Save settings: " + ex.Message); }
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "BluetoothBatteryWidget";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, false);
    }
}

static class Log
{
    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            var path = Path.Combine(AppSettings.Folder, "log.txt");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
