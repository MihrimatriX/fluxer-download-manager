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

/// Detail panel for the selected download: info, live segment map + speed graph, headers, peers.
public sealed partial class DetailViewModel : ObservableObject
{
    private const int HistoryLength = 240; // 60 s at 4 Hz
    private DownloadItemViewModel? _item;
    private int _slowTick;

    [ObservableProperty] private bool _hasItem;
    [ObservableProperty] private bool _isHttp;
    [ObservableProperty] private bool _isTorrent;
    [ObservableProperty] private bool _isMedia;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private SegmentRow[] _segments = [];
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private double[] _speedHistory = [];
    [ObservableProperty] private string _mediaProgress = "";

    public ObservableCollection<InfoRow> Info { get; } = [];
    public ObservableCollection<InfoRow> Headers { get; } = [];
    public ObservableCollection<PeerRow> Peers { get; } = [];
    public ObservableCollection<TrackerRow> Trackers { get; } = [];
    private readonly Queue<double> _history = new();

    public void Show(DownloadItemViewModel? item)
    {
        if (!ReferenceEquals(item, _item)) _history.Clear();
        _item = item;
        HasItem = item is not null;
        if (item is null) return;
        var d = item.Download;
        IsHttp = d is HttpDownload;
        IsTorrent = d is TorrentDownload;
        IsMedia = d is MediaDownload;
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
        Add(Strings.InfoAdded, DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAt).LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
        if (r.Error is { } e) Add(Strings.InfoError, Loc.Error(e));

        Headers.Clear();
        if (d is HttpDownload hd)
            foreach (var (k, v) in hd.ResponseHeaders) Headers.Add(new InfoRow(k, v));

        Peers.Clear();
        Trackers.Clear();
        Tick();

        void Add(string k, string v) => Info.Add(new InfoRow(k, v));
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
        if (d is TorrentDownload td && ++_slowTick % 4 == 0) _ = RefreshPeersAsync(td);
    }

    private async Task RefreshPeersAsync(TorrentDownload td)
    {
        var peers = await td.GetPeersAsync();
        Peers.Clear();
        foreach (var peer in peers.Take(200))
            Peers.Add(new PeerRow(peer.Uri.ToString(), peer.ClientApp.Client.ToString(), Loc.Speed(peer.Monitor.DownloadRate),
                Loc.Speed(peer.Monitor.UploadRate), $"{peer.BitField.PercentComplete:F0}%"));

        Trackers.Clear();
        if (td.Manager is { } m)
            foreach (var tier in m.TrackerManager.Tiers)
                foreach (var tracker in tier.Trackers)
                    Trackers.Add(new TrackerRow(tracker.Uri.ToString(), tracker.Status.ToString()));
    }
}
