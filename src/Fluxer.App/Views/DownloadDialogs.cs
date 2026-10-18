using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Fluxer.App.Services;
using Fluxer.App.ViewModels;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Media;

namespace Fluxer.App.Views;

public sealed record TorrentChoice(HashSet<int> Deselected, bool Sequential, string Folder);
public sealed record MediaChoice(List<MediaEntry> Entries, int? MaxHeight, bool AudioOnly, string Folder);

public static partial class Dialogs
{
    public static bool IsDownloadable(string s) =>
        s.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) ||
        Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    public static async Task<TorrentChoice?> TorrentAsync(Window? owner, AppSettings s, string name, IReadOnlyList<(string Path, long Length)> files)
    {
        TorrentChoice? result = null;
        // Rows use the shared TorrentFileItem template (App.axaml); the panel virtualizes, so a torrent
        // with thousands of files only creates the rows that fit in the box.
        var total = new TextBlock { Opacity = 0.7 };
        List<TorrentFileItem> items = [];
        items.AddRange(files.Select((f, i) => new TorrentFileItem(i, f.Path, f.Length, true, UpdateTotal)));
        var list = new ItemsControl { ItemsSource = items, ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()), Margin = new Thickness(0, 0, 14, 0) };
        void UpdateTotal() => total.Text = $"{Strings.InfoSize}: {Loc.Bytes(items.Where(x => x.IsSelected).Sum(x => x.Length))}";
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
                var deselected = items.Where(x => !x.IsSelected).Select(x => x.Index).ToHashSet();
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
    public static Task<T> BusyAsync<T>(Window? owner, string title, string text, Func<IProgress<string>, Task<T>> work) =>
        BusyAsync(owner, title, text, (progress, _) => work(progress), cancellable: false);

    /// Same window with a Cancel button; closing it also cancels. It stays open until the work has
    /// actually stopped, so the caller sees the OperationCanceledException before anything else runs.
    public static Task<T> BusyAsync<T>(Window? owner, string title, string text, Func<IProgress<string>, CancellationToken, Task<T>> work) =>
        BusyAsync(owner, title, text, work, cancellable: true);

    private static async Task<T> BusyAsync<T>(Window? owner, string title, string text, Func<IProgress<string>, CancellationToken, Task<T>> work, bool cancellable)
    {
        using var cts = new CancellationTokenSource();
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 12, Children = { label, new ProgressBar { IsIndeterminate = true } } };
        var w = cancellable
            ? Shell(title, body, 440, new Button(Strings.Cancel, false, () => { cts.Cancel(); return false; }))
            : Shell(title, body, 440);
        var finished = false;
        w.Closing += (_, e) =>
        {
            if (finished) return;
            e.Cancel = true;
            if (cancellable) cts.Cancel();
        };
        var shown = ShowAsync(w, owner);
        var progress = new Progress<string>(t => label.Text = t);
        try { return await work(progress, cts.Token); }
        finally { finished = true; w.Close(); await shown; }
    }
}
