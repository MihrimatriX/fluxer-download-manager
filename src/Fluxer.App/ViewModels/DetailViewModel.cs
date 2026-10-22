using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Fluxer.App.Services;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Media;
using Fluxer.Core.Torrent;

namespace Fluxer.App.ViewModels;

public sealed record InfoRow(string Label, string Value);
public sealed record PeerRow(string Address, string Client, string Down, string Up, string Progress, double Have);
public sealed record TrackerRow(string Url, string Status, bool Ok);
/// One HTTP connection (segment): how far it got through its byte range.
public sealed record ConnRow(string Number, double Percent, string Text, IBrush Brush);
public sealed record DetailTab(string Key, string Label);

/// Detail panel (right side) for the selected download: progress, speed graph and tabs —
/// torrents: info, files, peers, trackers, options; HTTP: info, connections, log, headers; media: info, log.
public sealed partial class DetailViewModel(AppHost host) : ObservableObject
{
    private const int HistoryLength = 240; // 60 s at 4 Hz
    private readonly Queue<double> _history = new(), _upHistory = new();
    private int _slowTick;
    private bool _loading;

    [ObservableProperty] private DownloadItemViewModel? _item;
    [ObservableProperty] private bool _hasItem;
    [ObservableProperty] private bool _isHttp;
    [ObservableProperty] private bool _isTorrent;
    [ObservableProperty] private bool _isMedia;
    [ObservableProperty] private string _kindLong = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private IReadOnlyList<DetailTab> _tabs = [];
    [ObservableProperty] private DetailTab? _selectedTab;
    [ObservableProperty] private string _tabKey = "info";
    [ObservableProperty] private double[] _speedHistory = [];
    [ObservableProperty] private double[] _upHistoryValues = [];
    [ObservableProperty] private string _mediaProgress = "";
    [ObservableProperty] private bool _filesPending;
    [ObservableProperty] private bool _noPeers;
    [ObservableProperty] private string _primaryLabel = "";
    // per-torrent controls
    [ObservableProperty] private decimal? _torrentDownKb;
    [ObservableProperty] private decimal? _torrentUpKb;
    [ObservableProperty] private bool _sequential;
    [ObservableProperty] private bool _seedOverride;
    [ObservableProperty] private decimal? _seedRatio;
    [ObservableProperty] private decimal? _seedMinutes;
    /// Folder tree of the selected torrent; built when the Files tab first shows, null otherwise.
    [ObservableProperty] private TorrentTree? _fileTree;

    public ObservableCollection<InfoRow> Info { get; } = [];
    public ObservableCollection<InfoRow> Headers { get; } = [];
    public ObservableCollection<PeerRow> Peers { get; } = [];
    public ObservableCollection<TrackerRow> Trackers { get; } = [];
    public ObservableCollection<ConnRow> Connections { get; } = [];

    partial void OnSelectedTabChanged(DetailTab? value)
    {
        if (value is null) return;
        TabKey = value.Key;
        if (value.Key == "files" && Item?.Download is TorrentDownload td) SyncFiles(td);
    }

    public void Show(DownloadItemViewModel? item)
    {
        var changed = !ReferenceEquals(item, Item);
        if (changed) { _history.Clear(); _upHistory.Clear(); }
        Item = item;
        HasItem = item is not null;
        if (item is null) return;
        var d = item.Download;
        IsHttp = d is HttpDownload;
        IsTorrent = d is TorrentDownload;
        IsMedia = d is MediaDownload;

        if (changed)
        {
            Tabs = IsTorrent
                ? [new("info", Strings.TabInfo), new("files", Strings.TabFiles), new("peers", Strings.TabPeers), new("trackers", Strings.TabTrackers), new("options", Strings.TabTorrentOptions)]
                : IsHttp
                    ? [new("info", Strings.TabInfo), new("conns", Strings.TabConnectionsList), new("log", Strings.TabLog), new("headers", Strings.TabHeaders)]
                    : [new("info", Strings.TabInfo), new("log", Strings.TabLog)];
            // Keep the tab when the new download has it too.
            SelectedTab = Tabs.FirstOrDefault(t => t.Key == TabKey) ?? Tabs[0];
        }

        var r = d.Record;
        KindLong = d switch
        {
            TorrentDownload td => string.Format(CultureInfo.CurrentCulture, Strings.KindTorrentFiles, Math.Max(1, td.FileCount)),
            HttpDownload => Strings.KindHttpMulti,
            _ => Strings.KindMedia,
        };
        ErrorText = item.IsError
            ? string.Format(CultureInfo.CurrentCulture, Strings.ErrorKept, Loc.Error(r.Error) is { Length: > 0 } e ? e : Loc.State(d.State), Loc.Bytes(d.GetProgress().BytesDone))
            : "";
        PrimaryLabel = item.IsActive ? Strings.Pause
            : item.IsError ? Strings.Retry
            : item.IsDone ? Strings.OpenFile
            : Strings.Start;

        Info.Clear();
        Add(Strings.InfoSavedTo, r.SaveDirectory);
        if (r.Http is { } h)
        {
            Add(Strings.InfoUrl, h.Url);
            if (h.FinalUrl is { } f && f != h.Url) Add(Strings.InfoFinalUrl, f);
            Add(Strings.InfoResumable, h.Resumable ? Strings.Yes : Strings.No);
            Add(Strings.InfoConnections, h.MaxConnections.ToString(CultureInfo.CurrentCulture));
            if (h.Referrer is { } refr) Add(Strings.InfoReferrer, refr);
            if (h.Mirrors.Count > 0 && d is HttpDownload mirrored) Add(Strings.InfoMirrors, MirrorSummary(mirrored));
        }
        if (r.Torrent is { } t)
        {
            Add(Strings.InfoInfoHash, t.InfoHash);
            if (d is TorrentDownload td) Add(Strings.InfoUploaded, Loc.Bytes(td.UploadedBytes));
        }
        if (r.Media is { } m) Add(Strings.InfoUrl, m.PageUrl);
        Add(Strings.InfoQueue, d.State == DownloadState.Scheduled ? DownloadItemViewModel.NightQueueLabel() : Strings.MainQueue);
        Add(Strings.InfoAdded, item.AddedText);
        if (r.Error is { } err) Add(Strings.InfoError, Loc.Error(err));

        Headers.Clear();
        if (d is HttpDownload hd)
            foreach (var (k, v) in hd.ResponseHeaders) Headers.Add(new InfoRow(k, v));

        if (changed)
        {
            Peers.Clear();
            Trackers.Clear();
            Connections.Clear();
            if (d is TorrentDownload tor) LoadTorrent(tor);
        }
        Tick();

        void Add(string k, string v) => Info.Add(new InfoRow(k, v));
    }

    /// "3 · 2 in use, 1 dropped" once a run has checked them, otherwise just the count.
    private static string MirrorSummary(HttpDownload d)
    {
        var mirrors = d.GetSources().Where(s => !s.Primary).ToList();
        if (mirrors.All(s => s.Health == SourceHealth.Unchecked)) return mirrors.Count.ToString(CultureInfo.CurrentCulture);
        return string.Format(CultureInfo.CurrentCulture, Strings.MirrorsSummary, mirrors.Count,
            mirrors.Count(s => s.Health == SourceHealth.Healthy), mirrors.Count(s => s.Health is SourceHealth.Rejected or SourceHealth.Retired));
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
        FileTree = null;
        FilesPending = td.FileCount == 0;
        if (TabKey == "files") SyncFiles(td);
    }

    /// Builds the list once metadata exists, then just updates per-file progress.
    /// Called only while the Files tab is showing; other tabs never pay for thousands of rows.
    private void SyncFiles(TorrentDownload td)
    {
        var rows = td.GetFiles();
        FilesPending = rows.Count == 0;
        if (FileTree?.FileCount != rows.Count)
            FileTree = new TorrentTree(rows.Select(r => (r.Index, r.Path, r.Length, r.Selected)), () => _ = ApplySelectionAsync(td));
        FileTree.SetPercents(rows.Select(r => (r.Index, r.Percent)));
    }

    private async Task ApplySelectionAsync(TorrentDownload td)
    {
        if (FileTree is not { } tree) return;
        var deselected = tree.Deselected;
        if (deselected.Count == tree.FileCount) // at least one file must stay selected
        {
            tree.Select(0);
            deselected.Remove(0);
        }
        await td.SetSelectionAsync(deselected);
        Item?.Refresh();
    }

    partial void OnTorrentDownKbChanged(decimal? value) => _ = ApplyLimitsAsync();
    partial void OnTorrentUpKbChanged(decimal? value) => _ = ApplyLimitsAsync();
    partial void OnSequentialChanged(bool value)
    {
        if (!_loading && Item?.Download is TorrentDownload td) _ = td.SetSequentialAsync(value);
    }
    partial void OnSeedOverrideChanged(bool value) => ApplySeed();
    partial void OnSeedRatioChanged(decimal? value) => ApplySeed();
    partial void OnSeedMinutesChanged(decimal? value) => ApplySeed();

    private async Task ApplyLimitsAsync()
    {
        if (_loading || Item?.Download is not TorrentDownload td) return;
        await td.SetLimitsAsync((long)(TorrentDownKb ?? 0) * 1024, (long)(TorrentUpKb ?? 0) * 1024);
    }

    private void ApplySeed()
    {
        if (_loading || Item?.Download is not TorrentDownload td) return;
        if (SeedOverride) td.SetSeedRules((double)(SeedRatio ?? 1), (int)(SeedMinutes ?? 60));
        else td.SetSeedRules(null, null);
    }

    public void Tick()
    {
        if (Item is null) return;
        var d = Item.Download;
        Item.Refresh();
        var p = d.GetProgress();
        Push(_history, d.State == DownloadState.Downloading ? p.BytesPerSecond : 0);
        Push(_upHistory, d is TorrentDownload up ? up.UploadRate : 0);
        SpeedHistory = [.. _history];
        UpHistoryValues = [.. _upHistory];
        if (d is HttpDownload hd) SyncConnections(hd.GetSegments(), Item.IsActive);
        if (d is MediaDownload md)
        {
            var (i, n) = md.Fragments;
            MediaProgress = n > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.FragmentFormat, i, n) : "";
        }
        if (d is TorrentDownload td && ++_slowTick % 4 == 0)
        {
            if (TabKey == "files") SyncFiles(td);
            _ = RefreshPeersAsync(td);
        }

        static void Push(Queue<double> q, double v)
        {
            q.Enqueue(v);
            while (q.Count > HistoryLength) q.Dequeue();
        }
    }

    private void SyncConnections(SegmentRow[] segments, bool active)
    {
        var brush = Theme.Brush(active ? "SystemControlHighlightAccentBrush" : Item!.IsError ? "ErrBrush" : Item.IsDone ? "OkBrush" : "IdleBrush");
        var rows = segments.Select(s =>
        {
            var len = Math.Max(1, s.End - s.Start);
            var done = Math.Clamp(s.Position - s.Start, 0, len);
            return new ConnRow($"#{s.Index + 1}", done * 100.0 / len, $"{Loc.Bytes(done)} / {Loc.Bytes(len)}", brush);
        }).ToList();
        if (rows.Count == Connections.Count && rows.SequenceEqual(Connections)) return;
        Connections.Clear();
        foreach (var r in rows) Connections.Add(r);
    }

    private async Task RefreshPeersAsync(TorrentDownload td)
    {
        var peers = await td.GetPeersAsync();
        Peers.Clear();
        foreach (var peer in peers.OrderByDescending(p => p.Monitor.DownloadRate).Take(200))
            Peers.Add(new PeerRow(peer.Uri.ToString(), peer.ClientApp.Client.ToString(), Loc.Speed(peer.Monitor.DownloadRate),
                Loc.Speed(peer.Monitor.UploadRate), Loc.Percent(peer.BitField.PercentComplete / 100), peer.BitField.PercentComplete));
        NoPeers = Peers.Count == 0;

        Trackers.Clear();
        if (td.Manager is { } m)
            foreach (var tier in m.TrackerManager.Tiers)
                foreach (var tracker in tier.Trackers)
                    Trackers.Add(new TrackerRow(tracker.Uri.ToString(), tracker.Status.ToString(), tracker.Status == MonoTorrent.Trackers.TrackerState.Ok));
    }
}

/// `value == parameter` as text, for "show this panel when the tab key is …".
public sealed class Is : Avalonia.Data.Converters.IValueConverter
{
    public static readonly Is Equal = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}

/// Opacity for something that is configured but switched off.
public sealed class Dim : Avalonia.Data.Converters.IValueConverter
{
    public static readonly Dim WhenOff = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? 1.0 : 0.45;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}
