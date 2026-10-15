using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fluxer.App.Services;
using Fluxer.Core.Downloads;
using Fluxer.Core.Media;

namespace Fluxer.App.Views;

public sealed record NewDownloadResult(List<string> Urls, string? FileName, string Folder, int Connections, bool Start, string? HashAlgorithm, string? Hash);
public sealed record TorrentChoice(HashSet<int> Deselected, bool Sequential, string Folder);
public sealed record MediaChoice(List<MediaEntry> Entries, int? MaxHeight, bool AudioOnly, string Folder);

public static partial class Dialogs
{
    public static async Task<NewDownloadResult?> NewDownloadAsync(Window? owner, AppSettings s, IReadOnlyList<string> urls, string? fileName, long? size)
    {
        NewDownloadResult? result = null;
        var urlBox = new TextBox
        {
            Text = string.Join(Environment.NewLine, urls), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            Height = urls.Count > 1 ? 120 : 64, PlaceholderText = Strings.UrlsWatermark,
        };
        var nameBox = new TextBox { Text = fileName ?? "", PlaceholderText = Strings.FileNameAuto };
        var (folder, folderValue) = FolderPicker(s.DownloadDirectory);
        var conns = new NumericUpDown { Minimum = 1, Maximum = 32, Value = s.ConnectionsPerDownload, Increment = 1, FormatString = "0", Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        var hashAlgo = new ComboBox { ItemsSource = new[] { "—", "SHA256", "MD5" }, SelectedIndex = 0, Width = 110 };
        var hashBox = new TextBox { PlaceholderText = Strings.ChecksumWatermark, Margin = new Thickness(6, 0, 0, 0) };
        var hashRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        hashRow.Children.Add(hashAlgo);
        Grid.SetColumn(hashBox, 1);
        hashRow.Children.Add(hashBox);
        var categoryFolders = new CheckBox { Content = Strings.UseCategoryFolders, IsChecked = s.UseCategoryFolders, Margin = new Thickness(0, 8, 0, 0) };

        var body = new StackPanel
        {
            Children =
            {
                Label(Strings.Urls), urlBox,
                Label(Strings.FileName), nameBox,
                Label(Strings.SaveTo), folder, categoryFolders,
                Label(Strings.Connections), conns,
                Label(Strings.Checksum), hashRow,
            },
        };
        if (size is > 0) body.Children.Insert(0, new TextBlock { Text = $"{Strings.InfoSize}: {Loc.Bytes(size)}", Opacity = 0.7 });

        bool Submit(bool start)
        {
            var list = (urlBox.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsDownloadable).Distinct().ToList();
            if (list.Count == 0) { urlBox.Focus(); return false; }
            var algo = hashAlgo.SelectedIndex > 0 && !string.IsNullOrWhiteSpace(hashBox.Text) ? (string)hashAlgo.SelectedItem! : null;
            var dir = folderValue();
            s.UseCategoryFolders = categoryFolders.IsChecked == true;
            result = new NewDownloadResult(list, list.Count == 1 && !string.IsNullOrWhiteSpace(nameBox.Text) ? nameBox.Text.Trim() : null,
                dir == s.DownloadDirectory ? "" : dir, (int)(conns.Value ?? 8), start, algo, algo is null ? null : hashBox.Text!.Trim());
            return true;
        }

        var w = Shell(Strings.NewDownload, body, 620,
            new Button(Strings.Cancel, false, () => true),
            new Button(Strings.AddPaused, false, () => Submit(false)),
            new Button(Strings.Download, true, () => Submit(true)));
        w.Opened += (_, _) => { if (urls.Count == 0) urlBox.Focus(); };
        await ShowAsync(w, owner);
        return result;
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
