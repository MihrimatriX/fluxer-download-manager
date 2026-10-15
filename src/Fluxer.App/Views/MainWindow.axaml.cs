using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Fluxer.Core.Downloads;
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
        InitializeComponent();
        var list = List;
        list.SelectionChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.Selection.Clear();
            vm.Selection.AddRange(list.SelectedItems.OfType<DownloadItemViewModel>());
        };
        list.DoubleTapped += (_, _) => (DataContext as MainViewModel)?.OpenDetailsCommand.Execute(null);
        // DataGrid consumes Enter/Space itself, so listen to handled events too.
        list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        // Column headers swallow the right click, so ContextRequested never fires there: watch the release.
        list.AddHandler(PointerReleasedEvent, OnHeaderPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel m)
                m.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.ShowDetail)) ApplyDetailRow(); };
        };

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

    // ================= layout the user can change =================

    /// Column keys in XAML order. Name and the action column cannot be hidden.
    private static readonly string[] ColumnKeys = ["name", "size", "progress", "speed", "eta", "status", "added", "action"];
    private static readonly HashSet<string> Fixed = ["name", "action"];

    /// Always the live object: the settings page replaces Settings with a copy when it saves.
    private static UiSettings? CurrentUi => Services.AppHost.Current?.Engine?.Settings.Ui;

    public void RestoreLayout(UiSettings ui)
    {
        if (ui.WindowBounds is [var x, var y, var w, var h] && w > 400 && h > 300
            && Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint((int)x + 40, (int)y + 20))))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)x, (int)y);
            Width = w;
            Height = h;
        }
        if (ui.WindowMaximized) WindowState = WindowState.Maximized;

        var cols = List.Columns;
        for (var i = 0; i < cols.Count && i < ColumnKeys.Length; i++)
        {
            var key = ColumnKeys[i];
            cols[i].IsVisible = Fixed.Contains(key) || !ui.HiddenColumns.Contains(key);
            if (key != "name" && ui.ColumnWidths.TryGetValue(key, out var width) && width > 20) cols[i].Width = new DataGridLength(width);
        }
        for (var order = 0; order < ui.ColumnOrder.Count; order++)
        {
            var i = Array.IndexOf(ColumnKeys, ui.ColumnOrder[order]);
            if (i >= 0 && i < cols.Count) cols[i].DisplayIndex = Math.Min(order, cols.Count - 1);
        }
        ApplyDetailRow();
    }

    public void CaptureLayout(UiSettings ui)
    {
        if (WindowState == WindowState.Normal)
            ui.WindowBounds = [Position.X, Position.Y, Width, Height];
        ui.WindowMaximized = WindowState == WindowState.Maximized;
        var cols = List.Columns;
        ui.ColumnWidths.Clear();
        for (var i = 0; i < cols.Count && i < ColumnKeys.Length; i++)
            if (ColumnKeys[i] != "name") ui.ColumnWidths[ColumnKeys[i]] = cols[i].ActualWidth;
        ui.ColumnOrder = [.. Enumerable.Range(0, Math.Min(cols.Count, ColumnKeys.Length)).OrderBy(i => cols[i].DisplayIndex).Select(i => ColumnKeys[i])];
        if (DownloadsPage.RowDefinitions[4].Height.IsAbsolute && DownloadsPage.RowDefinitions[4].Height.Value > 80)
            ui.DetailHeight = DownloadsPage.RowDefinitions[4].Height.Value;
    }

    /// The detail row has a remembered pixel height while visible, and no height when hidden.
    private void ApplyDetailRow()
    {
        if (DataContext is not MainViewModel vm || CurrentUi is null) return;
        var row = DownloadsPage.RowDefinitions[4];
        if (vm.ShowDetail) row.Height = new GridLength(Math.Clamp(CurrentUi!.DetailHeight, 120, 700));
        else
        {
            if (row.Height.IsAbsolute && row.Height.Value > 80) CurrentUi!.DetailHeight = row.Height.Value;
            row.Height = GridLength.Auto;
        }
    }

    /// Right-click on a column header: show/hide columns, reset layout.
    private void OnHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || e.Source is not Control c
            || c.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is null || CurrentUi is null) return;
        var menu = new ContextMenu();
        var cols = List.Columns;
        for (var i = 0; i < cols.Count && i < ColumnKeys.Length; i++)
        {
            var key = ColumnKeys[i];
            if (Fixed.Contains(key)) continue;
            var col = cols[i];
            var item = new MenuItem { Header = col.Header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = col.IsVisible };
            item.Click += (_, _) =>
            {
                col.IsVisible = !col.IsVisible;
                if (col.IsVisible) CurrentUi!.HiddenColumns.Remove(key); else CurrentUi!.HiddenColumns.Add(key);
                (DataContext as MainViewModel)?.SaveLayoutCommand.Execute(null);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = Strings.ResetColumns };
        reset.Click += (_, _) =>
        {
            CurrentUi!.HiddenColumns.Clear();
            CurrentUi!.ColumnWidths.Clear();
            CurrentUi!.ColumnOrder.Clear();
            for (var i = 0; i < cols.Count; i++)
            {
                cols[i].IsVisible = true;
                cols[i].DisplayIndex = i;
            }
            int[] defaults = [0, 130, 130, 95, 75, 170, 105, 40];
            for (var i = 1; i < cols.Count && i < defaults.Length; i++) cols[i].Width = new DataGridLength(defaults[i]);
            (DataContext as MainViewModel)?.SaveLayoutCommand.Execute(null);
        };
        menu.Items.Add(reset);
        e.Handled = true;
        // Open after the release finished; opening inside it lets the same click dismiss the menu.
        Dispatcher.UIThread.Post(() => menu.Open(c), DispatcherPriority.Input);
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
