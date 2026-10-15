using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluxer.App.Services;
using Fluxer.App.Views;
using Fluxer.Core.Downloads;
using Fluxer.Core.Platform.Windows;
using Fluxer.Ipc;

namespace Fluxer.App.ViewModels;

public sealed record BrowserRow(string Browser, bool Ok)
{
    public string Glyph => Ok ? "" : "";
    public string Text => Ok ? Strings.IntegrationOk : Strings.IntegrationMissing;
}

/// Settings page. Every change is saved automatically (debounced); there is no Save button.
public sealed partial class SettingsViewModel : ObservableObject
{
    private const long KB = 1024, MB = 1024 * 1024;
    private readonly AppHost _host;
    private readonly DispatcherTimer _saveTimer;
    private bool _loading;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, async (_, _) =>
        {
            _saveTimer!.Stop();
            await SaveAsync();
        });
    }

    public string[] Languages => [Strings.LangSystem, "Türkçe", "English"];
    public string[] Themes => [Strings.ThemeSystem, Strings.ThemeLight, Strings.ThemeDark];
    public string[] Conflicts => [Strings.ConflictRename, Strings.ConflictOverwrite];
    public string[] Units => ["KB/s", "MB/s"];
    public string[] AfterQueueOptions => [Strings.AfterNothing, Strings.AfterSleep, Strings.AfterShutdown];
    public ObservableCollection<BrowserRow> Browsers { get; } = [];
    public string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");
    public string ExtensionId => Protocol.AllowedExtensionIds[0];
    public string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";

    // General
    [ObservableProperty] private int _languageIndex;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private bool _notifications;
    [ObservableProperty] private bool _showAddDialog;
    [ObservableProperty] private bool _showProgressWindow;
    [ObservableProperty] private bool _showCompletedCard;
    // Downloads
    [ObservableProperty] private string _downloadDirectory = "";
    [ObservableProperty] private bool _useCategoryFolders;
    [ObservableProperty] private int _conflictIndex;
    [ObservableProperty] private decimal? _connections;
    [ObservableProperty] private decimal? _httpSlots;
    [ObservableProperty] private decimal? _torrentSlots;
    // Speed
    [ObservableProperty] private bool _httpLimitOn;
    [ObservableProperty] private decimal? _httpLimitValue;
    [ObservableProperty] private int _httpLimitUnit;
    [ObservableProperty] private bool _torrentDownOn;
    [ObservableProperty] private decimal? _torrentDownValue;
    [ObservableProperty] private int _torrentDownUnit;
    [ObservableProperty] private bool _torrentUpOn;
    [ObservableProperty] private decimal? _torrentUpValue;
    [ObservableProperty] private int _torrentUpUnit;
    // Torrent
    [ObservableProperty] private decimal? _seedRatio;
    [ObservableProperty] private decimal? _seedMinutes;
    [ObservableProperty] private bool _fileAssociations;
    // Schedule
    [ObservableProperty] private bool _scheduleStartOn;
    [ObservableProperty] private TimeSpan? _scheduleStart;
    [ObservableProperty] private bool _scheduleStopOn;
    [ObservableProperty] private TimeSpan? _scheduleStop;
    [ObservableProperty] private int _afterQueueIndex;
    // Advanced
    [ObservableProperty] private string _proxy = "";
    [ObservableProperty] private string _updateFeed = "";
    // Read-only info
    [ObservableProperty] private string _toolsText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUpdateTools))] private bool _toolsBusy;
    public bool CanUpdateTools => !ToolsBusy && _host.Engine.Tools.IsInstalled;
    [ObservableProperty] private bool _hasPendingUpdate;
    [ObservableProperty] private string _savedText = "";

    private static readonly HashSet<string> NotSettings =
    [
        nameof(ToolsText), nameof(ToolsBusy), nameof(CanUpdateTools), nameof(HasPendingUpdate), nameof(SavedText),
    ];

    public void Load()
    {
        _loading = true;
        var s = _host.Engine.Settings;
        LanguageIndex = s.Language switch { "tr" => 1, "en" => 2, _ => 0 };
        ThemeIndex = s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        Notifications = s.Notifications;
        ShowAddDialog = s.ShowAddDialog;
        ShowProgressWindow = s.ShowProgressWindow;
        ShowCompletedCard = s.ShowCompletedCard;
        DownloadDirectory = s.DownloadDirectory;
        UseCategoryFolders = s.UseCategoryFolders;
        ConflictIndex = s.Conflict == ConflictPolicy.Overwrite ? 1 : 0;
        Connections = s.ConnectionsPerDownload;
        HttpSlots = s.HttpSlots;
        TorrentSlots = s.TorrentSlots;
        (HttpLimitOn, HttpLimitValue, HttpLimitUnit) = Split(s.HttpLimit);
        (TorrentDownOn, TorrentDownValue, TorrentDownUnit) = Split(s.TorrentDownloadLimit);
        (TorrentUpOn, TorrentUpValue, TorrentUpUnit) = Split(s.TorrentUploadLimit);
        SeedRatio = (decimal)s.SeedRatio;
        SeedMinutes = s.SeedMinutes;
        FileAssociations = s.RegisterFileAssociations;
        ScheduleStartOn = s.ScheduleStart is not null;
        ScheduleStart = ParseTime(s.ScheduleStart) ?? new TimeSpan(2, 0, 0);
        ScheduleStopOn = s.ScheduleStop is not null;
        ScheduleStop = ParseTime(s.ScheduleStop) ?? new TimeSpan(8, 0, 0);
        AfterQueueIndex = s.AfterQueue switch { "Sleep" => 1, "Shutdown" => 2, _ => 0 };
        Proxy = s.Proxy ?? "";
        UpdateFeed = s.UpdateFeed ?? "";
        HasPendingUpdate = _host.HasPendingUpdate;
        SavedText = "";
        _loading = false;
    }

    /// Slower, read-only info (registry, tool versions): only when the page is opened.
    public void LoadInfo()
    {
        RefreshBrowsers();
        _ = RefreshToolsAsync();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null || NotSettings.Contains(e.PropertyName)) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync()
    {
        var engine = _host.Engine;
        var old = engine.Settings;
        var s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(old))!;
        s.Language = LanguageIndex switch { 1 => "tr", 2 => "en", _ => "" };
        s.Theme = ThemeIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
        s.Notifications = Notifications;
        s.ShowAddDialog = ShowAddDialog;
        s.ShowProgressWindow = ShowProgressWindow;
        s.ShowCompletedCard = ShowCompletedCard;
        if (!string.IsNullOrWhiteSpace(DownloadDirectory)) s.DownloadDirectory = DownloadDirectory.Trim();
        s.UseCategoryFolders = UseCategoryFolders;
        s.Conflict = ConflictIndex == 1 ? ConflictPolicy.Overwrite : ConflictPolicy.Rename;
        s.ConnectionsPerDownload = (int)Math.Clamp(Connections ?? 8, 1, 32);
        s.HttpSlots = (int)Math.Clamp(HttpSlots ?? 3, 1, 20);
        s.TorrentSlots = (int)Math.Clamp(TorrentSlots ?? 2, 1, 20);
        s.HttpLimit = Join(HttpLimitOn, HttpLimitValue, HttpLimitUnit);
        s.TorrentDownloadLimit = Join(TorrentDownOn, TorrentDownValue, TorrentDownUnit);
        s.TorrentUploadLimit = Join(TorrentUpOn, TorrentUpValue, TorrentUpUnit);
        s.SeedRatio = (double)(SeedRatio ?? 1);
        s.SeedMinutes = (int)(SeedMinutes ?? 60);
        s.RegisterFileAssociations = FileAssociations;
        s.ScheduleStart = ScheduleStartOn && ScheduleStart is { } a ? $"{a.Hours:00}:{a.Minutes:00}" : null;
        s.ScheduleStop = ScheduleStopOn && ScheduleStop is { } b ? $"{b.Hours:00}:{b.Minutes:00}" : null;
        s.AfterQueue = AfterQueueIndex switch { 1 => "Sleep", 2 => "Shutdown", _ => "None" };
        s.Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy.Trim();
        s.UpdateFeed = string.IsNullOrWhiteSpace(UpdateFeed) ? null : UpdateFeed.Trim();

        var uiChanged = s.Language != old.Language || s.Theme != old.Theme;
        var assocChanged = s.RegisterFileAssociations != old.RegisterFileAssociations;
        await engine.SaveSettingsAsync(s);
        if (assocChanged) _host.RegisterIntegration();
        _host.Main.UpdateQuickLimitText();
        SavedText = Strings.SettingsSaved;
        if (uiChanged) _host.ReloadUi();
    }

    private static (bool On, decimal? Value, int Unit) Split(long bytesPerSecond) => bytesPerSecond switch
    {
        <= 0 => (false, 1, 1),
        >= MB => (true, Math.Round((decimal)bytesPerSecond / MB, 1), 1),
        _ => (true, bytesPerSecond / KB, 0),
    };

    private static long Join(bool on, decimal? value, int unit) =>
        on && value is > 0 ? (long)(value.Value * (unit == 1 ? MB : KB)) : 0;

    private static TimeSpan? ParseTime(string? t) =>
        TimeOnly.TryParseExact(t, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v.ToTimeSpan() : null;

    // ================= actions =================

    private void RefreshBrowsers()
    {
        Browsers.Clear();
        foreach (var (b, ok) in ShellIntegration.Status()) Browsers.Add(new BrowserRow(b, ok));
    }

    [RelayCommand]
    private async Task BrowseFolder()
    {
        if (_host.Owner is { } w && await Dialogs.PickFolderAsync(w, DownloadDirectory) is { } path) DownloadDirectory = path;
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        if (Directory.Exists(DownloadDirectory)) Process.Start("explorer.exe", $"\"{DownloadDirectory}\"");
    }

    [RelayCommand]
    private void RepairIntegration()
    {
        _host.RegisterIntegration();
        RefreshBrowsers();
    }

    [RelayCommand]
    private void OpenExtensionFolder()
    {
        if (Directory.Exists(ExtensionFolder)) Process.Start("explorer.exe", $"\"{ExtensionFolder}\"");
    }

    [RelayCommand]
    private Task CopyExtensionId() => Dialogs.SetClipboardTextAsync(_host.Owner, ExtensionId);

    [RelayCommand]
    private void OpenLogs()
    {
        var logs = Path.Combine(Program.DataDirectory, "logs");
        if (Directory.Exists(logs)) Process.Start("explorer.exe", $"\"{logs}\"");
    }

    [RelayCommand]
    private void RestartToUpdate() => _host.RestartToUpdate();

    [RelayCommand]
    private async Task ResetDefaults()
    {
        if (!await Dialogs.ConfirmAsync(_host.Owner, Strings.ResetDefaults, Strings.ResetDefaultsConfirm)) return;
        var old = _host.Engine.Settings;
        var fresh = new AppSettings
        {
            MediaDisclaimerAccepted = old.MediaDisclaimerAccepted,
            LastYtDlpCheck = old.LastYtDlpCheck,
            RecentFolders = old.RecentFolders,
        };
        await _host.Engine.SaveSettingsAsync(fresh);
        Load();
        _host.ReloadUi();
    }

    private async Task RefreshToolsAsync()
    {
        var tools = _host.Engine.Tools;
        OnPropertyChanged(nameof(CanUpdateTools));
        if (!tools.IsInstalled) { ToolsText = Strings.ToolsNotInstalled; return; }
        try { ToolsText = $"yt-dlp {await tools.GetYtDlpVersionAsync(CancellationToken.None)} · deno · ffmpeg"; }
        catch (Exception e) { ToolsText = e.Message; }
    }

    [RelayCommand]
    private async Task UpdateTools()
    {
        if (!_host.Engine.Tools.IsInstalled) return;
        ToolsBusy = true;
        try
        {
            var updated = await _host.Engine.Tools.UpdateYtDlpAsync(CancellationToken.None);
            await RefreshToolsAsync();
            if (!updated) ToolsText += " · " + Strings.UpToDate;
        }
        catch (Exception e) { ToolsText = e.Message; }
        finally { ToolsBusy = false; }
    }

    [RelayCommand]
    private async Task ExportDiagnostics()
    {
        if (_host.Owner is not { } w) return;
        var file = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"fluxer-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            DefaultExtension = "zip",
        });
        if (file?.TryGetLocalPath() is { } path) WriteDiagnostics(_host.Engine.Settings, path);
    }

    /// Logs + settings (proxy credentials stripped) — never the database (cookies) or download URLs.
    private static void WriteDiagnostics(AppSettings settings, string zipPath)
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
        if (copy.Proxy is { } p && Uri.TryCreate(p, UriKind.Absolute, out var u) && u.UserInfo.Length > 0)
            copy.Proxy = p.Replace(u.UserInfo + "@", "***@", StringComparison.Ordinal);
        copy.RecentFolders = [];
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
