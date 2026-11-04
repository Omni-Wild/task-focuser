using System.Diagnostics;
using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace TaskFocuser.Core;

/// <summary>
/// The notification-area icon and its menu (SPEC 3.8). WinForms' NotifyIcon is the one tray API .NET ships;
/// WPF's dispatcher pumps the Win32 messages it needs, so its events arrive on the UI thread.
/// </summary>
public sealed class TrayIcon : ITray
{
    public event Action? ShowNote;
    public event Action? Tuck;
    public event Action? StartDot;
    public event Action? ToggleLock;
    public event Action? ToggleStartWithWindows;
    public event Action? Quit;

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _show, _tuck, _startDot, _lock, _startWithWindows, _quit;
    private readonly Drawing.Icon _image;
    private bool _disposed;

    public TrayIcon()
    {
        _show = Item("Show note", () => ShowNote?.Invoke());
        _tuck = Item("Tuck into the dot", () => Tuck?.Invoke());
        _startDot = Item("Start the dot", () => StartDot?.Invoke());
        _lock = Item("Lock", () => ToggleLock?.Invoke());
        // The check mark follows Update, not the click, so it can never disagree with the registry.
        _startWithWindows = Item("Start with Windows", () => ToggleStartWithWindows?.Invoke());
        _quit = Item("Quit", () => Quit?.Invoke());

        _menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        _menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _show, _tuck, _startDot,
            new Forms.ToolStripSeparator(),
            _lock, _startWithWindows,
            new Forms.ToolStripSeparator(),
            _quit,
        });

        _image = LoadIcon();
        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "Task Focuser",
            ContextMenuStrip = _menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowNote?.Invoke();
        };
        _icon.Visible = true;

        Update(Mode.Note, locked: false, startWithWindows: false);
    }

    public void Update(Mode mode, bool locked, bool startWithWindows)
    {
        if (_disposed) return;
        _show.Enabled = true; // in Note it brings the note to the front
        _tuck.Enabled = mode == Mode.Note;
        _startDot.Enabled = mode == Mode.Closed;
        _lock.Text = locked ? "Unlock" : "Lock";
        _startWithWindows.Checked = startWithWindows;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Hiding before disposing is what removes the icon at once instead of leaving a ghost
            // until the mouse passes over the tray.
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            _image.Dispose();
        }
        catch (Exception e) { Debug.WriteLine($"TrayIcon.Dispose: {e.Message}"); }
    }

    private static Forms.ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception e) { Debug.WriteLine($"Tray '{text}': {e}"); }
        };
        return item;
    }

    // ---- the icon: the embedded monster, else the exe's own icon, else a drawn one ----

    private static Drawing.Icon LoadIcon()
    {
        var size = Forms.SystemInformation.SmallIconSize;

        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/monster.ico"));
            if (res?.Stream is Stream s)
                using (s)
                    return new Drawing.Icon(s, size);
        }
        catch (Exception e) { Debug.WriteLine($"Tray icon resource: {e.Message}"); }

        try
        {
            string? exe = Environment.ProcessPath;
            // ExtractAssociatedIcon never fails on an exe without an icon: it hands back the generic
            // program icon. The drawn monster is the better fallback, so only take an icon the exe really has.
            if (exe is not null && Win32.ExtractIconEx(exe, -1, null, null, 0) > 0
                && Drawing.Icon.ExtractAssociatedIcon(exe) is Drawing.Icon assoc)
                return assoc;
        }
        catch (Exception e) { Debug.WriteLine($"Tray icon exe: {e.Message}"); }

        return DrawFallback();
    }

    /// <summary>A 16 px blue rounded square with two eyes: the monster at its most abstract.</summary>
    private static Drawing.Icon DrawFallback()
    {
        var body = Drawing.Color.FromArgb(0x4F, 0x8D, 0xFF);
        var outline = Drawing.Color.FromArgb(0x1F, 0x4F, 0xBF);
        var pupil = Drawing.Color.FromArgb(0x1A, 0x1A, 0x2E);

        using var bmp = new Drawing.Bitmap(16, 16, Drawing.Imaging.PixelFormat.Format32bppArgb);
        const int left = 1, top = 2, right = 14, bottom = 13;
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            bool cornerX = x == left || x == right, cornerY = y == top || y == bottom;
            if (cornerX && cornerY) continue; // rounded: the four corner pixels stay clear
            bmp.SetPixel(x, y, cornerX || cornerY ? outline : body);
        }
        // Eyes: 2x2 white, the pupil in the inner lower corner so it looks a little cross-eyed and cute.
        foreach (int ex in new[] { 4, 10 })
            for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
                bmp.SetPixel(ex + dx, 6 + dy, Drawing.Color.White);
        bmp.SetPixel(5, 7, pupil);
        bmp.SetPixel(10, 7, pupil);

        nint h = bmp.GetHicon();
        try
        {
            // FromHandle does not own the HICON; the clone does, so the native one can go right away.
            using var borrowed = Drawing.Icon.FromHandle(h);
            return (Drawing.Icon)borrowed.Clone();
        }
        finally
        {
            Win32.DestroyIcon(h);
        }
    }
}
