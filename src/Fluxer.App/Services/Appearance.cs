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

    /// Mica: the window chrome lets the system backdrop through; the content surface keeps a tint so text stays readable.
    private static readonly Dictionary<string, (string Light, string Dark)> OnMica = new()
    {
        ["MainBrush"] = ("#00FFFFFF", "#00000000"),
        ["SurfaceBrush"] = ("#D9FFFFFF", "#CC191C21"),
        ["CardBrush"] = ("#B3FFFFFF", "#0DFFFFFF"),
    };

    /// Acrylic: the blurred desktop shows through a light veil of the base colour.
    private static readonly Dictionary<string, (string Light, string Dark)> OnGlass = new()
    {
        ["MainBrush"] = ("#80EEF0F3", "#80121418"),
        ["SurfaceBrush"] = ("#C7FFFFFF", "#BF191C21"),
        ["CardBrush"] = ("#B3FFFFFF", "#0DFFFFFF"),
    };

    /// The logo blue; the accent when the user has not picked one.
    public const string DefaultAccent = "#2563EB";

    /// Fluent brushes drawn on top of the accent colour.
    private static readonly string[] OnAccentKeys =
    [
        "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed",
        "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed",
        "CheckBoxCheckGlyphForegroundIndeterminate", "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "CheckBoxCheckGlyphForegroundIndeterminatePressed",
        "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed",
        "RadioButtonCheckGlyphFill", "RadioButtonCheckGlyphFillPointerOver", "RadioButtonCheckGlyphFillPressed",
    ];

    private static bool Translucent(UiSettings ui) => ui.Backdrop is "glass" or "mica" && _backdropWorks;

    public static void Apply(Application app, UiSettings ui)
    {
        var fluent = app.Styles.OfType<FluentTheme>().First();
        fluent.DensityStyle = DensityStyle.Normal;

        fluent.Palettes.Clear();
        var accent = Color.TryParse(ui.Accent ?? DefaultAccent, out var a) ? a : Color.Parse(DefaultAccent);
        fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = accent };
        fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = accent };
        // Text on the accent: dark on light accents (amber, green), white on deep ones.
        var luminance = (0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B) / 255;
        var onAccent = new SolidColorBrush(luminance > 0.55 ? Color.Parse("#14110A") : Colors.White);
        app.Resources["OnAccentBrush"] = onAccent;
        foreach (var key in OnAccentKeys) app.Resources[key] = onAccent;
        app.Resources["AccentSoftBrush"] = new SolidColorBrush(accent, 0.14);
        // Selected settings section: a bar on the left edge; selected tab: a line under it.
        app.Resources["AccentBarShadow"] = BoxShadows.Parse($"inset 2 0 0 0 {accent}");
        app.Resources["UnderlineShadow"] = BoxShadows.Parse($"inset 0 -2 0 0 {accent}");

        app.Resources["RowHeight"] = ui.Compact ? 32.0 : 40.0;
        ApplyLayers(app, !Translucent(ui) ? null : ui.Backdrop == "mica" ? OnMica : OnGlass);

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
        // Clear (not null) to get MainBrush back from the Window style: a local null means no background at all.
        if (Translucent(ui)) w.Background = Brushes.Transparent; else w.ClearValue(Window.BackgroundProperty);
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
        if (w.IsVisible) NativeChrome(w); else w.Opened += (_, _) => NativeChrome(w);

        // Scale everything inside the window; vector rendering keeps it crisp.
        if (w.Content is Control content && w.Content is not Panel { Tag: TitleTag })
        {
            w.Content = null;
            var scaled = new LayoutTransformControl { Child = content };
            // The main window draws its own title area; the others get a slim strip with the app mark.
            // Windows with the "ownchrome" class draw their own header and close button (the add dialog).
            var root = new Panel { Tag = TitleTag };
            var own = w.Classes.Contains("ownchrome");
            if (w is Views.MainWindow || own) root.Children.Add(scaled);
            else { root.Children.Add(WithTop(scaled)); root.Children.Add(TitleStrip()); }
            if (!own) root.Children.Add(CaptionButtons(w));
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
        c.Margin = new Thickness(0, TitleBarHeight - 6, 0, 0);
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
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                IsHitTestVisible = false,
                Children =
                {
                    new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Fluxer/Assets/fluxer.png"))), Width = 14, Height = 14 },
                    new TextBlock { Text = "Fluxer", FontSize = 12, Opacity = 0.65, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
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

    /// No 1 px frame around the window (Windows 11), so the app reads as one surface. Border-only windows
    /// also lose the min/max styles, and with them double-click-to-maximize, Win+Up and drag-to-top
    /// snapping: put those back on resizable windows.
    private static void NativeChrome(Window w)
    {
        const int GwlStyle = -16, WsMinimizeBox = 0x20000, WsMaximizeBox = 0x10000, DwmwaBorderColor = 34;
        if (!OperatingSystem.IsWindows() || w.TryGetPlatformHandle()?.Handle is not { } hwnd) return;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(int));
        }
        if (w.CanResize)
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
        if (Application.Current is { } app) ApplyLayers(app, null);
        w.ClearValue(Window.BackgroundProperty);
    }

    /// Sets the translucent layer colours, or (null) puts App.axaml's opaque ones back.
    private static void ApplyLayers(Application app, Dictionary<string, (string Light, string Dark)>? table)
    {
        foreach (var (variant, light) in new[] { (ThemeVariant.Light, true), (ThemeVariant.Dark, false) })
        {
            if (!app.Resources.ThemeDictionaries.TryGetValue(variant, out var provider) || provider is not IResourceDictionary dict) continue;
            foreach (var key in OnMica.Keys)
            {
                if (!Original.ContainsKey((light, key))) Original[(light, key)] = dict.TryGetValue(key, out var o) ? o : null;
                if (table is not null && table.TryGetValue(key, out var v)) dict[key] = new SolidColorBrush(Color.Parse(light ? v.Light : v.Dark));
                else if (Original[(light, key)] is { } orig) dict[key] = orig;
            }
        }
    }

    /// App.axaml's opaque layer colours, kept so leaving a translucent style restores them.
    private static readonly Dictionary<(bool Light, string Key), object?> Original = [];

    /// The logo blue first (also what "no choice" means), then the rest.
    public static readonly string[] AccentChoices =
        [DefaultAccent, "#0D9488", "#16A34A", "#D97706", "#EA580C", "#E11D48", "#7C3AED", "#64748B"];
}
