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
    private MainViewModel? _vm;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowDetail)) ApplyDetailRow();
    }

    public MainWindow()
    {
        InitializeComponent();
        var list = List;
        // Check boxes mirror the selection both ways; the header box selects all / none.
        var all = new CheckBox { MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(all, Strings.SelectAll);
        ToolTip.SetTip(all, Strings.SelectAll);
        all.Click += (_, _) =>
        {
            if (list.SelectedItems.Count > 0 && list.SelectedItems.Count >= ((DataContext as MainViewModel)?.View.Count ?? 0)) list.SelectedItems.Clear();
            else list.SelectAll();
        };
        list.Columns[0].Header = all;
        // A 32 px header has no room for the default padding and sort glyph.
        all.AttachedToVisualTree += (_, _) =>
        {
            if (all.FindAncestorOfType<DataGridColumnHeader>() is not { } h) return;
            h.Padding = new Thickness(0);
            h.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            foreach (var g in h.GetVisualDescendants().OfType<Grid>())
                foreach (var cd in g.ColumnDefinitions) cd.MinWidth = 0;
        };
        list.SelectionChanged += (_, e) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.Selection.Clear();
            vm.Selection.AddRange(list.SelectedItems.OfType<DownloadItemViewModel>());
            foreach (var i in e.RemovedItems.OfType<DownloadItemViewModel>()) i.IsChecked = false;
            foreach (var i in e.AddedItems.OfType<DownloadItemViewModel>()) i.IsChecked = true;
            var n = list.SelectedItems.Count;
            all.IsChecked = n == 0 ? false : n >= vm.View.Count ? true : null;
            list.Classes.Set("checking", n > 1);
            vm.SelectionChanged();
        };
        // Size, speed and date line up on the right; so do their headers.
        foreach (var col in list.Columns.Where(c => c.Header is string h && (h == Strings.ColSize || h == Strings.ColSpeed || h == Strings.ColAdded)))
        {
            var text = new TextBlock { Text = (string)col.Header!, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            text.AttachedToVisualTree += (_, _) =>
            {
                if (text.FindAncestorOfType<DataGridColumnHeader>() is { } h) h.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            };
            col.Header = text;
        }
        list.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is not CheckBox { DataContext: DownloadItemViewModel item } box || !box.Classes.Contains("rowCheck")) return;
            if (item.IsChecked) list.SelectedItems.Add(item); else list.SelectedItems.Remove(item);
            e.Handled = true;
        });
        list.DoubleTapped += (_, _) => (DataContext as MainViewModel)?.OpenDetailsCommand.Execute(null);
        // DataGrid consumes Enter/Space itself, so listen to handled events too.
        list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        // Column headers swallow the right click, so ContextRequested never fires there: watch the release.
        list.AddHandler(PointerReleasedEvent, OnHeaderPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        // One subscription at a time, dropped on close: the window is rebuilt on language/theme changes
        // and must not keep the old one alive through the view model.
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as MainViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        };
        Closed += (_, _) => { if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged; };

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            if (DataContext is MainViewModel vm && IsVisible && WindowState != WindowState.Minimized && !vm.IsSettingsOpen)
                vm.Tick();
        });
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();

        // Narrow list (detail panel open, small window): the date column steps aside instead of being cut.
        list.SizeChanged += (_, e) => ApplyNarrow(e.NewSize.Width);

        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // ================= layout the user can change =================

    /// Column keys in XAML order. The check box and name columns cannot be hidden.
    private static readonly string[] ColumnKeys = ["check", "name", "size", "progress", "speed", "status", "added"];
    private static readonly HashSet<string> Fixed = ["check", "name"];
    private static readonly int[] DefaultWidths = [32, 0, 84, 150, 90, 156, 96];

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && SearchBox.IsVisible)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

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
            if (key is not ("name" or "check") && ui.ColumnWidths.TryGetValue(key, out var width) && width > 20) cols[i].Width = new DataGridLength(width);
        }
        // Layouts saved before the check box column existed: it goes first.
        List<string> saved = ui.ColumnOrder.Count == 0 || ui.ColumnOrder.Contains("check") ? ui.ColumnOrder : ["check", .. ui.ColumnOrder];
        for (var order = 0; order < saved.Count; order++)
        {
            var i = Array.IndexOf(ColumnKeys, saved[order]);
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
            if (ColumnKeys[i] is not ("name" or "check")) ui.ColumnWidths[ColumnKeys[i]] = cols[i].ActualWidth;
        ui.ColumnOrder = [.. Enumerable.Range(0, Math.Min(cols.Count, ColumnKeys.Length)).OrderBy(i => cols[i].DisplayIndex).Select(i => ColumnKeys[i])];
        if (ListArea.RowDefinitions[2].Height.IsAbsolute && ListArea.RowDefinitions[2].Height.Value > MinDetail)
            ui.DetailHeight = ListArea.RowDefinitions[2].Height.Value;
    }

    private void ApplyNarrow(double width)
    {
        var i = Array.IndexOf(ColumnKeys, "added");
        if (CurrentUi is null || i >= List.Columns.Count) return;
        List.Columns[i].IsVisible = !CurrentUi.HiddenColumns.Contains("added") && width >= 820;
    }

    /// The detail panel's smallest height: headline, progress strip, tabs and a few lines.
    private const double MinDetail = 150;

    /// The detail row under the list has a remembered pixel height while visible, and no height when hidden.
    private void ApplyDetailRow()
    {
        if (DataContext is not MainViewModel vm || CurrentUi is null) return;
        var row = ListArea.RowDefinitions[2];
        // At most half the list area, so a remembered tall panel can't squeeze the list to nothing.
        var max = ListArea.Bounds.Height > 0 ? Math.Max(MinDetail, ListArea.Bounds.Height * 0.5) : 400;
        if (vm.ShowDetail) row.Height = new GridLength(Math.Clamp(CurrentUi!.DetailHeight, MinDetail, max));
        else
        {
            if (row.Height.IsAbsolute && row.Height.Value > MinDetail) CurrentUi!.DetailHeight = row.Height.Value;
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
            var item = new MenuItem { Header = col.Header is TextBlock tb ? tb.Text : col.Header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = col.IsVisible };
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
            for (var i = 0; i < cols.Count && i < DefaultWidths.Length; i++)
                if (ColumnKeys[i] != "name") cols[i].Width = new DataGridLength(DefaultWidths[i]);
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
