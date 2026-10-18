using System.Runtime.InteropServices;

namespace Fluxer.Core.Platform.Windows;

/// Cheap change detection for clipboard monitoring: the sequence number goes up on every copy, so the
/// clipboard text is only read (and parsed) when something new was copied.
public static partial class ClipboardSequence
{
    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    public static uint Current => OperatingSystem.IsWindows() ? GetClipboardSequenceNumber() : 0;
}
