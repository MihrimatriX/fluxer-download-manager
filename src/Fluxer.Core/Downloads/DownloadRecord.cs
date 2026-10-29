namespace Fluxer.Core.Downloads;

/// Persisted state of one download (row in `downloads` + its kind-specific table).
public sealed class DownloadRecord
{
    public long Id { get; set; }
    public DownloadKind Kind { get; set; }
    public DownloadState State { get; set; }
    public string FileName { get; set; } = "";
    public string SaveDirectory { get; set; } = "";
    public string? Category { get; set; }
    /// Free labels the user gives downloads (filter in the list); stored as a JSON array.
    public List<string> Tags { get; set; } = [];
    public long? TotalBytes { get; set; }
    public long DoneBytes { get; set; }
    public long SpeedLimit { get; set; }
    public long QueueOrder { get; set; }
    public string? Error { get; set; }
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long? CompletedAt { get; set; }

    public HttpInfo? Http { get; set; }
    public TorrentInfo? Torrent { get; set; }
    public MediaInfo? Media { get; set; }

    public string FullPath => Path.Combine(SaveDirectory, FileName);
}

public sealed class HttpInfo
{
    public string Url { get; set; } = "";
    public string? FinalUrl { get; set; }
    public int MaxConnections { get; set; } = 8;
    public bool Resumable { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? Cookies { get; set; }           // "a=b; c=d"; encrypted at rest
    public string? HashAlgorithm { get; set; }     // "MD5" | "SHA256"
    public string? HashExpected { get; set; }
    /// A web page instead of a file means failure (browser captures, URLs that name a file).
    public bool RejectHtml { get; set; }
    /// Other addresses of the same file; Url stays the link that link refresh replaces.
    public List<string> Mirrors { get; set; } = [];

    public RequestContext Context => new(Referrer, UserAgent, Cookies);

    public static bool IsMirrorUrl(string s) =>
        Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// Valid http(s) addresses, trimmed, without duplicates or the primary link itself.
    public static List<string> CleanMirrors(IEnumerable<string>? urls, string primary) =>
        urls is null ? [] : [.. urls.Select(u => u.Trim()).Where(IsMirrorUrl).Where(u => u != primary).Distinct()];
}

public sealed class TorrentInfo
{
    public string InfoHash { get; set; } = "";
    public string? Magnet { get; set; }
    public byte[]? TorrentFile { get; set; }
    public byte[]? FastResume { get; set; }
    public bool Sequential { get; set; }
    public double? SeedRatio { get; set; }
    public int? SeedMinutes { get; set; }
    public long UploadedBytes { get; set; }
    public long SeedingStartedAt { get; set; }
    /// B/s, 0 = only the global limit. (Download limit: DownloadRecord.SpeedLimit.)
    public long UploadLimit { get; set; }
    /// null = all files selected.
    public HashSet<int>? DeselectedFiles { get; set; }
}

public sealed class MediaInfo
{
    public string PageUrl { get; set; } = "";
    public string FormatSpec { get; set; } = "bv*+ba/b";
    public string? Title { get; set; }
    public string? Cookies { get; set; }            // Netscape cookie lines; encrypted at rest
    public bool AudioOnly { get; set; }
    public string? Referrer { get; set; }           // page a captured stream (m3u8/mpd) was playing on
}
