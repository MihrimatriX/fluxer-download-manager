using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Fluxer.Core.Downloads;

namespace Fluxer.App.ViewModels;

/// One row. Refresh() pulls a snapshot from the engine; only properties that changed raise events.
/// Text properties are for display; the raw ones (SizeBytes, SpeedValue, …) are the sort keys.
public sealed partial class DownloadItemViewModel(IDownload download, Func<IDownload, int?> queuePosition) : ObservableObject
{
    public IDownload Download { get; } = download;
    public long Id => Download.Id;
    public DateTime AddedAt { get; } = DateTimeOffset.FromUnixTimeMilliseconds(download.Record.CreatedAt).LocalDateTime;
    /// Today: time only; otherwise a short date.
    public string AddedText => AddedAt.Date == DateTime.Today
        ? AddedAt.ToString("t", CultureInfo.CurrentCulture)
        : AddedAt.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);

    /// What a screen reader announces for the row.
    public string AccessibleText => string.Join(", ", new[] { Name, StatusText, SizeText, SpeedText, EtaText }.Where(s => s.Length > 0));

    public override string ToString() => Name;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _glyph = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _indeterminate;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private long _speedValue;
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private double _etaSeconds = double.MaxValue;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _stateOrder;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isSeeding;
    /// Row action: true → the button pauses, false → it resumes.
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private string _toggleGlyph = "";

    public bool IsRunningState => Download.State is DownloadState.Starting or DownloadState.Downloading
        or DownloadState.Seeding or DownloadState.Verifying;

    public void Refresh()
    {
        var d = Download;
        var p = d.GetProgress();
        var state = d.State;
        Name = d.FileName.Length > 0 ? d.FileName : d.Record.Http?.Url ?? d.Record.Media?.PageUrl ?? "…";
        Glyph = GlyphFor(d);
        IsActive = IsRunningState;
        Indeterminate = IsActive && p.TotalBytes is null or 0;
        Progress = state == DownloadState.Completed ? 100 : p.Fraction * 100;
        SizeBytes = p.TotalBytes ?? p.BytesDone;
        SizeText = state == DownloadState.Completed || p.TotalBytes is null
            ? Loc.Bytes(p.TotalBytes ?? p.BytesDone)
            : $"{Loc.Bytes(p.BytesDone)} / {Loc.Bytes(p.TotalBytes)}";
        SpeedValue = IsActive ? p.BytesPerSecond : 0;
        SpeedText = IsActive ? Loc.Speed(p.BytesPerSecond) : "";
        EtaSeconds = state == DownloadState.Downloading && p.Eta is { } eta ? eta.TotalSeconds : double.MaxValue;
        EtaText = state == DownloadState.Downloading ? Loc.Eta(p.Eta) : "";
        StatusText = Status(d, p);
        OnPropertyChanged(nameof(AccessibleText));
        StateOrder = state switch
        {
            DownloadState.Downloading or DownloadState.Starting or DownloadState.Verifying => 0,
            DownloadState.Seeding => 1,
            DownloadState.Queued => 2,
            DownloadState.Paused => 3,
            DownloadState.Failed or DownloadState.DiskFull or DownloadState.NeedsLinkRefresh => 4,
            _ => 5,
        };
        IsError = state is DownloadState.Failed or DownloadState.DiskFull or DownloadState.NeedsLinkRefresh;
        IsDone = state == DownloadState.Completed;
        IsSeeding = state == DownloadState.Seeding;
        CanPause = IsActive || state == DownloadState.Queued;
        ToggleGlyph = CanPause ? "" : state == DownloadState.Completed ? "" : ""; // pause / folder / play
    }

    private string Status(IDownload d, DownloadProgress p)
    {
        var state = d.State;
        var error = Loc.Error(d.Record.Error);
        if (error.Length > 0) return $"{Loc.State(state)} · {error}";
        if (state == DownloadState.Queued && queuePosition(d) is { } pos)
            return string.Format(CultureInfo.CurrentCulture, Strings.QueuedAt, pos);
        if (state == DownloadState.Downloading && p.TotalBytes > 0)
            return $"{p.Fraction * 100:F1}%" + (p.Connections > 1 ? $" · {p.Connections} {Strings.ConnectionsShort}" : "");
        return Loc.State(state);
    }

    /// Segoe Fluent Icons code points.
    private static string GlyphFor(IDownload d)
    {
        if (d.Kind == DownloadKind.Torrent) return "";
        return (d.Record.Category ?? "") switch
        {
            "Video" => "",
            "Music" => "",
            "Documents" => "",
            "Archives" => "",
            "Programs" => "",
            _ => "",
        };
    }
}
