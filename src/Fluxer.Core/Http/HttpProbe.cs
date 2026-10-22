using System.Net.Http.Headers;
using System.Text;
using Fluxer.Core.Downloads;

namespace Fluxer.Core.Http;

public enum ProbeKind { File, Html, Torrent, Error }

public sealed record ProbeResult(ProbeKind Kind, int Status, long? Size, string? FileName, string? MediaType, bool Resumable);

/// Asks for the first 64 bytes to learn what a URL really serves before committing to it.
/// Sites behind Cloudflare, one-time links or session checks answer non-browser clients with an
/// HTML page; a captured download must not turn into "f3a87…0362.html".
public static class HttpProbe
{
    private const int Sniff = 64;
    public const int MaxTorrentBytes = 10 * 1024 * 1024;

    public static async Task<ProbeResult> RunAsync(HttpEngine engine, string url, RequestContext? ctx, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var req = HttpEngine.NewRequest(HttpMethod.Get, url, ctx);
            req.Headers.Range = new RangeHeaderValue(0, Sniff - 1);
            using var resp = await engine.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)resp.StatusCode;
            if (status is not (200 or 206)) return new(ProbeKind.Error, status, null, null, null, false);

            var head = new byte[Sniff];
            var read = 0;
            await using (var s = await resp.Content.ReadAsStreamAsync(timeout.Token))
            {
                int n;
                while (read < Sniff && (n = await s.ReadAsync(head.AsMemory(read), timeout.Token)) > 0) read += n;
            }

            var partial = status == 206;
            var size = partial ? resp.Content.Headers.ContentRange?.Length : resp.Content.Headers.ContentLength;
            var media = resp.Content.Headers.ContentType?.MediaType;
            var finalUri = resp.RequestMessage?.RequestUri ?? new Uri(url);
            var name = FileNameResolver.Resolve(resp.Content.Headers.ContentDisposition, finalUri, media);
            return new(Classify(media, head.AsSpan(0, read)), status, size, name, media, partial && size is not null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Serilog.Log.Information("Probe of {Host} failed: {Error}", new Uri(url).Host, e.InnerException?.Message ?? e.Message);
            return new(ProbeKind.Error, 0, null, null, null, false);
        }
    }

    /// Probes with the browser's headers first. Some servers drop or challenge a request that claims
    /// to be Chrome but does not have Chrome's TLS fingerprint; if that happens, retry once with our
    /// own User-Agent and, when that works, return the context that worked for the download.
    public static async Task<(ProbeResult Result, RequestContext? Context)> RunWithFallbackAsync(
        HttpEngine engine, string url, RequestContext? ctx, CancellationToken ct)
    {
        var first = await RunAsync(engine, url, ctx, ct);
        if (first.Kind is ProbeKind.File or ProbeKind.Torrent || ctx?.UserAgent is null) return (first, ctx);
        var plain = ctx with { UserAgent = null };
        var second = await RunAsync(engine, url, plain, ct);
        if (second.Kind is not (ProbeKind.File or ProbeKind.Torrent)) return (first, ctx);
        Serilog.Log.Information("Probe of {Host}: browser User-Agent refused, using Fluxer's", new Uri(url).Host);
        return (second, plain);
    }

    internal static ProbeKind Classify(string? mediaType, ReadOnlySpan<byte> head)
    {
        if (mediaType is "application/x-bittorrent" || LooksLikeTorrent(head)) return ProbeKind.Torrent;
        if (mediaType is "text/html" or "application/xhtml+xml" || LooksLikeHtml(head)) return ProbeKind.Html;
        return ProbeKind.File;
    }

    /// A .torrent is a bencoded dictionary whose first key is one of a few well-known names.
    internal static bool LooksLikeTorrent(ReadOnlySpan<byte> head)
    {
        var s = Encoding.ASCII.GetString(head);
        return s.StartsWith('d') && (s.Contains(":announce", StringComparison.Ordinal) || s.Contains(":info", StringComparison.Ordinal)
               || s.Contains(":created by", StringComparison.Ordinal) || s.Contains(":comment", StringComparison.Ordinal)
               || s.Contains(":url-list", StringComparison.Ordinal) || s.Contains(":creation date", StringComparison.Ordinal));
    }

    internal static bool LooksLikeHtml(ReadOnlySpan<byte> head)
    {
        var s = Encoding.UTF8.GetString(head).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return s.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || s.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    /// Whole .torrent into memory (they are small); null if it is not one.
    public static async Task<byte[]?> FetchTorrentAsync(HttpEngine engine, string url, RequestContext? ctx, CancellationToken ct)
    {
        using var req = HttpEngine.NewRequest(HttpMethod.Get, url, ctx);
        using var resp = await engine.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > MaxTorrentBytes) return null;
        using var buffer = new MemoryStream();
        await using (var s = await resp.Content.ReadAsStreamAsync(ct))
        {
            var chunk = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxTorrentBytes) return null;
            }
        }
        var bytes = buffer.ToArray();
        return LooksLikeTorrent(bytes.AsSpan(0, Math.Min(Sniff, bytes.Length))) ? bytes : null;
    }
}
