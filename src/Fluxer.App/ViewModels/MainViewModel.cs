using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
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
        "active" => Strings.CatActive,
        "done" => Strings.CatDone,
        "Video" => Strings.CatVideo,
        "Music" => Strings.CatMusic,
        "Documents" => Strings.CatDocuments,
        "Archives" => Strings.CatArchives,
        "Programs" => Strings.CatPrograms,
        "Torrent" => Strings.CatTorrent,
        _ => Key,
    };
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly Dictionary<long, DownloadItemViewModel> _all = [];

    public MainViewModel(AppHost host)
    {
        _host = host;
        Categories =
        [
            new("all", ""), new("active", ""), new("done", ""),
            new("Video", ""), new("Music", ""), new("Documents", ""),
            new("Archives", ""), new("Programs", ""), new("Torrent", ""),
        ];
        _selectedCategory = Categories[0];
        Detail = new DetailViewModel();
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];
    public IReadOnlyList<CategoryItem> Categories { get; private set; }
    public DetailViewModel Detail { get; }

    /// Multi-selection, kept in sync by the view.
    public List<DownloadItemViewModel> Selection { get; } = [];

    [ObservableProperty] private CategoryItem _selectedCategory;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private DownloadItemViewModel? _selectedItem;
    [ObservableProperty] private string _totalSpeed = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isEmpty = true;

    partial void OnSelectedCategoryChanged(CategoryItem value) => Refilter();
    partial void OnSearchChanged(string value) => Refilter();
    partial void OnSelectedItemChanged(DownloadItemViewModel? value) => Detail.Show(value);

    public void Reload()
    {
        _all.Clear();
        foreach (var d in _host.Engine.Queue.Items) Track(d);
        Refilter();
    }

    public void RefreshTexts()
    {
        Categories = [.. Categories.Select(c => c with { })];
        OnPropertyChanged(nameof(Categories));
        foreach (var vm in _all.Values) vm.Refresh();
    }

    public void OnAdded(IDownload d)
    {
        var vm = Track(d);
        if (Matches(vm)) Items.Insert(0, vm);
        IsEmpty = Items.Count == 0;
    }

    public void OnRemoved(IDownload d)
    {
        if (!_all.Remove(d.Id, out var vm)) return;
        Items.Remove(vm);
        if (SelectedItem == vm) SelectedItem = null;
        IsEmpty = Items.Count == 0;
    }

    public void OnStateChanged(IDownload d)
    {
        if (!_all.TryGetValue(d.Id, out var vm)) return;
        vm.Refresh();
        var shown = Items.Contains(vm);
        if (shown != Matches(vm)) Refilter();
        if (SelectedItem == vm) Detail.Show(vm);
    }

    /// Called at ~4 Hz by the view with the rows currently realized by the virtualizing panel.
    public void Tick(IEnumerable<DownloadItemViewModel> visible)
    {
        foreach (var vm in visible) vm.Refresh();
        Detail.Tick();
    }

    public void UpdateTotals(int active, int queued, long speed)
    {
        TotalSpeed = speed > 0 ? "↓ " + Loc.Speed(speed) : "";
        Summary = string.Format(CultureInfo.CurrentCulture, Strings.SummaryFormat, _all.Count, active, queued);
    }

    private DownloadItemViewModel Track(IDownload d)
    {
        var vm = new DownloadItemViewModel(d);
        vm.Refresh();
        _all[d.Id] = vm;
        return vm;
    }

    private void Refilter()
    {
        var keep = SelectedItem;
        Items.Clear();
        foreach (var vm in _all.Values.OrderByDescending(v => v.Download.Record.CreatedAt).ThenByDescending(v => v.Id))
            if (Matches(vm)) Items.Add(vm);
        IsEmpty = Items.Count == 0;
        if (keep is not null && Items.Contains(keep)) SelectedItem = keep;
    }

    private bool Matches(DownloadItemViewModel vm)
    {
        var d = vm.Download;
        if (Search.Length > 0 && !d.FileName.Contains(Search, StringComparison.CurrentCultureIgnoreCase)) return false;
        return SelectedCategory.Key switch
        {
            "all" => true,
            "active" => d.State is not (DownloadState.Completed),
            "done" => d.State == DownloadState.Completed,
            "Torrent" => d.Kind == DownloadKind.Torrent,
            var cat => d.Record.Category == cat,
        };
    }

    private IEnumerable<IDownload> Targets =>
        Selection.Count > 0 ? Selection.Select(s => s.Download) : SelectedItem is { } s1 ? [s1.Download] : [];

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
        foreach (var d in Targets.ToList()) await _host.Engine.Queue.PauseAsync(d.Id);
    }

    [RelayCommand]
    private async Task TogglePause()
    {
        foreach (var d in Targets.ToList())
        {
            if (_host.Engine.Queue.IsRunning(d.Id) || d.State == DownloadState.Queued) await _host.Engine.Queue.PauseAsync(d.Id);
            else _host.Engine.Queue.Resume(d.Id);
        }
    }

    [RelayCommand]
    private async Task Delete()
    {
        var targets = Targets.ToList();
        if (targets.Count == 0) return;
        var (ok, deleteFiles) = await Dialogs.ConfirmDeleteAsync(_host.Owner, targets.Count);
        if (!ok) return;
        foreach (var d in targets) await _host.Engine.Queue.RemoveAsync(d.Id, deleteFiles);
    }

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

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedItem?.Download is not { } d) return;
        var path = d.Record.FullPath;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
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
    private Task Settings() => Dialogs.SettingsAsync(_host);

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

    public async Task DropAsync(IReadOnlyList<string> files, string? text)
    {
        foreach (var f in files.Where(f => f.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)))
            await Flows.TorrentAsync(_host, await File.ReadAllBytesAsync(f), null);
        if (!string.IsNullOrWhiteSpace(text)) await Flows.AddTextAsync(_host, text);
    }

    public bool IsTorrentSelected => SelectedItem?.Download is TorrentDownload;
}
