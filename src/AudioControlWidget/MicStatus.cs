using System.Windows.Automation;
using Microsoft.Win32;

namespace AudioCtl;

static class MicStatus
{
    public sealed record State(bool InUse, bool AppMuted, string? App, bool CallMute = false);

    static State _state = new(false, false, null);

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    public static void ToggleCallMute()
    {
        const byte Win = 0x5B, Alt = 0x12, K = 0x4B;
        const uint Up = 2;
        keybd_event(Win, 0, 0, IntPtr.Zero); keybd_event(Alt, 0, 0, IntPtr.Zero); keybd_event(K, 0, 0, IntPtr.Zero);
        keybd_event(K, 0, Up, IntPtr.Zero); keybd_event(Alt, 0, Up, IntPtr.Zero); keybd_event(Win, 0, Up, IntPtr.Zero);
        _at = DateTime.MinValue;
    }
    static DateTime _at = DateTime.MinValue;
    static int _busy;

    public static State Current
    {
        get
        {
            if ((DateTime.Now - _at).TotalSeconds >= 2 && Interlocked.Exchange(ref _busy, 1) == 0)
            {
                Task.Run(() =>
                {
                    try { _state = ReadTray() ?? ReadPrivacyRecords(); }
                    catch { _state = ReadPrivacyRecords(); }
                    finally { _at = DateTime.Now; Interlocked.Exchange(ref _busy, 0); }
                });
            }
            return _state;
        }
    }

    static State? ReadTray()
    {
        var tray = AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
        if (tray == null) return null;
        var buttons = tray.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        if (buttons.Count == 0) return null;
        foreach (AutomationElement b in buttons)
        {
            var first = (b.Current.Name ?? "").Replace("\r", "").Split('\n')[0].Trim();
            if (!first.StartsWith("Microphone", StringComparison.OrdinalIgnoreCase)) continue;
            bool unmuted = first.Contains("unmuted", StringComparison.OrdinalIgnoreCase);
            bool muted = !unmuted && first.Contains("muted", StringComparison.OrdinalIgnoreCase);
            string? app = null;
            int colon = first.IndexOf(':');
            int by = first.IndexOf(" by ", StringComparison.OrdinalIgnoreCase);
            if (colon >= 0) app = first[(colon + 1)..].Trim();
            else if (by >= 0) app = first[(by + 4)..].Trim();
            return new State(true, muted, string.IsNullOrEmpty(app) ? null : app, muted || unmuted);
        }
        return new State(false, false, null);
    }

    static State ReadPrivacyRecords()
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(root);
            if (key == null) return new State(false, false, null);
            foreach (var (k, name) in Apps(key))
                if (k.GetValue("LastUsedTimeStop") is long stop && stop == 0 && k.GetValue("LastUsedTimeStart") is long start && start > 0)
                    return new State(true, false, name);
        }
        catch { }
        return new State(false, false, null);
    }

    static IEnumerable<(RegistryKey Key, string Name)> Apps(RegistryKey root)
    {
        foreach (var sub in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(sub);
            if (k == null) continue;
            if (sub == "NonPackaged")
            {
                foreach (var exe in k.GetSubKeyNames())
                {
                    var ek = k.OpenSubKey(exe);
                    if (ek != null) yield return (ek, Path.GetFileNameWithoutExtension(exe.Replace('#', '\\')));
                }
            }
            else yield return (root.OpenSubKey(sub)!, sub.Split('_')[0].Split('.').Last());
        }
    }
}
