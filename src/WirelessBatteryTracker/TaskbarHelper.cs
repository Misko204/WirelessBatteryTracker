using Microsoft.Win32;

namespace BatteryTray;

public static class TaskbarHelper
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Same value name the installer's "Run at startup" option writes, so both stay in sync.
    const string RunValue = "WirelessBatteryTracker";

    public static bool IsStartWithWindows()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { return false; }
    }

    /// <summary>
    /// Windows 11 puts new tray icons in the hidden "^" overflow by default. Each icon has a
    /// per-user entry under Control Panel\NotifyIconSettings; IsPromoted=1 shows it on the taskbar.
    /// Only entries belonging to this executable are touched.
    /// </summary>
    /// <returns>True if any entry was changed (the icons must then be re-added for Explorer to apply it).</returns>
    public static bool PromoteOwnIcons()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return false;
        bool changed = false;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return false;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name, writable: true);
                if (k?.GetValue("ExecutablePath") is not string path) continue;
                if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) continue;
                if (k.GetValue("IsPromoted") is int v && v == 1) continue;
                k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                changed = true;
            }
        }
        catch (Exception ex) { Log.Write("PromoteOwnIcons: " + ex.Message); }
        return changed;
    }

    public static void SetStartWithWindows(bool enable)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable && Environment.ProcessPath is string exe) k.SetValue(RunValue, $"\"{exe}\"");
            else k.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex) { Log.Write("SetStartWithWindows: " + ex.Message); }
    }
}
