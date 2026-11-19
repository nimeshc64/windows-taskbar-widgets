using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BtBattery;
using Microsoft.Win32;

namespace SysTools;

public sealed class SavedWindow
{
    public string Process { get; set; } = "";
    public string Class { get; set; } = "";
    public string Title { get; set; } = "";
    public int ShowCmd { get; set; }
    public int L { get; set; }
    public int T { get; set; }
    public int R { get; set; }
    public int B { get; set; }
}

public sealed class WindowLayout
{
    public string Name { get; set; } = "";
    public DateTime Saved { get; set; }
    public int Monitors { get; set; }
    public List<SavedWindow> Windows { get; set; } = new();
}

sealed class ToolsService
{
    public const string AppName = "SystemToolsWidget";
    public static void Log(string msg) => AppFiles.Log(AppName, msg);

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public long BinBytes { get; private set; }
    public long BinItems { get; private set; }

    public async Task ReadRecycleBinAsync()
    {
        var r = await Task.Run(() =>
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (-1L, -1L);
        });
        if (r.Item1 >= 0) { BinBytes = r.Item1; BinItems = r.Item2; }
    }

    public async Task<string?> EmptyRecycleBinAsync()
    {
        int hr = await Task.Run(() => SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4));
        await ReadRecycleBinAsync();
        return hr == 0 || BinItems == 0 ? null : $"error 0x{hr:X8}";
    }

    const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    static int ReadDword(string key, string name, int fallback)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(key); return k?.GetValue(name) is int v ? v : fallback; }
        catch { return fallback; }
    }

    static void WriteDword(string key, string name, int value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    public bool HiddenFiles => ReadDword(Advanced, "Hidden", 2) == 1;
    public bool FileExtensions => ReadDword(Advanced, "HideFileExt", 1) == 0;
    public bool DarkMode => ReadDword(Personalize, "AppsUseLightTheme", 1) == 0;

    public void SetHiddenFiles(bool show) { WriteDword(Advanced, "Hidden", show ? 1 : 2); RefreshExplorer(); }
    public void SetFileExtensions(bool show) { WriteDword(Advanced, "HideFileExt", show ? 0 : 1); RefreshExplorer(); }

    public void SetDarkMode(bool dark)
    {
        WriteDword(Personalize, "AppsUseLightTheme", dark ? 0 : 1);
        WriteDword(Personalize, "SystemUsesLightTheme", dark ? 0 : 1);
        Broadcast("ImmersiveColorSet");
    }

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wp, string lp, uint flags, uint timeout, out IntPtr result);

    static void Broadcast(string area) =>
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, area, 0x0002, 1000, out _);

    static void RefreshExplorer()
    {
        Broadcast("ShellState");
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            foreach (dynamic w in shell.Windows())
            {
                try { w.Refresh(); } catch { }
            }
        }
        catch (Exception ex) { Log("Refresh Explorer: " + ex.Message); }
    }

    public bool RebootPending { get; private set; }
    public int? PendingUpdates { get; private set; }
    public List<string> PendingTitles { get; private set; } = new();
    public DateTime? LastChecked { get; private set; }
    public DateTime? LastInstalled { get; private set; }
    public DateTime? PausedUntil { get; private set; }
    public bool CheckingUpdates { get; private set; }

    public void ReadUpdateRegistry()
    {
        static bool KeyExists(string path)
        {
            try { using var k = Registry.LocalMachine.OpenSubKey(path); return k != null; } catch { return false; }
        }
        RebootPending = KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
                     || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");
            PausedUntil = k?.GetValue("PauseUpdatesExpiryTime") is string s && DateTime.TryParse(s, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
                && t > DateTime.UtcNow ? t.ToLocalTime() : null;
        }
        catch { PausedUntil = null; }
    }

    public async Task ReadUpdatesAsync()
    {
        if (CheckingUpdates) return;
        CheckingUpdates = true;
        ReadUpdateRegistry();
        try
        {
            var (count, titles, last, installed) = await Task.Run(() =>
            {
                DateTime? lastSearch = null, lastInstall = null;
                try
                {
                    dynamic au = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate")!)!;
                    dynamic results = au.Results;
                    if (results.LastSearchSuccessDate is DateTime a && a.Year > 2000) lastSearch = DateTime.SpecifyKind(a, DateTimeKind.Utc).ToLocalTime();
                    if (results.LastInstallationSuccessDate is DateTime b && b.Year > 2000) lastInstall = DateTime.SpecifyKind(b, DateTimeKind.Utc).ToLocalTime();
                }
                catch (Exception ex) { Log("AutoUpdate: " + ex.Message); }

                dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
                dynamic searcher = session.CreateUpdateSearcher();
                searcher.Online = false;
                dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
                int n = result.Updates.Count;
                var list = new List<string>();
                for (int i = 0; i < Math.Min(n, 5); i++) list.Add((string)result.Updates.Item(i).Title);
                return (n, list, lastSearch, lastInstall);
            });
            PendingUpdates = count;
            PendingTitles = titles;
            LastChecked = last;
            LastInstalled = installed;
        }
        catch (Exception ex)
        {
            PendingUpdates = null;
            Log("Update search: " + ex.Message);
        }
        finally { CheckingUpdates = false; }
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT min, max;
        public RECT normal;
    }

    static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
    };

    static readonly HashSet<string> SkipProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "LockApp",
        "SystemToolsWidget", "NetworkWidget", "DevToolsWidget", "NowPlayingWidget", "SystemMonitorWidget",
        "AudioControlWidget", "BluetoothBatteryWidget",
    };

    static List<(IntPtr Handle, SavedWindow Info)> Snapshot()
    {
        var list = new List<(IntPtr, SavedWindow)>();
        var names = new Dictionary<uint, string>();
        var sb = new StringBuilder(512);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true;
            int ex = GetWindowLong(h, -20);
            if ((ex & 0x80) != 0) return true;
            if (DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
            sb.Clear(); GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.Length == 0) return true;
            sb.Clear(); GetClassName(h, sb, sb.Capacity);
            var cls = sb.ToString();
            if (SkipClasses.Contains(cls)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!names.TryGetValue(pid, out var proc))
            {
                try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { proc = ""; }
                names[pid] = proc;
            }
            if (proc.Length == 0 || SkipProcesses.Contains(proc)) return true;
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(h, ref wp)) return true;
            list.Add((h, new SavedWindow
            {
                Process = proc, Class = cls, Title = title, ShowCmd = wp.showCmd,
                L = wp.normal.L, T = wp.normal.T, R = wp.normal.R, B = wp.normal.B,
            }));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = "Edge", ["chrome"] = "Chrome", ["firefox"] = "Firefox", ["Code"] = "VS Code", ["explorer"] = "Explorer",
        ["studio64"] = "Android Studio", ["WindowsTerminal"] = "Terminal", ["devenv"] = "Visual Studio", ["qemu-system-x86_64"] = "Emulator",
        ["OUTLOOK"] = "Outlook", ["WINWORD"] = "Word", ["EXCEL"] = "Excel", ["POWERPNT"] = "PowerPoint", ["ms-teams"] = "Teams",
        ["Spotify"] = "Spotify", ["slack"] = "Slack", ["Discord"] = "Discord", ["figma"] = "Figma", ["notepad"] = "Notepad",
        ["claude"] = "Claude", ["ChatGPT"] = "ChatGPT", ["WhatsApp"] = "WhatsApp", ["Telegram"] = "Telegram",
    };

    public static string AppLabel(SavedWindow w) =>
        w.Process.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ? w.Title
        : AppNames.TryGetValue(w.Process, out var n) ? n
        : char.ToUpper(w.Process[0]) + w.Process[1..];

    public static WindowLayout CaptureLayout()
    {
        var wins = Snapshot().Select(x => x.Info).ToList();
        var apps = wins.Select(AppLabel).Distinct().ToList();
        string name = apps.Count == 0 ? "Empty layout" : string.Join(", ", apps.Take(3)) + (apps.Count > 3 ? $" +{apps.Count - 3}" : "");
        return new WindowLayout { Name = name, Saved = DateTime.Now, Monitors = Screen.AllScreens.Length, Windows = wins };
    }

    public static (int Restored, int Total) Restore(WindowLayout layout)
    {
        var current = Snapshot();
        var used = new HashSet<IntPtr>();
        int ok = 0;
        foreach (var saved in layout.Windows)
        {
            var candidates = current.Where(c => !used.Contains(c.Handle) &&
                                                c.Info.Process.Equals(saved.Process, StringComparison.OrdinalIgnoreCase) &&
                                                c.Info.Class == saved.Class).ToList();
            if (candidates.Count == 0) continue;
            var pick = candidates.FirstOrDefault(c => c.Info.Title == saved.Title);
            if (pick.Handle == IntPtr.Zero)
                pick = candidates.OrderByDescending(c => CommonPrefix(c.Info.Title, saved.Title)).First();
            used.Add(pick.Handle);

            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(pick.Handle, ref wp)) continue;
            if (wp.showCmd == 3) ShowWindow(pick.Handle, 9);
            wp.normal = new RECT { L = saved.L, T = saved.T, R = saved.R, B = saved.B };
            wp.showCmd = saved.ShowCmd switch { 3 => 3, 2 => 7, _ => 4 };
            wp.flags = 0;
            if (SetWindowPlacement(pick.Handle, ref wp)) ok++;
        }
        return (ok, layout.Windows.Count);
    }

    static int CommonPrefix(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i;
    }

    public static void Open(string target, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (args != null) psi.Arguments = args;
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Log("Open: " + ex.Message); }
    }

    public static string Size(long bytes)
    {
        double v = bytes;
        if (v < 1024) return $"{v:0} B";
        v /= 1024; if (v < 1024) return $"{v:0} KB";
        v /= 1024; if (v < 1024) return v < 10 ? $"{v:0.0} MB" : $"{v:0} MB";
        v /= 1024; return $"{v:0.0} GB";
    }
}
