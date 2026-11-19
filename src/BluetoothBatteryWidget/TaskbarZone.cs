using System.Windows.Automation;

namespace BtBattery;

static class TaskbarZone
{
    static (int Left, int Right)? _zone;
    static DateTime _at = DateTime.MinValue;
    static int _busy;

    public static (int Left, int Right)? Get()
    {
        if ((DateTime.Now - _at).TotalSeconds >= 2 && Interlocked.Exchange(ref _busy, 1) == 0)
        {
            Task.Run(() =>
            {
                try { _zone = Read(); }
                catch { _zone = null; }
                finally { _at = DateTime.Now; Interlocked.Exchange(ref _busy, 0); }
            });
        }
        return _zone;
    }

    static (int Left, int Right)? Read()
    {
        var tray = AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
        if (tray == null) return null;
        var bar = tray.Current.BoundingRectangle;
        var buttons = tray.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>()
            .Select(e => (Cur: e.Current, Rect: e.Current.BoundingRectangle))
            .Where(b => !b.Rect.IsEmpty && b.Rect.Width > 0 && b.Rect.Left >= bar.Left - 1 && b.Rect.Right <= bar.Right + 1)
            .Select(b => (L: (int)b.Rect.Left, R: (int)b.Rect.Right, Class: b.Cur.ClassName ?? "", Id: b.Cur.AutomationId ?? ""))
            .ToList();
        if (buttons.Count == 0) return null;

        double mid = bar.Left + bar.Width / 2;
        bool IsRightGroup((int L, int R, string Class, string Id) b) =>
            b.Class.StartsWith("SystemTray.", StringComparison.Ordinal) || (b.Id == "WidgetsButton" && b.L > mid);
        var right = buttons.Where(IsRightGroup).ToList();
        if (right.Count == 0) return null;
        int groupLeft = right.Min(b => b.L);
        var leftSide = buttons.Where(b => !IsRightGroup(b) && b.L < groupLeft).ToList();
        int left = leftSide.Count > 0 ? leftSide.Max(b => b.R) : (int)bar.Left;

        if (int.TryParse(Environment.GetEnvironmentVariable("WIDGETS_ZONE_LEFT"), out var testLeft)) left = Math.Max(left, testLeft);
        return (left, Math.Max(left, groupLeft));
    }
}
