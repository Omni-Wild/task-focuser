using System.Windows;

namespace TaskFocuser.Core;

/// <summary>
/// Monitor geometry in screen DIPs. Asked fresh on every call (no cache), so a monitor plugged in or a
/// taskbar moved is seen at once; the calls are cheap and none sits in a per-frame path that matters.
/// </summary>
public static class Monitors
{
    /// <summary>A last resort when the Win32 calls fail, so callers always get a usable rect.</summary>
    private static readonly Rect Fallback = new(0, 0, 1920, 1040);

    public static Rect PrimaryWorkArea
    {
        get
        {
            var info = InfoAt(new Win32.POINT(), Win32.MONITOR_DEFAULTTOPRIMARY);
            return info is { } i ? Win32.ToDip(i.rcWork) : Fallback;
        }
    }

    /// <summary>The work area (screen minus taskbar) of the monitor containing <paramref name="p"/>, or the nearest one.</summary>
    public static Rect WorkAreaAt(Point p)
    {
        var info = InfoAt(Win32.ToPhysical(p), Win32.MONITOR_DEFAULTTONEAREST);
        return info is { } i ? Win32.ToDip(i.rcWork) : Fallback;
    }

    /// <summary>The full bounds of the monitor containing <paramref name="p"/>, or the nearest one.</summary>
    public static Rect BoundsAt(Point p)
    {
        var info = InfoAt(Win32.ToPhysical(p), Win32.MONITOR_DEFAULTTONEAREST);
        return info is { } i ? Win32.ToDip(i.rcMonitor) : Fallback;
    }

    /// <summary>
    /// The work area of the monitor holding <paramref name="r"/>'s centre (nearest when none). Used to put a
    /// saved position back on a screen that still exists.
    /// </summary>
    public static Rect VisibleWorkAreaFor(Rect r)
    {
        if (r.IsEmpty || double.IsNaN(r.X) || double.IsNaN(r.Y)) return PrimaryWorkArea;
        return WorkAreaAt(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
    }

    /// <summary>
    /// Moves <paramref name="r"/> (size unchanged) so it lies inside <paramref name="area"/>. When it is larger than
    /// the area on an axis, its top or left edge aligns with the area's so the start of the text stays visible.
    /// </summary>
    public static Rect ClampInto(Rect r, Rect area)
    {
        if (r.IsEmpty || area.IsEmpty) return r;
        double x = r.Width >= area.Width ? area.Left : Math.Clamp(r.X, area.Left, area.Right - r.Width);
        double y = r.Height >= area.Height ? area.Top : Math.Clamp(r.Y, area.Top, area.Bottom - r.Height);
        if (double.IsNaN(x)) x = area.Left;
        if (double.IsNaN(y)) y = area.Top;
        return new Rect(x, y, r.Width, r.Height);
    }

    /// <summary>Where the dot rests on a monitor (SPEC 3.4).</summary>
    public static Point TopRightCorner(Rect area) => new(area.Right - 28, area.Top + 28);

    /// <summary>Where the close target sits on a monitor (SPEC 3.7).</summary>
    public static Point BottomRightCorner(Rect area) => new(area.Right - 36, area.Bottom - 36);

    /// <summary>
    /// True when <paramref name="p"/> lies on a monitor that exists now, give or take <paramref name="slack"/> DIP
    /// (a dot dropped at a screen edge may hang a little over it). False for a spot on an unplugged monitor.
    /// </summary>
    public static bool IsOnScreen(Point p, double slack)
    {
        var b = BoundsAt(p);
        b.Inflate(slack, slack);
        return b.Contains(p);
    }

    /// <summary>True when both points fall on the same monitor (each snapped to its nearest).</summary>
    public static bool SameMonitor(Point a, Point b)
    {
        try
        {
            nint ma = Win32.MonitorFromPoint(Win32.ToPhysical(a), Win32.MONITOR_DEFAULTTONEAREST);
            nint mb = Win32.MonitorFromPoint(Win32.ToPhysical(b), Win32.MONITOR_DEFAULTTONEAREST);
            return ma == mb;
        }
        catch { return true; }
    }

    public static Point CursorPosition() => Win32.CursorPosition();

    private static Win32.MONITORINFO? InfoAt(Win32.POINT pt, uint flags)
    {
        try
        {
            nint mon = Win32.MonitorFromPoint(pt, flags);
            if (mon == 0) return null;
            var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
            return Win32.GetMonitorInfo(mon, ref info) ? info : null;
        }
        catch { return null; }
    }
}
