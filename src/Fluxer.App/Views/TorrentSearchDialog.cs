using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Fluxer.App.Services;
using Fluxer.Core.Torrent;
using Serilog;

namespace Fluxer.App.Views;

public static partial class Dialogs
{
    /// Torrent search over the user's Torznab indexers (Jackett, Prowlarr…), all at once; a result is added through
    /// the usual torrent window. Stays open beside the main window, so several can be picked.
    public static async Task TorrentSearchAsync(AppHost host)
    {
        if (host.Engine.Settings.Indexers.Count == 0)
        {
            if (await ConfirmAsync(host.Owner, Strings.SearchTorrents, Strings.TorznabNone, Strings.OpenSettingsShort, Strings.Cancel))
                host.Main.OpenSettingsAt("torrent");
            return;
        }
        var w = TorrentSearchWindow(host);
        if (host.Owner is { } owner) w.Show(owner);
        else { w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.ShowInTaskbar = true; w.Show(); }
    }

    public static Window TorrentSearchWindow(AppHost host)
    {
        (string Label, string? Cats)[] categories =
        [
            (Strings.TorznabCatAll, null), (Strings.CatVideo, "2000,5000"), (Strings.CatMusic, "3000"),
            (Strings.CatPrograms, "4000"), (Strings.TorznabCatBooks, "7000"),
        ];
        var box = new TextBox { PlaceholderText = Strings.TorznabQuery };
        var category = new ComboBox { ItemsSource = categories.Select(c => c.Label).ToList(), SelectedIndex = 0, MinWidth = 130 };
        var go = new Avalonia.Controls.Button { Content = Strings.TorznabSearch, IsDefault = true, Classes = { "accent" } };
        var status = new TextBlock { Classes = { "subtle" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) };
        var results = new ObservableCollection<TorznabResult>();

        Control Row(TorznabResult r)
        {
            var title = new TextBlock { Text = r.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(title, r.Title);
            var meta = string.Join("  ·  ", new[]
            {
                r.Size is { } size ? Loc.Bytes(size) : null,
                r.Seeders is { } se ? $"↑ {se}" + (r.Leechers is { } le ? $"  ↓ {le}" : "") : null,
                r.Indexer,
                r.Published?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
            }.Where(t => t is not null));
            var add = new Avalonia.Controls.Button { Content = Strings.TorznabAdd, VerticalAlignment = VerticalAlignment.Center, IsEnabled = r.Target is not null };
            add.Click += (_, _) => Pick(r);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10, Margin = new Thickness(2, 4) };
            grid.Children.Add(new StackPanel { Spacing = 2, Children = { title, new TextBlock { Text = meta, FontSize = 12, Classes = { "subtle" } } } });
            Grid.SetColumn(add, 1);
            grid.Children.Add(add);
            return grid;
        }

        void Pick(TorznabResult r)
        {
            if (r.Magnet is { } magnet) _ = Flows.TorrentAsync(host, null, magnet);
            else if (r.Link is { } link) _ = Flows.TorrentFromUrlAsync(host, link, null);
        }

        var list = new ListBox
        {
            ItemsSource = results,
            Height = 420,
            ItemTemplate = new FuncDataTemplate<TorznabResult>((r, _) => r is null ? new Panel() : Row(r)),
        };
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is TorznabResult r) Pick(r); };

        CancellationTokenSource? running = null;
        async Task SearchAsync()
        {
            var query = box.Text?.Trim();
            if (string.IsNullOrEmpty(query)) return;
            if (running is not null) await running.CancelAsync();
            var cts = running = new CancellationTokenSource();
            results.Clear();
            status.Text = Strings.TorznabSearching;
            go.IsEnabled = false;
            var indexers = host.Engine.Settings.Indexers.ToList();
            var cats = categories[Math.Max(0, category.SelectedIndex)].Cats;
            var tasks = indexers.Select(async ix =>
            {
                try { return (ix.Name, Items: await Torznab.SearchAsync(host.Engine.Http.Client, ix, query, cats, cts.Token), Error: (string?)null); }
                catch (Exception e) when (e is not OperationCanceledException || !cts.IsCancellationRequested)
                {
                    Log.Information("Torznab {Indexer}: {Error}", ix.Name, e.Message);
                    return (ix.Name, Items: new List<TorznabResult>(), Error: (string?)e.Message);
                }
            }).ToList();
            try
            {
                var done = await Task.WhenAll(tasks);
                if (cts.IsCancellationRequested) return;
                foreach (var r in done.SelectMany(d => d.Items).OrderByDescending(r => r.Seeders ?? -1).Take(500)) results.Add(r);
                var failed = done.Where(d => d.Error is not null).Select(d => $"{d.Name}: {d.Error}").ToList();
                status.Text = string.Format(CultureInfo.CurrentCulture, Strings.TorznabResults, results.Count, done.Length - failed.Count, done.Length)
                              + (failed.Count > 0 ? "  —  " + string.Join("; ", failed) : "");
            }
            catch (OperationCanceledException) { }
            finally { if (ReferenceEquals(running, cts)) go.IsEnabled = true; }
        }
        go.Click += async (_, _) => await SearchAsync();

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        bar.Children.Add(box);
        Grid.SetColumn(category, 1);
        bar.Children.Add(category);
        Grid.SetColumn(go, 2);
        bar.Children.Add(go);
        var body = new StackPanel { Children = { bar, status, new Border { Classes = { "card" }, Padding = new Thickness(4), Child = list } } };
        var w = Shell(Strings.TorrentSearchTitle, body, 860, new Button(Strings.Close, false, () => true));
        w.Opened += (_, _) => box.Focus();
        w.Closed += (_, _) => running?.Cancel();
        Appearance.ApplyTo(w, host.Engine.Settings.Ui);
        return w;
    }
}
