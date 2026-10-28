using System.Text;

namespace Fluxer.Core.Platform.Windows;

public static class FileNameSanitizer
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const int MaxLength = 200;

    /// Returns a safe leaf file name: no directories, no invalid chars, no reserved device names,
    /// no trailing dots/spaces. Never returns empty.
    public static string Sanitize(string? name, string fallback = "download")
    {
        name ??= "";
        // Drop any directory component (both separators) — defeats "../" traversal.
        var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0) name = name[(slash + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(c < 32 || invalid.Contains(c) ? '_' : c);
        name = sb.ToString().Trim().TrimEnd('.', ' ');

        if (name.Length == 0 || name.All(c => c == '.')) name = fallback;

        var stem = Path.GetFileNameWithoutExtension(name);
        var dot = stem.IndexOf('.');
        if (Reserved.Contains(dot >= 0 ? stem[..dot] : stem)) name = "_" + name;

        if (name.Length > MaxLength)
        {
            var ext = Path.GetExtension(name);
            if (ext.Length > 20) ext = "";
            name = name[..(MaxLength - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return name;
    }

    /// "a.zip" → "a (1).zip", "a (2).zip"... until neither the file nor its ".part" exists.
    public static string MakeUnique(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = fileName;
        for (var i = 1; Exists(candidate); i++) candidate = $"{stem} ({i}){ext}";
        return candidate;

        bool Exists(string n) => File.Exists(Path.Combine(directory, n)) || File.Exists(Path.Combine(directory, n + ".part"));
    }

    /// Like <see cref="MakeUnique"/>, but also claims the name by creating its (empty) ".part", so downloads
    /// that start at the same moment (a batch) can never pick the same name and then share one ".part".
    /// The caller opens that ".part" afterwards (OpenOrCreate), so leaving it empty is fine.
    public static string ReserveUnique(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 0; ; i++)
        {
            var candidate = i == 0 ? fileName : $"{stem} ({i}){ext}";
            if (File.Exists(Path.Combine(directory, candidate))) continue;
            try
            {
                // CreateNew is the check and the claim in one step: of two downloads asking at once, one gets it.
                new FileStream(Path.Combine(directory, candidate + ".part"), FileMode.CreateNew).Dispose();
                return candidate;
            }
            catch (IOException e) when (e.HResult == ErrorFileExists) { }   // taken; anything else (denied, no disk) is real
        }
    }

    private const int ErrorFileExists = unchecked((int)0x80070050);
}
