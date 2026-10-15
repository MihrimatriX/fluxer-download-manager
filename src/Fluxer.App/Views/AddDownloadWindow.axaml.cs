using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Fluxer.App.ViewModels;

namespace Fluxer.App.Views;

public sealed partial class AddDownloadWindow : Window
{
    private readonly AddDownloadViewModel _vm;
    private AddResult? _result;

    public AddDownloadWindow() : this(null!) { } // designer

    public AddDownloadWindow(AddDownloadViewModel vm)
    {
        AvaloniaXamlLoader.Load(this);
        _vm = vm;
        DataContext = vm;
        var folder = this.FindControl<TextBox>("FolderBox")!;
        folder.TextChanged += (_, _) => { if (folder.IsFocused) vm?.MarkFolderTouched(); };
        Opened += (_, _) =>
        {
            if (vm is { Count: 0 }) this.FindControl<TextBox>("UrlBox")?.Focus();
        };
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }

    /// Shows the window (modal to the main window when it is visible) and returns the choice, or null.
    public static async Task<AddResult?> ShowAsync(Window? owner, AddDownloadViewModel vm)
    {
        var w = new AddDownloadWindow(vm);
        await Dialogs.ShowAsync(w, owner);
        return w._result;
    }

    private void OnSubmit(object? sender, RoutedEventArgs e) => Submit(startNow: true);

    private void OnQueue(object? sender, RoutedEventArgs e) => Submit(startNow: false);

    private void Submit(bool startNow)
    {
        _vm.Flush();
        if (_vm.Count == 0) return;
        _result = _vm.Result(startNow);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnPaste(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } cb || await cb.TryGetTextAsync() is not { Length: > 0 } text) return;
        _vm.UrlsText = string.IsNullOrWhiteSpace(_vm.UrlsText) ? text.Trim() : _vm.UrlsText.TrimEnd() + Environment.NewLine + text.Trim();
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFolderAsync(this, _vm.Folder) is { } path) _vm.SetFolderByUser(path);
    }
}
