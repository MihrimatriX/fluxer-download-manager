using System.Runtime.InteropServices;

namespace Fluxer.Core.Platform.Windows;

/// What the smart speed rules look at: is someone using another program, and is the laptop on battery.
public static partial class Activity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);

    /// Time since the last keyboard or mouse input anywhere on the desktop.
    public static TimeSpan Idle()
    {
        if (!OperatingSystem.IsWindows()) return TimeSpan.MaxValue;
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.MaxValue;
    }

    /// The window in front belongs to another program (not Fluxer).
    public static bool OtherAppInFront()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var hwnd = GetForegroundWindow();
        return hwnd != 0 && GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid != Environment.ProcessId;
    }

    /// Battery percentage when running on battery; null on mains power or without a battery.
    public static int? OnBatteryPercent()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var s)) return null;
        if (s.ACLineStatus != 0 || s.BatteryLifePercent > 100 || (s.BatteryFlag & 128) != 0) return null;  // 128: no battery
        return s.BatteryLifePercent;
    }
}
