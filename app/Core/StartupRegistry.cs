using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TaskFocuser.Core;

/// <summary>
/// The "Start with Windows" toggle: a value in the per-user Run key, so no admin rights are needed.
/// One fixed value name, so enabling repoints an existing entry (an older build, another folder)
/// instead of adding a second one.
/// </summary>
public static class StartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Task Manager's Startup tab and Settings > Apps > Startup never touch the Run value: they write a 12-byte
    // flag of the same name here (first byte odd = disabled) and Windows then skips the entry.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Task Focuser";

    /// <summary>True only when the entry exists, starts this very exe and Windows has not switched it off.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key?.GetValue(ValueName) is not string value) return false;
            string? exe = Environment.ProcessPath;
            return exe is not null && SamePath(ExeOf(value), exe) && !DisabledByWindows();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"StartupRegistry.IsEnabled: {e.Message}");
            return false;
        }
    }

    /// <summary>On: writes (or repoints) the entry to this exe. Off: removes it. Never throws.</summary>
    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (exe is null) return;
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
                // Without this a tick after "Disable" in Task Manager writes the value and Windows still skips it.
                ClearWindowsSwitch();
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"StartupRegistry.Set: {e.Message}");
        }
    }

    /// <summary>
    /// The entry follows the exe: when it points at a file that no longer exists (he ticked the box, then moved
    /// or renamed the exe), it is repointed here. An entry that starts an existing file is left alone.
    /// </summary>
    public static void RepairIfMoved()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string value) return;
            if (File.Exists(Environment.ExpandEnvironmentVariables(ExeOf(value)))) return;
            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"StartupRegistry.RepairIfMoved: {e.Message}");
        }
    }

    private static bool DisabledByWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: false);
            return key?.GetValue(ValueName) is byte[] flag && flag.Length > 0 && (flag[0] & 1) == 1;
        }
        catch { return false; }
    }

    // No flag = enabled, which is also how a fresh Run entry starts out.
    private static void ClearWindowsSwitch()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception e) { Debug.WriteLine($"StartupRegistry.ClearWindowsSwitch: {e.Message}"); }
    }

    /// <summary>The executable part of a Run command line: the quoted path, or everything when unquoted.</summary>
    private static string ExeOf(string command)
    {
        string c = command.Trim();
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : c.Trim('"');
        }
        // Unquoted paths with arguments are ambiguous; cut at ".exe" when there is one.
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            a = Path.GetFullPath(Environment.ExpandEnvironmentVariables(a));
            b = Path.GetFullPath(b);
        }
        catch { /* compare as given */ }
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
