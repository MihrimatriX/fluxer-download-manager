using System.Runtime.InteropServices;

namespace Fluxer.Core.Platform.Windows;

/// Clipboard monitoring without a window: the sequence number goes up on every copy, so the text is
/// only read (and parsed) when something new was copied. Reading goes through Win32 directly because
/// the app often has no visible window (tray only) to borrow an Avalonia clipboard from.
public static partial class ClipboardSequence
{
    private const uint CF_UNICODETEXT = 13;

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint mem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint mem);

    public static uint Current => OperatingSystem.IsWindows() ? GetClipboardSequenceNumber() : 0;

    /// The clipboard's text, or null when it holds none or another process has it open right now.
    public static string? ReadText()
    {
        if (!OperatingSystem.IsWindows() || !IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(0))
            {
                try
                {
                    var handle = GetClipboardData(CF_UNICODETEXT);
                    if (handle == 0) return null;
                    var ptr = GlobalLock(handle);
                    if (ptr == 0) return null;
                    try { return Marshal.PtrToStringUni(ptr); }
                    finally { GlobalUnlock(handle); }
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(20); // the copying app may still hold it
        }
        return null;
    }
}
