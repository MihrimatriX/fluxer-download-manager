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
    private MonoTorrent.Torrent? _parsed;
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
    public long Id => Record.Id;
    public DownloadKind Kind => Record.Kind;
    public DownloadState State => Record.State;
    public string FileName => Record.FileName;
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
                await AddExtraTrackersAsync(m);     // a magnet needs them most while it looks for the metadata
                await m.WaitForMetadataAsync(ct);
                if (m.MetadataPath is { } path && File.Exists(path)) Info.TorrentFile = await File.ReadAllBytesAsync(path, CancellationToken.None);
            }
            Record.FileName = m.Torrent!.Name;
            await AddExtraTrackersAsync(m);
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

    private TorrentSettings BuildSettings() => new TorrentSettingsBuilder
    {
        AllowDht = _settings().Dht,
        AllowPeerExchange = _settings().PeerExchange,
        MaximumConnections = Math.Max(5, _settings().MaxPeersPerTorrent),
        CreateContainingDirectory = true,
        MaximumDownloadRate = (int)Math.Min(int.MaxValue, Record.SpeedLimit),
        MaximumUploadRate = (int)Math.Min(int.MaxValue, Info.UploadLimit),
    }.ToSettings();

    /// Per-torrent limits in B/s (0 = global limit only), applied live.
    public async Task SetLimitsAsync(long download, long upload)
    {
        Record.SpeedLimit = Math.Max(0, download);
        Info.UploadLimit = Math.Max(0, upload);
        _db.Update(Record);
        if (_manager is { } m) await m.UpdateSettingsAsync(BuildSettings());
    }

    public async Task SetSequentialAsync(bool sequential)
    {
        Info.Sequential = sequential;
        _db.Update(Record);
        if (_manager is { } m)
            await m.ChangePickerAsync(sequential
                ? new MonoTorrent.PiecePicking.StreamingPieceRequester()
                : new MonoTorrent.PiecePicking.StandardPieceRequester(new MonoTorrent.PiecePicking.PieceRequesterSettings()));
    }

    /// null = use the global setting.
    public void SetSeedRules(double? ratio, int? minutes)
    {
        Info.SeedRatio = ratio;
        Info.SeedMinutes = minutes;
        _db.Update(Record);
    }

    public sealed record FileRow(int Index, string Path, long Length, double Percent, bool Selected);

    /// Empty until metadata is known (magnet links).
    public IReadOnlyList<FileRow> GetFiles()
    {
        if (_manager is { HasMetadata: true } m)
            return [.. m.Files.Select((f, i) => new FileRow(i, f.Path.ToString(), f.Length, f.BitField.PercentComplete, Info.DeselectedFiles?.Contains(i) != true))];
        if (Parsed() is { } t)
            return [.. t.Files.Select((f, i) => new FileRow(i, f.Path.ToString(), f.Length, 0, Info.DeselectedFiles?.Contains(i) != true))];
        return [];
    }

    public int FileCount => _manager is { HasMetadata: true } m ? m.Files.Count : Parsed()?.Files.Count ?? 0;

    /// The .torrent parsed once (for the file list while no manager exists); large torrents take a while to parse.
    private MonoTorrent.Torrent? Parsed()
    {
        if (_parsed is null && Info.TorrentFile is { } bytes && MonoTorrent.Torrent.TryLoad(bytes, out var t)) _parsed = t;
        return _parsed;
    }

    private async Task<TorrentManager> AddAsync()
    {
        var settings = BuildSettings();
        Directory.CreateDirectory(Record.SaveDirectory);
        var m = Info.TorrentFile is { } bytes
            ? await _host.Engine.AddAsync(MonoTorrent.Torrent.Load(bytes), Record.SaveDirectory, settings)
            : await _host.Engine.AddAsync(MagnetLink.Parse(Info.Magnet!), Record.SaveDirectory, settings);
        if (Info.Sequential) await m.ChangePickerAsync(new MonoTorrent.PiecePicking.StreamingPieceRequester());
        return m;
    }

    /// Extra trackers this session added (Settings → Torrent), so they can be taken back.
    private readonly HashSet<string> _ourTrackers = new(StringComparer.OrdinalIgnoreCase);

    /// Adds the user's extra trackers that are missing. A magnet gets them before its metadata says whether the
    /// torrent is private; once it does and it is, they are removed again, so a private swarm is not announced
    /// to public trackers any further.
    private async Task AddExtraTrackersAsync(TorrentManager m)
    {
        var trackers = m.TrackerManager.Tiers.SelectMany(t => t.Trackers).ToList();
        if (m.Torrent is { IsPrivate: true })
        {
            foreach (var t in trackers.Where(t => _ourTrackers.Contains(t.Uri.ToString())))
                await m.TrackerManager.RemoveTrackerAsync(t);
            _ourTrackers.Clear();
            return;
        }
        var known = trackers.Select(t => t.Uri.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var url in _settings().AllExtraTrackers)
        {
            try
            {
                var uri = new Uri(url);             // compared as MonoTorrent prints them
                if (known.Contains(uri.ToString())) continue;
                await m.TrackerManager.AddTrackerAsync(uri);
                _ourTrackers.Add(uri.ToString());
                added++;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or UriFormatException or InvalidOperationException) { }
        }
        if (added > 0) Log.Information("Torrent {Id}: {Count} extra trackers", Record.Id, added);
    }

    /// Reads the data back from disk and checks every piece: after files were changed, replaced or moved by hand.
    /// Only while the download is not running (the caller pauses it first).
    /// `wasCompleted`: it was finished (not just stopped while seeding) and stays so when every piece checks out.
    public async Task RecheckAsync(bool wasCompleted)
    {
        var m = _manager ??= await AddAsync();
        if (!m.HasMetadata) throw new InvalidOperationException("no-metadata");
        if (m.State != TorrentState.Stopped) await StopAsync(m);
        SetState(DownloadState.Verifying, null);
        try { await m.HashCheckAsync(autoStart: false); }
        finally
        {
            Record.DoneBytes = (long)(SelectedSize(m) * m.PartialProgress / 100.0);
            SetState(wasCompleted && m.PartialProgress >= 100 ? DownloadState.Completed : DownloadState.Paused, null);
        }
    }

    /// Moves the data to `folder` (move), or points the torrent at a copy that is already there and checks it.
    /// Only while the download is not running.
    public async Task RelocateAsync(string folder, bool move, bool wasCompleted)
    {
        if (move)
        {
            var m = _manager ??= await AddAsync();
            if (m.State != TorrentState.Stopped) await StopAsync(m);
            await m.MoveFilesAsync(folder, false);     // never over files already there
            Record.SaveDirectory = folder;
            _db.Update(Record);
            return;
        }
        await DetachAsync();      // MonoTorrent fixes the save path when it adds the torrent
        Record.SaveDirectory = folder;
        _db.Update(Record);
        await RecheckAsync(wasCompleted);
    }

    /// The piece map drawn in the detail panel, in `buckets` columns: how much of each column is here, how much
    /// of it some connected peer has, and the swarm's distributed copies (like "availability" in other clients).
    public sealed record PieceMap(double[] Have, double[] Available, double Availability, int Pieces);

    public async Task<PieceMap?> GetPieceMapAsync(int buckets)
    {
        if (_manager is not { HasMetadata: true } m) return null;
        var have = m.Bitfield;
        var n = have.Length;
        if (n == 0) return null;
        var peers = await m.GetPeersAsync();
        return await Task.Run(() =>
        {
            var counts = new int[n];
            var seeds = 0;
            foreach (var bf in peers.Take(150).Select(p => p.BitField).Where(bf => bf.Length == n))
            {
                if (bf.AllTrue) { seeds++; continue; }
                for (var i = bf.FirstTrue(); i >= 0 && i < n; i = i + 1 < n ? bf.FirstTrue(i + 1, n - 1) : -1) counts[i]++;
            }
            var b = Math.Clamp(buckets, 1, n);
            double[] haveCols = new double[b], availCols = new double[b];
            for (var c = 0; c < b; c++)
            {
                int from = (int)((long)c * n / b), to = (int)((long)(c + 1) * n / b);
                int had = 0, avail = 0;
                for (var i = from; i < to; i++)
                {
                    if (have[i]) had++;
                    if (counts[i] + seeds > 0) avail++;
                }
                var len = Math.Max(1, to - from);
                (haveCols[c], availCols[c]) = ((double)had / len, (double)avail / len);
            }
            var min = counts.Min() + seeds;
            var above = counts.Count(x => x + seeds > min);
            return new PieceMap(haveCols, availCols, min + (double)above / n, n);
        });
    }

    /// Touches only files whose priority changes: each call is a round trip to the engine loop,
    /// and one check box change must not re-apply every file of a big torrent.
    private async Task ApplySelectionAsync(TorrentManager m)
    {
        for (var i = 0; i < m.Files.Count; i++)
        {
            var priority = Info.DeselectedFiles?.Contains(i) == true ? Priority.DoNotDownload : Priority.Normal;
            if (m.Files[i].Priority != priority) await m.SetFilePriorityAsync(m.Files[i], priority);
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
        _ourTrackers.Clear();       // the next manager starts with the torrent's own trackers again
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
