using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Storage;
using Serilog;

namespace Fluxer.Core.Torrent;

/// One RSS/Atom feed whose new torrent items are added without asking (Settings → Torrent).
public sealed class RssFeed
{
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string? Include { get; set; }        // regex on the item title, case-insensitive; null = everything
    public string? Exclude { get; set; }
    public string? SaveDirectory { get; set; }  // null = the usual torrent folder
}

/// What the poller remembers about a feed, keyed by its URL. Kept under its own settings key: the
/// settings page rewrites AppSettings wholesale and would throw this away.
public sealed class RssFeedState
{
    public const int MaxSeen = 1000;

    /// Ids of items already added (or tried); oldest first, trimmed to MaxSeen.
    public List<string> Seen { get; set; } = [];
    /// False until the first successful fetch; that fetch only fills Seen (see RssFilter.TakeNew).
    public bool Primed { get; set; }
    public DateTime? LastCheck { get; set; }    // UTC, successful or not
    public string? LastError { get; set; }
    public int LastAdded { get; set; }

    public void MarkSeen(string id)
    {
        if (Seen.Contains(id)) return;
        Seen.Add(id);
        if (Seen.Count > MaxSeen) Seen.RemoveRange(0, Seen.Count - MaxSeen);
    }
}

/// A feed entry that points at a torrent. Id is the guid/atom id, or the link when there is none.
public sealed record RssItem(string Id, string Title, string? TorrentUrl, string? Magnet);

/// RSS 2.0 / RSS 1.0 / Atom → torrent items. Namespaces are ignored (feeds mix them freely); entries
/// without a .torrent link or magnet are dropped.
public static class RssParser
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,   // old RSS 0.91 feeds carry a DOCTYPE; never resolve it
        XmlResolver = null,
        IgnoreComments = true,
    };

    // Feeds still declare windows-1254, ISO-8859-9 and friends; .NET only ships UTF/Latin1 without this.
    static RssParser() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    public static IReadOnlyList<RssItem> Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings);
        return Parse(XDocument.Load(reader));
    }

    public static IReadOnlyList<RssItem> Parse(Stream xml)
    {
        using var reader = XmlReader.Create(xml, ReaderSettings);
        return Parse(XDocument.Load(reader));
    }

    private static List<RssItem> Parse(XDocument doc)
    {
        var list = new List<RssItem>();
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName is "item" or "entry"))
            if (ParseItem(e) is { } item) list.Add(item);
        return list;
    }

    private static RssItem? ParseItem(XElement e)
    {
        string? torrent = null, magnet = null, fallback = null;
        foreach (var c in e.Elements())
        {
            var name = c.Name.LocalName;
            var type = (string?)c.Attribute("type");
            var url = name switch
            {
                "enclosure" => (string?)c.Attribute("url"),
                "link" => (string?)c.Attribute("href") ?? c.Value,  // Atom puts it in href, RSS in the text
                "magnetURI" or "magnetUri" => c.Value,              // ezRSS torrent namespace
                "guid" => c.Value,
                _ => null,
            };
            url = url?.Trim();
            if (string.IsNullOrEmpty(url)) continue;
            if (IsMagnet(url)) { magnet ??= url; continue; }
            if (!IsHttp(url) || name == "guid") continue;
            if (string.Equals(type, "application/x-bittorrent", StringComparison.OrdinalIgnoreCase)) torrent ??= url;
            else if (EndsWithTorrent(url)) fallback ??= url;
        }
        torrent ??= fallback;
        if (torrent is null && magnet is null) return null;
        var title = Child(e, "title") ?? "";
        var id = Child(e, "guid") ?? Child(e, "id") ?? torrent ?? magnet!;
        return new RssItem(id, title, torrent, magnet);
    }

    private static string? Child(XElement e, string localName) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == localName)?.Value.Trim() is { Length: > 0 } v ? v : null;

    private static bool IsMagnet(string url) =>
        url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) && url.Contains("xt=urn:bt", StringComparison.OrdinalIgnoreCase);

    internal static bool IsHttp(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static bool EndsWithTorrent(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
}

/// Title filters and the "only new items" rule; pure, so tests need no network.
public static class RssFilter
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// Empty/blank = no filter. A pattern that does not compile is an error, not "match all".
    public static bool IsValid(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        try { _ = new Regex(pattern, RegexOptions.None, MatchTimeout); return true; }
        catch (ArgumentException) { return false; }
    }

    /// Include (if set) must match and exclude (if set) must not. Invalid patterns and runaway
    /// matches (timeout) count as "no", so a broken filter never downloads a whole feed.
    public static bool Matches(RssFeed feed, string title)
    {
        if (!string.IsNullOrWhiteSpace(feed.Include) && Test(feed.Include, title) != true) return false;
        if (!string.IsNullOrWhiteSpace(feed.Exclude) && Test(feed.Exclude, title) != false) return false;
        return true;
    }

    private static bool? Test(string pattern, string title)
    {
        try { return Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout); }
        catch (ArgumentException) { return null; }      // invalid pattern
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// Items to add from one fetch: matching and not seen before. The caller marks each one seen once
    /// it was added (or failed for good), so a .torrent that could not be fetched is tried again.
    ///
    /// The first fetch of a feed adds nothing by default: everything it lists is marked seen and only
    /// items published after that are downloaded. Otherwise adding a feed would download its whole
    /// backlog (often 50-100 torrents) at once.
    public static List<RssItem> TakeNew(RssFeed feed, RssFeedState state, IReadOnlyList<RssItem> items)
    {
        if (!state.Primed)
        {
            // Feeds list newest first; mark oldest first so trimming keeps the newest ids.
            foreach (var i in items.Reverse()) state.MarkSeen(i.Id);
            state.Primed = true;
            return [];
        }
        var seen = state.Seen.ToHashSet(StringComparer.Ordinal);
        var batch = new HashSet<string>(StringComparer.Ordinal);
        return [.. items.Where(i => !seen.Contains(i.Id) && batch.Add(i.Id) && Matches(feed, i.Title))];
    }
}

/// Checks the enabled feeds every AppSettings.RssIntervalMinutes and adds new matching items through
/// the same path as the torrent watch folder. One bad feed is logged and skipped; it never stops the rest.
public sealed class RssPoller : IAsyncDisposable
{
    public const int MaxFeedBytes = 5 * 1024 * 1024;
    public const int MaxTorrentBytes = 10 * 1024 * 1024;
    private const string StateKey = "rss";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<HttpClient> _client;
    private readonly Func<AppSettings> _settings;
    private readonly Database _db;
    private readonly Func<byte[]?, string?, string?, IDownload?> _add;
    private readonly Dictionary<string, RssFeedState> _state;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private Task? _loop;
    private bool _force;

    /// add: (torrent file, magnet, save directory) → the new download, or null when already listed.
    public RssPoller(Func<HttpClient> client, Func<AppSettings> settings, Database db, Func<byte[]?, string?, string?, IDownload?> add)
    {
        _client = client;
        _settings = settings;
        _db = db;
        _add = add;
        _state = db.GetSetting<Dictionary<string, RssFeedState>>(StateKey);
    }

    /// Raised after a round of checks (any thread).
    public event Action? Checked;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// Feeds changed: new feeds are fetched (primed) right away, the others keep their schedule.
    public void Reschedule() => Wake();

    /// Settings → "Check now": every enabled feed, regardless of when it was last checked.
    public void CheckNow()
    {
        lock (_lock) _force = true;
        Wake();
    }

    private void Wake() { if (_wake.CurrentCount == 0) _wake.Release(); }

    public RssFeedState? StateOf(string url)
    {
        lock (_lock)
            return _state.TryGetValue(url, out var s)
                ? new RssFeedState { Primed = s.Primed, LastCheck = s.LastCheck, LastError = s.LastError, LastAdded = s.LastAdded }
                : null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Let startup finish before the first fetch.
            await WaitAsync(TimeSpan.FromSeconds(20), ct);
            while (!ct.IsCancellationRequested)
            {
                await CheckDueAsync(ct);
                await WaitAsync(TimeSpan.FromMinutes(1), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct) => await _wake.WaitAsync(delay, ct);

    private async Task CheckDueAsync(CancellationToken ct)
    {
        var s = _settings();
        var interval = TimeSpan.FromMinutes(Math.Clamp(s.RssIntervalMinutes, 5, 24 * 60));
        bool force;
        lock (_lock) { force = _force; _force = false; }
        var feeds = s.RssFeeds.Where(f => f.Enabled && RssParser.IsHttp(f.Url)).DistinctBy(f => f.Url).ToList();
        var any = false;
        foreach (var feed in feeds)
        {
            RssFeedState state;
            lock (_lock) state = _state.TryGetValue(feed.Url, out var st) ? st : _state[feed.Url] = new RssFeedState();
            if (!force && state.Primed && state.LastCheck is { } last && DateTime.UtcNow - last < interval) continue;
            any = true;
            await CheckFeedAsync(feed, state, ct);
        }
        lock (_lock)
        {
            // Forget feeds that were removed from the settings.
            foreach (var url in _state.Keys.Where(u => s.RssFeeds.All(f => f.Url != u)).ToList()) { _state.Remove(url); any = true; }
            if (any) _db.SetSetting(StateKey, _state);
        }
        if (any) Checked?.Invoke();
    }

    private async Task CheckFeedAsync(RssFeed feed, RssFeedState state, CancellationToken ct)
    {
        var host = Uri.TryCreate(feed.Url, UriKind.Absolute, out var u) ? u.Host : "?"; // URLs often carry a passkey
        var added = 0;
        string? error = null;
        try
        {
            if (!RssFilter.IsValid(feed.Include) || !RssFilter.IsValid(feed.Exclude)) throw new FormatException("invalid filter");
            var bytes = await GetAsync(feed.Url, MaxFeedBytes, ct);
            var items = RssParser.Parse(new MemoryStream(bytes));
            List<RssItem> fresh;
            lock (_lock) fresh = RssFilter.TakeNew(feed, state, items);
            foreach (var item in fresh)
            {
                ct.ThrowIfCancellationRequested();
                if (await AddAsync(feed, item, ct) is not { } ok) continue;   // transient: retry next round
                lock (_lock) state.MarkSeen(item.Id);
                if (ok) added++;
            }
            Log.Information("RSS {Host}: {Items} item(s), {Added} added", host, items.Count, added);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            error = e is OperationCanceledException ? "timeout" : e.Message;
            Log.Warning("RSS {Host} failed: {Error}", host, error);
        }
        lock (_lock)
        {
            state.LastCheck = DateTime.UtcNow;
            state.LastError = error;
            state.LastAdded = added;
        }
    }

    /// true = added, false = failed for good or already listed (do not retry), null = try again later.
    private async Task<bool?> AddAsync(RssFeed feed, RssItem item, CancellationToken ct)
    {
        byte[]? file = null;
        if (item.TorrentUrl is { } url)
        {
            try { file = await GetAsync(url, MaxTorrentBytes, ct); }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException
                                          || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                Log.Information("RSS: .torrent for {Title} unavailable: {Error}", item.Title, e.Message);
                if (item.Magnet is null) return null;
            }
        }
        try
        {
            var dir = string.IsNullOrWhiteSpace(feed.SaveDirectory) ? null : feed.SaveDirectory;
            var d = file is not null ? _add(file, null, dir) : _add(null, item.Magnet, dir);
            Log.Information("RSS: {Title} {Result}", item.Title, d is null ? "already in the list" : "added");
            return d is not null;
        }
        catch (Exception e)
        {
            Log.Warning("RSS: {Title} is not a valid torrent: {Error}", item.Title, e.Message);
            return false;
        }
    }

    /// GET with a size cap and timeout. gzip is unpacked by hand (the shared handler leaves bodies
    /// encoded for range downloads); the cap applies to the unpacked size.
    private async Task<byte[]> GetAsync(string url, int maxBytes, CancellationToken ct)
    {
        if (!RssParser.IsHttp(url)) throw new InvalidDataException("not an http(s) URL");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", HttpEngine.DefaultUserAgent);
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        using var resp = await _client().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("response too large");
        await using var raw = await resp.Content.ReadAsStreamAsync(timeout.Token);
        await using var body = resp.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase)
            ? new GZipStream(raw, CompressionMode.Decompress) : raw;
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(buf, timeout.Token)) > 0)
        {
            if (ms.Length + n > maxBytes) throw new InvalidDataException("response too large");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
            try { await _loop; } catch (OperationCanceledException) { }
        lock (_lock) _db.SetSetting(StateKey, _state);
        _stop.Dispose();
        _wake.Dispose();
    }
}
