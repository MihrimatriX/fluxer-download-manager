using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Fluxer.Core.Downloads;

namespace Fluxer.App.Services;

/// Applies the user's look settings (density, motion, scale) to the app and every open window, live: no
/// restart, no window rebuild. Light or dark is the app's theme variant (AppHost); the accent is fixed.
public static partial class Appearance
{
    /// The logo blue: buttons, progress, selection.
    public const string Accent = "#2563EB";

    /// Fluent brushes drawn on top of the accent colour.
    private static readonly string[] OnAccentKeys =
    [
        "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed",
        "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed",
        "CheckBoxCheckGlyphForegroundIndeterminate", "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "CheckBoxCheckGlyphForegroundIndeterminatePressed",
        "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed",
        "RadioButtonCheckGlyphFill", "RadioButtonCheckGlyphFillPointerOver", "RadioButtonCheckGlyphFillPressed",
    ];

    public static void Apply(Application app, UiSettings ui)
    {
        var fluent = app.Styles.OfType<FluentTheme>().First();
        fluent.DensityStyle = DensityStyle.Normal;

        var accent = Color.Parse(Accent);
        fluent.Palettes.Clear();
        fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = accent };
        fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = accent };
        var onAccent = new SolidColorBrush(Colors.White);
        app.Resources["OnAccentBrush"] = onAccent;
        foreach (var key in OnAccentKeys) app.Resources[key] = onAccent;
        app.Resources["AccentSoftBrush"] = new SolidColorBrush(accent, 0.14);
        // Selected settings section: a bar on the left edge; selected tab: a line under it.
        app.Resources["AccentBarShadow"] = BoxShadows.Parse($"inset 2 0 0 0 {accent}");
        app.Resources["UnderlineShadow"] = BoxShadows.Parse($"inset 0 -2 0 0 {accent}");

        app.Resources["RowHeight"] = ui.Compact ? 32.0 : 40.0;

        // Only windows set up by ApplyTo; the tray popup and the palette draw their own frame.
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var w in desktop.Windows)
                if (w.Content is Panel { Tag: TitleTag }) ApplyTo(w, ui);
    }

    /// Per-window part: motion class, title area, UI scale. Call once after a window is created.
    public static void ApplyTo(Window w, UiSettings ui)
    {
        w.Classes.Set("motion", ui.Animations);

        // Content runs up to the top edge. The system caption is dropped (Windows paints its own strip, in a
        // colour that does not match ours); we draw the caption buttons ourselves and tag them so Windows still
        // treats them as caption buttons (Snap Layouts on maximize).
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

    [System.Runtime.InteropServices.LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
