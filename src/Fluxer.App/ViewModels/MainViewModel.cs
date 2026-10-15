using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.App.Services;
using Fluxer.App.Views;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Torrent;

namespace Fluxer.App.ViewModels;

public sealed record CategoryItem(string Key, string Glyph)
{
    public string Title => Key switch
    {
        "all" => Strings.CatAll,
        "Video" => Strings.CatVideo,
        "Music" => Strings.CatMusic,
        "Documents" => Strings.CatDocuments,
        "Archives" => Strings.CatArchives,
        "Programs" => Strings.CatPrograms,
        "Torrent" => Strings.CatTorrent,
        _ => Key,
    };

    public override string ToString() => Title;
}

/// State chip above the list ("Active 3").
public sealed partial class StateFilter(string key) : ObservableObject
{
    public string Key { get; } = key;
    [ObservableProperty] private int _count;
    public string Title => Key switch
    {
        "all" => Strings.FilterAll,
        "active" => Strings.FilterActive,
        "queued" => Strings.FilterQueued,
        "paused" => Strings.FilterPaused,
        "done" => Strings.FilterDone,
        "error" => Strings.FilterError,
        _ => Key,
    };
    public string Label => Count > 0 || Key == "all" ? $"{Title}  {Count}" : Title;
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Label));

    public override string ToString() => Label;

    public bool Matches(DownloadState s) => Key switch
    {
        "active" => s is DownloadState.Starting or DownloadState.Downloading or DownloadState.Verifying or DownloadState.Seeding,
        "queued" => s == DownloadState.Queued,
        "paused" => s == DownloadState.Paused,
        "done" => s == DownloadState.Completed,
        "error" => s is DownloadState.Failed or DownloadState.DiskFull or DownloadState.NeedsLinkRefresh,
        _ => true,
    };
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly Dictionary<long, DownloadItemViewModel> _all = [];
    private readonly ObservableCollection<DownloadItemViewModel> _source = [];

    public MainViewModel(AppHost host)
    {
        _host = host;
        Categories =
        [
            new("all", ""), new("Video", ""), new("Music", ""), new("Documents", ""),
            new("Archives", ""), new("Programs", ""), new("Torrent", ""),
        ];
        StateFilters = [new("all"), new("active"), new("queued"), new("paused"), new("done"), new("error")];
        _selectedCategory = Categories[0];
        _selectedStateFilter = StateFilters[0];
        View = new DataGridCollectionView(_source) { Filter = o => o is DownloadItemViewModel vm && Matches(vm) };
        View.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(DownloadItemViewModel.AddedAt), ListSortDirection.Descending));
        Detail = new DetailViewModel(host);
        Settings = new SettingsViewModel(host);
    }

    public DataGridCollectionView View { get; }
    public IReadOnlyList<CategoryItem> Categories { get; private set; }
    public IReadOnlyList<StateFilter> StateFilters { get; private set; }
    public DetailViewModel Detail { get; }
    public SettingsViewModel Settings { get; }

    /// Multi-selection, kept in sync by the view.
    public List<DownloadItemViewModel> Selection { get; } = [];

    [ObservableProperty] private CategoryItem _selectedCategory;
    [ObservableProperty] private StateFilter _selectedStateFilter;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private DownloadItemViewModel? _selectedItem;
    [ObservableProperty] private string _totalSpeed = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private string _quickLimitText = "";

    partial void OnSelectedCategoryChanged(CategoryItem value) { IsSettingsOpen = false; Refilter(); }
    partial void OnSelectedStateFilterChanged(StateFilter value) => Refilter();
    partial void OnSearchChanged(string value) => Refilter();
    partial void OnSelectedItemChanged(DownloadItemViewModel? value) => Detail.Show(value);

    public void Reload()
    {
        _all.Clear();
        _source.Clear();
        foreach (var d in _host.Engine.Queue.Items) _source.Add(Track(d));
        Refilter();
        UpdateQuickLimitText();
        Settings.Load(); // before any view binds to it, so controls never write defaults back
    }

    public void RefreshTexts()
    {
        Categories = [.. Categories.Select(c => c with { })];
        StateFilters = [.. StateFilters.Select(f => new StateFilter(f.Key))];
        SelectedStateFilter = StateFilters[0];
        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(StateFilters));
        foreach (var vm in _all.Values) vm.Refresh();
        UpdateCounts();
        UpdateQuickLimitText();
        Settings.Load();
    }

    public void OnAdded(IDownload d)
    {
        _source.Add(Track(d));
        UpdateCounts();
    }

    public void OnRemoved(IDownload d)
    {
        if (!_all.Remove(d.Id, out var vm)) return;
        _source.Remove(vm);
        if (SelectedItem == vm) SelectedItem = null;
        UpdateCounts();
    }

    public void OnStateChanged(IDownload d)
    {
        if (!_all.TryGetValue(d.Id, out var vm)) return;
        vm.Refresh();
        if (View.Contains(vm) != Matches(vm)) Refilter();
        else UpdateCounts();
        if (SelectedItem == vm) Detail.Show(vm);
    }

    public void OnReordered()
    {
        foreach (var vm in _all.Values) if (vm.Download.State == DownloadState.Queued) vm.Refresh();
    }

    /// ~4 Hz. Only running downloads change between state events, so only they are refreshed.
    public void Tick()
    {
        foreach (var vm in _all.Values)
            if (vm.IsActive || vm.IsRunningState) vm.Refresh();
        Detail.Tick();
    }

    public void UpdateTotals(int active, int queued, long speed)
    {
        TotalSpeed = speed > 0 ? "↓ " + Loc.Speed(speed) : "";
        Summary = string.Format(CultureInfo.CurrentCulture, Strings.SummaryFormat, _all.Count, active, queued);
    }

    private DownloadItemViewModel Track(IDownload d)
    {
        var vm = new DownloadItemViewModel(d, _host.Engine.Queue.QueuePosition);
        vm.Refresh();
        _all[d.Id] = vm;
        return vm;
    }

    private void Refilter()
    {
        var keep = SelectedItem;
        View.Refresh();
        UpdateCounts();
        if (keep is not null && View.Contains(keep)) SelectedItem = keep;
    }

    private void UpdateCounts()
    {
        var inCategory = _all.Values.Where(MatchesCategory).ToList();
        foreach (var f in StateFilters) f.Count = inCategory.Count(vm => f.Matches(vm.Download.State));
        IsEmpty = View.Count == 0;
    }

    private bool MatchesCategory(DownloadItemViewModel vm)
    {
        var d = vm.Download;
        if (Search.Length > 0 && !d.FileName.Contains(Search, StringComparison.CurrentCultureIgnoreCase)) return false;
        return SelectedCategory.Key switch
        {
            "all" => true,
            "Torrent" => d.Kind == DownloadKind.Torrent,
            var cat => d.Record.Category == cat,
        };
    }

    private bool Matches(DownloadItemViewModel vm) => MatchesCategory(vm) && SelectedStateFilter.Matches(vm.Download.State);

    private List<IDownload> Targets =>
        Selection.Count > 0 ? [.. Selection.Select(s => s.Download)] : SelectedItem is { } s1 ? [s1.Download] : [];

    // ================= commands =================

    [RelayCommand]
    private Task AddUrl() => Flows.NewDownloadAsync(_host, [], null, null);

    [RelayCommand]
    private async Task AddTorrent()
    {
        var files = await Dialogs.PickTorrentFilesAsync(_host.Owner);
        foreach (var f in files) await Flows.TorrentAsync(_host, await File.ReadAllBytesAsync(f), null);
    }

    [RelayCommand]
    private void Resume()
    {
        foreach (var d in Targets) _host.Engine.Queue.Resume(d.Id);
    }

    [RelayCommand]
    private async Task Pause()
    {
        foreach (var d in Targets) await _host.Engine.Queue.PauseAsync(d.Id);
    }

    [RelayCommand]
    private async Task TogglePause()
    {
        foreach (var d in Targets) await Toggle(d);
    }

    /// Row button: pause/resume, or open the folder once completed.
    [RelayCommand]
    private async Task ToggleItem(DownloadItemViewModel? vm)
    {
        if (vm is null) return;
        if (vm.IsDone) ShowInFolder(vm.Download);
        else await Toggle(vm.Download);
    }

    private async Task Toggle(IDownload d)
    {
        if (_host.Engine.Queue.IsRunning(d.Id) || d.State == DownloadState.Queued) await _host.Engine.Queue.PauseAsync(d.Id);
        else _host.Engine.Queue.Resume(d.Id);
    }

    [RelayCommand]
    private async Task Delete()
    {
        var targets = Targets;
        if (targets.Count == 0) return;
        var (ok, deleteFiles) = await Dialogs.ConfirmDeleteAsync(_host.Owner, targets.Count);
        if (!ok) return;
        foreach (var d in targets) await _host.Engine.Queue.RemoveAsync(d.Id, deleteFiles);
    }

    /// Removes finished downloads from the list (files stay on disk).
    [RelayCommand]
    private async Task ClearCompleted()
    {
        var done = _host.Engine.Queue.Items.Where(d => d.State == DownloadState.Completed).ToList();
        if (done.Count == 0) return;
        if (!await Dialogs.ConfirmAsync(_host.Owner, Strings.ClearCompleted,
                string.Format(CultureInfo.CurrentCulture, Strings.ClearCompletedConfirm, done.Count))) return;
        foreach (var d in done) await _host.Engine.Queue.RemoveAsync(d.Id, deleteFiles: false);
    }

    [RelayCommand] private void MoveTop() => Reorder(DownloadQueue.Move.Top);
    [RelayCommand] private void MoveUp() => Reorder(DownloadQueue.Move.Up);
    [RelayCommand] private void MoveDown() => Reorder(DownloadQueue.Move.Down);
    [RelayCommand] private void MoveBottom() => Reorder(DownloadQueue.Move.Bottom);

    private void Reorder(DownloadQueue.Move move) =>
        _host.Engine.Queue.Reorder([.. Targets.Select(d => d.Id)], move);

    [RelayCommand]
    private Task PauseAll() => _host.Engine.Queue.PauseAllAsync();

    [RelayCommand]
    private void ResumeAll() => _host.Engine.Queue.ResumeAll();

    [RelayCommand]
    private void OpenFile()
    {
        if (SelectedItem?.Download is { State: DownloadState.Completed } d && (File.Exists(d.Record.FullPath) || Directory.Exists(d.Record.FullPath)))
            Process.Start(new ProcessStartInfo(d.Record.FullPath) { UseShellExecute = true });
    }

    /// Progress / completion window of the selected download (double-click).
    [RelayCommand]
    private void OpenDetails()
    {
        if (SelectedItem?.Download is { } d) _host.OpenDownloadWindow(d);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedItem?.Download is { } d) ShowInFolder(d);
    }

    private static void ShowInFolder(IDownload d)
    {
        var path = d.Record.FullPath;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        else if (Directory.Exists(d.Record.SaveDirectory)) Process.Start("explorer.exe", $"\"{d.Record.SaveDirectory}\"");
    }

    [RelayCommand]
    private async Task CopyUrl()
    {
        var url = SelectedItem?.Download.Record switch
        {
            { Http: { } h } => h.Url,
            { Torrent: { } t } => t.Magnet ?? $"magnet:?xt=urn:btih:{t.InfoHash}",
            { Media: { } m } => m.PageUrl,
            _ => null,
        };
        if (url is not null) await Dialogs.SetClipboardTextAsync(_host.Owner, url);
    }

    [RelayCommand]
    private async Task RefreshLink()
    {
        if (SelectedItem?.Download is not HttpDownload d) return;
        var url = await Dialogs.InputAsync(_host.Owner, Strings.RefreshLinkTitle, Strings.RefreshLinkPrompt, d.Record.Http!.Url);
        if (string.IsNullOrWhiteSpace(url)) return;
        await Flows.RefreshLinkAsync(_host, d, url.Trim(), null);
    }

    [RelayCommand]
    private async Task SpeedLimit()
    {
        if (SelectedItem?.Download is not HttpDownload d) return;
        var text = await Dialogs.InputAsync(_host.Owner, Strings.SpeedLimitTitle, Strings.SpeedLimitPrompt,
            d.SpeedLimit > 0 ? (d.SpeedLimit / 1024).ToString(CultureInfo.CurrentCulture) : "0");
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var kb) && kb >= 0) d.SpeedLimit = kb * 1024;
    }

    [RelayCommand]
    private async Task Paste()
    {
        var text = await Dialogs.GetClipboardTextAsync(_host.Owner);
        if (!string.IsNullOrWhiteSpace(text)) await Flows.AddTextAsync(_host, text);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        Settings.Load();
        Settings.LoadInfo();
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    /// Status-bar limiter: one value for HTTP and torrent download speed (B/s, 0 = off).
    [RelayCommand]
    private async Task SetQuickLimit(string bytesPerSecond)
    {
        var s = _host.Engine.Settings;
        s.HttpLimit = s.TorrentDownloadLimit = long.Parse(bytesPerSecond, CultureInfo.InvariantCulture);
        await _host.Engine.SaveSettingsAsync(s);
        UpdateQuickLimitText();
    }

    public void UpdateQuickLimitText()
    {
        var limit = _host.Engine.Settings.HttpLimit;
        QuickLimitText = limit > 0 ? Loc.Speed(limit) : Strings.Unlimited;
    }

    public async Task DropAsync(IReadOnlyList<string> files, string? text)
    {
        foreach (var f in files.Where(f => f.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)))
            await Flows.TorrentAsync(_host, await File.ReadAllBytesAsync(f), null);
        if (!string.IsNullOrWhiteSpace(text)) await Flows.AddTextAsync(_host, text);
    }
}
