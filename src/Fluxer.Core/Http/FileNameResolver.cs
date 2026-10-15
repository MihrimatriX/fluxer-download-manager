using System.Net.Http.Headers;
using Fluxer.Core.Platform.Windows;

namespace Fluxer.Core.Http;

public static class FileNameResolver
{
    private static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/zip"] = ".zip", ["application/pdf"] = ".pdf", ["application/x-msdownload"] = ".exe",
        ["application/x-msi"] = ".msi", ["application/x-7z-compressed"] = ".7z", ["application/vnd.rar"] = ".rar",
        ["application/x-rar-compressed"] = ".rar", ["application/gzip"] = ".gz", ["application/x-iso9660-image"] = ".iso",
        ["application/json"] = ".json", ["text/plain"] = ".txt", ["text/html"] = ".html",
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/gif"] = ".gif", ["image/webp"] = ".webp",
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["video/x-matroska"] = ".mkv",
        ["audio/mpeg"] = ".mp3", ["audio/ogg"] = ".ogg", ["audio/flac"] = ".flac",
        ["application/x-bittorrent"] = ".torrent",
    };

    /// Content-Disposition (RFC 6266, filename* first) → last URL path segment → MIME guess.
    public static string Resolve(ContentDispositionHeaderValue? disposition, Uri url, string? mediaType)
    {
        var name = disposition?.FileNameStar ?? Unquote(disposition?.FileName);

        if (string.IsNullOrWhiteSpace(name))
        {
            var last = url.Segments.Length > 0 ? url.Segments[^1] : "";
            name = Uri.UnescapeDataString(last.TrimEnd('/'));
        }

        if (string.IsNullOrWhiteSpace(name)) name = "download";

        if (!Path.HasExtension(name) && mediaType is not null && MimeExtensions.TryGetValue(mediaType, out var ext))
            name += ext;

        return FileNameSanitizer.Sanitize(name);
    }

    private static string? Unquote(string? s) => s?.Trim().Trim('"');
}
