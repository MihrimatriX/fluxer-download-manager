using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Fluxer.Core.Downloads;

namespace Fluxer.App.Services;

/// Applies the user's look settings (accent, density, Mica, motion, scale) to the app and every
/// open window, live — no restart, no window rebuild.
public static class Appearance
{
    private static bool _micaWorks = true;

    /// Opaque palette and the translucent layers used on top of Mica (Windows 11 "layer" colours).
    private static readonly Dictionary<string, (string Light, string Dark)> Solid = new()
    {
        ["SidebarBrush"] = ("#F3F3F5", "#1C1C1F"),
        ["MainBrush"] = ("#FCFCFD", "#161618"),
        ["SurfaceBrush"] = ("#FFFFFF", "#1E1E21"),
        ["CardBrush"] = ("#F6F6F8", "#222226"),
    };

    private static readonly Dictionary<string, (string Light, string Dark)> OnMica = new()
    {
        ["SidebarBrush"] = ("#00FFFFFF", "#00000000"),
        ["MainBrush"] = ("#B3FFFFFF", "#4C2A2A2E"),
        ["SurfaceBrush"] = ("#80FFFFFF", "#26FFFFFF"),
        ["CardBrush"] = ("#B3FFFFFF", "#12FFFFFF"),
    };

    public static void Apply(Application app, UiSettings ui)
    {
        var fluent = app.Styles.OfType<FluentTheme>().First();
        fluent.DensityStyle = ui.Compact ? DensityStyle.Compact : DensityStyle.Normal;

        fluent.Palettes.Clear();
        if (ui.Accent is { } hex && Color.TryParse(hex, out var accent))
        {
            fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = accent };
            fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = accent };
        }

        app.Resources["RowHeight"] = ui.Compact ? 32.0 : 42.0;
        ApplyLayers(app, ui.Mica && _micaWorks);

        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var w in desktop.Windows) ApplyTo(w, ui);
    }

    /// Per-window part: backdrop, motion class, UI scale. Call once after a window is created.
    public static void ApplyTo(Window w, UiSettings ui)
    {
        w.Classes.Set("motion", ui.Animations);
        w.TransparencyLevelHint = ui.Mica && _micaWorks
            ? [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.None];
        w.Background = ui.Mica && _micaWorks ? Brushes.Transparent : null; // null → MainBrush from the Window style
        if (ui.Mica && !w.IsVisible) w.Opened += CheckBackdrop;

        // Scale everything inside the window; vector rendering keeps it crisp.
        if (w.Content is Control content && content is not LayoutTransformControl)
        {
            w.Content = null;
            w.Content = new LayoutTransformControl { Child = content };
        }
        if (w.Content is LayoutTransformControl ltc)
            ltc.LayoutTransform = Math.Abs(ui.Scale - 1) < 0.01 ? null : new ScaleTransform(ui.Scale, ui.Scale);
    }

    /// Windows 10 (or a disabled effect) has no Mica: fall back to opaque colours once, app-wide.
    private static void CheckBackdrop(object? sender, EventArgs e)
    {
        if (sender is not Window w) return;
        w.Opened -= CheckBackdrop;
        if (w.ActualTransparencyLevel != WindowTransparencyLevel.None || !_micaWorks) return;
        _micaWorks = false;
        if (Application.Current is { } app) ApplyLayers(app, false);
        w.Background = null;
    }

    private static void ApplyLayers(Application app, bool mica)
    {
        var table = mica ? OnMica : Solid;
        foreach (var (variant, light) in new[] { (ThemeVariant.Light, true), (ThemeVariant.Dark, false) })
        {
            if (app.Resources.ThemeDictionaries.TryGetValue(variant, out var provider) && provider is IResourceDictionary dict)
                foreach (var (key, (l, d)) in table)
                    dict[key] = new SolidColorBrush(Color.Parse(light ? l : d));
        }
    }

    public static readonly string[] AccentChoices =
        ["#0A84FF", "#5E5CE6", "#BF5AF2", "#FF375F", "#FF9F0A", "#30D158", "#40C8E0", "#8E8E93"];
}
