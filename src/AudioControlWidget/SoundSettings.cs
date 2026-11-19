using System.Text.Json;
using AudioCtl.Interop;
using Microsoft.Win32;

namespace AudioCtl;

public sealed class SoundSettings
{
    public bool ShowWidget { get; set; } = true;
    public bool ShowMicInWidget { get; set; } = true;
    public int ScrollStep { get; set; } = 2;
    public int? WidgetX { get; set; }
    public int? WidgetY { get; set; }

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioControlWidget");
    static string FilePath => Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static SoundSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SoundSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex) { SLog.Write("Load settings: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { SLog.Write("Save settings: " + ex.Message); }
    }
}

static class SoundStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "AudioControlWidget";

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
        catch (Exception ex) { SLog.Write("Startup: " + ex.Message); }
    }
}

static class SLog
{
    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(SoundSettings.Folder);
            var path = Path.Combine(SoundSettings.Folder, "log.txt");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }
}

static class SoundGlyphs
{
    public const string Mic = "";
    public const string MicOff = "";
    public const string Mixer = "";

    public static string Volume(float v, bool muted) =>
        muted ? "" : v <= 0.005f ? "" : v < 0.34f ? "" : v < 0.67f ? "" : "";

    public static string Level(SoundService.Endpoint ep) =>
        ep.Flow == EDataFlow.Capture ? (ep.Muted ? MicOff : Mic) : Volume(ep.Volume, ep.Muted);

    public static string ForDevice(AudioDevice d)
    {
        if (d.Interface.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)) return "";
        return d.FormFactor switch
        {
            3 => "",
            5 or 6 => "",
            4 => Mic,
            9 => "",
            _ => d.Flow == EDataFlow.Capture ? Mic : "",
        };
    }
}
