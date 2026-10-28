using Avalonia;
using Avalonia.Controls;

namespace Fluxer.App.Controls;

/// One settings line: title and description on the left, the control (Content) on the right.
/// Template lives in App.axaml.
public sealed class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Title));
    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// Screen readers announce the row's title for its control ("Animations, toggle switch, on").
    /// Done on load: a panel's children are added after it becomes Content.
    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (Content is not Control c) return;
        var targets = c is Panel p ? p.Children.OfType<Control>().Where(x => x is not TextBlock) : [c];
        foreach (var t in targets)
        {
            if (string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(t)))
                Avalonia.Automation.AutomationProperties.SetName(t, Title);
            if (Description is { Length: > 0 } d)
                Avalonia.Automation.AutomationProperties.SetHelpText(t, d);
        }
    }
}
