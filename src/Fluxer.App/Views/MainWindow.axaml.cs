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
    private readonly ListBox _list;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _list = this.FindControl<ListBox>("List")!;
        _list.SelectionChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.Selection.Clear();
            vm.Selection.AddRange(_list.SelectedItems?.OfType<DownloadItemViewModel>() ?? []);
        };
        _list.DoubleTapped += (_, _) => (DataContext as MainViewModel)?.OpenFileCommand.Execute(null);
        _list.KeyDown += OnListKeyDown;

        // ~4 Hz, and only for rows the virtualizing panel has actually realized.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            if (DataContext is MainViewModel vm && IsVisible && WindowState != WindowState.Minimized)
                vm.Tick(_list.GetRealizedContainers().Select(c => c.DataContext).OfType<DownloadItemViewModel>());
        });
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();

        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
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
