namespace Fluxer.Core.Downloads;

/// A download category: one of the built-in ones (Video, Music, Documents, Archives, Programs, Torrent) or the
/// user's own. A download lands in it by file extension, or by the site it comes from (site rules win), and is
/// saved in its folder.
public sealed class Category
{
    public static readonly string[] BuiltIn = ["Video", "Music", "Documents", "Archives", "Programs", "Torrent"];

    /// Built-in name, or the user's name for the category (also the default folder name).
    public string Key { get; set; } = "";
    public List<string> Extensions { get; set; } = [];
    /// Host patterns: "github.com" (also its subdomains), "*.example.org".
    public List<string> Sites { get; set; } = [];
    /// null = "download folder\Key" when category folders are on.
    public string? Folder { get; set; }

    public bool IsBuiltIn => BuiltIn.Contains(Key);

    public bool MatchesHost(string? host) =>
        host is { Length: > 0 } && Sites.Any(p => HostMatches(host, p));

    public static bool HostMatches(string host, string pattern)
    {
        pattern = pattern.Trim().ToLowerInvariant();
        if (pattern.StartsWith("*.", StringComparison.Ordinal)) pattern = pattern[2..];
        host = host.ToLowerInvariant();
        return pattern.Length > 0 && (host == pattern || host.EndsWith("." + pattern, StringComparison.Ordinal));
    }

    public static List<Category> Defaults() =>
    [
        new() { Key = "Video", Extensions = ["mp4", "mkv", "avi", "mov", "webm", "wmv", "flv", "m4v"] },
        new() { Key = "Music", Extensions = ["mp3", "flac", "wav", "aac", "ogg", "m4a", "opus", "wma"] },
        new() { Key = "Documents", Extensions = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "epub", "odt"] },
        new() { Key = "Archives", Extensions = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso"] },
        new() { Key = "Programs", Extensions = ["exe", "msi", "msix", "appx", "bat", "apk"] },
        new() { Key = "Torrent" },
    ];

    /// "mp4, .MKV  webm" → ["mp4", "mkv", "webm"].
    public static List<string> ParseList(string? text, bool extensions) =>
        [.. (text ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => extensions ? t.TrimStart('.', '*').ToLowerInvariant() : t.ToLowerInvariant())
            .Where(t => t.Length > 0).Distinct()];
}
