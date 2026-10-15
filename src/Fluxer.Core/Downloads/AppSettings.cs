namespace Fluxer.Core.Downloads;

public enum ConflictPolicy { Rename, Overwrite, Ask }

public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int HttpSlots { get; set; } = 3;
    public int TorrentSlots { get; set; } = 2;
    public int ConnectionsPerDownload { get; set; } = 8;
    public long HttpLimit { get; set; }            // B/s, 0 = unlimited
    public long TorrentDownloadLimit { get; set; }
    public long TorrentUploadLimit { get; set; }
    public double SeedRatio { get; set; } = 1.0;
    public int SeedMinutes { get; set; } = 60;
    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Rename;
    public string? Proxy { get; set; }             // "http://host:port", null = system
    public string Language { get; set; } = "";     // "" = system, "tr", "en"
    public string Theme { get; set; } = "System";  // System | Light | Dark
    public bool UseCategoryFolders { get; set; } = true;
    public string? ScheduleStart { get; set; }     // "HH:mm" or null
    public string? ScheduleStop { get; set; }
    public string AfterQueue { get; set; } = "None"; // None | Sleep | Shutdown
    public bool ShowAddDialog { get; set; } = true;
    public UiSettings Ui { get; set; } = new();
    public bool ShowProgressWindow { get; set; } = true;
    public bool ShowCompletedCard { get; set; } = true;
    public List<string> RecentFolders { get; set; } = [];
    public bool Notifications { get; set; } = true;
    public bool RegisterFileAssociations { get; set; } = true;
    public string? UpdateFeed { get; set; }        // Velopack feed URL or folder; null = no auto-update
    public bool MediaDisclaimerAccepted { get; set; }
    public bool ToolsConsentGiven { get; set; }
    public long LastYtDlpCheck { get; set; }

    public Dictionary<string, string[]> Categories { get; set; } = new()
    {
        ["Video"] = ["mp4", "mkv", "avi", "mov", "webm", "wmv", "flv", "m4v"],
        ["Music"] = ["mp3", "flac", "wav", "aac", "ogg", "m4a", "opus", "wma"],
        ["Documents"] = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "epub", "odt"],
        ["Archives"] = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso"],
        ["Programs"] = ["exe", "msi", "msix", "appx", "bat", "apk"],
    };

    public string? CategoryFor(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        foreach (var (cat, exts) in Categories)
            if (exts.Contains(ext)) return cat;
        return null;
    }
}

/// Look and layout the user can change; remembered between sessions.
public sealed class UiSettings
{
    public string? Accent { get; set; }                 // "#RRGGBB", null = Windows accent
    public string Backdrop { get; set; } = "glass";     // "glass" (acrylic blur), "mica", "none"
    public bool Compact { get; set; }                   // denser rows and controls
    public double Scale { get; set; } = 1.0;            // 0.85 – 1.3
    public bool Animations { get; set; } = true;
    public bool ToolbarLabels { get; set; } = true;
    public bool SidebarCollapsed { get; set; }
    public bool DetailVisible { get; set; } = true;
    public double DetailHeight { get; set; } = 290;
    public List<string> HiddenColumns { get; set; } = [];
    public Dictionary<string, double> ColumnWidths { get; set; } = [];
    public List<string> ColumnOrder { get; set; } = [];
    public double[]? WindowBounds { get; set; }         // x, y, width, height
    public bool WindowMaximized { get; set; }
}
