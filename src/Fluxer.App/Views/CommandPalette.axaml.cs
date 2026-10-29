using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fluxer.App.ViewModels;

namespace Fluxer.App.Views;

/// The Ctrl+K overlay. Keys go to the search box; ↑/↓ move through the results, Enter runs, Esc closes.
public sealed partial class CommandPalette : UserControl
{
    public CommandPalette()
    {
        AvaloniaXamlLoader.Load(this);
        var query = this.FindControl<TextBox>("QueryBox")!;
        var results = this.FindControl<ListBox>("Results")!;
        this.FindControl<Border>("Scrim")!.PointerPressed += (_, _) => Vm?.Close();
        // Tunnel: the text box would otherwise take Up/Down for its caret.
        query.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        results.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        results.Tapped += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is PaletteItem item) Vm?.Run(item);
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty || !IsVisible) return;
            Dispatcher.UIThread.Post(() => { query.Focus(); query.SelectAll(); }, DispatcherPriority.Input);
        };
    }

    private CommandPaletteViewModel? Vm => DataContext as CommandPaletteViewModel;

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Escape: vm.Close(); break;
            case Key.Down: vm.Move(1); break;
            case Key.Up: vm.Move(-1); break;
            case Key.Enter: vm.Run(); break;
            default: return;
        }
        if (vm.Selected is { } s) this.FindControl<ListBox>("Results")!.ScrollIntoView(s);
        e.Handled = true;
    }
}
