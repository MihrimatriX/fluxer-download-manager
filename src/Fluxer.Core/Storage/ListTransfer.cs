using System.Text.Json;
using Fluxer.Core.Downloads;

namespace Fluxer.Core.Storage;

/// One download in an export file. Cookies are left out on purpose: they are sessions, not part of a list.
public sealed class ExportItem
{
    public string Kind { get; set; } = "http";
    public bool Completed { get; set; }
    public string FileName { get; set; } = "";
    public string SaveDirectory { get; set; } = "";
    public string? Category { get; set; }
    public List<string>? Tags { get; set; }
    public long? TotalBytes { get; set; }
    public long CreatedAt { get; set; }
    public long? CompletedAt { get; set; }
    public string? Url { get; set; }
    public List<string>? Mirrors { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? HashAlgorithm { get; set; }
    public string? HashExpected { get; set; }
    public string? InfoHash { get; set; }
    public string? Magnet { get; set; }
    public string? TorrentFile { get; set; }       // base64
    public bool Sequential { get; set; }
    public string? PageUrl { get; set; }
    public string? FormatSpec { get; set; }
    public string? Title { get; set; }
    public bool AudioOnly { get; set; }
}

public sealed class ExportFile
{
    public const string Tag = "fluxer-downloads";
    /// Absent in anything that is not a Fluxer export.
    public string Format { get; set; } = "";
    public int Version { get; set; } = 1;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ExportItem> Items { get; set; } = [];
}

/// Settings → Advanced → "Download list": export to JSON (everything needed to add it again) or to a plain
/// list of addresses, and import either back.
public static class ListTransfer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ExportItem ToItem(DownloadRecord r) => new()
    {
        Kind = r.Kind.ToString().ToLowerInvariant(),
        Completed = r.State == DownloadState.Completed,
        FileName = r.FileName,
        SaveDirectory = r.SaveDirectory,
        Category = r.Category,
        Tags = r.Tags.Count > 0 ? [.. r.Tags] : null,
        TotalBytes = r.TotalBytes,
        CreatedAt = r.CreatedAt,
        CompletedAt = r.CompletedAt,
        Url = r.Http?.Url,
        Mirrors = r.Http?.Mirrors is { Count: > 0 } m ? m : null,
        Referrer = r.Http?.Referrer ?? r.Media?.Referrer,
        UserAgent = r.Http?.UserAgent,
        HashAlgorithm = r.Http?.HashAlgorithm,
        HashExpected = r.Http?.HashExpected,
        InfoHash = r.Torrent?.InfoHash,
        Magnet = r.Torrent?.Magnet,
        TorrentFile = r.Torrent?.TorrentFile is { } t ? Convert.ToBase64String(t) : null,
        Sequential = r.Torrent?.Sequential ?? false,
        PageUrl = r.Media?.PageUrl,
        FormatSpec = r.Media?.FormatSpec,
        Title = r.Media?.Title,
        AudioOnly = r.Media?.AudioOnly ?? false,
    };

    /// A fresh record (no id, no progress): unfinished ones come back paused, finished ones as history.
    public static DownloadRecord? ToRecord(ExportItem i)
    {
        var r = new DownloadRecord
        {
            FileName = i.FileName,
            SaveDirectory = i.SaveDirectory,
            Category = i.Category,
            Tags = i.Tags ?? [],
            TotalBytes = i.TotalBytes,
            CreatedAt = i.CreatedAt > 0 ? i.CreatedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            State = i.Completed ? DownloadState.Completed : DownloadState.Paused,
        };
        if (i.Completed) { r.CompletedAt = i.CompletedAt; r.DoneBytes = i.TotalBytes ?? 0; }
        switch (i.Kind)
        {
            case "http" when HttpInfo.IsMirrorUrl(i.Url ?? ""):
                r.Kind = DownloadKind.Http;
                r.Http = new HttpInfo
                {
                    Url = i.Url!, Referrer = i.Referrer, UserAgent = i.UserAgent, HashAlgorithm = i.HashAlgorithm, HashExpected = i.HashExpected,
                    Mirrors = HttpInfo.CleanMirrors(i.Mirrors, i.Url!),
                };
                return r;
            case "torrent" when i.InfoHash is { Length: > 0 } && (i.Magnet is not null || i.TorrentFile is not null):
                r.Kind = DownloadKind.Torrent;
                r.Torrent = new TorrentInfo
                {
                    InfoHash = i.InfoHash, Magnet = i.Magnet, Sequential = i.Sequential,
                    TorrentFile = i.TorrentFile is { } b ? Convert.FromBase64String(b) : null,
                };
                return r;
            case "media" when HttpInfo.IsMirrorUrl(i.PageUrl ?? ""):
                r.Kind = DownloadKind.Media;
                r.Media = new MediaInfo { PageUrl = i.PageUrl!, FormatSpec = i.FormatSpec ?? "bv*+ba/b", Title = i.Title, AudioOnly = i.AudioOnly, Referrer = i.Referrer };
                return r;
            default:
                return null;
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    public static string ToJson(IEnumerable<DownloadRecord> records) =>
        JsonSerializer.Serialize(new ExportFile { Format = ExportFile.Tag, Items = [.. records.Select(ToItem)] }, Json);

    /// One address per line: the link, magnet or page of each download.
    public static string ToText(IEnumerable<DownloadRecord> records) =>
        string.Join(Environment.NewLine, records.Select(r => r.Http?.Url ?? r.Torrent?.Magnet ?? r.Media?.PageUrl).OfType<string>()) + Environment.NewLine;

    /// A Fluxer export, or null when the text is something else (then it is read as a list of links).
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    public static ExportFile? ParseExport(string text)
    {
        if (!text.TrimStart().StartsWith('{')) return null;
        try
        {
            var f = JsonSerializer.Deserialize<ExportFile>(text, Json);
            return f?.Format == ExportFile.Tag ? f : null;
        }
        catch (JsonException) { return null; }
    }

    /// http(s) links and magnets, one per line; anything else is skipped.
    public static List<string> ParseLinks(string text) =>
        [.. text.Split('\n').Select(l => l.Trim()).Where(l => HttpInfo.IsMirrorUrl(l) || l.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)).Distinct()];
}
