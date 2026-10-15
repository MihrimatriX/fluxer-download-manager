using Fluxer.Core.Downloads;
using Fluxer.Core.Storage;
using MonoTorrent;
using MonoTorrent.Client;
using Serilog;

namespace Fluxer.Core.Torrent;

/// Thin adapter: MonoTorrent does the work, we map its state onto IDownload and apply seed rules.
public sealed class TorrentDownload : IDownload
{
    private readonly TorrentEngineHost _host;
    private readonly Database _db;
    private readonly Func<AppSettings> _settings;
    private TorrentManager? _manager;
    private long _uploadedBase;

    public TorrentDownload(DownloadRecord record, TorrentEngineHost host, Database db, Func<AppSettings> settings)
    {
        Record = record;
        _host = host;
        _db = db;
        _settings = settings;
        _uploadedBase = record.Torrent!.UploadedBytes;
    }

    public DownloadRecord Record { get; }
    private TorrentInfo Info => Record.Torrent!;
    public TorrentManager? Manager => _manager;

    public event Action<IDownload>? StateChanged;

    public DownloadProgress GetProgress()
    {
        if (_manager is not { } m) return new(Record.DoneBytes, Record.TotalBytes, 0, null, 0);
        var total = SelectedSize(m);
        var done = (long)(total * m.PartialProgress / 100.0);
        var speed = m.Monitor.DownloadRate;
        return new(done, total, speed, Downloads.SpeedMeter.Eta(done, total, speed), m.OpenConnections);
    }

    public long UploadRate => _manager?.Monitor.UploadRate ?? 0;
    public long UploadedBytes => _uploadedBase + (_manager?.Monitor.DataBytesSent ?? 0);

    public async Task RunAsync(CancellationToken ct)
    {
        SetState(DownloadState.Starting, null);
        try
        {
            var m = _manager ??= await AddAsync();
            await m.StartAsync();

            if (!m.HasMetadata)
            {
                await m.WaitForMetadataAsync(ct);
                if (m.MetadataPath is { } path && File.Exists(path)) Info.TorrentFile = await File.ReadAllBytesAsync(path, CancellationToken.None);
            }
            Record.FileName = m.Torrent!.Name;
            await ApplySelectionAsync(m);
            Record.TotalBytes = SelectedSize(m);
            _db.Update(Record);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (true)
            {
                var next = m.State switch
                {
                    TorrentState.Seeding => DownloadState.Seeding,
                    TorrentState.Hashing => DownloadState.Verifying,
                    TorrentState.Error => DownloadState.Failed,
                    _ => DownloadState.Downloading,
                };
                if (next == DownloadState.Failed)
                    throw new InvalidOperationException(m.Error?.Exception?.Message ?? "torrent-error");

                if (next == DownloadState.Seeding && Record.State != DownloadState.Seeding)
                {
                    Info.SeedingStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    Record.DoneBytes = Record.TotalBytes ?? 0;
                }
                if (next != Record.State) SetState(next, null);

                if (next == DownloadState.Seeding && SeedGoalReached())
                {
                    await StopAsync(m);
                    Record.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    SetState(DownloadState.Completed, null);
                    return;
                }
                await timer.WaitForNextTickAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var wasSeeding = Record.State == DownloadState.Seeding;
            if (_manager is { } m) await StopAsync(m);
            SetState(wasSeeding ? DownloadState.Completed : DownloadState.Paused, null);
        }
        catch (Exception e)
        {
            Log.Error(e, "Torrent {Id} failed", Record.Id);
            if (_manager is { } m) await StopAsync(m);
            SetState(DownloadState.Failed, e.Message);
        }
    }

    private async Task<TorrentManager> AddAsync()
    {
        var settings = new TorrentSettingsBuilder { AllowDht = true, AllowPeerExchange = true, CreateContainingDirectory = true }.ToSettings();
        Directory.CreateDirectory(Record.SaveDirectory);
        var m = Info.TorrentFile is { } bytes
            ? await _host.Engine.AddAsync(MonoTorrent.Torrent.Load(bytes), Record.SaveDirectory, settings)
            : await _host.Engine.AddAsync(MagnetLink.Parse(Info.Magnet!), Record.SaveDirectory, settings);
        if (Info.Sequential) await m.ChangePickerAsync(new MonoTorrent.PiecePicking.StreamingPieceRequester());
        return m;
    }

    private async Task ApplySelectionAsync(TorrentManager m)
    {
        for (var i = 0; i < m.Files.Count; i++)
        {
            var wanted = Info.DeselectedFiles?.Contains(i) != true;
            await m.SetFilePriorityAsync(m.Files[i], wanted ? Priority.Normal : Priority.DoNotDownload);
        }
    }

    private long SelectedSize(TorrentManager m) =>
        m.HasMetadata ? m.Files.Where((f, i) => Info.DeselectedFiles?.Contains(i) != true).Sum(f => f.Length) : Record.TotalBytes ?? 0;

    private bool SeedGoalReached()
    {
        var s = _settings();
        var ratio = Info.SeedRatio ?? s.SeedRatio;
        var minutes = Info.SeedMinutes ?? s.SeedMinutes;
        var total = Math.Max(1, Record.TotalBytes ?? 1);
        var seeded = TimeSpan.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Info.SeedingStartedAt);
        return (ratio > 0 && UploadedBytes / (double)total >= ratio) || (minutes > 0 && seeded.TotalMinutes >= minutes);
    }

    private async Task StopAsync(TorrentManager m)
    {
        Info.UploadedBytes = UploadedBytes;
        _uploadedBase = Info.UploadedBytes;
        try { await m.StopAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) { Log.Warning(e, "Stopping torrent {Id}", Record.Id); }
        _uploadedBase -= m.Monitor.DataBytesSent; // monitor keeps counting across stop/start of the same manager
    }

    /// Changing the file selection while running is applied live.
    public async Task SetSelectionAsync(HashSet<int> deselected)
    {
        Info.DeselectedFiles = deselected.Count == 0 ? null : deselected;
        if (_manager is { HasMetadata: true } m)
        {
            await ApplySelectionAsync(m);
            Record.TotalBytes = SelectedSize(m);
        }
        _db.Update(Record);
    }

    public async Task<IReadOnlyList<PeerId>> GetPeersAsync() => _manager is { } m ? await m.GetPeersAsync() : [];

    public async Task DetachAsync()
    {
        if (_manager is not { } m) return;
        _manager = null;
        if (m.State != TorrentState.Stopped) await StopAsync(m);
        await _host.Engine.RemoveAsync(m, RemoveMode.CacheDataOnly);
    }

    public void DeleteFiles()
    {
        try
        {
            var path = Record.FullPath;
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException e) { Log.Warning(e, "Deleting torrent data {Id}", Record.Id); }
    }

    private void SetState(DownloadState state, string? error)
    {
        Record.State = state;
        Record.Error = error;
        if (_manager is { } m && m.HasMetadata) Record.DoneBytes = GetProgress().BytesDone;
        _db.Update(Record);
        StateChanged?.Invoke(this);
    }

    /// Info-hash (hex) of a magnet or .torrent, used to reject duplicates.
    public static string InfoHashOf(byte[]? torrentFile, string? magnet) =>
        torrentFile is not null
            ? MonoTorrent.Torrent.Load(torrentFile).InfoHashes.V1OrV2.ToHex()
            : MagnetLink.Parse(magnet!).InfoHashes.V1OrV2.ToHex();

    public static string NameOf(byte[]? torrentFile, string? magnet) =>
        torrentFile is not null ? MonoTorrent.Torrent.Load(torrentFile).Name : MagnetLink.Parse(magnet!).Name ?? "magnet";
}
