using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fluxer.App.Services;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Media;

namespace Fluxer.App.Views;

public sealed record NewDownloadResult(List<string> Urls, string? FileName, string Folder, int Connections, bool Start,
    string? HashAlgorithm, string? Hash, ProbeKind? Kind, RequestContext? Context);
public sealed record TorrentChoice(HashSet<int> Deselected, bool Sequential, string Folder);
public sealed record MediaChoice(List<MediaEntry> Entries, int? MaxHeight, bool AudioOnly, string Folder);

public static partial class Dialogs
{
    /// Add dialog. With a single http(s) link it probes the address and shows what the server really
    /// serves (name, size, type, resumable) — and warns when it is a web page or a torrent.
    public static async Task<NewDownloadResult?> NewDownloadAsync(Window? owner, AppSettings s, HttpEngine http,
        IReadOnlyList<string> urls, string? fileName, RequestContext? ctx, ProbeResult? known)
    {
        NewDownloadResult? result = null;
        ProbeKind? kind = known?.Kind;
        var nameEdited = false;

        var urlBox = new TextBox
        {
            Text = string.Join(Environment.NewLine, urls), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            MinHeight = 38, MaxHeight = 130, PlaceholderText = Strings.UrlsWatermark,
        };
        // --- info card
        var glyph = new TextBlock { Classes = { "icon" }, FontSize = 26, Text = "", Margin = new Thickness(0, 0, 14, 0) };
        var nameBox = new TextBox { Text = fileName ?? "", PlaceholderText = Strings.FileNameAuto };
        nameBox.TextChanged += (_, _) => nameEdited = nameBox.IsFocused || nameEdited;
        var meta = new TextBlock { Classes = { "subtle" }, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        var warn = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0), IsVisible = false, FontSize = 12.5 };
        var cardText = new StackPanel { Children = { nameBox, meta, warn } };
        var card = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        card.Children.Add(glyph);
        Grid.SetColumn(cardText, 1);
        card.Children.Add(cardText);
        var cardBorder = new Border
        {
            Child = card, Padding = new Thickness(14, 12), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 12, 0, 0),
            [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("CardBrush"),
        };

        // --- folder with recent folders
        var defaultDir = s.DownloadDirectory;
        var (folder, folderValue, setFolder) = FolderPickerWithRecent(defaultDir, s.RecentFolders);

        // --- advanced (collapsed)
        var conns = new NumericUpDown { Minimum = 1, Maximum = 32, Value = s.ConnectionsPerDownload, Increment = 1, FormatString = "0", Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        var hashAlgo = new ComboBox { ItemsSource = new[] { "—", "SHA256", "MD5" }, SelectedIndex = 0, Width = 110 };
        var hashBox = new TextBox { PlaceholderText = Strings.ChecksumWatermark, Margin = new Thickness(6, 0, 0, 0) };
        var hashRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        hashRow.Children.Add(hashAlgo);
        Grid.SetColumn(hashBox, 1);
        hashRow.Children.Add(hashBox);
        var categoryFolders = new CheckBox { Content = Strings.UseCategoryFolders, IsChecked = s.UseCategoryFolders, Margin = new Thickness(0, 8, 0, 0) };
        var advanced = new Expander
        {
            Header = Strings.AdvancedOptions, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Children = { Label(Strings.Connections), conns, Label(Strings.Checksum), hashRow, categoryFolders } },
        };

        var body = new StackPanel { Children = { Label(Strings.Urls), urlBox, cardBorder, Label(Strings.SaveTo), folder, advanced } };

        void Show(ProbeResult? p, bool probing)
        {
            var list = ParseUrls(urlBox.Text);
            cardBorder.IsVisible = list.Count == 1;
            warn.IsVisible = false;
            kind = p?.Kind;
            if (list.Count != 1) return;
            if (list[0].StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                glyph.Text = "";
                meta.Text = Strings.KindMagnet;
                return;
            }
            if (probing) { meta.Text = Strings.Probing; return; }
            if (p is null) { meta.Text = ""; return; }
            if (!nameEdited && string.IsNullOrWhiteSpace(fileName) && p.FileName is { } n) nameBox.Text = n;
            var name = string.IsNullOrWhiteSpace(nameBox.Text) ? p.FileName ?? "" : nameBox.Text;
            glyph.Text = GlyphForName(name, p.Kind);
            var parts = new List<string>();
            if (p.Size is { } size) parts.Add(Loc.Bytes(size));
            if (Path.GetExtension(name) is { Length: > 1 } ext) parts.Add(ext[1..].ToUpperInvariant());
            if (p.Kind == ProbeKind.File) parts.Add(p.Resumable ? Strings.Resumable : Strings.NotResumable);
            meta.Text = string.Join("  ·  ", parts);
            (warn.IsVisible, warn.Text, warn.Foreground) = p.Kind switch
            {
                ProbeKind.Html => (true, Strings.WarnHtml, Brushes.IndianRed),
                ProbeKind.Torrent => (true, Strings.WarnTorrent, Brushes.MediumPurple),
                ProbeKind.Error => (true, p.Status > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.WarnHttp, p.Status) : Strings.WarnUnreachable, Brushes.IndianRed),
                _ => (false, "", warn.Foreground),
            };
            // Suggest the category folder for the detected type.
            if (categoryFolders.IsChecked == true && folderValue() == defaultDir && s.CategoryFor(name) is { } cat)
                setFolder(Path.Combine(defaultDir, cat), false);
        }

        CancellationTokenSource? probeCts = null;
        async Task ProbeAsync()
        {
            var list = ParseUrls(urlBox.Text);
            if (list.Count != 1 || !list[0].StartsWith("http", StringComparison.OrdinalIgnoreCase)) { Show(null, false); return; }
            probeCts?.Cancel();
            var cts = probeCts = new CancellationTokenSource();
            Show(null, true);
            var (p, used) = await HttpProbe.RunWithFallbackAsync(http, list[0], ctx, cts.Token);
            if (cts.IsCancellationRequested) return;
            ctx = used;
            Show(p, false);
        }

        var debounce = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        debounce.Tick += (_, _) => { debounce.Stop(); _ = ProbeAsync(); };
        urlBox.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };

        bool Submit(bool start)
        {
            var list = ParseUrls(urlBox.Text);
            if (list.Count == 0) { urlBox.Focus(); return false; }
            var algo = hashAlgo.SelectedIndex > 0 && !string.IsNullOrWhiteSpace(hashBox.Text) ? (string)hashAlgo.SelectedItem! : null;
            var dir = folderValue();
            s.UseCategoryFolders = categoryFolders.IsChecked == true;
            result = new NewDownloadResult(list, list.Count == 1 && !string.IsNullOrWhiteSpace(nameBox.Text) ? nameBox.Text.Trim() : null,
                dir, (int)(conns.Value ?? 8), start, algo, algo is null ? null : hashBox.Text!.Trim(), list.Count == 1 ? kind : null, ctx);
            return true;
        }

        var w = Shell(Strings.NewDownload, body, 640,
            new Button(Strings.Cancel, false, () => true),
            new Button(Strings.AddPaused, false, () => Submit(false)),
            new Button(Strings.Download, true, () => Submit(true)));
        w.Opened += (_, _) =>
        {
            if (urls.Count == 0) urlBox.Focus();
            if (known is not null) Show(known, false);
            else _ = ProbeAsync();
        };
        w.Closed += (_, _) => { debounce.Stop(); probeCts?.Cancel(); };
        await ShowAsync(w, owner);
        return result;
    }

    private static List<string> ParseUrls(string? text) =>
        [.. (text ?? "").Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsDownloadable).Distinct()];

    private static string GlyphForName(string name, ProbeKind kind)
    {
        if (kind == ProbeKind.Torrent) return "";
        if (kind == ProbeKind.Html) return "";
        return new AppSettings().CategoryFor(name) switch
        {
            "Video" => "", "Music" => "", "Documents" => "", "Archives" => "", "Programs" => "",
            _ => "",
        };
    }

    /// Folder box + browse + a menu of recently used folders.
    public static (Control View, Func<string> Value, Action<string, bool> Set) FolderPickerWithRecent(string initial, IReadOnlyList<string> recent)
    {
        var box = new TextBox { Text = initial };
        var userPicked = false;
        var browse = new Avalonia.Controls.Button { Content = "…", Margin = new Thickness(6, 0, 0, 0), MinWidth = 40 };
        browse.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(browse) is { } top && await PickFolderAsync(top, box.Text) is { } path) { box.Text = path; userPicked = true; }
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        grid.Children.Add(box);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(browse);
        if (recent.Count > 0)
        {
            var menu = new MenuFlyout();
            foreach (var r in recent)
            {
                var item = new MenuItem { Header = r };
                item.Click += (_, _) => { box.Text = r; userPicked = true; };
                menu.Items.Add(item);
            }
            var history = new Avalonia.Controls.Button
            {
                Content = new TextBlock { Classes = { "icon" }, Text = "" }, Margin = new Thickness(6, 0, 0, 0), MinWidth = 40, Flyout = menu,
            };
            ToolTip.SetTip(history, Strings.RecentFolders);
            Avalonia.Automation.AutomationProperties.SetName(history, Strings.RecentFolders);
            Grid.SetColumn(history, 2);
            grid.Children.Add(history);
        }
        box.TextChanged += (_, _) => userPicked |= box.IsFocused;
        return (grid, () => box.Text ?? initial, (path, force) => { if (force || !userPicked) box.Text = path; });
    }

    public static bool IsDownloadable(string s) =>
        s.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
        Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    public static async Task<TorrentChoice?> TorrentAsync(Window? owner, AppSettings s, string name, IReadOnlyList<(string Path, long Length)> files)
    {
        TorrentChoice? result = null;
        var checks = files.Select(f => new CheckBox { Content = $"{f.Path}   ({Loc.Bytes(f.Length)})", IsChecked = true }).ToList();
        var list = new StackPanel { Spacing = 2 };
        foreach (var c in checks) list.Children.Add(c);
        var total = new TextBlock { Opacity = 0.7 };
        void UpdateTotal() => total.Text = $"{Strings.InfoSize}: {Loc.Bytes(files.Where((_, i) => checks[i].IsChecked == true).Sum(f => f.Length))}";
        foreach (var c in checks) c.IsCheckedChanged += (_, _) => UpdateTotal();
        UpdateTotal();
        var sequential = new CheckBox { Content = Strings.Sequential };
        var defaultDir = s.UseCategoryFolders ? Path.Combine(s.DownloadDirectory, "Torrent") : s.DownloadDirectory;
        var (folder, folderValue) = FolderPicker(defaultDir);

        var body = new StackPanel { Children = { new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap } } };
        if (files.Count > 0)
        {
            body.Children.Add(Label(Strings.Files));
            body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280 });
            body.Children.Add(total);
        }
        else body.Children.Add(Paragraph(Strings.MagnetNoFiles));
        body.Children.Add(Label(Strings.SaveTo));
        body.Children.Add(folder);
        body.Children.Add(sequential);

        var w = Shell(Strings.AddTorrent, body, 620,
            new Button(Strings.Cancel, false, () => true),
            new Button(Strings.Download, true, () =>
            {
                var deselected = checks.Select((c, i) => (c, i)).Where(x => x.c.IsChecked != true).Select(x => x.i).ToHashSet();
                if (files.Count > 0 && deselected.Count == files.Count) return false;
                result = new TorrentChoice(deselected, sequential.IsChecked == true, folderValue());
                return true;
            }));
        await ShowAsync(w, owner);
        return result;
    }

    public static async Task<MediaChoice?> MediaAsync(Window? owner, AppSettings s, MediaProbe probe)
    {
        MediaChoice? result = null;
        var options = new List<(string Text, int? Height, bool Audio)> { (Strings.QualityBest, null, false) };
        options.AddRange(probe.Heights.Where(h => h >= 144).Distinct().Select(h => ($"{h}p", (int?)h, false)));
        if (probe.IsPlaylist) options.AddRange(new[] { 2160, 1440, 1080, 720, 480, 360 }.Select(h => ($"≤ {h}p", (int?)h, false)));
        options.Add((Strings.AudioOnly, null, true));
        var quality = new ComboBox { ItemsSource = options.Select(o => o.Text).ToList(), SelectedIndex = 0, MinWidth = 200 };
        var preferred = options.FindIndex(o => o.Height == 1080);
        if (preferred > 0) quality.SelectedIndex = preferred;

        var checks = probe.Entries.Select(e => new CheckBox
        {
            Content = e.Duration is { } d ? $"{e.Title}   ({TimeSpan.FromSeconds(d):g})" : e.Title,
            IsChecked = true,
        }).ToList();
        var (folder, folderValue) = FolderPicker(s.UseCategoryFolders ? Path.Combine(s.DownloadDirectory, "Video") : s.DownloadDirectory);

        var body = new StackPanel { Children = { new TextBlock { Text = probe.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap } } };
        if (probe.IsPlaylist)
        {
            var list = new StackPanel { Spacing = 2 };
            foreach (var c in checks) list.Children.Add(c);
            var all = new CheckBox { Content = Strings.SelectAll, IsChecked = true };
            all.IsCheckedChanged += (_, _) => { foreach (var c in checks) c.IsChecked = all.IsChecked; };
            body.Children.Add(Label(string.Format(CultureInfo.CurrentCulture, Strings.PlaylistItems, probe.Entries.Count)));
            body.Children.Add(all);
            body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 300 });
        }
        body.Children.Add(Label(Strings.Quality));
        body.Children.Add(quality);
        body.Children.Add(Label(Strings.SaveTo));
        body.Children.Add(folder);

        var w = Shell(Strings.MediaTitle, body, 640,
            new Button(Strings.Cancel, false, () => true),
            new Button(Strings.Download, true, () =>
            {
                var entries = probe.Entries.Where((_, i) => checks[i].IsChecked == true).ToList();
                if (entries.Count == 0) return false;
                var o = options[Math.Max(0, quality.SelectedIndex)];
                result = new MediaChoice(entries, o.Height, o.Audio, folderValue());
                return true;
            }));
        await ShowAsync(w, owner);
        return result;
    }

    /// Indeterminate "working…" window that closes when the task ends.
    public static async Task<T> BusyAsync<T>(Window? owner, string title, string text, Func<IProgress<string>, Task<T>> work)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 12, Children = { label, new ProgressBar { IsIndeterminate = true } } };
        var w = Shell(title, body, 440);
        var finished = false;
        w.Closing += (_, e) => e.Cancel = !finished;
        var shown = ShowAsync(w, owner);
        var progress = new Progress<string>(t => label.Text = t);
        try { return await work(progress); }
        finally { finished = true; w.Close(); await shown; }
    }
}
