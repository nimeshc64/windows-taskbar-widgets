using System.Runtime.InteropServices;

namespace BtBattery;

static class Native
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x3;

    public static void StyleWindow(IntPtr hwnd, bool dark)
    {
        try
        {
            int v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int));
            int corner = 2;
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        }
        catch {  }
    }

    public static Rectangle? TrayNotifyRect()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return null;
        var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero || !GetWindowRect(notify, out var r)) return null;
        var rect = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
        return rect.Width > 0 && rect.Height > 0 ? rect : null;
    }

    public static Rectangle? WindowRect(string title)
    {
        var h = FindWindow(null, title);
        if (h == IntPtr.Zero || !IsWindowVisible(h) || !GetWindowRect(h, out var r)) return null;
        var rect = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
        return rect.Width > 0 && rect.Height > 0 ? rect : null;
    }

    public static Point? DockLocation(Size size, string[] neighbors, float scale)
    {
        var screen = Screen.PrimaryScreen!;
        var b = screen.Bounds;
        var wa = screen.WorkingArea;
        if (wa.Bottom >= b.Bottom) return null;
        int y = wa.Bottom + (b.Bottom - wa.Bottom - size.Height) / 2;
        var rects = neighbors.Select(WindowRect).OfType<Rectangle>()
            .Where(r => r.Top >= wa.Bottom - 4 && b.Contains(r.Location)).ToList();
        if (rects.Count > 0) return new Point(rects.Min(r => r.Left) - size.Width - (int)(6 * scale), y);
        if (TaskbarZone.Get() is var (_, zoneRight)) return new Point(zoneRight - size.Width - (int)(8 * scale), y);
        if (TrayNotifyRect() is Rectangle t && t.Left > b.Left + b.Width / 2) return new Point(t.Left - size.Width - (int)(8 * scale), y);
        return new Point(b.Right - size.Width - (int)(320 * scale), y);
    }

    public static bool FitsInTaskbarZone(int x, float scale) =>
        TaskbarZone.Get() is not var (zoneLeft, _) || x >= zoneLeft + (int)(6 * scale);

    public static bool InTaskbarZone(Rectangle r, float scale)
    {
        var screen = Screen.PrimaryScreen!;
        var wa = screen.WorkingArea;
        bool onTaskbar = screen.Bounds.Contains(r.Location) && r.Top >= wa.Bottom - 4 && wa.Bottom < screen.Bounds.Bottom;
        if (!onTaskbar) return screen.Bounds.Contains(r.Location) || Screen.AllScreens.Any(s => s.Bounds.Contains(r.Location));
        return TaskbarZone.Get() is not var (left, right) || (r.Left >= left + (int)(4 * scale) && r.Right <= right - (int)(4 * scale));
    }

    public static bool OnTaskbar(Rectangle r)
    {
        var c = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        return Screen.AllScreens.Any(s => s.Bounds.Contains(c) && !s.WorkingArea.Contains(c));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetProp(IntPtr h, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr h, string name);

    public static readonly string[] StripOrder =
    {
        "System Tools Widget", "Network Widget", "Developer Tools Widget", "Now Playing Widget",
        "System Monitor Widget", "Audio Control Widget", "Bluetooth Battery Widget",
    };

    public static void MarkWanted(IntPtr hwnd, bool wanted) => SetProp(hwnd, "TaskbarWidgetWanted", wanted ? 1 : 0);

    public static int ReserveForLeft(string title, int iconWidth, float scale)
    {
        int i = Array.IndexOf(StripOrder, title);
        if (i <= 0) return 0;
        int n = 0;
        for (int k = 0; k < i; k++)
        {
            var h = FindWindow(null, StripOrder[k]);
            if (h != IntPtr.Zero && GetProp(h, "TaskbarWidgetWanted") != IntPtr.Zero) n++;
        }
        return n * (iconWidth + (int)(6 * scale));
    }

    public static Point? ClearOfNeighbors(Rectangle bounds, string[] neighbors, float scale)
    {
        var hit = neighbors.Select(WindowRect).OfType<Rectangle>().Where(r => r.IntersectsWith(bounds)).ToList();
        return hit.Count == 0 ? null : new Point(hit.Min(r => r.Left) - bounds.Width - (int)(6 * scale), bounds.Top);
    }

    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);

    public static bool IsAboveTaskbar(IntPtr hwnd)
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return true;
        int guard = 0;
        for (var h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && guard++ < 5000; h = GetWindow(h, 2))
        {
            if (h == hwnd) return true;
            if (h == tray) return false;
        }
        return false;
    }

    public static bool FullScreenAppRunning()
    {
        try
        {
            if (SHQueryUserNotificationState(out int s) == 0 && (s == 2 || s == 3 || s == 4)) return true;
            return ForegroundCoversTaskbarMonitor();
        }
        catch { return false; }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);

    static bool ForegroundCoversTaskbarMonitor()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !GetWindowRect(fg, out var r)) return false;
        var sb = new System.Text.StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        var cls = sb.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow"
            or "XamlExplorerHostIslandWindow" or "ControlCenterWindow" or "TopLevelWindowForOverflowXamlIsland"
            or "NotifyIconOverflowWindow" or "MultitaskingViewFrame" or "ForegroundStaging" or "XamlExplorerHostIslandWindow_WASDK")
            return false;
        var mon = Screen.PrimaryScreen!.Bounds;
        if (!(r.L <= mon.Left && r.T <= mon.Top && r.R >= mon.Right && r.B >= mon.Bottom)) return false;
        return !IsShellProcess(fg);
    }

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "ShellHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "TextInputHost", "LockApp", "Widgets", "WidgetBoard", "PeopleExperienceHost", "CredentialUIBroker", "consent"
    };
    static uint _lastPid;
    static bool _lastShell;

    public static bool ShellHasFocus()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return true;
        var sb = new System.Text.StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        if (sb.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow"
            or "TopLevelWindowForOverflowXamlIsland" or "Windows.UI.Core.CoreWindow" or "ControlCenterWindow"
            or "XamlExplorerHostIslandWindow") return true;
        return IsShellProcess(fg) && sb.ToString() is not ("Progman" or "WorkerW" or "CabinetWClass");
    }

    static bool IsShellProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _lastPid) return _lastShell;
        bool shell;
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); shell = ShellProcesses.Contains(p.ProcessName); }
        catch { shell = true; }
        _lastPid = pid;
        _lastShell = shell;
        return shell;
    }
}
