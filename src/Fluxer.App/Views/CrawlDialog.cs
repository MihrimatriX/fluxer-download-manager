using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Fluxer.App.Services;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Ipc;
using Serilog;

namespace Fluxer.App.Views;

public static partial class Dialogs
{
    /// Start address and limits for the site crawler; null when cancelled.
    public static async Task<(Uri Start, SiteCrawler.Options Options)?> CrawlOptionsAsync(Window? owner, string? suggested)
    {
        (Uri, SiteCrawler.Options)? result = null;
        var w = CrawlOptionsWindow(suggested, r => result = r);
        await ShowAsync(w, owner);
        return result;
    }

    public static Window CrawlOptionsWindow(string? suggested, Action<(Uri Start, SiteCrawler.Options Options)> chosen)
    {
        var url = new TextBox { Text = suggested ?? "", PlaceholderText = "https://example.org/files/" };
        var depth = new NumericUpDown { Minimum = 0, Maximum = 5, Value = 1, Increment = 1, FormatString = "0", Width = 120 };
        var pages = new NumericUpDown { Minimum = 1, Maximum = 2000, Value = 100, Increment = 50, FormatString = "0", Width = 120 };
        var same = new CheckBox { Content = Strings.CrawlSameSite, IsChecked = true };
        var ext = new TextBox { PlaceholderText = Strings.CrawlExtensionsHint };
        var error = new TextBlock { Classes = { "subtle" }, FontSize = 12, IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };

        static StackPanel Field(string label, Control c) =>
            new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 20, 6), Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, c } };

        var body = new StackPanel
        {
            Children =
            {
                Paragraph(Strings.CrawlDesc),
                Label(Strings.CrawlStart), url,
                Label(Strings.CrawlLimits),
                new WrapPanel { Children = { Field(Strings.CrawlDepth, depth), Field(Strings.CrawlMaxPages, pages) } },
                same,
                Label(Strings.CrawlExtensions), ext,
                error,
            },
        };
        var w = Shell(Strings.CrawlTitle, body, 560,
            new Button(Strings.Cancel, false, () => true),
            new Button(Strings.CrawlGo, true, () =>
            {
                if (!Uri.TryCreate(url.Text?.Trim(), UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https"))
                {
                    error.Text = Strings.CrawlBadUrl;
                    error.IsVisible = true;
                    return false;
                }
                chosen((start, new SiteCrawler.Options((int)(depth.Value ?? 1), same.IsChecked == true, (int)(pages.Value ?? 100),
                    Category.ParseList(ext.Text, extensions: true))));
                return true;
            }));
        w.Opened += (_, _) => url.Focus();
        return w;
    }
}
