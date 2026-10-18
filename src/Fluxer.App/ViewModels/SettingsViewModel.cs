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

public sealed record AccentSwatch(int Index, string Hex, string Name)
{
    public Avalonia.Media.IBrush Brush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Hex));
    public override string ToString() => Name;
}

public sealed record BrowserRow(string Browser, bool Ok)
{
    public string Glyph => Ok ? "" : "";
    public string Text => Ok ? Strings.IntegrationOk : Strings.IntegrationMissing;
}

public sealed record SettingsSection(string Key, string Label)
{
    public override string ToString() => Label;
}

/// One speed profile card. Speeds in MB/s, 0 = unlimited.
public sealed partial class ProfileCard(int index, Action changed) : ObservableObject
{
    public int Index { get; } = index;
    public string Name => MainViewModel.ProfileName(Index);
    public Avalonia.Media.IBrush Swatch => Index switch
    {
        0 => Theme.Brush("SystemControlHighlightAccentBrush"),
        1 => Theme.Brush("InfoBrush"),
        _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#5B6478")),
    };
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private decimal? _downMb;
    [ObservableProperty] private decimal? _upMb;
    [ObservableProperty] private decimal? _slots;
    public bool Loading { get; set; }

    partial void OnDownMbChanged(decimal? value) { if (!Loading) changed(); }
    partial void OnUpMbChanged(decimal? value) { if (!Loading) changed(); }
    partial void OnSlotsChanged(decimal? value) { if (!Loading) changed(); }
}

/// Settings page. Every change is saved automatically (debounced); there is no Save button.
public sealed partial class SettingsViewModel : ObservableObject
{
    private const long KB = 1024, MB = 1024 * 1024;
    private readonly AppHost _host;
    private readonly DispatcherTimer _saveTimer;
    private bool _loading;
    private int _savedStamp;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, async (_, _) =>
        {
            _saveTimer!.Stop();
            await SaveAsync();
        });
        Profiles = [.. Enumerable.Range(0, 3).Select(i => new ProfileCard(i, QueueSave))];
        Sections =
        [
            new("general", Strings.SecGeneral), new("downloads", Strings.SecDownloads), new("speed", Strings.SecSpeedSchedule),
            new("torrent", Strings.SecTorrent), new("browser", Strings.SecBrowserIntegration), new("advanced", Strings.TabAdvanced),
        ];
        _selectedSection = Sections[0];
    }

    public string[] Languages => [Strings.LangSystem, "Türkçe", "English"];
    public string[] Themes => [Strings.ThemeSystem, Strings.ThemeLight, Strings.ThemeDark];
    public string[] Conflicts => [Strings.ConflictRename, Strings.ConflictOverwrite];
    public string[] AfterQueueOptions => [Strings.AfterNothing, Strings.AfterSleep, Strings.AfterShutdown];
    public string[] Encryptions => [Strings.EncPrefer, Strings.EncRequire, Strings.EncOff];
    public ObservableCollection<BrowserRow> Browsers { get; } = [];
    public string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");
    public string ExtensionId => Protocol.AllowedExtensionIds[0];
    public string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";

    // Navigation
    public IReadOnlyList<SettingsSection> Sections { get; }
    [ObservableProperty] private SettingsSection _selectedSection;
    /// Key of the visible section; MainViewModel sets it to open a page directly ("speed").
    public string Section
    {
        get => SelectedSection.Key;
        set => SelectedSection = Sections.FirstOrDefault(s => s.Key == value) ?? Sections[0];
    }
    partial void OnSelectedSectionChanged(SettingsSection value) => OnPropertyChanged(nameof(Section));

    // Appearance
    public IReadOnlyList<AccentSwatch> Accents { get; } =
        [.. Appearance.AccentChoices.Select((h, i) => new AccentSwatch(i, h, Strings.AccentNames.Split('|').ElementAtOrDefault(i) ?? h))];
    [ObservableProperty] private int _accentIndex;
    [ObservableProperty] private int _backdropIndex;
    public string[] Backdrops => [Strings.BackdropNone, Strings.BackdropGlass, Strings.BackdropMica, Strings.BackdropNeu];
    [ObservableProperty] private bool _compact;
    [ObservableProperty] private double _scalePercent = 100;
    [ObservableProperty] private bool _animations;
    [ObservableProperty] private bool _toolbarLabelsOn;
    [ObservableProperty] private bool _sidebarCollapsedOn;
    public string ScaleText => Loc.Percent(ScalePercent / 100);
    partial void OnScalePercentChanged(double value) => OnPropertyChanged(nameof(ScaleText));

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
    // Speed and schedule
    public IReadOnlyList<ProfileCard> Profiles { get; }
    [ObservableProperty] private int _brushIndex = 2;
    public string BrushName => MainViewModel.ProfileName(BrushIndex);
    partial void OnBrushIndexChanged(int value) => OnPropertyChanged(nameof(BrushName));
    [ObservableProperty] private bool _weekScheduleOn;
    [ObservableProperty] private string _weekSchedule = "";
    [ObservableProperty] private bool _scheduleStartOn;
    [ObservableProperty] private TimeSpan? _scheduleStart;
    [ObservableProperty] private bool _scheduleStopOn;
    [ObservableProperty] private TimeSpan? _scheduleStop;
    [ObservableProperty] private int _afterQueueIndex;
    // Torrent
    [ObservableProperty] private decimal? _listenPort;
    [ObservableProperty] private bool _dht;
    [ObservableProperty] private bool _peerExchange;
    [ObservableProperty] private bool _localPeerDiscovery;
    [ObservableProperty] private bool _portForwarding;
    [ObservableProperty] private int _encryptionIndex;
    [ObservableProperty] private decimal? _seedRatio;
    [ObservableProperty] private decimal? _seedMinutes;
    [ObservableProperty] private decimal? _torrentSlots;
    [ObservableProperty] private decimal? _maxPeers;
    [ObservableProperty] private bool _fileAssociations;
    // Advanced
    [ObservableProperty] private string _proxy = "";
    [ObservableProperty] private string _updateFeed = "";
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private bool _betaUpdates;
    [ObservableProperty] private bool _scanDownloads;
    [ObservableProperty] private bool _watchClipboard;
    [ObservableProperty] private bool _watchFolderOn;
    [ObservableProperty] private string _watchFolder = "";
    // Read-only info
    [ObservableProperty] private string _toolsText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUpdateTools))] private bool _toolsBusy;
    public bool CanUpdateTools => !ToolsBusy && _host.Engine.Tools.IsInstalled;
    [ObservableProperty] private bool _hasPendingUpdate;
    [ObservableProperty] private string _savedText = "";

    private static readonly HashSet<string> NotSettings =
    [
        nameof(ScaleText), nameof(SelectedSection), nameof(Section), nameof(BrushIndex), nameof(BrushName),
        nameof(ToolsText), nameof(ToolsBusy), nameof(CanUpdateTools), nameof(HasPendingUpdate), nameof(SavedText),
    ];

    public void Load()
    {
        _loading = true;
        var s = _host.Engine.Settings;
        LanguageIndex = s.Language switch { "tr" => 1, "en" => 2, _ => 0 };
        AccentIndex = Math.Max(0, Array.IndexOf(Appearance.AccentChoices, s.Ui.Accent ?? Appearance.DefaultAccent));
        BackdropIndex = s.Ui.Backdrop switch { "glass" => 1, "mica" => 2, "neu" => 3, _ => 0 };
        Compact = s.Ui.Compact;
        ScalePercent = Math.Round(s.Ui.Scale * 100);
        Animations = s.Ui.Animations;
        ToolbarLabelsOn = s.Ui.ToolbarLabels;
        SidebarCollapsedOn = s.Ui.SidebarCollapsed;
        ThemeIndex = s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        Notifications = s.Notifications;
        ShowAddDialog = s.ShowAddDialog;
        ShowProgressWindow = s.ShowProgressWindow;
        ShowCompletedCard = s.ShowCompletedCard;
        DownloadDirectory = s.DownloadDirectory;
        UseCategoryFolders = s.UseCategoryFolders;
        ConflictIndex = s.Conflict == ConflictPolicy.Overwrite ? 1 : 0;
        Connections = s.ConnectionsPerDownload;
        LoadProfiles(s);
        WeekScheduleOn = s.WeekScheduleOn;
        WeekSchedule = s.WeekSchedule.Length == 168 ? s.WeekSchedule : AppSettings.DefaultWeek();
        ScheduleStartOn = s.ScheduleStart is not null;
        ScheduleStart = ParseTime(s.ScheduleStart) ?? new TimeSpan(1, 0, 0);
        ScheduleStopOn = s.ScheduleStop is not null;
        ScheduleStop = ParseTime(s.ScheduleStop) ?? new TimeSpan(8, 0, 0);
        AfterQueueIndex = s.AfterQueue switch { "Sleep" => 1, "Shutdown" => 2, _ => 0 };
        ListenPort = s.ListenPort;
        Dht = s.Dht;
        PeerExchange = s.PeerExchange;
        LocalPeerDiscovery = s.LocalPeerDiscovery;
        PortForwarding = s.PortForwarding;
        EncryptionIndex = s.Encryption switch { "require" => 1, "off" => 2, _ => 0 };
        SeedRatio = (decimal)s.SeedRatio;
        SeedMinutes = s.SeedMinutes;
        TorrentSlots = s.TorrentSlots;
        MaxPeers = s.MaxPeersPerTorrent;
        FileAssociations = s.RegisterFileAssociations;
        Proxy = s.Proxy ?? "";
        UpdateFeed = s.UpdateFeed ?? "";
        AutoUpdate = s.AutoUpdate;
        BetaUpdates = s.BetaUpdates;
        ScanDownloads = s.ScanDownloads;
        WatchClipboard = s.WatchClipboard;
        WatchFolderOn = s.WatchFolder is not null;
        WatchFolder = s.WatchFolder ?? "";
        HasPendingUpdate = _host.HasPendingUpdate;
        SavedText = "";
        _loading = false;
    }

    private void LoadProfiles(AppSettings s)
    {
        foreach (var card in Profiles)
        {
            var p = s.Profiles.ElementAtOrDefault(card.Index) ?? new SpeedProfile();
            card.Loading = true;
            card.DownMb = Math.Round((decimal)p.Down / MB, 2);
            card.UpMb = Math.Round((decimal)p.Up / MB, 2);
            card.Slots = p.Slots;
            card.IsActive = card.Index == s.ActiveProfile;
            card.Loading = false;
        }
    }

    /// The host switched profiles (status bar, schedule): only the highlight moves.
    public void ProfileApplied()
    {
        foreach (var card in Profiles) card.IsActive = card.Index == _host.Engine.Settings.ActiveProfile;
    }

    [RelayCommand]
    private Task PickProfile(ProfileCard card) => _host.UseProfileAsync(card.Index);

    [RelayCommand]
    private void PickBrush(string index) => BrushIndex = int.Parse(index, CultureInfo.InvariantCulture);

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
        QueueSave();
    }

    private void QueueSave()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync()
    {
        var engine = _host.Engine;
        var old = engine.Settings;
        var s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(old))!;
        s.Language = LanguageIndex switch { 1 => "tr", 2 => "en", _ => "" };
        s.Ui.Accent = AccentIndex > 0 ? Appearance.AccentChoices[AccentIndex] : null;
        s.Ui.Backdrop = BackdropIndex switch { 1 => "glass", 2 => "mica", 3 => "neu", _ => "none" };
        s.Ui.Compact = Compact;
        s.Ui.Scale = Math.Clamp(ScalePercent / 100, 0.85, 1.3);
        s.Ui.Animations = Animations;
        s.Ui.ToolbarLabels = ToolbarLabelsOn;
        s.Ui.SidebarCollapsed = SidebarCollapsedOn;
        s.Theme = ThemeIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
        s.Notifications = Notifications;
        s.ShowAddDialog = ShowAddDialog;
        s.ShowProgressWindow = ShowProgressWindow;
        s.ShowCompletedCard = ShowCompletedCard;
        if (!string.IsNullOrWhiteSpace(DownloadDirectory)) s.DownloadDirectory = DownloadDirectory.Trim();
        s.UseCategoryFolders = UseCategoryFolders;
        s.Conflict = ConflictIndex == 1 ? ConflictPolicy.Overwrite : ConflictPolicy.Rename;
        s.ConnectionsPerDownload = (int)Math.Clamp(Connections ?? 8, 1, 32);
        s.Profiles = [.. Profiles.Select(c => new SpeedProfile
        {
            Down = (long)((c.DownMb ?? 0) * MB),
            Up = (long)((c.UpMb ?? 0) * MB),
            Slots = (int)Math.Clamp(c.Slots ?? 3, 1, 20),
        })];
        s.UseProfile(s.ActiveProfile); // edited values of the active profile apply at once
        s.WeekScheduleOn = WeekScheduleOn;
        s.WeekSchedule = WeekSchedule;
        s.ScheduleStart = ScheduleStartOn && ScheduleStart is { } a ? $"{a.Hours:00}:{a.Minutes:00}" : null;
        s.ScheduleStop = ScheduleStopOn && ScheduleStop is { } b ? $"{b.Hours:00}:{b.Minutes:00}" : null;
        s.AfterQueue = AfterQueueIndex switch { 1 => "Sleep", 2 => "Shutdown", _ => "None" };
        s.ListenPort = (int)Math.Clamp(ListenPort ?? 51413, 1024, 65535);
        s.Dht = Dht;
        s.PeerExchange = PeerExchange;
        s.LocalPeerDiscovery = LocalPeerDiscovery;
        s.PortForwarding = PortForwarding;
        s.Encryption = EncryptionIndex switch { 1 => "require", 2 => "off", _ => "prefer" };
        s.SeedRatio = (double)(SeedRatio ?? 1);
        s.SeedMinutes = (int)(SeedMinutes ?? 60);
        s.TorrentSlots = (int)Math.Clamp(TorrentSlots ?? 2, 1, 20);
        s.MaxPeersPerTorrent = (int)Math.Clamp(MaxPeers ?? 100, 5, 1000);
        s.RegisterFileAssociations = FileAssociations;
        s.Proxy = string.IsNullOrWhiteSpace(Proxy) ? null : Proxy.Trim();
        s.UpdateFeed = string.IsNullOrWhiteSpace(UpdateFeed) ? null : UpdateFeed.Trim();
        s.AutoUpdate = AutoUpdate;
        s.BetaUpdates = BetaUpdates;
        s.ScanDownloads = ScanDownloads;
        s.WatchClipboard = WatchClipboard;
        s.WatchFolder = WatchFolderOn && !string.IsNullOrWhiteSpace(WatchFolder) ? WatchFolder.Trim() : null;
        var updatesChanged = s.AutoUpdate != old.AutoUpdate || s.BetaUpdates != old.BetaUpdates || s.UpdateFeed != old.UpdateFeed;
        var watchChanged = s.WatchFolder != old.WatchFolder || s.WatchClipboard != old.WatchClipboard;

        var uiChanged = s.Language != old.Language || s.Theme != old.Theme;
        var assocChanged = s.RegisterFileAssociations != old.RegisterFileAssociations;
        await engine.SaveSettingsAsync(s);
        if (assocChanged) _host.RegisterIntegration();
        if (watchChanged) _host.ApplyWatchers();
        if (updatesChanged) _host.CheckForAppUpdate();
        if (Avalonia.Application.Current is { } app) Appearance.Apply(app, s.Ui);
        _host.Main.LoadUi();
        _host.Main.UpdateTotals();
        SavedText = Strings.SettingsSaved;
        // The confirmation fades after a moment instead of staying forever.
        var stamp = ++_savedStamp;
        Avalonia.Threading.DispatcherTimer.RunOnce(() => { if (stamp == _savedStamp) SavedText = ""; }, TimeSpan.FromSeconds(2.5));
        if (uiChanged) _host.ReloadUi();
    }

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
    private async Task BrowseWatchFolder()
    {
        if (_host.Owner is { } w && await Dialogs.PickFolderAsync(w, WatchFolder.Length > 0 ? WatchFolder : DownloadDirectory) is { } path)
        {
            WatchFolder = path;
            WatchFolderOn = true;
        }
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
            Ui = new UiSettings { DesignRevision = 1 },
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
            var updated = await _host.Engine.Tools.UpdateAllAsync(CancellationToken.None);
            await RefreshToolsAsync();
            if (updated.Count == 0) ToolsText += " · " + Strings.UpToDate;
        }
        catch (Exception e) { ToolsText = Loc.Error(e.Message); }
        finally { ToolsBusy = false; }
    }

    [RelayCommand]
    private Task ExportDiagnostics() => ExportDiagnosticsAsync();

    /// Also offered by the "Fluxer closed unexpectedly" prompt on the next start.
    public async Task ExportDiagnosticsAsync()
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
