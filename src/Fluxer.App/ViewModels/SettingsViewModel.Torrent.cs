using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.Core.Downloads;
using Fluxer.Core.Torrent;

namespace Fluxer.App.ViewModels;

/// One Torznab indexer in Settings → Torrent.
public sealed partial class IndexerRow(Action changed) : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _status = "";
    public bool Loading { get; set; }
    private string? _blob;
    private string _loadedKey = "";

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!Loading && e.PropertyName != nameof(Status)) changed();
    }

    public static IndexerRow From(TorznabIndexer ix, Action changed)
    {
        var row = new IndexerRow(changed) { Loading = true, _blob = ix.ApiKeyProtected };
        row.Name = ix.Name;
        row.Url = ix.Url;
        row.ApiKey = row._loadedKey = ix.ApiKey ?? "";
        row.Loading = false;
        return row;
    }

    public TorznabIndexer ToIndexer()
    {
        var ix = new TorznabIndexer { Name = Name.Trim().Length > 0 ? Name.Trim() : "Torznab", Url = Url.Trim() };
        if (ApiKey == _loadedKey) ix.ApiKeyProtected = _blob;
        else
        {
            ix.ApiKey = ApiKey.Length > 0 ? ApiKey.Trim() : null;
            (_blob, _loadedKey) = (ix.ApiKeyProtected, ApiKey);
        }
        return ix;
    }
}

public sealed partial class SettingsViewModel
{
    [ObservableProperty] private bool _trackerListOn;
    [ObservableProperty] private string _trackerListUrl = TrackerList.Suggested;
    [ObservableProperty] private string _extraTrackersText = "";
    [ObservableProperty] private string _trackerStatus = "";
    [ObservableProperty] private bool _ipFilterOn;
    [ObservableProperty] private string _ipFilterSource = "";
    [ObservableProperty] private string _ipFilterStatus = "";
    [ObservableProperty] private string _bannedText = "";
    public ObservableCollection<IndexerRow> IndexerRows { get; } = [];

    private void LoadTorrentExtras(AppSettings s)
    {
        TrackerListOn = s.TrackerListUrl is not null;
        TrackerListUrl = s.TrackerListUrl ?? TrackerList.Suggested;
        ExtraTrackersText = string.Join(Environment.NewLine, s.ExtraTrackers);
        IpFilterOn = s.IpFilterSource is not null;
        IpFilterSource = s.IpFilterSource ?? "";
        IndexerRows.Clear();
        foreach (var ix in s.Indexers) IndexerRows.Add(IndexerRow.From(ix, QueueSave));
        RefreshTorrentStatus();
    }

    private void RefreshTorrentStatus()
    {
        var s = _host.Engine.Settings;
        TrackerStatus = s.TrackerListUrl is null ? ""
            : s.TrackerListAt == 0 ? Strings.TrackerListPending
            : string.Format(CultureInfo.CurrentCulture, Strings.TrackerListStatus, s.TrackerListCache.Count,
                DateTimeOffset.FromUnixTimeSeconds(s.TrackerListAt).ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        IpFilterStatus = _host.IpFilterStatus;
        BannedText = s.BannedPeers.Count > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.BannedCount, s.BannedPeers.Count) : Strings.BannedNone;
    }

    /// Returns what changed, so the save can fetch the lists that need it.
    private (bool Trackers, bool Filter) SaveTorrentExtras(AppSettings s, AppSettings old)
    {
        s.TrackerListUrl = TrackerListOn && Uri.TryCreate(TrackerListUrl.Trim(), UriKind.Absolute, out var list) && list.Scheme is "http" or "https"
            ? TrackerListUrl.Trim() : null;
        s.ExtraTrackers = TrackerList.Parse(ExtraTrackersText);
        s.IpFilterSource = IpFilterOn && !string.IsNullOrWhiteSpace(IpFilterSource) ? IpFilterSource.Trim() : null;
        s.Indexers = [.. IndexerRows.Select(r => r.ToIndexer()).Where(i => Uri.TryCreate(i.Url, UriKind.Absolute, out _))];
        if (s.TrackerListUrl != old.TrackerListUrl) { s.TrackerListCache = []; s.TrackerListAt = 0; }
        return (s.TrackerListUrl is not null && s.TrackerListUrl != old.TrackerListUrl, s.IpFilterSource != old.IpFilterSource);
    }

    /// After a save that changed a list's source: fetch it now rather than at the next hourly check.
    private async Task FetchChangedListsAsync(bool trackers, bool filter)
    {
        if (trackers) await _host.RefreshTrackerListAsync();
        if (filter) await _host.RefreshIpFilterAsync(download: true);
        RefreshTorrentStatus();
    }

    [RelayCommand]
    private async Task RefreshTrackers()
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); await SaveAsync(); }
        TrackerStatus = Strings.TrackerListPending;
        var n = await _host.RefreshTrackerListAsync();
        RefreshTorrentStatus();
        if (n is null) TrackerStatus = Strings.TrackerListFailed;
    }

    [RelayCommand]
    private async Task BrowseIpFilter()
    {
        if (_host.Owner is not { } w) return;
        var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.IpFilter,
            FileTypeFilter = [new FilePickerFileType(Strings.IpFilter) { Patterns = ["*.dat", "*.p2p", "*.txt", "*.gz", "*"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) IpFilterSource = path;
    }

    [RelayCommand]
    private async Task RefreshIpFilter()
    {
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); await SaveAsync(); }
        IpFilterStatus = Strings.TrackerListPending;
        await _host.RefreshIpFilterAsync(download: true);
        RefreshTorrentStatus();
    }

    [RelayCommand]
    private async Task ClearBanned()
    {
        var s = _host.Engine.Settings;
        s.BannedPeers = [];
        await _host.Engine.SaveSettingsAsync(s);
        RefreshTorrentStatus();
    }

    [RelayCommand]
    private void AddIndexer()
    {
        IndexerRows.Add(IndexerRow.From(new TorznabIndexer { Name = "Jackett", Url = "http://127.0.0.1:9117/api/v2.0/indexers/all/results/torznab/api" }, QueueSave));
        QueueSave();
    }

    [RelayCommand]
    private void RemoveIndexer(IndexerRow row)
    {
        IndexerRows.Remove(row);
        QueueSave();
    }

    /// One search, to show the address and key work.
    [RelayCommand]
    private async Task TestIndexer(IndexerRow row)
    {
        row.Status = Strings.TorznabSearching;
        try
        {
            var found = await Torznab.SearchAsync(_host.Engine.Http.Client, row.ToIndexer(), "ubuntu", null, CancellationToken.None);
            row.Status = string.Format(CultureInfo.CurrentCulture, Strings.TorznabTestOk, found.Count);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Xml.XmlException or UriFormatException)
        {
            row.Status = string.Format(CultureInfo.CurrentCulture, Strings.TorznabTestFailed, e.Message);
        }
    }
}
