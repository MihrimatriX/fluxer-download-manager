using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Fluxer.App.Views;

/// Small window above the tray icon: speeds, quick add, what is running. Hides when it loses focus.
public sealed partial class TrayWindow : Window
{
    public TrayWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Deactivated += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        this.FindControl<TextBox>("QuickBox")!.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is ViewModels.MainViewModel vm) vm.QuickAddCommand.Execute(null);
        };
    }

    /// Shows the window in the bottom-right corner of the primary screen (above the tray), or hides it.
    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        var screen = Screens.Primary;
        if (screen is null) return;
        Show(); // size is known once shown
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var w = (int)(Bounds.Width * scale);
        var h = (int)(Bounds.Height * scale);
        Position = new PixelPoint(area.Right - w - (int)(12 * scale), area.Bottom - h - (int)(12 * scale));
        Activate();
    }
}
