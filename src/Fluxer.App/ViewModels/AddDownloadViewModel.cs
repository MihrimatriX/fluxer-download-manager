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
    [ObservableProperty] private IBrush _tile = Gray;
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private string _warning = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WarningSoft))] private IBrush _warningBrush = Red;
    /// Tinted banner behind the warning (no outline: the app has no lines).
    public IBrush WarningSoft => new SolidColorBrush(WarningBrush is ISolidColorBrush b ? b.Color : Colors.Gray, 0.14);
    // the app's status colours, looked up for the current theme
    private static IBrush Red => Theme.Brush("ErrBrush");
    private static IBrush Gray => Theme.Brush("CatOtherBrush");
    [ObservableProperty] private bool _probing;
    [ObservableProperty] private string? _category;
    // design tiles and batch rows
    [ObservableProperty] private string _sizeText = "—";
    [ObservableProperty] private string _resumeText = "—";
    [ObservableProperty] private IBrush _resumeBrush = Brushes.Gray;
    [ObservableProperty] private string _typeText = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private IBrush _stateBrush = Brushes.Gray;
    /// Batch: unticked links are left out (failing ones and web pages start unticked).
    [ObservableProperty] private bool _isIncluded = true;
    public bool Ok => Kind is ProbeKind.File or ProbeKind.Torrent || IsMagnet;
    partial void OnProbingChanged(bool value) { if (value) (StateText, StateBrush) = (Strings.Probing, Theme.Brush("SubtleBrush")); }

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
            (Glyph, Tile, Meta, Category) = ("", Theme.Brush("CatTorrentBrush"), Strings.KindMagnet, "Torrent");
            (StateText, StateBrush, TypeText) = (Strings.Ready, Theme.Brush("OkBrush"), "Torrent");
            return;
        }
        if (!UserNamed && p?.FileName is { } fn) Name = fn;
        if (Name.Length == 0) Name = Uri.TryCreate(Url, UriKind.Absolute, out var u) ? Uri.UnescapeDataString(u.Segments[^1].TrimEnd('/')) : Url;

        Category = p?.Kind == ProbeKind.Torrent ? "Torrent" : new AppSettings().CategoryFor(Name);
        (Glyph, Tile) = (p?.Kind) switch
        {
            ProbeKind.Html => ("", Gray),
            _ => Category switch
            {
                "Video" => ("", Theme.Brush("CatVideoBrush")), "Music" => ("", Theme.Brush("CatMusicBrush")),
                "Documents" => ("", Theme.Brush("CatDocumentsBrush")), "Archives" => ("", Theme.Brush("CatArchivesBrush")),
                "Programs" => ("", Theme.Brush("CatProgramsBrush")), "Torrent" => ("", Theme.Brush("CatTorrentBrush")),
                _ => ("", Gray),
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
            ProbeKind.Html => (Strings.WarnHtml, Red),
            ProbeKind.Torrent => (Strings.WarnTorrent, Theme.Brush("InfoTextBrush")),
            ProbeKind.Error => (p.Status > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.WarnHttp, p.Status) : Strings.WarnUnreachable, Red),
            _ => ("", Red),
        };

        SizeText = Loc.Bytes(p?.Size);
        (ResumeText, ResumeBrush) = p?.Kind != ProbeKind.File ? ("—", Theme.Brush("SubtleBrush"))
            : p.Resumable ? (Strings.Supported, Theme.Brush("OkBrush")) : (Strings.NotSupported, Theme.Brush("WarnTextBrush"));
        TypeText = Category switch
        {
            "Video" => Strings.CatVideo, "Music" => Strings.CatMusic, "Documents" => Strings.CatDocuments,
            "Archives" => Strings.CatArchives, "Programs" => Strings.CatPrograms, "Torrent" => "Torrent",
            _ => Path.GetExtension(Name) is { Length: > 1 } e ? e[1..].ToUpperInvariant() : "—",
        };
        if (p is null) return;
        (StateText, StateBrush) = p.Kind switch
        {
            ProbeKind.File or ProbeKind.Torrent => (Strings.Ready, Theme.Brush("OkBrush")),
            ProbeKind.Html => (Strings.WebPage, Theme.Brush("WarnTextBrush")),
            _ => (p.Status > 0 ? $"HTTP {p.Status}" : Strings.NoAnswer, Theme.Brush("ErrBrush")),
        };
        // A sign-in page instead of the file (session-only "download.php?id=…" links) is 1 KB of HTML, not a download.
        IsIncluded = p.Kind is not (ProbeKind.Error or ProbeKind.Html);
    }

    private bool _applying;
    partial void OnNameChanged(string value) { if (!_applying) UserNamed = true; }

    /// A theme colour as a faint fill (badges).
    public static IBrush Soft(string key, double opacity = 0.14) =>
        new SolidColorBrush(Theme.Brush(key) is ISolidColorBrush b ? b.Color : Colors.Gray, opacity);
}

public sealed record CategoryChip(string Key, string Title, string Glyph)
{
    public override string ToString() => Title;
}

/// Now: jumps to the front of the main queue; Queue: waits its turn; Night: waits for the night queue.
public enum StartMode { Now, Queue, Paused, Night }

/// A link and what is already known about it: a browser capture brings its own cookies and referrer,
/// the probe run when it was accepted, and the browser's file name.
public sealed record Captured(string Url, string? FileName = null, RequestContext? Context = null, ProbeResult? Probe = null);

/// What the add window returns to Flows.
public sealed record AddResult(IReadOnlyList<AddItem> Items, string Folder, bool FolderIsPerFile, StartMode Mode, int Connections, string? HashAlgorithm, string? Hash,
    IReadOnlyList<string> Mirrors);

public sealed partial class AddDownloadViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly HttpEngine _http;
    private readonly RequestContext? _ctx;
    private readonly Dictionary<string, Captured> _known = [];
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _probeCts;
    private bool _folderTouched;

    /// `ctx`: browser context for links typed into the box (captured links carry their own).
    public AddDownloadViewModel(AppSettings settings, HttpEngine http, IReadOnlyList<Captured> links, RequestContext? ctx)
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
        foreach (var l in links) _known[l.Url] = l;
        _urlsText = string.Join(Environment.NewLine, links.Select(l => l.Url));
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rebuild(); };

        Rebuild();
    }

    /// More browser captures while the window is open (a site's "download all" button): they join the list.
    /// A link already listed is replaced by its capture, which brings the browser's cookies (see OpenPagesInBrowser).
    public void AddLinks(IEnumerable<Captured> links)
    {
        var fresh = new List<string>();
        foreach (var l in links)
        {
            _known[l.Url] = l;
            var at = Items.Select(i => i.Url).ToList().IndexOf(l.Url);
            if (at < 0) { fresh.Add(l.Url); continue; }
            Items[at].PropertyChanged -= OnItemChanged;
            Items[at] = NewItem(l.Url);
            Items[at].PropertyChanged += OnItemChanged;
        }
        if (fresh.Count == 0) { UpdateFolder(); return; }
        UrlsText = string.Join(Environment.NewLine, [.. (UrlsText ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries), .. fresh]);
        Flush();
    }

    /// Links that answered with a web page (a sign-in or session check, "Çerezler bulunamadı") work in the browser,
    /// which has the session: they open there, and the extension sends each download back here with its cookies.
    /// They open in the browser the extension is installed in ("Add to browser" picks it the same way), not the
    /// default one: a default Edge has neither the extension nor the sign-in.
    [RelayCommand]
    private void OpenPagesInBrowser()
    {
        var pages = Items.Where(i => i.Kind == ProbeKind.Html).Select(i => i.Url).ToList();
        // ponytail: Chrome first; remember the capturing browser (its user agent) if people use the extension elsewhere.
        if (ShellIntegration.ExtensionsPage() is not (var exe, _))
        {
            foreach (var url in pages) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return;
        }
        foreach (var chunk in pages.Chunk(50))   // one command line each, well under its 32K limit
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe);
            foreach (var url in chunk) psi.ArgumentList.Add(url);
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
    }

    public bool HasPages => Items.Any(i => i.Kind == ProbeKind.Html);

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
    /// 0 main queue, 1 night queue, 2 paused.
    [ObservableProperty] private int _queueIndex;
    [ObservableProperty] private double _connections;
    [ObservableProperty] private int _hashIndex;
    [ObservableProperty] private string _hash = "";
    /// Single link only: other addresses of the same file, one per line.
    [ObservableProperty] private string _mirrorsText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSubmit))] private string _mirrorError = "";
    [ObservableProperty] private bool _dontAskAgain;
    [ObservableProperty] private string _badge = "";
    [ObservableProperty] private IBrush _badgeBrush = Brushes.Gray;
    [ObservableProperty] private IBrush _badgeBackground = Brushes.Transparent;
    [ObservableProperty] private string _detected = "";
    [ObservableProperty] private IBrush _detectedBrush = Brushes.Gray;
    [ObservableProperty] private string _categoryText = "";

    public bool IsSingle => Count == 1;
    public bool IsMulti => Count > 1;
    public bool IsSingleHttp => Count == 1 && !Items[0].IsMagnet;
    public AddItem? Single => Items.Count == 1 ? Items[0] : null;
    public bool CanSubmit => Count > 0 && (!IsSingleHttp || MirrorError.Length == 0);
    public string[] Queues => [Strings.MainQueueNow, string.Format(CultureInfo.CurrentCulture, Strings.NightQueueOption,
        _settings.ScheduleStart ?? Strings.Manual), Strings.AddPaused];
    public string SubmitText => QueueIndex switch
    {
        1 => Strings.AddToNight,
        2 => Strings.AddPaused,
        _ => Count > 1 ? string.Format(CultureInfo.CurrentCulture, Strings.StartN, Items.Count(i => i.IsIncluded)) : Strings.StartDownload,
    };
    /// Night queue / paused: one button says what will happen.
    public bool ShowQueueButton => QueueIndex == 0;

    partial void OnUrlsTextChanged(string value) { _debounce.Stop(); _debounce.Start(); }
    partial void OnMirrorsTextChanged(string value)
    {
        var bad = MirrorLines().FirstOrDefault(l => !HttpInfo.IsMirrorUrl(l));
        MirrorError = bad is null ? "" : string.Format(CultureInfo.CurrentCulture, Strings.MirrorInvalid, bad);
    }

    private string[] MirrorLines() => (MirrorsText ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    partial void OnQueueIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowQueueButton));
        OnPropertyChanged(nameof(SubmitText));
    }
    partial void OnSelectedCategoryChanged(CategoryChip value) { _folderTouched = false; UpdateFolder(); }
    partial void OnFolderChanged(string value) => UpdateSpace();

    private void Rebuild()
    {
        var urls = (UrlsText ?? "").Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Dialogs.IsDownloadable).Distinct().ToList();
        if (urls.SequenceEqual(Items.Select(i => i.Url))) return;

        var keep = Items.ToDictionary(i => i.Url);
        Items.Clear();
        foreach (var u in urls) Items.Add(keep.TryGetValue(u, out var old) ? old : NewItem(u));
        Count = Items.Count;
        foreach (var i in Items) { i.PropertyChanged -= OnItemChanged; i.PropertyChanged += OnItemChanged; }
        OnPropertyChanged(nameof(Single)); // one link swapped for another keeps Count at 1

        _probeCts?.Cancel();
        var cts = _probeCts = new CancellationTokenSource();
        var pending = Items.Where(i => !i.IsMagnet && i.Kind is null).ToList();
        _ = ProbeAllAsync(pending, cts.Token);
        UpdateFolder();
    }

    /// A captured link starts out already probed and named, so it is not probed a second time.
    private AddItem NewItem(string url)
    {
        if (!_known.TryGetValue(url, out var c)) return new AddItem(url, _ctx);
        var item = new AddItem(url, c.Context ?? _ctx);
        if (c.Probe is { } p) item.Apply(p);
        if (c.FileName is { Length: > 0 } name) item.Name = name;   // marks it UserNamed
        return item;
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddItem.IsIncluded)) OnPropertyChanged(nameof(SubmitText));
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
            ? string.Format(CultureInfo.CurrentCulture, Strings.FilesTotal, Items.Count(i => i.Ok), Loc.Bytes(total))
            : "";
        UpdateSummary();
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

    /// Badge in the link box, the green/red line under it and the category note.
    private void UpdateSummary()
    {
        OnPropertyChanged(nameof(IsSingleHttp));
        OnPropertyChanged(nameof(CanSubmit));
        OnPropertyChanged(nameof(SubmitText));
        OnPropertyChanged(nameof(HasPages));
        var torrent = Items.Count == 1 && (Items[0].IsMagnet || Items[0].Kind == ProbeKind.Torrent);
        (Badge, BadgeBrush, BadgeBackground) = Items.Count switch
        {
            0 => ("", Brushes.Transparent, Brushes.Transparent),
            > 1 => (Strings.BadgeBatch, Theme.Brush("WarnTextBrush"), AddItem.Soft("WarnBrush")),
            _ when torrent => ("TORRENT", Theme.Brush("InfoTextBrush"), AddItem.Soft("InfoBrush")),
            _ => ("HTTP", Theme.Brush("TextBrush"), Theme.Brush("SelectedBrush")),
        };

        if (Items.Count == 0) (Detected, DetectedBrush) = ("", Brushes.Transparent);
        else if (Items.Any(i => i.Probing)) (Detected, DetectedBrush) = (Strings.Probing, Theme.Brush("SubtleBrush"));
        else if (Items.Count == 1)
            (Detected, DetectedBrush) = Items[0] switch
            {
                { IsMagnet: true } => (Strings.MagnetDetected, Theme.Brush("OkBrush")),
                { Kind: ProbeKind.File or ProbeKind.Torrent } => (Strings.ServerAnswered, Theme.Brush("OkBrush")),
                { Warning.Length: > 0 } i => (i.Warning, i.WarningBrush),
                _ => ("", Brushes.Transparent),
            };
        else
        {
            int ok = Items.Count(i => i.Ok), pages = Items.Count(i => i.Kind == ProbeKind.Html), bad = Items.Count - ok - pages;
            Detected = pages > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.LinksCheckedPages, ok, pages)
                : bad > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.LinksCheckedSome, ok, bad)
                : string.Format(CultureInfo.CurrentCulture, Strings.LinksChecked, ok);
            DetectedBrush = Theme.Brush(pages + bad > 0 ? "WarnTextBrush" : "OkBrush");
        }

        var key = SelectedCategory.Key;
        var cat = key == "auto" ? Items.Count == 1 ? Items[0].Category : null : key == "none" ? null : key;
        var title = Categories.FirstOrDefault(c => c.Key == cat)?.Title ?? (cat == "Torrent" ? Strings.CatTorrent : Items.Count > 1 && key == "auto" ? Strings.Mixed : Strings.CatNone);
        CategoryText = string.Format(CultureInfo.CurrentCulture, key == "auto" ? Strings.CategoryAuto : Strings.CategoryManual, title);
    }

    [RelayCommand]
    private void PickCategory(CategoryChip chip) => SelectedCategory = chip;

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

    /// `startNow`: the primary button (front of the queue) rather than "Add to queue".
    public AddResult Result(bool startNow) => new(
        [.. Items.Where(i => i.IsIncluded || Items.Count == 1)],
        Folder,
        SelectedCategory.Key == "auto" && Items.Count > 1 && !_folderTouched,
        QueueIndex switch { 1 => StartMode.Night, 2 => StartMode.Paused, _ => startNow ? StartMode.Now : StartMode.Queue },
        (int)Connections,
        HashIndex > 0 && !string.IsNullOrWhiteSpace(Hash) ? HashAlgorithms[HashIndex] : null,
        HashIndex > 0 && !string.IsNullOrWhiteSpace(Hash) ? Hash.Trim() : null,
        IsSingleHttp ? MirrorLines() : []);
}

/// Two-way "ModeIndex == n" for radio buttons.
public sealed class IndexIs(int n) : Avalonia.Data.Converters.IValueConverter
{
    public static readonly IndexIs Zero = new(0), One = new(1), Two = new(2);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i && i == n;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? n : Avalonia.Data.BindingOperations.DoNothing;
}
