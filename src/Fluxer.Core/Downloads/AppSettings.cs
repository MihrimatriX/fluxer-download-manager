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
