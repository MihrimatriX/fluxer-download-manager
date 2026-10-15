using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.App.Views;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Platform.Windows;

namespace Fluxer.App.ViewModels;

/// One link in the add window, probed in the background.
public sealed partial class AddItem : ObservableObject
{
    public AddItem(string url, RequestContext? ctx)
    {
        Url = url;
        Context = ctx;
        Host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
        IsMagnet = url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
        Apply(null);
    }

    public string Url { get; }
    public string Host { get; }
    public bool IsMagnet { get; }
    public RequestContext? Context { get; set; }
    public ProbeKind? Kind { get; private set; }
    public long? Size { get; private set; }
    public bool UserNamed { get; set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _glyph = "";
    [ObservableProperty] private IBrush _tile = Brushes.SlateGray;
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private IBrush _warningBrush = Brushes.IndianRed;
    [ObservableProperty] private bool _probing;
    [ObservableProperty] private string? _category;

    public void Apply(ProbeResult? p)
    {
        _applying = true;
        try { ApplyCore(p); } finally { _applying = false; }
    }

    private void ApplyCore(ProbeResult? p)
    {
        Kind = p?.Kind;
        Size = p?.Size;
        if (IsMagnet)
        {
            Name = MonoTorrent.MagnetLink.TryParse(Url, out var m) && m.Name is { } n ? n : "magnet";
            (Glyph, Tile, Meta, Category) = ("", Brush("#06B6D4"), Strings.KindMagnet, "Torrent");
            return;
        }
        if (!UserNamed && p?.FileName is { } fn) Name = fn;
        if (Name.Length == 0) Name = Uri.TryCreate(Url, UriKind.Absolute, out var u) ? Uri.UnescapeDataString(u.Segments[^1].TrimEnd('/')) : Url;

        Category = p?.Kind == ProbeKind.Torrent ? "Torrent" : new AppSettings().CategoryFor(Name);
        (Glyph, Tile) = (p?.Kind) switch
        {
            ProbeKind.Html => ("", Brush("#64748B")),
            _ => Category switch
            {
                "Video" => ("", Brush("#8B5CF6")), "Music" => ("", Brush("#EC4899")),
                "Documents" => ("", Brush("#3B82F6")), "Archives" => ("", Brush("#F59E0B")),
                "Programs" => ("", Brush("#10B981")), "Torrent" => ("", Brush("#06B6D4")),
                _ => ("", Brush("#64748B")),
            },
        };

        var parts = new List<string>();
        if (p?.Size is { } s) parts.Add(Loc.Bytes(s));
        if (Path.GetExtension(Name) is { Length: > 1 } ext) parts.Add(ext[1..].ToUpperInvariant());
        if (p?.Kind == ProbeKind.File) parts.Add(p.Resumable ? "✓ " + Strings.Resumable : Strings.NotResumable);
        if (Host.Length > 0) parts.Add(Host);
        Meta = string.Join("  ·  ", parts);

        (Warning, WarningBrush) = p?.Kind switch
        {
            ProbeKind.Html => (Strings.WarnHtml, Brushes.IndianRed),
            ProbeKind.Torrent => (Strings.WarnTorrent, Brush("#8B5CF6")),
            ProbeKind.Error => (p.Status > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.WarnHttp, p.Status) : Strings.WarnUnreachable, Brushes.IndianRed),
            _ => ("", Brushes.IndianRed),
        };
    }

    private bool _applying;
    partial void OnNameChanged(string value) { if (!_applying) UserNamed = true; }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}

public sealed record CategoryChip(string Key, string Title, string Glyph)
{
    public override string ToString() => Title;
}

public enum StartMode { Now, Queue, Paused }

/// What the add window returns to Flows.
public sealed record AddResult(IReadOnlyList<AddItem> Items, string Folder, bool FolderIsPerFile, StartMode Mode, int Connections, string? HashAlgorithm, string? Hash);

public sealed partial class AddDownloadViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly HttpEngine _http;
    private readonly RequestContext? _ctx;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _probeCts;
    private bool _folderTouched;

    public AddDownloadViewModel(AppSettings settings, HttpEngine http, IReadOnlyList<string> urls, string? fileName, RequestContext? ctx, ProbeResult? known)
    {
        _settings = settings;
        _http = http;
        _ctx = ctx;
        Categories =
        [
            new("auto", Strings.CatAuto, ""), new("Video", Strings.CatVideo, ""), new("Music", Strings.CatMusic, ""),
            new("Documents", Strings.CatDocuments, ""), new("Archives", Strings.CatArchives, ""),
            new("Programs", Strings.CatPrograms, ""), new("none", Strings.CatNone, ""),
        ];
        _selectedCategory = settings.UseCategoryFolders ? Categories[0] : Categories[^1];
        RecentFolders = [.. settings.RecentFolders];
        _connections = settings.ConnectionsPerDownload;
        _folder = settings.DownloadDirectory;
        _urlsText = string.Join(Environment.NewLine, urls);
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rebuild(); };

        Rebuild(known);
        if (fileName is { Length: > 0 } && Items.Count == 1) { Items[0].Name = fileName; Items[0].UserNamed = true; }
    }

    public ObservableCollection<AddItem> Items { get; } = [];
    public IReadOnlyList<CategoryChip> Categories { get; }
    public IReadOnlyList<string> RecentFolders { get; }
    public bool HasRecent => RecentFolders.Count > 0;
    public string[] HashAlgorithms => ["—", "SHA256", "MD5"];

    [ObservableProperty] private string _urlsText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSingle), nameof(IsMulti), nameof(Single), nameof(CanSubmit))] private int _count;
    [ObservableProperty] private CategoryChip _selectedCategory;
    [ObservableProperty] private string _folder;
    [ObservableProperty] private string _freeSpaceText = "";
    [ObservableProperty] private bool _notEnoughSpace;
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private double _connections;
    [ObservableProperty] private int _hashIndex;
    [ObservableProperty] private string _hash = "";

    public bool IsSingle => Count == 1;
    public bool IsMulti => Count > 1;
    public AddItem? Single => Items.Count == 1 ? Items[0] : null;
    public bool CanSubmit => Count > 0;
    public string SubmitText => ModeIndex switch { 1 => Strings.AddToQueue, 2 => Strings.AddPaused, _ => Strings.Download };

    partial void OnUrlsTextChanged(string value) { _debounce.Stop(); _debounce.Start(); }
    partial void OnModeIndexChanged(int value) => OnPropertyChanged(nameof(SubmitText));
    partial void OnSelectedCategoryChanged(CategoryChip value) { _folderTouched = false; UpdateFolder(); }
    partial void OnFolderChanged(string value) => UpdateSpace();

    private void Rebuild(ProbeResult? known = null)
    {
        var urls = (UrlsText ?? "").Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Dialogs.IsDownloadable).Distinct().ToList();
        if (urls.SequenceEqual(Items.Select(i => i.Url))) return;

        var keep = Items.ToDictionary(i => i.Url);
        Items.Clear();
        foreach (var u in urls) Items.Add(keep.TryGetValue(u, out var old) ? old : new AddItem(u, _ctx));
        Count = Items.Count;

        _probeCts?.Cancel();
        var cts = _probeCts = new CancellationTokenSource();
        var pending = Items.Where(i => !i.IsMagnet && i.Kind is null).ToList();
        if (known is not null && pending.Count == 1) { pending[0].Apply(known); pending.Clear(); }
        _ = ProbeAllAsync(pending, cts.Token);
        UpdateFolder();
    }

    private async Task ProbeAllAsync(List<AddItem> items, CancellationToken ct)
    {
        foreach (var i in items) i.Probing = true;
        try
        {
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (item, token) =>
            {
                var (p, used) = await HttpProbe.RunWithFallbackAsync(_http, item.Url, item.Context, token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.Context = used;
                    item.Apply(p);
                    item.Probing = false;
                    UpdateFolder();
                });
            });
        }
        catch (OperationCanceledException) { }
    }

    /// Category chip → folder suggestion (until the user picks a folder by hand).
    private void UpdateFolder()
    {
        var total = Items.Sum(i => i.Size ?? 0);
        TotalText = Items.Count > 1
            ? string.Format(CultureInfo.CurrentCulture, Strings.FilesTotal, Items.Count, Loc.Bytes(total))
            : "";
        if (!_folderTouched)
        {
            var key = SelectedCategory.Key;
            var cat = key == "auto" ? (Items.Count == 1 ? Items[0].Category : null) : key == "none" ? null : key;
            Folder = cat is null ? _settings.DownloadDirectory : Path.Combine(_settings.DownloadDirectory, cat);
            _folderTouched = false;
        }
        UpdateSpace();
    }

    private void UpdateSpace()
    {
        try
        {
            var free = DiskErrors.FreeBytes(Folder);
            FreeSpaceText = string.Format(CultureInfo.CurrentCulture, Strings.FreeSpace, Loc.Bytes(free));
            NotEnoughSpace = Items.Sum(i => i.Size ?? 0) > free;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            FreeSpaceText = "";
            NotEnoughSpace = false;
        }
    }

    /// Applies a pending URL edit right away (submit before the debounce fired).
    public void Flush()
    {
        _debounce.Stop();
        Rebuild();
    }

    public void MarkFolderTouched() => _folderTouched = true;

    public void SetFolderByUser(string path)
    {
        Folder = path;
        _folderTouched = true;
    }

    [RelayCommand]
    private void PickRecent(string path) => SetFolderByUser(path);

    public AddResult Result() => new(
        [.. Items],
        Folder,
        SelectedCategory.Key == "auto" && Items.Count > 1 && !_folderTouched,
        (StartMode)ModeIndex,
        (int)Connections,
        HashIndex > 0 && !string.IsNullOrWhiteSpace(Hash) ? HashAlgorithms[HashIndex] : null,
        HashIndex > 0 && !string.IsNullOrWhiteSpace(Hash) ? Hash.Trim() : null);
}

/// Two-way "ModeIndex == n" for radio buttons.
public sealed class IndexIs(int n) : Avalonia.Data.Converters.IValueConverter
{
    public static readonly IndexIs Zero = new(0), One = new(1), Two = new(2);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i && i == n;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? n : Avalonia.Data.BindingOperations.DoNothing;
}
