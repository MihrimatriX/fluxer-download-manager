namespace Fluxer.Core.Platform.Windows;

public static class DiskErrors
{
    private const int ERROR_HANDLE_DISK_FULL = unchecked((int)0x80070027);
    private const int ERROR_DISK_FULL = unchecked((int)0x80070070);

    public static bool IsDiskFull(Exception e) => e is IOException { HResult: ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL };

    public static long FreeBytes(string directory) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
}
