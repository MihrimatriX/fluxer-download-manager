using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Fluxer.Core.Downloads;

namespace Fluxer.App.Services;

/// Applies the user's look settings (accent, density, backdrop, motion, scale) to the app and every
/// open window, live — no restart, no window rebuild.
public static partial class Appearance
{
    /// False once a window opened without the requested backdrop (Windows 10, effects off): stay opaque.
    private static bool _backdropWorks = true;

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

    /// Frosted glass: the blurred desktop shows through a light tint, so layers carry more of the colour.
    private static readonly Dictionary<string, (string Light, string Dark)> OnGlass = new()
    {
        ["SidebarBrush"] = ("#00FFFFFF", "#00000000"),
        ["MainBrush"] = ("#80FBFBFD", "#40141418"),
        ["SurfaceBrush"] = ("#8CFFFFFF", "#1FFFFFFF"),
        ["CardBrush"] = ("#99FFFFFF", "#14FFFFFF"),
    };

    private static bool Translucent(UiSettings ui) => ui.Backdrop != "none" && _backdropWorks;

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
        ApplyLayers(app, Translucent(ui) ? ui.Backdrop == "mica" ? OnMica : OnGlass : Solid);

        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var w in desktop.Windows) ApplyTo(w, ui);
    }

    /// Per-window part: backdrop, motion class, UI scale. Call once after a window is created.
    public static void ApplyTo(Window w, UiSettings ui)
    {
        w.Classes.Set("motion", ui.Animations);
        // Both translucent modes start as Mica (Avalonia then sets the window up for a Windows 11 system
        // backdrop, title area included); glass switches that backdrop to the system acrylic.
        w.TransparencyLevelHint = Translucent(ui)
            ? [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.None];
        w.Background = Translucent(ui) ? Brushes.Transparent : null; // null → MainBrush from the Window style
        if (Translucent(ui))
        {
            var kind = ui.Backdrop == "glass" ? DwmsbtTransientWindow : DwmsbtMainWindow;
            // After Avalonia has applied its own Mica setup.
            void Later() => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetSystemBackdrop(w, kind), Avalonia.Threading.DispatcherPriority.Background);
            if (w.IsVisible) Later();
            else
            {
                w.Opened += CheckBackdrop;
                w.Opened += (_, _) => Later();
            }
        }

        // Content runs up to the top edge. The system caption is dropped (Windows paints its strip opaque, which
        // hides the backdrop there); we draw the caption buttons ourselves and tag them so Windows still treats
        // them as caption buttons (Snap Layouts on maximize).
        w.ExtendClientAreaToDecorationsHint = true;
        w.ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        w.WindowDecorations = WindowDecorations.BorderOnly;
        if (w.IsVisible) RestoreSizeBoxes(w); else w.Opened += (_, _) => RestoreSizeBoxes(w);

        // Scale everything inside the window; vector rendering keeps it crisp.
        if (w.Content is Control content && w.Content is not Panel { Tag: TitleTag })
        {
            w.Content = null;
            var scaled = new LayoutTransformControl { Child = content };
            // The main window draws its own title area; the others get a slim strip with the app mark.
            var root = new Panel { Tag = TitleTag };
            if (w is Views.MainWindow) root.Children.Add(scaled);
            else { root.Children.Add(WithTop(scaled)); root.Children.Add(TitleStrip()); }
            root.Children.Add(CaptionButtons(w));
            w.Content = root;
        }
        var ltc = (w.Content as Panel)?.Children.OfType<LayoutTransformControl>().FirstOrDefault();
        if (ltc is not null)
            ltc.LayoutTransform = Math.Abs(ui.Scale - 1) < 0.01 ? null : new ScaleTransform(ui.Scale, ui.Scale);
    }

    public const double TitleBarHeight = 40;
    private const string TitleTag = "fluxer-titlebar";

    private static Control WithTop(Control c)
    {
        c.Margin = new Thickness(0, TitleBarHeight - 8, 0, 0);
        return c;
    }

    /// Drag area across the top: app icon and name, caption buttons sit over its right end.
    private static Control TitleStrip()
    {
        var strip = new Border
        {
            Height = TitleBarHeight,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Background = Brushes.Transparent,
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                IsHitTestVisible = false,
                Children =
                {
                    new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Fluxer/Assets/fluxer.png"))), Width = 16, Height = 16 },
                    new TextBlock { Text = "Fluxer", FontSize = 12, Opacity = 0.7, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                },
            },
        };
        Avalonia.Controls.Chrome.WindowDecorationProperties.SetElementRole(strip, Avalonia.Input.WindowDecorationsElementRole.TitleBar);
        return strip;
    }

    /// Minimize / maximize / close in the top-right corner, Windows 11 sized. Dialogs get only close.
    private static Control CaptionButtons(Window w)
    {
        Button Make(string glyph, string name, Avalonia.Input.WindowDecorationsElementRole role, Action click, bool close = false)
        {
            var b = new Button { Content = glyph, Classes = { "caption" } };
            if (close) b.Classes.Add("close");
            Avalonia.Automation.AutomationProperties.SetName(b, name);
            Avalonia.Controls.Chrome.WindowDecorationProperties.SetElementRole(b, role);
            b.Click += (_, _) => click();
            return b;
        }
        var max = Make("", Strings.Maximize, Avalonia.Input.WindowDecorationsElementRole.MaximizeButton,
            () => w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        var min = Make("", Strings.Minimize, Avalonia.Input.WindowDecorationsElementRole.MinimizeButton, () => w.WindowState = WindowState.Minimized);
        var close = Make("", Strings.CloseWindow, Avalonia.Input.WindowDecorationsElementRole.CloseButton, w.Close, close: true);
        void Sync()
        {
            max.Content = w.WindowState == WindowState.Maximized ? "" : "";
            max.IsVisible = w.CanResize;
            min.IsVisible = w.CanResize || w.ShowInTaskbar;
        }
        w.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty || e.Property == Window.CanResizeProperty || e.Property == Window.ShowInTaskbarProperty) Sync();
        };
        Sync();
        return new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Children = { min, max, close },
        };
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    /// Border-only windows lose the min/max styles, and with them double-click-to-maximize, Win+Up and
    /// drag-to-top snapping. Put them back on resizable windows.
    private static void RestoreSizeBoxes(Window w)
    {
        const int GwlStyle = -16, WsMinimizeBox = 0x20000, WsMaximizeBox = 0x10000;
        if (!w.CanResize || w.TryGetPlatformHandle()?.Handle is not { } hwnd) return;
        _ = SetWindowLongPtr(hwnd, GwlStyle, GetWindowLongPtr(hwnd, GwlStyle) | WsMinimizeBox | WsMaximizeBox);
    }

    private const int DwmwaSystemBackdropType = 38, DwmsbtMainWindow = 2, DwmsbtTransientWindow = 3;

    [System.Runtime.InteropServices.LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private struct Margins { public int Left, Right, Top, Bottom; }

    [System.Runtime.InteropServices.LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    /// Windows 11 22H2+: Mica (2) or acrylic (3). Older systems ignore it and keep Avalonia's fallback.
    private static void SetSystemBackdrop(Window w, int kind)
    {
        if (w.TryGetPlatformHandle()?.Handle is not { } hwnd || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return;
        // "Sheet of glass": the backdrop shows wherever we paint nothing, title area included.
        var all = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref all);
        _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref kind, sizeof(int));
    }

    /// Windows 10 (or a disabled effect) has no Mica: fall back to opaque colours once, app-wide.
    private static void CheckBackdrop(object? sender, EventArgs e)
    {
        if (sender is not Window w) return;
        w.Opened -= CheckBackdrop;
        if (w.ActualTransparencyLevel != WindowTransparencyLevel.None || !_backdropWorks) return;
        _backdropWorks = false;
        if (Application.Current is { } app) ApplyLayers(app, Solid);
        w.Background = null;
    }

    private static void ApplyLayers(Application app, Dictionary<string, (string Light, string Dark)> table)
    {
        foreach (var (variant, light) in new[] { (ThemeVariant.Light, true), (ThemeVariant.Dark, false) })
        {
            if (app.Resources.ThemeDictionaries.TryGetValue(variant, out var provider) && provider is IResourceDictionary dict)
            {
                foreach (var (key, (l, d)) in table)
                    dict[key] = new SolidColorBrush(Color.Parse(light ? l : d));
                // Fluent's expander is solid; give it the card layer so it sits on the glass like the rest.
                dict["ExpanderHeaderBackground"] = dict["CardBrush"];
                dict["ExpanderContentBackground"] = dict["CardBrush"];
            }
        }
    }

    public static readonly string[] AccentChoices =
        ["#0A84FF", "#5E5CE6", "#BF5AF2", "#FF375F", "#FF9F0A", "#30D158", "#40C8E0", "#8E8E93"];
}
