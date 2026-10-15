using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Fluxer.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => AvaloniaXamlLoader.Load(this);
}
