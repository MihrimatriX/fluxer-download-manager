using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Fluxer.App.Services;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Ipc;

namespace Fluxer.App.Views;

public sealed partial class GrabRow(PageResource r) : ObservableObject
{
    public string Url { get; } = r.Url;
    public string Kind { get; } = r.Kind;
    public int? Width { get; } = r.Width;
    public int? Height { get; } = r.Height;
    public string Extension { get; } = Path.GetExtension(new Uri(r.Url).AbsolutePath).TrimStart('.').ToLowerInvariant();

    [ObservableProperty] private bool _isChecked = true;
    [ObservableProperty] private long? _size;
    [ObservableProperty] private string _info = "";

    public void UpdateInfo() =>
        Info = string.Join("  ·  ", new[]
        {
            Kind switch { "image" => Strings.KindImage, "media" => Strings.KindMedia, _ => Strings.KindFile },
            Width is > 0 && Height is > 0 ? $"{Width}×{Height}" : null,
            Size is { } s ? Loc.Bytes(s) : null,
        }.Where(x => x is not null));
}

public static partial class Dialogs
{
    public static async Task<(List<string> Urls, string Folder)?> GrabAsync(Window? owner, AppSettings s, PageResources page, HttpEngine http, RequestContext ctx)
    {
        var rows = page.Resources.Where(r => Uri.TryCreate(r.Url, UriKind.Absolute, out _)).Select(r => new GrabRow(r)).ToList();
        foreach (var r in rows) r.UpdateInfo();
        var visible = new ObservableCollection<GrabRow>();

        var kind = new ComboBox { ItemsSource = new[] { Strings.KindAll, Strings.KindFile, Strings.KindImage, Strings.KindMedia }, SelectedIndex = rows.Any(r => r.Kind == "image") ? 2 : 0, Width = 124 };
        var ext = new TextBox { PlaceholderText = "jpg, png, pdf", Width = 140 };
        var minDim = new NumericUpDown { Minimum = 0, Maximum = 10000, Value = 0, Increment = 100, FormatString = "0", Width = 124 };
        var minKb = new NumericUpDown { Minimum = 0, Maximum = 10_000_000, Value = 0, Increment = 50, FormatString = "0", Width = 124 };
        var pattern = new TextBox { PlaceholderText = Strings.UrlPatternWatermark };
        var count = new TextBlock { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };

        var list = new ListBox
        {
            ItemsSource = visible,
            MinHeight = 140,
            MaxHeight = 300,
            Background = Avalonia.Media.Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<GrabRow>((_, _) =>
            {
                var check = new CheckBox { [!ToggleButton_IsChecked] = CompiledBinding.Create((GrabRow r) => r.IsChecked, mode: BindingMode.TwoWay) };
                var url = new TextBlock { [!TextBlock.TextProperty] = CompiledBinding.Create((GrabRow r) => r.Url), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, FontSize = 12 };
                var info = new TextBlock { [!TextBlock.TextProperty] = CompiledBinding.Create((GrabRow r) => r.Info), Classes = { "subtle" }, FontSize = 11 };
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                var text = new StackPanel { Children = { url, info }, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(text, 1);
                grid.Children.Add(check);
                grid.Children.Add(text);
                return grid;
            }),
        };

        void Apply()
        {
            var exts = (ext.Text ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(e => e.TrimStart('.').ToLowerInvariant()).ToHashSet();
            Regex? rx = null;
            if (!string.IsNullOrWhiteSpace(pattern.Text))
                try { rx = new Regex(pattern.Text, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); } catch (ArgumentException) { }
            var k = kind.SelectedIndex switch { 1 => "link", 2 => "image", 3 => "media", _ => null };
            var dim = (int)(minDim.Value ?? 0);
            var kb = (long)(minKb.Value ?? 0) * 1024;
            visible.Clear();
            foreach (var r in rows)
            {
                if (k is not null && r.Kind != k) continue;
                if (exts.Count > 0 && !exts.Contains(r.Extension)) continue;
                if (dim > 0 && r.Width is { } w && r.Height is { } h && Math.Max(w, h) < dim) continue;
                if (kb > 0 && r.Size is { } size && size < kb) continue;   // unknown size passes
                if (rx is not null ? !SafeMatch(rx, r.Url) : !string.IsNullOrWhiteSpace(pattern.Text) && !r.Url.Contains(pattern.Text, StringComparison.OrdinalIgnoreCase)) continue;
                visible.Add(r);
            }
            count.Text = string.Format(CultureInfo.CurrentCulture, Strings.GrabCount, visible.Count(r => r.IsChecked), visible.Count);
        }

        kind.SelectionChanged += (_, _) => Apply();
        ext.TextChanged += (_, _) => Apply();
        minDim.ValueChanged += (_, _) => Apply();
        minKb.ValueChanged += (_, _) => Apply();
        pattern.TextChanged += (_, _) => Apply();
        foreach (var r in rows) r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(GrabRow.IsChecked)) count.Text = string.Format(CultureInfo.CurrentCulture, Strings.GrabCount, visible.Count(x => x.IsChecked), visible.Count); };

        // Selection helpers sit with the count, not among the dialog's actions.
        var all = new Avalonia.Controls.Button { Content = Strings.SelectAll, Classes = { "tool" }, Padding = new Thickness(8, 3) };
        all.Click += (_, _) => { foreach (var r in visible) r.IsChecked = true; };
        var none = new Avalonia.Controls.Button { Content = Strings.SelectNone, Classes = { "tool" }, Padding = new Thickness(8, 3) };
        none.Click += (_, _) => { foreach (var r in visible) r.IsChecked = false; };
        var selectors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { all, none } };
        DockPanel.SetDock(selectors, Dock.Right);
        var (folder, folderValue) = FolderPicker(Path.Combine(s.DownloadDirectory, FolderNameFor(page.PageUrl)));

        var filters = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, control) in new (string, Control)[] { (Strings.Kind, kind), (Strings.Extensions, ext), (Strings.MinDimension, minDim), (Strings.MinSizeKb, minKb) })
            filters.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 12, 6), Children = { Label(label), control } });

        var body = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = page.PageUrl, Classes = { "subtle" }, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
                filters, Label(Strings.UrlPattern), pattern,
                new DockPanel { Margin = new Thickness(0, 8, 0, 4), Children = { selectors, count } },
                new Border { Classes = { "card" }, Child = list },
                Label(Strings.SaveTo), folder,
            },
        };
        Apply();

        (List<string>, string)? result = null;
        using var cts = new CancellationTokenSource();
        var w = Shell(Strings.GrabTitle, body, 700,
            new Button(Strings.Cancel, false, () => true),
            new Button(Strings.Download, true, () =>
            {
                var chosen = visible.Where(r => r.IsChecked).Select(r => r.Url).ToList();
                if (chosen.Count == 0) return false;
                result = (chosen, folderValue());
                return true;
            }));
        w.Opened += (_, _) => _ = FetchSizesAsync(rows, http, ctx, () => Dispatcher.UIThread.Post(Apply), cts.Token);
        await ShowAsync(w, owner);
        await cts.CancelAsync();
        return result;
    }

    private static readonly AvaloniaProperty ToggleButton_IsChecked = Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private static bool SafeMatch(Regex rx, string s)
    {
        try { return rx.IsMatch(s); } catch (RegexMatchTimeoutException) { return false; }
    }

    private static string FolderNameFor(string pageUrl) =>
        Fluxer.Core.Platform.Windows.FileNameSanitizer.Sanitize(Uri.TryCreate(pageUrl, UriKind.Absolute, out var u) ? u.Host : "page");

    /// HEAD requests (6 at a time) so the minimum-size filter has something to work with.
    private static async Task FetchSizesAsync(List<GrabRow> rows, HttpEngine http, RequestContext ctx, Action changed, CancellationToken ct)
    {
        try
        {
            await Parallel.ForEachAsync(rows.Take(600), new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (row, token) =>
            {
                try
                {
                    using var req = HttpEngine.NewRequest(HttpMethod.Head, row.Url, ctx);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    using var resp = await http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if (resp.Content.Headers.ContentLength is { } len)
                        Dispatcher.UIThread.Post(() => { row.Size = len; row.UpdateInfo(); });
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException) { }
            });
            changed();
        }
        catch (OperationCanceledException) { }
    }
}
