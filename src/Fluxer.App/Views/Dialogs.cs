using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Fluxer.App.Services;

namespace Fluxer.App.Views;

/// Small modal dialogs built in code. When the main window is hidden (browser-initiated flows),
/// dialogs open topmost on their own so they are not lost behind the browser.
public static partial class Dialogs
{
    public sealed record Button(string Text, bool Accent, Func<bool> OnClick);

    /// Standard dialog chrome: title, body, right-aligned buttons. A button closes the window when OnClick returns true.
    public static Window Shell(string title, Control body, double width, params Button[] buttons)
    {
        var w = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxHeight = 820,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Fluxer/Assets/fluxer.ico"))),
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons)
        {
            var btn = new Avalonia.Controls.Button { Content = b.Text, MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (b.Accent) { btn.Classes.Add("accent"); btn.IsDefault = true; }
            btn.Click += (_, _) => { if (b.OnClick()) w.Close(); };
            bar.Children.Add(btn);
        }
        // Same frame as every window: chrome around one surface, the title inside it.
        var root = new DockPanel { Margin = new Thickness(18, 14, 18, 14), Width = width - 52 };
        var header = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(bar, Dock.Bottom);
        bar.Margin = new Thickness(0, buttons.Length > 0 ? 16 : 0, 0, 0);
        root.Children.Add(header);
        root.Children.Add(bar);
        // Tall bodies (and 130 % scale) scroll instead of being cut at MaxHeight.
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        w.Content = new Border { Classes = { "surface" }, Margin = new Thickness(8, 0, 8, 8), Child = root };
        w.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) w.Close(); };
        return w;
    }

    public static async Task ShowAsync(Window w, Window? owner)
    {
        Appearance.ApplyTo(w, AppHost.Current.Engine.Settings.Ui);
        if (owner is not null)
        {
            await w.ShowDialog(owner);
            return;
        }
        var tcs = new TaskCompletionSource();
        w.Closed += (_, _) => tcs.TrySetResult();
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowInTaskbar = true;
        w.Topmost = true;
        w.Show();
        w.Activate();
        await tcs.Task;
    }

    public static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 4), FontSize = 11.5, Classes = { "subtle" } };

    public static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };

    // ================= simple dialogs =================

    public static async Task<bool> ConfirmAsync(Window? owner, string title, string message, string? yes = null, string? no = null)
    {
        var result = false;
        var w = Shell(title, Paragraph(message), 460,
            new Button(no ?? Strings.Cancel, false, () => true),
            new Button(yes ?? Strings.Ok, true, () => result = true));
        await ShowAsync(w, owner);
        return result;
    }

    public static async Task MessageAsync(Window? owner, string title, string message)
    {
        var w = Shell(title, Paragraph(message), 460, new Button(Strings.Ok, true, () => true));
        await ShowAsync(w, owner);
    }

    public static async Task<string?> InputAsync(Window? owner, string title, string prompt, string? initial = null)
    {
        string? result = null;
        var box = new TextBox { Text = initial ?? "" };
        var body = new StackPanel { Children = { Paragraph(prompt), box } };
        box.Margin = new Thickness(0, 10, 0, 0);
        var w = Shell(title, body, 520,
            new Button(Strings.Cancel, false, () => true),
            new Button(Strings.Ok, true, () => { result = box.Text; return true; }));
        w.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await ShowAsync(w, owner);
        return result;
    }

    public static async Task<(bool Ok, bool DeleteFiles)> ConfirmDeleteAsync(Window? owner, int count)
    {
        var ok = false;
        var files = new CheckBox { Content = Strings.DeleteFilesToo, Margin = new Thickness(0, 12, 0, 0) };
        var body = new StackPanel { Children = { Paragraph(string.Format(CultureInfo.CurrentCulture, Strings.DeleteConfirm, count)), files } };
        var w = Shell(Strings.Delete, body, 440,
            new Button(Strings.Cancel, false, () => true),
            new Button(Strings.Delete, true, () => ok = true));
        await ShowAsync(w, owner);
        return (ok, files.IsChecked == true);
    }

    // ================= platform helpers =================

    public static async Task<IReadOnlyList<string>> PickTorrentFilesAsync(Window? owner)
    {
        if (owner is null) return [];
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Torrent") { Patterns = ["*.torrent"] }],
        });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public static async Task<string?> PickFolderAsync(TopLevel top, string? start)
    {
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            SuggestedStartLocation = start is null ? null : await top.StorageProvider.TryGetFolderFromPathAsync(start),
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task SetClipboardTextAsync(Window? owner, string text)
    {
        if (owner?.Clipboard is { } cb) await cb.SetTextAsync(text);
    }

    public static async Task<string?> GetClipboardTextAsync(Window? owner) =>
        owner?.Clipboard is { } cb ? await cb.TryGetTextAsync() : null;

    /// Folder text box + browse button.
    public static (Control View, Func<string> Value) FolderPicker(string initial)
    {
        var box = new TextBox { Text = initial };
        var browse = new Avalonia.Controls.Button { Content = Strings.Browse, Margin = new Thickness(6, 0, 0, 0), MinHeight = 30 };
        browse.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(browse) is { } top && await PickFolderAsync(top, box.Text) is { } path) box.Text = path;
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(box);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(browse);
        return (grid, () => box.Text ?? initial);
    }
}
