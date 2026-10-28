using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.App.Services;
using Fluxer.App.Views;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Platform.Windows;
using Fluxer.Core.Torrent;

namespace Fluxer.App.ViewModels;

/// Sidebar category with its count.
public sealed partial class CategoryItem(string key, string iconKey) : ObservableObject
{
    public string Key { get; } = key;
    public Avalonia.Media.Geometry? Icon { get; } = Theme.Geometry(iconKey);
    [ObservableProperty] private int _count;

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

/// State pill above the list ("Active 3").
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

    public override string ToString() => $"{Title} {Count}";

    public bool Matches(DownloadState s) => Key switch
    {
        "active" => s is DownloadState.Starting or DownloadState.Downloading or DownloadState.Verifying or DownloadState.Seeding,
        "queued" => s is DownloadState.Queued or DownloadState.Scheduled,
        "paused" => s == DownloadState.Paused,
        "done" => s is DownloadState.Completed or DownloadState.Seeding,
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
            new("all", "IcAll"), new("Video", "IcVideo"), new("Music", "IcMusic"), new("Documents", "IcDoc"),
            new("Archives", "IcArchive"), new("Programs", "IcApp"), new("Torrent", "IcTorrent"),
        ];
        StateFilters = [new("all"), new("active"), new("queued"), new("paused"), new("done"), new("error")];
#pragma warning disable MVVMTK0034 // the setter would also close the settings page (language change rebuilds this)
        _selectedCategory = Categories[0];
#pragma warning restore MVVMTK0034
        _selectedStateFilter = StateFilters[0];
        View = new DataGridCollectionView(_source) { Filter = o => o is DownloadItemViewModel vm && Matches(vm) };
        View.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(DownloadItemViewModel.AddedAt), ListSortDirection.Descending));
        Detail = new DetailViewModel(host);
        Detail.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DetailViewModel.HasItem)) OnPropertyChanged(nameof(ShowDetail)); };
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
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isSettingsOpen;
    /// Null until the settings page is first opened (lazy view creation).
    [ObservableProperty] private SettingsViewModel? _settingsPage;

    // header, sidebar and status bar texts
    [ObservableProperty] private string _headerNote = "";
    [ObservableProperty] private string _targetNote = "";
    [ObservableProperty] private string _mainQueueText = "";
    [ObservableProperty] private string _nightQueueText = "";
    [ObservableProperty] private bool _nightQueueOn;
    [ObservableProperty] private string _diskPath = "";
    [ObservableProperty] private string _diskFreeText = "";
    [ObservableProperty] private double _diskUsed;
    [ObservableProperty] private string _totalDown = "";
    [ObservableProperty] private string _totalUp = "";
    [ObservableProperty] private string _dhtText = "";
    [ObservableProperty] private string _profileText = "";

    // undo toast
    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private bool _toastVisible;

    // ---- layout the user can change (remembered in Settings.Ui) ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SidebarWidth), nameof(SidebarExpanded))] private bool _sidebarCollapsed;
    [ObservableProperty] private bool _toolbarLabels = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowDetail))] private bool _detailVisible;
    private bool _loadingUi;

    public double SidebarWidth => SidebarCollapsed ? 72 : 236;
    public bool SidebarExpanded => !SidebarCollapsed;
    public bool ShowDetail => DetailVisible && Detail.HasItem;
    public bool DownloadsOpen => !IsSettingsOpen;

    partial void OnIsSettingsOpenChanged(bool value) => OnPropertyChanged(nameof(DownloadsOpen));
    partial void OnSidebarCollapsedChanged(bool value) => SaveUi(ui => ui.SidebarCollapsed = value);
    partial void OnToolbarLabelsChanged(bool value) => SaveUi(ui => ui.ToolbarLabels = value);
    partial void OnDetailVisibleChanged(bool value) => SaveUi(ui => ui.DetailVisible = value);

    /// Mirrors Settings.Ui into the bindable properties (startup, after the settings page saved).
    public void LoadUi()
    {
        _loadingUi = true;
        var ui = _host.Engine.Settings.Ui;
        SidebarCollapsed = ui.SidebarCollapsed;
        ToolbarLabels = ui.ToolbarLabels;
        DetailVisible = ui.DetailVisible;
        _loadingUi = false;
        UpdateProfileText();
    }

    private void SaveUi(Action<UiSettings> change)
    {
        if (_loadingUi) return;
        change(_host.Engine.Settings.Ui);
        _ = _host.Engine.SaveSettingsAsync(_host.Engine.Settings);
    }

    [RelayCommand]
    private void SaveLayout() => _host.SaveLayout();

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    [RelayCommand]
    private void ToggleDetail() => DetailVisible = !DetailVisible;

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
        Settings.Load(); // before any view binds to it, so controls never write defaults back
        LoadUi();
        UpdateTotals();
    }

    public void RefreshTexts()
    {
        Categories = [.. Categories.Select(c => new CategoryItem(c.Key, IconKeyOf(c.Key)))];
        StateFilters = [.. StateFilters.Select(f => new StateFilter(f.Key))];
#pragma warning disable MVVMTK0034 // the setter would also close the settings page (language change rebuilds this)
        _selectedCategory = Categories[0];
#pragma warning restore MVVMTK0034
        SelectedStateFilter = StateFilters[0];
        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(StateFilters));
        OnPropertyChanged(nameof(SelectedCategory));
        foreach (var vm in _all.Values) vm.Refresh();
        UpdateCounts();
        UpdateTotals();
        Settings.Load();

        static string IconKeyOf(string key) => key switch
        {
            "Video" => "IcVideo", "Music" => "IcMusic", "Documents" => "IcDoc", "Archives" => "IcArchive",
            "Programs" => "IcApp", "Torrent" => "IcTorrent", _ => "IcAll",
        };
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

    /// Every 2 s from the host: totals, queues, disk, DHT.
    public void UpdateTotals()
    {
        var engine = _host.Engine;
        var s = engine.Settings;
        var items = engine.Queue.Items.Where(d => !_pendingRemove.Contains(d.Id)).ToList();
        var downloading = items.Count(d => d.State is DownloadState.Starting or DownloadState.Downloading or DownloadState.Verifying);
        var seeding = items.Count(d => d.State == DownloadState.Seeding);
        var queued = items.Count(d => d.State is DownloadState.Queued or DownloadState.Scheduled);
        var down = items.Where(d => d.State == DownloadState.Downloading).Sum(d => d.GetProgress().BytesPerSecond);
        var up = items.OfType<TorrentDownload>().Sum(t => t.UploadRate);

        Summary = string.Format(CultureInfo.CurrentCulture, Strings.SummaryFormat, items.Count, downloading + seeding, queued);
        HeaderNote = downloading > 0 ? "· " + string.Format(CultureInfo.CurrentCulture, Strings.HeaderDownloading, downloading) : "";
        TotalDown = "↓ " + (Loc.Speed(down) is { Length: > 0 } a ? a : "0 B/s");
        TotalUp = "↑ " + (Loc.Speed(up) is { Length: > 0 } b ? b : "0 B/s");
        DhtText = engine.DhtNodes is { } n ? string.Format(CultureInfo.CurrentCulture, Strings.DhtNodes, n) : "";
        MainQueueText = string.Format(CultureInfo.CurrentCulture, Strings.Concurrent, s.HttpSlots);
        var night = items.Count(d => d.State == DownloadState.Scheduled);
        NightQueueOn = s.ScheduleStart is not null;
        NightQueueText = (s.ScheduleStart ?? Strings.Manual) + (night > 0 ? $" · {night}" : "");

        UpdateTray(items, downloading, seeding, down, up);

        DiskPath = s.DownloadDirectory;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(s.DownloadDirectory))!);
            DiskFreeText = string.Format(CultureInfo.CurrentCulture, Strings.FreeShort, Loc.Bytes(drive.AvailableFreeSpace));
            DiskUsed = drive.TotalSize > 0 ? 100.0 * (drive.TotalSize - drive.TotalFreeSpace) / drive.TotalSize : 0;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            DiskFreeText = "";
            DiskUsed = 0;
        }
    }

    // ---- tray popup ----

    public ObservableCollection<DownloadItemViewModel> TrayItems { get; } = [];
    private readonly Queue<double> _trayDown = new(), _trayUp = new();
    [ObservableProperty] private string _trayHeader = "";
    [ObservableProperty] private string _trayErrorText = "";
    [ObservableProperty] private double[] _trayDownHistory = [];
    [ObservableProperty] private double[] _trayUpHistory = [];
    [ObservableProperty] private string _downNumber = "0";
    [ObservableProperty] private string _downUnit = "B/s";
    [ObservableProperty] private string _upNumber = "0";
    [ObservableProperty] private string _upUnit = "B/s";
    [ObservableProperty] private string _quickAddText = "";

    private void UpdateTray(List<IDownload> items, int downloading, int seeding, long down, long up)
    {
        TrayHeader = string.Format(CultureInfo.CurrentCulture, Strings.TrayHeader, downloading, seeding);
        var errors = items.Count(d => d.State is DownloadState.Failed or DownloadState.DiskFull or DownloadState.NeedsLinkRefresh);
        TrayErrorText = errors > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.TrayErrors, errors) : "";
        (DownNumber, DownUnit) = Split(down);
        (UpNumber, UpUnit) = Split(up);
        TrayDownHistory = Push(_trayDown, down);
        TrayUpHistory = Push(_trayUp, up);

        var show = items.Where(d => d.State is DownloadState.Downloading or DownloadState.Starting or DownloadState.Seeding)
            .OrderBy(d => d.State == DownloadState.Seeding).Take(4).Select(d => _all.GetValueOrDefault(d.Id)).OfType<DownloadItemViewModel>().ToList();
        if (!show.SequenceEqual(TrayItems))
        {
            TrayItems.Clear();
            foreach (var vm in show) TrayItems.Add(vm);
        }
        foreach (var vm in show) vm.Refresh();

        static (string, string) Split(long bps)
        {
            var text = Loc.Bytes(bps);
            var at = text.LastIndexOf(' ');
            return at > 0 ? (text[..at], text[(at + 1)..] + "/s") : (text, "");
        }
        static double[] Push(Queue<double> q, double v)
        {
            q.Enqueue(v);
            while (q.Count > 30) q.Dequeue();
            return [.. q];
        }
    }

    [RelayCommand]
    private async Task QuickAdd()
    {
        var text = QuickAddText;
        QuickAddText = "";
        if (!string.IsNullOrWhiteSpace(text)) await Flows.AddTextAsync(_host, text);
    }

    /// Tray: "1 download failed · show".
    [RelayCommand]
    private void ShowErrors()
    {
        _host.ShowMainWindow();
        IsSettingsOpen = false;
        SelectedStateFilter = StateFilters.First(f => f.Key == "error");
    }

    [RelayCommand]
    private void ShowWindow() => _host.ShowMainWindow();

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
        var visible = _all.Values.Where(vm => !_pendingRemove.Contains(vm.Id) && MatchesSearch(vm)).ToList();
        foreach (var c in Categories) c.Count = visible.Count(vm => InCategory(vm, c.Key));
        var inCategory = visible.Where(vm => InCategory(vm, SelectedCategory.Key)).ToList();
        foreach (var f in StateFilters) f.Count = inCategory.Count(vm => f.Matches(vm.Download.State));
        IsEmpty = View.Count == 0;
    }

    private bool MatchesSearch(DownloadItemViewModel vm) =>
        Search.Length == 0 || vm.Download.FileName.Contains(Search, StringComparison.CurrentCultureIgnoreCase);

    private static bool InCategory(DownloadItemViewModel vm, string key) => key switch
    {
        "all" => true,
        "Torrent" => vm.Download.Kind == DownloadKind.Torrent,
        var cat => vm.Download.Record.Category == cat,
    };

    private bool Matches(DownloadItemViewModel vm) =>
        !_pendingRemove.Contains(vm.Id) && MatchesSearch(vm) && InCategory(vm, SelectedCategory.Key) && SelectedStateFilter.Matches(vm.Download.State);

    private List<IDownload> Targets =>
        Selection.Count > 0 ? [.. Selection.Select(s => s.Download)] : SelectedItem is { } s1 ? [s1.Download] : [];

    /// "3 selected" next to the toolbar when several rows are ticked.
    public void SelectionChanged() =>
        TargetNote = Selection.Count > 1 ? string.Format(CultureInfo.CurrentCulture, Strings.SelectedCount, Selection.Count) : "";

    // ================= commands =================

    [RelayCommand]
    private Task AddUrl() => Flows.NewDownloadAsync(_host, [], null);

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

    /// Detail panel's main button: pause, resume/retry, or open the finished file.
    [RelayCommand]
    private async Task PrimaryAction()
    {
        if (SelectedItem is not { } vm) return;
        if (vm.IsDone) OpenFileOf(vm.Download);
        else await Toggle(vm.Download);
    }

    private async Task Toggle(IDownload d)
    {
        if (_host.Engine.Queue.IsRunning(d.Id) || d.State is DownloadState.Queued or DownloadState.Scheduled) await _host.Engine.Queue.PauseAsync(d.Id);
        else _host.Engine.Queue.Resume(d.Id);
    }

    // ---- remove with undo: rows disappear at once, the engine forgets them when the toast expires ----

    private readonly HashSet<long> _pendingRemove = [];
    private List<long> _resumeOnUndo = [];
    private IDisposable? _undoTimer;

    /// Toolbar / Delete key: off the list, files stay on disk, undoable for a few seconds.
    [RelayCommand]
    private Task Delete() => RemoveWithUndoAsync(Targets);

    /// Context menu: the old confirm dialog, which can also delete the files.
    [RelayCommand]
    private async Task DeleteWithFiles()
    {
        var targets = Targets;
        if (targets.Count == 0) return;
        var (ok, deleteFiles) = await Dialogs.ConfirmDeleteAsync(_host.Owner, targets.Count);
        if (!ok) return;
        foreach (var d in targets) await _host.Engine.Queue.RemoveAsync(d.Id, deleteFiles);
    }

    /// Removes finished downloads from the list (files stay on disk).
    [RelayCommand]
    private Task ClearCompleted() =>
        RemoveWithUndoAsync([.. _host.Engine.Queue.Items.Where(d => d.State == DownloadState.Completed)]);

    private async Task RemoveWithUndoAsync(IReadOnlyList<IDownload> items)
    {
        if (items.Count == 0) return;
        await CommitPendingRemovalsAsync(); // a second removal makes the first one final
        var q = _host.Engine.Queue;
        _resumeOnUndo = [.. items.Where(d => q.IsRunning(d.Id) || d.State == DownloadState.Queued).Select(d => d.Id)];
        foreach (var d in items)
        {
            _pendingRemove.Add(d.Id);
            if (q.IsRunning(d.Id) || d.State == DownloadState.Queued) await q.PauseAsync(d.Id);
        }
        if (SelectedItem is { } s && _pendingRemove.Contains(s.Id)) SelectedItem = null;
        Refilter();
        UpdateTotals();
        ToastText = string.Format(CultureInfo.CurrentCulture, Strings.RemovedToast, items.Count);
        ToastVisible = true;
        _undoTimer?.Dispose();
        _undoTimer = DispatcherTimer.RunOnce(() => _ = CommitPendingRemovalsAsync(), TimeSpan.FromSeconds(6));
    }

    [RelayCommand]
    private void Undo()
    {
        _undoTimer?.Dispose();
        _pendingRemove.Clear();
        foreach (var id in _resumeOnUndo) _host.Engine.Queue.Resume(id);
        _resumeOnUndo = [];
        ToastVisible = false;
        Refilter();
        UpdateTotals();
    }

    /// Also called on exit, so a pending removal is never lost.
    public async Task CommitPendingRemovalsAsync()
    {
        _undoTimer?.Dispose();
        ToastVisible = false;
        var ids = _pendingRemove.ToList();
        foreach (var id in ids) await _host.Engine.Queue.RemoveAsync(id, deleteFiles: false);
        _pendingRemove.Clear();
        _resumeOnUndo = [];
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

    /// Sends the selected waiting downloads to the night queue.
    [RelayCommand]
    private async Task MoveToNight()
    {
        foreach (var d in Targets.Where(d => d.State is not (DownloadState.Completed or DownloadState.Seeding)))
        {
            await _host.Engine.Queue.PauseAsync(d.Id);
            _host.Engine.Queue.Defer(d.Id);
        }
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (SelectedItem?.Download is { } d) OpenFileOf(d);
    }

    public static void OpenFileOf(IDownload d)
    {
        if (d.State == DownloadState.Completed && (File.Exists(d.Record.FullPath) || Directory.Exists(d.Record.FullPath)))
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

    public static void ShowInFolder(IDownload d)
    {
        var path = d.Record.FullPath;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        else if (Directory.Exists(d.Record.SaveDirectory)) Process.Start("explorer.exe", $"\"{d.Record.SaveDirectory}\"");
    }

    private static string? SourceUrl(DownloadRecord? r) => r switch
    {
        { Http: { } h } => h.Url,
        { Torrent: { } t } => t.Magnet ?? $"magnet:?xt=urn:btih:{t.InfoHash}",
        { Media: { } m } => m.PageUrl,
        _ => null,
    };

    [RelayCommand]
    private async Task CopyUrl()
    {
        if (SourceUrl(SelectedItem?.Download.Record) is { } url) await Dialogs.SetClipboardTextAsync(_host.Owner, url);
    }

    /// Error panel: let the browser try (the page it came from, or the link itself).
    [RelayCommand]
    private void OpenInBrowser()
    {
        var r = SelectedItem?.Download.Record;
        var url = r?.Http?.Referrer ?? SourceUrl(r);
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
    private void OpenSettings() => OpenSettingsAt("general");

    /// Toolbar "Scheduler": straight to the speed and schedule page.
    [RelayCommand]
    private void OpenSchedule() => OpenSettingsAt("speed");

    public void OpenSettingsAt(string section)
    {
        Settings.Load();
        Settings.LoadInfo();
        Settings.Section = section;
        SettingsPage ??= Settings;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    // ---- speed profile pill ----

    [RelayCommand]
    private Task CycleProfile() => _host.UseProfileAsync((_host.Engine.Settings.ActiveProfile + 1) % _host.Engine.Settings.Profiles.Count);

    public void UpdateProfileText()
    {
        var s = _host.Engine.Settings;
        var p = s.Profiles[Math.Clamp(s.ActiveProfile, 0, s.Profiles.Count - 1)];
        var name = ProfileName(s.ActiveProfile);
        var limits = new List<string>();
        if (p.Down > 0) limits.Add("↓" + Loc.Speed(p.Down));
        if (p.Up > 0) limits.Add("↑" + Loc.Speed(p.Up));
        ProfileText = limits.Count == 0 ? name : $"{name} · {string.Join(" ", limits)}";
    }

    public static string ProfileName(int index) => Strings.ProfileNames.Split('|').ElementAtOrDefault(index) ?? $"#{index + 1}";

    public async Task DropAsync(IReadOnlyList<string> files, string? text)
    {
        foreach (var f in files.Where(f => f.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)))
            await Flows.TorrentAsync(_host, await File.ReadAllBytesAsync(f), null);
        if (!string.IsNullOrWhiteSpace(text)) await Flows.AddTextAsync(_host, text);
    }
}
