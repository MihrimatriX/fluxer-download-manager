using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Fluxer.App.ViewModels;

namespace Fluxer.App.Views;

/// Per-download progress window; in "card" mode a small completion card in the corner that closes
/// itself after a few seconds (not while the pointer is over it).
public sealed partial class DownloadWindow : Window
{
    private const int CardSeconds = 15;
    private readonly DispatcherTimer _timer;
    private readonly DownloadWindowViewModel _vm;
    private int _cardTicks;

    public DownloadWindow() : this(null!) { } // designer

    public DownloadWindow(DownloadWindowViewModel vm)
    {
        AvaloniaXamlLoader.Load(this);
        _vm = vm;
        DataContext = vm;
        if (vm is null) { _timer = new DispatcherTimer(); return; }

        if (vm.IsCard)
        {
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Opened += (_, _) => PlaceInCorner();
        }

        vm.Completed += OnCompleted;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => OnTick());
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        // Escape closes like every dialog; the download keeps running.
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }

    public long DownloadId => _vm.Download.Id;

    private void OnTick()
    {
        _vm.Tick();
        if (!_vm.IsCard || !_vm.IsCompleted) return;
        if (IsPointerOver) { _cardTicks = 0; SetCountdown(""); return; }
        var left = CardSeconds - ++_cardTicks / 4;
        SetCountdown(string.Format(CultureInfo.CurrentCulture, Strings.ClosesIn, left));
        if (left <= 0) Close();
    }

    private void SetCountdown(string text)
    {
        if (this.FindControl<TextBlock>("Countdown") is { } t) t.Text = text;
    }

    private void OnCompleted()
    {
        if (_vm.OpenFileWhenDone) _vm.OpenFileCommand.Execute(null);
        if (_vm.OpenFolderWhenDone) _vm.OpenFolderCommand.Execute(null);
        if (_vm.CloseWhenDone) Close();
    }

    private void PlaceInCorner()
    {
        if (Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        Position = new PixelPoint(
            area.Right - (int)(Bounds.Width * scale) - (int)(16 * scale),
            area.Bottom - (int)(Bounds.Height * scale) - (int)(16 * scale));
    }

    private void OnHide(object? sender, RoutedEventArgs e) => Close(); // the download keeps running
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
