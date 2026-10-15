using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Media;
using Fluxer.Core.Platform.Windows;
using Fluxer.Core.Storage;
using Fluxer.Core.Torrent;

namespace Fluxer.Core;

/// Composition root of the engine; the app and the CLI each own exactly one.
public sealed class FluxerEngine : IAsyncDisposable
{
    private readonly Lazy<TorrentEngineHost> _torrent;
    private readonly SleepBlocker _sleep;

    public FluxerEngine(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        Db = new Database(Path.Combine(dataDirectory, "fluxer.db"));
        Settings = Db.GetSetting<AppSettings>("app");
        Http = new HttpEngine(Settings.Proxy);
        Http.GlobalBucket.BytesPerSecond = Settings.HttpLimit;
        // The torrent engine opens ports and joins DHT; only start it once a torrent exists.
        _torrent = new(() => new TorrentEngineHost(Path.Combine(dataDirectory, "torrent"), Settings.TorrentDownloadLimit, Settings.TorrentUploadLimit));
        Tools = new ToolManager(Path.Combine(dataDirectory, "tools"), Http);
        Queue = new DownloadQueue(Db, () => Settings, Create);
        _sleep = new SleepBlocker(() => Queue.AnyActive);
    }

    public string DataDirectory { get; }
    public Database Db { get; }
    public HttpEngine Http { get; }
    public ToolManager Tools { get; }
    public DownloadQueue Queue { get; }
    public AppSettings Settings { get; private set; }

    public async Task SaveSettingsAsync(AppSettings s)
    {
        var proxyChanged = s.Proxy != Settings.Proxy;
        Settings = s;
        Db.SetSetting("app", s);
        Http.GlobalBucket.BytesPerSecond = s.HttpLimit;
        if (proxyChanged) Http.SetProxy(s.Proxy);
        if (_torrent.IsValueCreated) await _torrent.Value.SetLimitsAsync(s.TorrentDownloadLimit, s.TorrentUploadLimit);
        Queue.Schedule();
    }

    private IDownload Create(DownloadRecord r) => r.Kind switch
    {
        DownloadKind.Http => new HttpDownload(r, Http, Db, () => Settings),
        DownloadKind.Torrent => new TorrentDownload(r, _torrent.Value, Db, () => Settings),
        DownloadKind.Media => new MediaDownload(r, Tools, Db, () => Settings, () => Queue.ActiveMediaCount),
        _ => throw new NotSupportedException(r.Kind.ToString()),
    };

    public IDownload AddHttp(string url, string? fileName = null, RequestContext? ctx = null, string? saveDirectory = null,
        bool start = true, string? hashAlgorithm = null, string? hashExpected = null, bool rejectHtml = false, int? connections = null)
    {
        var r = new DownloadRecord
        {
            Kind = DownloadKind.Http,
            FileName = fileName ?? "",
            SaveDirectory = saveDirectory ?? "",
            Http = new HttpInfo
            {
                Url = url,
                MaxConnections = connections ?? Settings.ConnectionsPerDownload,
                RejectHtml = rejectHtml,
                Referrer = ctx?.Referrer,
                UserAgent = ctx?.UserAgent,
                Cookies = ctx?.Cookies,
                HashAlgorithm = hashAlgorithm,
                HashExpected = hashExpected,
            },
        };
        return Queue.Add(r, start);
    }

    /// Returns null when the same torrent is already in the list.
    public IDownload? AddTorrent(byte[]? torrentFile, string? magnet, HashSet<int>? deselected = null, bool sequential = false, string? saveDirectory = null)
    {
        var hash = TorrentDownload.InfoHashOf(torrentFile, magnet);
        if (Queue.Items.Any(d => d.Record.Torrent?.InfoHash == hash)) return null;
        var r = new DownloadRecord
        {
            Kind = DownloadKind.Torrent,
            FileName = FileNameSanitizer.Sanitize(TorrentDownload.NameOf(torrentFile, magnet)),
            SaveDirectory = saveDirectory ?? (Settings.UseCategoryFolders ? Path.Combine(Settings.DownloadDirectory, "Torrent") : Settings.DownloadDirectory),
            Category = "Torrent",
            Torrent = new TorrentInfo { InfoHash = hash, Magnet = magnet, TorrentFile = torrentFile, Sequential = sequential, DeselectedFiles = deselected is { Count: > 0 } ? deselected : null },
        };
        return Queue.Add(r);
    }

    public IDownload AddMedia(string pageUrl, string title, string formatSpec, bool audioOnly, string? netscapeCookies, string? saveDirectory = null)
    {
        var r = new DownloadRecord
        {
            Kind = DownloadKind.Media,
            FileName = FileNameSanitizer.Sanitize(title),
            SaveDirectory = saveDirectory ?? (Settings.UseCategoryFolders ? Path.Combine(Settings.DownloadDirectory, audioOnly ? "Music" : "Video") : Settings.DownloadDirectory),
            Category = audioOnly ? "Music" : "Video",
            Media = new MediaInfo { PageUrl = pageUrl, Title = title, FormatSpec = formatSpec, AudioOnly = audioOnly, Cookies = netscapeCookies },
        };
        return Queue.Add(r);
    }

    public async ValueTask DisposeAsync()
    {
        await Queue.StopAllAsync();
        if (_torrent.IsValueCreated) await _torrent.Value.DisposeAsync();
        _sleep.Dispose();
        Http.Dispose();
        Db.Dispose();
    }
}
