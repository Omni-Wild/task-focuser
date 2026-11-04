using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TaskFocuser.Core;

/// <summary>
/// The few P/Invokes the app needs. Every public call swallows failures: a missing style bit is a
/// cosmetic problem, a crash is not.
/// </summary>
public static class Win32
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const long WS_EX_LAYERED = 0x80000;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_APPWINDOW = 0x40000;

    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
        SWP_FRAMECHANGED = 0x20, SWP_NOOWNERZORDER = 0x200;

    private const uint LWA_ALPHA = 0x2;

    private static double? _scale;

    /// <summary>
    /// Physical pixels per DIP. The app is system-DPI aware, so one factor covers every monitor;
    /// read once because the system DPI only changes at sign-in.
    /// </summary>
    public static double Scale
    {
        get
        {
            if (_scale is double s) return s;
            uint dpi = 96;
            try { dpi = GetDpiForSystem(); } catch { /* pre-1607 Windows: keep 96 */ }
            if (dpi == 0) dpi = 96;
            _scale = dpi / 96.0;
            return _scale.Value;
        }
    }

    /// <summary>The window's HWND, created on demand so styles can be set before the first Show.</summary>
    public static nint Handle(Window w)
    {
        try { return new WindowInteropHelper(w).EnsureHandle(); }
        catch { return 0; }
    }

    /// <summary>A no-activate window takes clicks without stealing keyboard focus from the app he works in.</summary>
    public static void SetNoActivate(Window w, bool on) => SetExStyle(w, WS_EX_NOACTIVATE, on);

    /// <summary>Keeps the window out of Alt-Tab and the taskbar.</summary>
    public static void SetToolWindow(Window w)
    {
        // APPWINDOW would force a taskbar button back, so it goes when TOOLWINDOW comes.
        Modify(w, add: WS_EX_TOOLWINDOW, remove: WS_EX_APPWINDOW, frameChanged: true);
    }

    /// <summary>
    /// Mouse input passes through to whatever is below. TRANSPARENT alone only affects painting order;
    /// the hit test passes through only on a layered window, hence LAYERED too.
    /// </summary>
    public static void SetClickThrough(Window w, bool on)
    {
        nint h = Handle(w);
        if (h == 0) return;
        try
        {
            long ex = GetExStyle(h);
            if (on)
            {
                long next = ex | WS_EX_TRANSPARENT | WS_EX_LAYERED;
                if (next == ex) return;
                SetExStyleRaw(h, next);
                // A window that was not layered before turns invisible until it gets layer attributes.
                // An AllowsTransparency window is already layered (WPF drives it with UpdateLayeredWindow,
                // which SetLayeredWindowAttributes would break), so only a freshly layered one gets them.
                if ((ex & WS_EX_LAYERED) == 0) SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);
            }
            else
            {
                // LAYERED stays: WPF may depend on it, and a layered window at alpha 255 looks the same.
                long next = ex & ~WS_EX_TRANSPARENT;
                if (next != ex) SetExStyleRaw(h, next);
            }
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine($"SetClickThrough: {e.Message}"); }
    }

    /// <summary>The window that has the keyboard right now, or zero.</summary>
    public static nint ForegroundWindow()
    {
        try { return GetForegroundWindow(); } catch { return 0; }
    }

    /// <summary>Hands the keyboard back to <paramref name="h"/>; works because the caller is the foreground process.</summary>
    public static void GiveForeground(nint h)
    {
        if (h == 0) return;
        try { SetForegroundWindow(h); } catch { /* best effort */ }
    }

    /// <summary>The cursor in screen DIPs.</summary>
    public static Point CursorPosition()
    {
        try
        {
            if (GetCursorPos(out POINT p)) return ToDip(p.X, p.Y);
        }
        catch { /* fall through */ }
        return new Point(0, 0);
    }

    internal static Point ToDip(int x, int y) => new(x / Scale, y / Scale);

    internal static POINT ToPhysical(Point p) =>
        new() { X = (int)Math.Round(p.X * Scale), Y = (int)Math.Round(p.Y * Scale) };

    internal static Rect ToDip(RECT r) =>
        new(r.Left / Scale, r.Top / Scale, Math.Max(0, r.Right - r.Left) / Scale, Math.Max(0, r.Bottom - r.Top) / Scale);

    private static void SetExStyle(Window w, long bits, bool on) =>
        Modify(w, add: on ? bits : 0, remove: on ? 0 : bits, frameChanged: false);

    private static void Modify(Window w, long add, long remove, bool frameChanged)
    {
        nint h = Handle(w);
        if (h == 0) return;
        try
        {
            long ex = GetExStyle(h);
            long next = (ex | add) & ~remove;
            if (next == ex) return; // idempotent: no redundant SetWindowPos churn
            SetExStyleRaw(h, next);
            if (frameChanged)
                SetWindowPos(h, 0, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine($"SetExStyle: {e.Message}"); }
    }

    private static long GetExStyle(nint h) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(h, GWL_EXSTYLE) : GetWindowLong32(h, GWL_EXSTYLE);

    private static void SetExStyleRaw(nint h, long value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(h, GWL_EXSTYLE, (nint)value);
        else SetWindowLong32(h, GWL_EXSTYLE, unchecked((int)value));
    }

    // ---- native ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    internal const uint MONITOR_DEFAULTTONEAREST = 2;
    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr64(nint h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr64(nint h, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(nint h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(nint h, int index, int value);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint h, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] internal static extern nint MonitorFromRect(ref RECT r, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyIcon(nint h);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    internal static extern uint ExtractIconEx(string file, int index, nint[]? large, nint[]? small, uint count);
}
