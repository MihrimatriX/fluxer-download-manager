using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Fluxer.Core.Platform.Windows;

internal static partial class Native
{
    private const uint FSCTL_SET_SPARSE = 0x000900C4;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint inBuffer, uint inSize,
        nint outBuffer, uint outSize, out uint bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle file);

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    /// Handle must be synchronous (no FILE_FLAG_OVERLAPPED) since we pass no OVERLAPPED.
    public static void SetSparse(SafeFileHandle handle)
    {
        if (!DeviceIoControl(handle, FSCTL_SET_SPARSE, 0, 0, 0, 0, out _, 0))
            throw new IOException("FSCTL_SET_SPARSE failed", Marshal.GetHRForLastWin32Error());
    }

    public static void Flush(SafeFileHandle handle)
    {
        if (!FlushFileBuffers(handle))
            throw new IOException("FlushFileBuffers failed", Marshal.GetHRForLastWin32Error());
    }

    /// Without ES_CONTINUOUS this just resets the idle-sleep timer; call it periodically.
    public static void PokeSystemAwake() => SetThreadExecutionState(ES_SYSTEM_REQUIRED);
}
