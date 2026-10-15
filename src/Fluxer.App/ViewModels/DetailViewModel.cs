using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Fluxer.App.Services;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Media;
using Fluxer.Core.Torrent;

namespace Fluxer.App.ViewModels;

public sealed record InfoRow(string Label, string Value);
public sealed record PeerRow(string Address, string Client, string Down, string Up, string Progress);
public sealed record TrackerRow(string Url, string Status);

/// A file inside a torrent; unticking it stops downloading that file (applied live).
public sealed partial class TorrentFileItem(int index, string path, long length, Action changed) : ObservableObject
{
    public int Index { get; } = index;
    public string Path { get; } = path;
    public string SizeText { get; } = Loc.Bytes(length);
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _percentText = "";
    [ObservableProperty] private double _percent;
    public bool Loading { get; set; }
    partial void OnIsSelectedChanged(bool value) { if (!Loading) changed(); }
}

/// Detail panel for the selected download: info, live segment map + speed graph, headers,
/// and for torrents: files, peers/trackers and per-torrent controls.
public sealed partial class DetailViewModel(AppHost host) : ObservableObject
{
    private const int HistoryLength = 240; // 60 s at 4 Hz
    private readonly Queue<double> _history = new();
    private DownloadItemViewModel? _item;
    private int _slowTick;
    private bool _loading;

    [ObservableProperty] private bool _hasItem;
    [ObservableProperty] private bool _isHttp;
    /// Tab order: general, activity, files, peers, torrent options (torrent only), headers (HTTP only).
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isTorrent;
    [ObservableProperty] private bool _isMedia;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private SegmentRow[] _segments = [];
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private double[] _speedHistory = [];
    [ObservableProperty] private string _mediaProgress = "";
    [ObservableProperty] private bool _filesPending;
    // per-torrent controls
    [ObservableProperty] private decimal? _torrentDownKb;
    [ObservableProperty] private decimal? _torrentUpKb;
    [ObservableProperty] private bool _sequential;
    [ObservableProperty] private bool _seedOverride;
    [ObservableProperty] private decimal? _seedRatio;
    [ObservableProperty] private decimal? _seedMinutes;

    public ObservableCollection<InfoRow> Info { get; } = [];
    public ObservableCollection<InfoRow> Headers { get; } = [];
    public ObservableCollection<PeerRow> Peers { get; } = [];
    public ObservableCollection<TrackerRow> Trackers { get; } = [];
    public ObservableCollection<TorrentFileItem> Files { get; } = [];

    public void Show(DownloadItemViewModel? item)
    {
        var changed = !ReferenceEquals(item, _item);
        if (changed) _history.Clear();
        _item = item;
        HasItem = item is not null;
        if (item is null) return;
        var d = item.Download;
        IsHttp = d is HttpDownload;
        IsTorrent = d is TorrentDownload;
        IsMedia = d is MediaDownload;
        // A tab the new kind hides would stay selected (and show stale content): fall back to General.
        if (SelectedTab is >= 2 and <= 4 && !IsTorrent || SelectedTab == 5 && !IsHttp) SelectedTab = 0;
        Title = item.Name;

        var r = d.Record;
        Info.Clear();
        Add(Strings.InfoStatus, item.StatusText);
        Add(Strings.InfoSize, Loc.Bytes(r.TotalBytes));
        Add(Strings.InfoSavedTo, r.FullPath);
        if (r.Http is { } h)
        {
            Add(Strings.InfoUrl, h.Url);
            if (h.FinalUrl is { } f && f != h.Url) Add(Strings.InfoFinalUrl, f);
            Add(Strings.InfoResumable, h.Resumable ? Strings.Yes : Strings.No);
            Add(Strings.InfoConnections, h.MaxConnections.ToString(CultureInfo.CurrentCulture));
            if (h.Referrer is { } refr) Add(Strings.InfoReferrer, refr);
        }
        if (r.Torrent is { } t)
        {
            Add(Strings.InfoInfoHash, t.InfoHash);
            if (d is TorrentDownload td) Add(Strings.InfoUploaded, Loc.Bytes(td.UploadedBytes));
        }
        if (r.Media is { } m) Add(Strings.InfoUrl, m.PageUrl);
        Add(Strings.InfoAdded, item.AddedText);
        if (r.Error is { } e) Add(Strings.InfoError, Loc.Error(e));

        Headers.Clear();
        if (d is HttpDownload hd)
            foreach (var (k, v) in hd.ResponseHeaders) Headers.Add(new InfoRow(k, v));

        if (changed)
        {
            Peers.Clear();
            Trackers.Clear();
            if (d is TorrentDownload tor) LoadTorrent(tor);
        }
        Tick();

        void Add(string k, string v) => Info.Add(new InfoRow(k, v));
    }

    private void LoadTorrent(TorrentDownload td)
    {
        _loading = true;
        var info = td.Record.Torrent!;
        TorrentDownKb = td.Record.SpeedLimit / 1024;
        TorrentUpKb = info.UploadLimit / 1024;
        Sequential = info.Sequential;
        SeedOverride = info.SeedRatio is not null || info.SeedMinutes is not null;
        SeedRatio = (decimal)(info.SeedRatio ?? host.Engine.Settings.SeedRatio);
        SeedMinutes = info.SeedMinutes ?? host.Engine.Settings.SeedMinutes;
        _loading = false;
        Files.Clear();
        SyncFiles(td);
    }

    /// Fills the list once metadata exists, then just updates per-file progress.
    private void SyncFiles(TorrentDownload td)
    {
        var rows = td.GetFiles();
        FilesPending = rows.Count == 0;
        if (Files.Count != rows.Count)
        {
            Files.Clear();
            foreach (var r in rows)
                Files.Add(new TorrentFileItem(r.Index, r.Path, r.Length, () => _ = ApplySelectionAsync(td)) { Loading = true, IsSelected = r.Selected });
            foreach (var f in Files) f.Loading = false;
        }
        foreach (var r in rows)
        {
            var f = Files[r.Index];
            f.Percent = r.Percent;
            f.PercentText = Loc.Percent(r.Percent / 100);
        }
    }

    private async Task ApplySelectionAsync(TorrentDownload td)
    {
        var deselected = Files.Where(f => !f.IsSelected).Select(f => f.Index).ToHashSet();
        if (deselected.Count == Files.Count) // at least one file must stay selected
        {
            Files[0].Loading = true;
            Files[0].IsSelected = true;
            Files[0].Loading = false;
            deselected.Remove(0);
        }
        await td.SetSelectionAsync(deselected);
        _item?.Refresh();
    }

    partial void OnTorrentDownKbChanged(decimal? value) => _ = ApplyLimitsAsync();
    partial void OnTorrentUpKbChanged(decimal? value) => _ = ApplyLimitsAsync();
    partial void OnSequentialChanged(bool value)
    {
        if (!_loading && _item?.Download is TorrentDownload td) _ = td.SetSequentialAsync(value);
    }
    partial void OnSeedOverrideChanged(bool value) => ApplySeed();
    partial void OnSeedRatioChanged(decimal? value) => ApplySeed();
    partial void OnSeedMinutesChanged(decimal? value) => ApplySeed();

    private async Task ApplyLimitsAsync()
    {
        if (_loading || _item?.Download is not TorrentDownload td) return;
        await td.SetLimitsAsync((long)(TorrentDownKb ?? 0) * 1024, (long)(TorrentUpKb ?? 0) * 1024);
    }

    private void ApplySeed()
    {
        if (_loading || _item?.Download is not TorrentDownload td) return;
        if (SeedOverride) td.SetSeedRules((double)(SeedRatio ?? 1), (int)(SeedMinutes ?? 60));
        else td.SetSeedRules(null, null);
    }

    public void Tick()
    {
        if (_item is null) return;
        var d = _item.Download;
        _item.Refresh();
        var p = d.GetProgress();
        _history.Enqueue(p.BytesPerSecond);
        while (_history.Count > HistoryLength) _history.Dequeue();
        SpeedHistory = [.. _history];
        TotalBytes = p.TotalBytes ?? 0;
        if (d is HttpDownload hd) Segments = hd.GetSegments();
        if (d is MediaDownload md)
        {
            var (i, n) = md.Fragments;
            MediaProgress = n > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.FragmentFormat, i, n) : "";
        }
        if (d is TorrentDownload td && ++_slowTick % 4 == 0)
        {
            SyncFiles(td);
            _ = RefreshPeersAsync(td);
        }
    }

    private async Task RefreshPeersAsync(TorrentDownload td)
    {
        var peers = await td.GetPeersAsync();
        Peers.Clear();
        foreach (var peer in peers.OrderByDescending(p => p.Monitor.DownloadRate).Take(200))
            Peers.Add(new PeerRow(peer.Uri.ToString(), peer.ClientApp.Client.ToString(), Loc.Speed(peer.Monitor.DownloadRate),
                Loc.Speed(peer.Monitor.UploadRate), Loc.Percent(peer.BitField.PercentComplete / 100)));

        Trackers.Clear();
        if (td.Manager is { } m)
            foreach (var tier in m.TrackerManager.Tiers)
                foreach (var tracker in tier.Trackers)
                    Trackers.Add(new TrackerRow(tracker.Uri.ToString(), tracker.Status.ToString()));
    }
}
