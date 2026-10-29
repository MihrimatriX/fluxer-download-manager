using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Fluxer.App.Controls;
using Fluxer.App.ViewModels;

namespace Fluxer.App.Views;

/// The small always-on-top square links can be dropped on while the main window is hidden: links, text with links
/// and .torrent files are added just as on the main window. Drag it anywhere; double-click opens Fluxer; it shows
/// the total download speed while something downloads.
public sealed class DropTargetWindow : Window
{
    private readonly TextBlock _speed;
    private readonly Border _face;

    public DropTargetWindow(MainViewModel main, Action open, Action hide)
    {
        Width = Height = 64;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Title = "Fluxer";
        _speed = new TextBlock { FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
        var icon = new Icon { Data = ViewModels.Theme.Geometry("IcDown"), Size = 22, StrokeThickness = 2.4, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        _face = new Border
        {
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(4),
            Background = ViewModels.Theme.Brush("SystemControlHighlightAccentBrush"),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #50000000"),
            Opacity = 0.88,
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Children = { icon, _speed } },
        };
        Content = _face;
        ToolTip.SetTip(_face, Strings.DropTargetTip);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, (_, _) => _face.Opacity = 1);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => _face.Opacity = 0.88);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            _face.Opacity = 0.88;
            var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
            await main.DropAsync(files, e.DataTransfer.TryGetText());
        });
        PointerEntered += (_, _) => _face.Opacity = 1;
        PointerExited += (_, _) => _face.Opacity = 0.88;
        PointerPressed += (_, e) =>
        {
            if (e.ClickCount == 2) open();
            else if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        ContextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = Strings.TrayOpen, Command = new CommunityToolkit.Mvvm.Input.RelayCommand(open) },
                new MenuItem { Header = Strings.AddUrl, Command = main.AddUrlCommand },
                new Separator(),
                new MenuItem { Header = Strings.DropTargetHide, Command = new CommunityToolkit.Mvvm.Input.RelayCommand(hide) },
            },
        };
    }

    /// "12 MB/s" under the arrow while downloading; nothing when idle.
    public void Update(string speed)
    {
        _speed.Text = speed;
        _speed.IsVisible = speed.Length > 0;
    }
}
