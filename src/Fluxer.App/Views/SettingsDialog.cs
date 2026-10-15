using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Fluxer.App.Services;
using Fluxer.Core.Downloads;
using Fluxer.Core.Platform.Windows;
using Fluxer.Ipc;

namespace Fluxer.App.Views;

public static partial class Dialogs
{
    public static async Task SettingsAsync(AppHost host)
    {
        var engine = host.Engine;
        // Work on a copy; nothing changes until Save.
        var s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(engine.Settings))!;

        // --- General
        var (folder, folderValue) = FolderPicker(s.DownloadDirectory);
        var categoryFolders = Check(Strings.UseCategoryFolders, s.UseCategoryFolders);
        var language = Combo([Strings.LangSystem, "Türkçe", "English"], s.Language switch { "tr" => 1, "en" => 2, _ => 0 });
        var theme = Combo([Strings.ThemeSystem, Strings.ThemeLight, Strings.ThemeDark], s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 });
        var showAdd = Check(Strings.ShowAddDialog, s.ShowAddDialog);
        var notifications = Check(Strings.ShowNotifications, s.Notifications);
        var conflict = Combo([Strings.ConflictRename, Strings.ConflictOverwrite], s.Conflict == ConflictPolicy.Overwrite ? 1 : 0);

        // --- Connections & speed
        var conns = Number(s.ConnectionsPerDownload, 1, 32);
        var httpSlots = Number(s.HttpSlots, 1, 20);
        var torrentSlots = Number(s.TorrentSlots, 1, 20);
        var httpLimit = Number(s.HttpLimit / 1024, 0, 10_000_000);
        var tDown = Number(s.TorrentDownloadLimit / 1024, 0, 10_000_000);
        var tUp = Number(s.TorrentUploadLimit / 1024, 0, 10_000_000);
        var seedRatio = new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 0.1m, Value = (decimal)s.SeedRatio, FormatString = "0.0", Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        var seedMinutes = Number(s.SeedMinutes, 0, 100_000);
        var proxy = new TextBox { Text = s.Proxy ?? "", PlaceholderText = "http://host:port" };

        // --- Schedule
        var start = new TextBox { Text = s.ScheduleStart ?? "", PlaceholderText = "HH:mm", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var stop = new TextBox { Text = s.ScheduleStop ?? "", PlaceholderText = "HH:mm", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var after = Combo([Strings.AfterNothing, Strings.AfterSleep, Strings.AfterShutdown], s.AfterQueue switch { "Sleep" => 1, "Shutdown" => 2, _ => 0 });

        // --- Integration
        var integration = new StackPanel { Spacing = 4 };
        void FillIntegration()
        {
            integration.Children.Clear();
            foreach (var (browser, ok) in ShellIntegration.Status())
                integration.Children.Add(new TextBlock { Text = $"{(ok ? "✓" : "✗")}  {browser}", Foreground = ok ? Brushes.SeaGreen : Brushes.IndianRed });
        }
        FillIntegration();
        var assoc = Check(Strings.RegisterAssociations, s.RegisterFileAssociations);
        var extId = new SelectableTextBlock { Text = $"{Strings.ExtensionId}: {Protocol.AllowedExtensionIds[0]}", Opacity = 0.75, FontSize = 12 };
        var extPath = new SelectableTextBlock { Text = Path.Combine(AppContext.BaseDirectory, "extension"), Opacity = 0.75, FontSize = 12, TextWrapping = TextWrapping.Wrap };

        // --- Media tools
        var toolsInfo = new TextBlock { Text = engine.Tools.IsInstalled ? "…" : Strings.ToolsNotInstalled, Opacity = 0.8 };
        if (engine.Tools.IsInstalled) _ = Task.Run(async () =>
        {
            var v = await engine.Tools.GetYtDlpVersionAsync(CancellationToken.None);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => toolsInfo.Text = $"yt-dlp {v}");
        });
        var updateTools = new Avalonia.Controls.Button { Content = Strings.UpdateNow, IsEnabled = engine.Tools.IsInstalled };
        updateTools.Click += async (_, _) =>
        {
            updateTools.IsEnabled = false;
            try
            {
                var updated = await engine.Tools.UpdateYtDlpAsync(CancellationToken.None);
                toolsInfo.Text = $"yt-dlp {await engine.Tools.GetYtDlpVersionAsync(CancellationToken.None)}" + (updated ? " ✓" : "");
            }
            catch (Exception e) { toolsInfo.Text = e.Message; }
            updateTools.IsEnabled = true;
        };

        // --- Updates & diagnostics
        var feed = new TextBox { Text = s.UpdateFeed ?? "", PlaceholderText = Strings.UpdateFeedWatermark };
        var restart = new Avalonia.Controls.Button { Content = Strings.RestartToUpdate, IsVisible = host.HasPendingUpdate };
        restart.Click += (_, _) => host.RestartToUpdate();
        var export = new Avalonia.Controls.Button { Content = Strings.ExportDiagnostics };
        export.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(export) is not { } top) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                SuggestedFileName = $"fluxer-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
                DefaultExtension = "zip",
            });
            if (file?.TryGetLocalPath() is { } path) ExportDiagnostics(engine.Settings, path);
        };

        var tabs = new TabControl
        {
            Height = 470,
            ItemsSource = new[]
            {
                Tab(Strings.TabGeneral, Strings.SaveTo, folder, null, categoryFolders, Strings.Language, language, Strings.Theme, theme,
                    null, showAdd, null, notifications, Strings.Conflict, conflict),
                Tab(Strings.TabConnections, Strings.Connections, conns, Strings.HttpSlots, httpSlots, Strings.TorrentSlots, torrentSlots,
                    Strings.HttpLimit, httpLimit, Strings.Proxy, proxy),
                Tab(Strings.TabTorrent, Strings.TorrentDownLimit, tDown, Strings.TorrentUpLimit, tUp, Strings.SeedRatio, seedRatio,
                    Strings.SeedMinutes, seedMinutes),
                Tab(Strings.TabSchedule, Strings.ScheduleStart, start, Strings.ScheduleStop, stop, Strings.AfterQueue, after),
                Tab(Strings.TabIntegration, Strings.BrowserStatus, integration, null, assoc, Strings.ExtensionFolder, extPath, null, extId,
                    Strings.MediaTools, toolsInfo, null, updateTools),
                Tab(Strings.TabAdvanced, Strings.UpdateFeed, feed, null, restart, Strings.Diagnostics, export),
            },
        };

        var saved = false;
        var w = Shell(Strings.Settings, tabs, 720,
            new Button(Strings.Cancel, false, () => true),
            new Button(Strings.Save, true, () =>
            {
                s.DownloadDirectory = folderValue();
                s.UseCategoryFolders = categoryFolders.IsChecked == true;
                s.Language = language.SelectedIndex switch { 1 => "tr", 2 => "en", _ => "" };
                s.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
                s.ShowAddDialog = showAdd.IsChecked == true;
                s.Notifications = notifications.IsChecked == true;
                s.Conflict = conflict.SelectedIndex == 1 ? ConflictPolicy.Overwrite : ConflictPolicy.Rename;
                s.ConnectionsPerDownload = (int)(conns.Value ?? 8);
                s.HttpSlots = (int)(httpSlots.Value ?? 3);
                s.TorrentSlots = (int)(torrentSlots.Value ?? 2);
                s.HttpLimit = (long)(httpLimit.Value ?? 0) * 1024;
                s.TorrentDownloadLimit = (long)(tDown.Value ?? 0) * 1024;
                s.TorrentUploadLimit = (long)(tUp.Value ?? 0) * 1024;
                s.SeedRatio = (double)(seedRatio.Value ?? 1);
                s.SeedMinutes = (int)(seedMinutes.Value ?? 60);
                s.Proxy = string.IsNullOrWhiteSpace(proxy.Text) ? null : proxy.Text.Trim();
                s.ScheduleStart = ValidTime(start.Text);
                s.ScheduleStop = ValidTime(stop.Text);
                s.AfterQueue = after.SelectedIndex switch { 1 => "Sleep", 2 => "Shutdown", _ => "None" };
                s.RegisterFileAssociations = assoc.IsChecked == true;
                s.UpdateFeed = string.IsNullOrWhiteSpace(feed.Text) ? null : feed.Text.Trim();
                saved = true;
                return true;
            }));
        await ShowAsync(w, host.Owner);
        if (!saved) return;

        var uiChanged = s.Language != engine.Settings.Language || s.Theme != engine.Settings.Theme;
        await engine.SaveSettingsAsync(s);
        if (uiChanged) host.ReloadUi();
    }

    private static string? ValidTime(string? t) =>
        TimeOnly.TryParseExact(t?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v.ToString("HH:mm", CultureInfo.InvariantCulture) : null;

    private static CheckBox Check(string text, bool value) => new() { Content = text, IsChecked = value };

    private static ComboBox Combo(string[] items, int index) => new() { ItemsSource = items, SelectedIndex = index, MinWidth = 220 };

    private static NumericUpDown Number(long value, long min, long max) => new()
    {
        Minimum = min, Maximum = max, Value = value, Increment = 1, FormatString = "0", Width = 160, HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// Tab built from (label, control) pairs; a null label means "no caption".
    private static TabItem Tab(string header, params object?[] pairs)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 8, 12, 8) };
        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            if (pairs[i] is string label) panel.Children.Add(Label(label));
            var c = (Control)pairs[i + 1]!;
            if (pairs[i] is null) c.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(c);
        }
        return new TabItem { Header = header, FontSize = 14, Content = new ScrollViewer { Content = panel } };
    }

    /// Logs + settings (proxy credentials stripped) — never the database (cookies) or download URLs.
    private static void ExportDiagnostics(AppSettings settings, string zipPath)
    {
        File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var logs = Path.Combine(Program.DataDirectory, "logs");
        if (Directory.Exists(logs))
            foreach (var f in Directory.EnumerateFiles(logs, "*.log"))
            {
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = zip.CreateEntry("logs/" + Path.GetFileName(f)).Open();
                src.CopyTo(dst);
            }
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        if (copy.Proxy is { } p && Uri.TryCreate(p, UriKind.Absolute, out var u) && u.UserInfo.Length > 0) copy.Proxy = p.Replace(u.UserInfo + "@", "***@", StringComparison.Ordinal);
        using (var w = new StreamWriter(zip.CreateEntry("settings.json").Open()))
            w.Write(JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        using (var w = new StreamWriter(zip.CreateEntry("system.txt").Open()))
        {
            w.WriteLine($"Fluxer {typeof(App).Assembly.GetName().Version}");
            w.WriteLine($"OS {Environment.OSVersion}  .NET {Environment.Version}  x64={Environment.Is64BitProcess}");
            foreach (var (b, ok) in ShellIntegration.Status()) w.WriteLine($"{b}: {(ok ? "ok" : "missing")}");
        }
    }
}
