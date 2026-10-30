using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Fluxer.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        // Escape goes back to the downloads, like closing a dialog.
        KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape || TopLevel.GetTopLevel(this)?.DataContext is not ViewModels.MainViewModel vm) return;
            vm.CloseSettingsCommand.Execute(null);
            e.Handled = true;
        };
    }
}
