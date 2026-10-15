using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fluxer.App.ViewModels;

namespace Fluxer.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        var list = this.FindControl<DataGrid>("List")!;
        list.SelectionChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.Selection.Clear();
            vm.Selection.AddRange(list.SelectedItems.OfType<DownloadItemViewModel>());
        };
        list.DoubleTapped += (_, _) => (DataContext as MainViewModel)?.OpenDetailsCommand.Execute(null);
        // DataGrid consumes Enter/Space itself, so listen to handled events too.
        list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            if (DataContext is MainViewModel vm && IsVisible && WindowState != WindowState.Minimized && !vm.IsSettingsOpen)
                vm.Tick();
        });
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();

        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Delete: vm.DeleteCommand.Execute(null); e.Handled = true; break;
            case Key.Space: vm.TogglePauseCommand.Execute(null); e.Handled = true; break;
            case Key.Enter: vm.OpenFileCommand.Execute(null); e.Handled = true; break;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        var text = e.DataTransfer.TryGetText();
        await vm.DropAsync(files, text);
    }
}
