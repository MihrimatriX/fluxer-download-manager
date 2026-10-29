using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Fluxer.App.Controls;
using Fluxer.App.ViewModels;
using Fluxer.Core.Stats;

namespace Fluxer.App.Views;

public static partial class Dialogs
{
    /// Statistics: downloaded today / this week / this month / ever, the last year day by day, and which sites
    /// and categories the downloads in the list came from.
    /// `day`: average speed per 5 minutes over the last 24 hours (B/s), oldest first.
    public static Window StatsWindow(StatsReport r, (double[] Down, double[] Up) day)
    {
        static string Files(int n) => string.Format(CultureInfo.CurrentCulture, Strings.StatsFiles, n);
        var tiles = new UniformGrid { Columns = 4, ColumnSpacing = 10 };
        tiles.Children.Add(Tile(Strings.StatsToday, r.Today, Files(r.FilesToday)));
        tiles.Children.Add(Tile(Strings.StatsWeek, r.Week, null));
        tiles.Children.Add(Tile(Strings.StatsMonth, r.Month, null));
        tiles.Children.Add(Tile(Strings.StatsAllTime, r.AllTime, $"↑ {Loc.Bytes(r.UploadedAllTime)} · {Files(r.FilesAllTime)}"));

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        legend.Children.Add(new TextBlock { Text = Strings.StatsLess, FontSize = 11, Classes = { "faint" }, Margin = new Thickness(0, 0, 4, 0) });
        foreach (var level in Heatmap.Levels)
            legend.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = AccentBrush(level), VerticalAlignment = VerticalAlignment.Center });
        legend.Children.Add(new TextBlock { Text = Strings.StatsMore, FontSize = 11, Classes = { "faint" }, Margin = new Thickness(4, 0, 0, 0) });

        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 14 };
        lists.Children.Add(Ranking(Strings.StatsSites, r.Sites, s => s));
        var cats = Ranking(Strings.StatsCategories, r.Categories, Cats.Title);
        Grid.SetColumn(cats, 1);
        lists.Children.Add(cats);

        var body = new StackPanel
        {
            Children =
            {
                tiles,
                Label(Strings.Last24h),
                new Border
                {
                    Classes = { "card" }, Padding = new Thickness(12, 10),
                    Child = new SpeedGraph { Height = 90, ShowScale = true, Capacity = 288, Values = day.Down, UpValues = day.Up },
                },
                Label(Strings.StatsYear),
                new Border { Classes = { "card" }, Padding = new Thickness(12, 10), Child = new StackPanel { Children = { new Heatmap { Days = r.Year }, legend } } },
                lists,
            },
        };
        return Shell(Strings.Statistics, body, 780, new Button(Strings.Close, true, () => true));
    }

    private static IBrush AccentBrush(double opacity) =>
        new SolidColorBrush(Application.Current?.TryFindResource("SystemAccentColor", Application.Current.ActualThemeVariant, out var v) == true && v is Color c ? c : Color.Parse("#0A84FF"), opacity);

    private static Border Tile(string label, long bytes, string? note)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Classes = { "subtle" } });
        panel.Children.Add(new TextBlock { Text = bytes > 0 ? Loc.Bytes(bytes) : "0 B", FontSize = 19, FontWeight = FontWeight.SemiBold });
        if (note is not null) panel.Children.Add(new TextBlock { Text = note, FontSize = 11, Classes = { "faint" }, TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border { Classes = { "card" }, Padding = new Thickness(12, 10), Child = panel };
    }

    /// Name, a bar against the biggest, size and count.
    private static StackPanel Ranking(string title, IReadOnlyList<Ranked> items, Func<string, string> name)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(title));
        var card = new StackPanel { Spacing = 8 };
        var max = Math.Max(1, items.Count > 0 ? items.Max(i => i.Bytes) : 1);
        foreach (var i in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new TextBlock { Text = name(i.Name), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 });
            var size = new TextBlock { Text = $"{Loc.Bytes(i.Bytes)} · {i.Count}", FontSize = 11.5, Classes = { "subtle" } };
            Grid.SetColumn(size, 1);
            row.Children.Add(size);
            var bar = new Bar { Value = 100.0 * i.Bytes / max, Height = 4, Foreground = AccentBrush(0.9), Background = AccentBrush(0.12), Margin = new Thickness(0, 4, 0, 0) };
            Grid.SetRow(bar, 1);
            Grid.SetColumnSpan(bar, 2);
            row.Children.Add(bar);
            card.Children.Add(row);
        }
        if (items.Count == 0) card.Children.Add(new TextBlock { Text = Strings.StatsNothing, Classes = { "subtle" }, FontSize = 12 });
        panel.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(12, 10), Child = card });
        return panel;
    }
}
