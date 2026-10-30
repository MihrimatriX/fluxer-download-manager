using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Fluxer.App.ViewModels;

/// One command in the palette: what it says, where it belongs, its shortcut and what it does.
public sealed record PaletteItem(string Title, string Group, string? Shortcut, Geometry? Icon, Action Run)
{
    public bool HasShortcut => !string.IsNullOrEmpty(Shortcut);
}

/// Ctrl+K: every action of the window in one searchable list, so the toolbar can stay small.
/// The list is built when the palette opens (texts follow the language, profiles and filters the current state).
public sealed partial class CommandPaletteViewModel(MainViewModel main) : ObservableObject
{
    private List<PaletteItem> _all = [];

    public ObservableCollection<PaletteItem> Items { get; } = [];
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteItem? _selected;
    [ObservableProperty] private bool _isEmpty;

    partial void OnQueryChanged(string value) => Filter();

    public void Open()
    {
        _all = Build();
        Query = "";
        Filter();
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    /// Runs the highlighted command (Enter, or a click).
    [RelayCommand]
    public void Run(PaletteItem? item = null)
    {
        item ??= Selected;
        if (item is null) return;
        Close();
        item.Run();
    }

    public void Move(int delta)
    {
        if (Items.Count == 0) return;
        var i = Selected is null ? -1 : Items.IndexOf(Selected);
        Selected = Items[(i + delta + Items.Count) % Items.Count];
    }

    private void Filter()
    {
        var words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        bool Has(string text, string w) => compare.IndexOf(text, w, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        // Every word has to appear (in the title or the group); titles that start with the query come first.
        var hits = _all
            .Where(c => words.All(w => Has(c.Title, w) || Has(c.Group, w)))
            .OrderBy(c => words.Length > 0 && compare.IsPrefix(c.Title, Query.Trim(), CompareOptions.IgnoreCase) ? 0 : 1)
            .Take(60)
            .ToList();
        Items.Clear();
        foreach (var h in hits) Items.Add(h);
        Selected = Items.FirstOrDefault();
        IsEmpty = Items.Count == 0;
    }

    private List<PaletteItem> Build()
    {
        var list = new List<PaletteItem>();
        void Add(string group, string title, string? keys, string? icon, Action run) =>
            list.Add(new PaletteItem(title, group, keys, icon is null ? null : Theme.Geometry(icon), run));
        void Cmd(string group, string title, string? keys, string? icon, System.Windows.Input.ICommand command) =>
            Add(group, title, keys, icon, () => { if (command.CanExecute(null)) command.Execute(null); });

        var dl = Strings.PaletteGroupDownloads;
        Cmd(dl, Strings.NewDownload, "Ctrl+N", "IcPlus", main.AddUrlCommand);
        Cmd(dl, Strings.AddTorrent, "Ctrl+O", "IcTorrent", main.AddTorrentCommand);
        Cmd(dl, Strings.PasteLinks, "Ctrl+V", "IcLink", main.PasteCommand);
        Cmd(dl, Strings.CrawlMenu, null, "IcGlobe", main.CrawlSiteCommand);
        Cmd(dl, Strings.SearchTorrents, null, "IcSearch", main.SearchTorrentsCommand);
        Cmd(dl, Strings.ResumeAll, "Ctrl+Shift+R", "IcPlay", main.ResumeAllCommand);
        Cmd(dl, Strings.PauseAll, "Ctrl+Shift+P", "IcPause", main.PauseAllCommand);
        Cmd(dl, Strings.ClearCompleted, null, "IcBroom", main.ClearCompletedCommand);

        var sel = Strings.PaletteGroupSelected;
        Cmd(sel, Strings.Resume, null, "IcPlay", main.ResumeCommand);
        Cmd(sel, Strings.Pause, "Space", "IcPause", main.PauseCommand);
        Cmd(sel, Strings.OpenFile, "Enter", "IcFile", main.OpenFileCommand);
        Cmd(sel, Strings.OpenFolder, null, "IcFolder", main.OpenFolderCommand);
        if (main.SelectedIsArchive) Cmd(sel, Strings.ExtractHere, null, "IcArchive", main.ExtractCommand);
        if (main.SelectedIsTorrent)
        {
            Cmd(sel, Strings.RecheckTorrent, null, "IcRefresh", main.RecheckTorrentCommand);
            Cmd(sel, Strings.RelocateTorrent, null, "IcFolder", main.RelocateTorrentCommand);
        }
        Cmd(sel, Strings.ProgressWindow, null, "IcExternal", main.OpenDetailsCommand);
        Cmd(sel, Strings.CopyUrl, null, "IcLink", main.CopyUrlCommand);
        Cmd(sel, Strings.RefreshLink, null, "IcRefresh", main.RefreshLinkCommand);
        Cmd(sel, Strings.SpeedLimitTitle, null, "IcGauge", main.SpeedLimitCommand);
        Cmd(sel, Strings.MoveTop, "Ctrl+Home", "IcUp", main.MoveTopCommand);
        Cmd(sel, Strings.MoveUp, "Ctrl+Up", "IcUp", main.MoveUpCommand);
        Cmd(sel, Strings.MoveDown, "Ctrl+Down", "IcDown", main.MoveDownCommand);
        Cmd(sel, Strings.MoveBottom, "Ctrl+End", "IcDown", main.MoveBottomCommand);
        Cmd(sel, Strings.MoveToNight, null, "IcClock", main.MoveToNightCommand);
        foreach (var t in main.QueueTargets.Skip(2))
            Add(sel, string.Format(CultureInfo.CurrentCulture, Strings.PaletteMoveToQueue, t.Label), null, "IcClock", () => t.Command.Execute(null));
        Cmd(sel, Strings.Delete, "Del", "IcTrash", main.DeleteCommand);
        Cmd(sel, Strings.DeleteWithFiles, null, "IcTrash", main.DeleteWithFilesCommand);

        var view = Strings.PaletteGroupView;
        foreach (var f in main.StateFilters)
            Add(view, string.Format(CultureInfo.CurrentCulture, Strings.PaletteShow, f.Title), null, "IcAll", () => main.ShowState(f.Key));
        foreach (var c in main.Categories.Skip(1))
            Add(view, string.Format(CultureInfo.CurrentCulture, Strings.PaletteShow, c.Title), null, Cats.IconKey(c.Key), () => main.ShowCategory(c.Key));
        Cmd(view, Strings.ToggleDetail, "Ctrl+I", "IcPanel", main.ToggleDetailCommand);
        Cmd(view, Strings.Statistics, null, "IcChart", main.OpenStatsCommand);
        Cmd(view, main.IsLight ? Strings.PaletteDarkTheme : Strings.PaletteLightTheme, null, main.IsLight ? "IcMoon" : "IcSun", main.ToggleThemeCommand);

        var queues = Strings.PaletteGroupQueues;
        Add(queues, string.Format(CultureInfo.CurrentCulture, Strings.PaletteStartQueue, Strings.NightQueue), null, "IcPlay", () => main.StartQueue(null));
        foreach (var q in main.NamedQueues)
            Add(queues, string.Format(CultureInfo.CurrentCulture, Strings.PaletteStartQueue, q.Name), null, "IcPlay", () => main.StartQueue(q.Id));

        var speed = Strings.PaletteGroupSpeed;
        foreach (var p in main.ProfileChoices.Where(p => p.ToggleType == Avalonia.Controls.MenuItemToggleType.Radio))
            Add(speed, string.Format(CultureInfo.CurrentCulture, Strings.PaletteProfile, p.Label), null, "IcGauge", () => p.Command.Execute(null));

        var settings = Strings.Settings;
        foreach (var s in main.Settings.Sections)
            Add(settings, $"{Strings.Settings}: {s.Label}", s.Key == "general" ? "Ctrl+," : null, "IcGear", () => main.OpenSettingsAt(s.Key));
        return list;
    }
}
