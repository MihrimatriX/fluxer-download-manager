using System.Runtime.InteropServices;

namespace Fluxer.Core.Platform.Windows;

/// Event sounds: the user's .wav, or a Windows sound from %WINDIR%\Media. Played asynchronously by winmm.
public static partial class Sounds
{
    private const uint SndAsync = 0x1, SndNoDefault = 0x2, SndAlias = 0x10000, SndFilename = 0x20000, SndSystem = 0x200000;

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PlaySound(string? sound, nint module, uint flags);

    /// Windows' own sounds per event; the first one that exists is used.
    public static string? DefaultFile(string evt)
    {
        var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        string[] names = evt switch
        {
            "failed" => ["Windows Exclamation.wav", "chord.wav"],
            "queue" => ["tada.wav"],
            _ => ["Windows Notify System Generic.wav", "Windows Notify.wav", "chimes.wav"],
        };
        return names.Select(n => Path.Combine(media, n)).FirstOrDefault(File.Exists);
    }

    public static void Play(string? file, string evt)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = file is { Length: > 0 } && File.Exists(file) ? file : DefaultFile(evt);
        if (path is not null) _ = PlaySound(path, 0, SndFilename | SndAsync | SndNoDefault | SndSystem);
        else _ = PlaySound(".Default", 0, SndAlias | SndAsync | SndSystem);
    }
}
