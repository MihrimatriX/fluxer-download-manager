using Microsoft.Win32;

namespace Fluxer.Core.Platform.Windows;

/// "Start with Windows": a value under HKCU\…\Run that starts Fluxer in the tray (--background), so the browser
/// extension always finds the app and the night queue runs even when nobody opened it.
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Fluxer";

    public static string CommandFor(string exe) => $"\"{exe}\" --background";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string;
    }

    /// Also rewrites a stale value (the app moved) when it is on.
    public static void Set(bool on, string exe)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(ValueName, CommandFor(exe));
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// Uninstall: removes the value only when it starts this copy (another copy may own it).
    public static void RemoveIfFor(string exe)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k?.GetValue(ValueName) is string v && v == CommandFor(exe)) k.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
