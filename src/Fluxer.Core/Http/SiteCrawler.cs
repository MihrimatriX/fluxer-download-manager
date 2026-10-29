using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Fluxer.Core.Downloads;

namespace Fluxer.Core.Http;

/// Collects the files a site links to: reads the start page, follows its page links up to `Depth` levels (same site
/// unless told otherwise), and returns every non-page link it saw, optionally only some extensions. Pages are read
/// a few at a time and at most `MaxPages` of them; a link whose answer is not HTML counts as a file without its
/// body being read.
public static partial class SiteCrawler
{
    public sealed record Options(int Depth = 1, bool SameSite = true, int MaxPages = 100, IReadOnlyCollection<string>? Extensions = null);

    /// Kind: "file", "image" or "media", as the page grabber shows them.
    public sealed record Found(string Url, string Kind);

    public sealed record Result(List<Found> Files, int Pages, int Failed);

    private static readonly HashSet<string> PageExtensions = ["", "html", "htm", "xhtml", "php", "asp", "aspx", "jsp", "cgi", "shtml"];
    private static readonly HashSet<string> Images = ["jpg", "jpeg", "png", "gif", "webp", "avif", "bmp", "svg", "tif", "tiff", "heic"];
    private static readonly HashSet<string> Media = ["mp4", "mkv", "webm", "avi", "mov", "wmv", "m4v", "mp3", "m4a", "flac", "ogg", "opus", "wav", "aac"];
    /// Never worth collecting from a page.
    private static readonly HashSet<string> Ignored = ["css", "js", "mjs", "json", "xml", "ico", "woff", "woff2", "ttf", "eot", "map", "rss", "atom"];
    private const int MaxHtmlBytes = 5 * 1024 * 1024;

    [GeneratedRegex(@"(?:href|src|data-src|data-href|poster)\s*=\s*(?:""(?<u>[^""]*)""|'(?<u>[^']*)'|(?<u>[^\s>""']+))", RegexOptions.IgnoreCase)]
    private static partial Regex LinkAttribute();
    [GeneratedRegex(@"<base\s[^>]*href\s*=\s*[""']?(?<u>[^""'\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BaseHref();
    [GeneratedRegex(@"srcset\s*=\s*[""'](?<s>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex SrcSet();

    public static string Extension(Uri u)
    {
        var name = u.AbsolutePath;
        var slash = name.LastIndexOf('/');
        var dot = name.LastIndexOf('.');
        return dot > slash ? name[(dot + 1)..].ToLowerInvariant() : "";
    }

    public static string KindOf(Uri u) => Extension(u) is var e && Images.Contains(e) ? "image" : Media.Contains(e) ? "media" : "file";

    /// Absolute http(s) links of a page, fragments removed, HTML entities decoded.
    public static IEnumerable<Uri> Links(string html, Uri page)
    {
        var baseUri = BaseHref().Match(html) is { Success: true } b && Uri.TryCreate(page, WebUtility.HtmlDecode(b.Groups["u"].Value), out var bu) ? bu : page;
        var raw = LinkAttribute().Matches(html).Select(m => m.Groups["u"].Value)
            .Concat(SrcSet().Matches(html).SelectMany(m => m.Groups["s"].Value.Split(',').Select(c => c.Trim().Split(' ')[0])));
        foreach (var r in raw)
        {
            var value = WebUtility.HtmlDecode(r).Trim();
            if (value.Length == 0 || value[0] == '#' || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(baseUri, value, out var u) || u.Scheme is not ("http" or "https")) continue;
            yield return new UriBuilder(u) { Fragment = "" }.Uri;
        }
    }

    internal static bool SameSite(Uri a, Uri b) =>
        string.Equals(Root(a.Host), Root(b.Host), StringComparison.OrdinalIgnoreCase);

    /// Second-level labels under country domains ("example.com.tr", "example.co.uk").
    private static readonly HashSet<string> SecondLevel = ["co", "com", "net", "org", "gov", "edu", "ac", "gen", "web", "bel", "k12", "biz", "info", "ne", "or", "go", "gob", "ltd", "plc"];

    /// "cdn.example.org" and "www.example.org" are one site (the registrable name); an address is only itself.
    private static string Root(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;
        var parts = host.Split('.');
        var take = parts.Length >= 3 && parts[^1].Length == 2 && SecondLevel.Contains(parts[^2].ToLowerInvariant()) ? 3 : 2;
        return parts.Length <= take ? host : string.Join('.', parts[^take..]);
    }

    public static async Task<Result> CrawlAsync(HttpClient http, Uri start, Options o, RequestContext? ctx, IProgress<(int Pages, int Files)>? progress, CancellationToken ct)
    {
        var wanted = o.Extensions?.Select(e => e.TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).ToHashSet() ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal) { start.AbsoluteUri };
        var files = new List<Found>();
        var fileSet = new HashSet<string>(StringComparer.Ordinal);
        var level = new List<Uri> { start };
        int pages = 0, failed = 0;
        using var gate = new SemaphoreSlim(4);

        void AddFile(Uri u)
        {
            var ext = Extension(u);
            if (Ignored.Contains(ext) || (wanted.Count > 0 && !wanted.Contains(ext))) return;
            lock (files)
                if (fileSet.Add(u.AbsoluteUri)) files.Add(new Found(u.AbsoluteUri, KindOf(u)));
        }

        // Cancelling stops the crawl and keeps what was found so far.
        try
        {
            for (var depth = 0; depth <= o.Depth && level.Count > 0; depth++)
            {
                var next = new List<Uri>();
                var budget = Math.Max(0, o.MaxPages - pages);
                var batch = level.Take(budget).ToList();
                await Task.WhenAll(batch.Select(async page =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var html = await FetchHtmlAsync(http, page, ctx, ct);
                        if (html is null) { AddFile(page); return; }        // not a page after all: a file behind a page-like link
                        Interlocked.Increment(ref pages);
                        foreach (var link in Links(html, page))
                        {
                            var isPage = PageExtensions.Contains(Extension(link));
                            if (!isPage) { AddFile(link); continue; }
                            if (depth == o.Depth || (o.SameSite && !SameSite(link, start))) continue;
                            lock (seen) if (seen.Add(link.AbsoluteUri)) next.Add(link);
                        }
                    }
                    catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref failed);      // one bad page does not end the crawl
                    }
                    finally
                    {
                        gate.Release();
                        progress?.Report((pages, files.Count));
                    }
                }));
                level = next;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        lock (files) return new Result([.. files], pages, failed);
    }

    /// The page's text, or null when the answer is not HTML (its body is not read).
    private static async Task<string?> FetchHtmlAsync(HttpClient http, Uri page, RequestContext? ctx, CancellationToken ct)
    {
        using var req = HttpEngine.NewRequest(HttpMethod.Get, page.AbsoluteUri, ctx);
        req.Headers.Remove("Accept");
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.5");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        res.EnsureSuccessStatusCode();
        var type = res.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;
        await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int n;
        while ((n = await stream.ReadAsync(chunk, cts.Token)) > 0 && buffer.Length < MaxHtmlBytes) buffer.Write(chunk, 0, n);
        var charset = res.Content.Headers.ContentType?.CharSet;
        Encoding encoding;
        try { encoding = charset is { Length: > 0 } ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8; }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
