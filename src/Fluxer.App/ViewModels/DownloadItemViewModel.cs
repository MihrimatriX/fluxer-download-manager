using CommunityToolkit.Mvvm.ComponentModel;
using Fluxer.Core.Downloads;

namespace Fluxer.App.ViewModels;

/// One row. Refresh() pulls a snapshot from the engine; only properties that changed raise events.
public sealed partial class DownloadItemViewModel(IDownload download) : ObservableObject
{
    public IDownload Download { get; } = download;
    public long Id => Download.Id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _glyph = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _indeterminate;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isSeeding;
    [ObservableProperty] private bool _isActive;

    public DownloadProgress LastProgress { get; private set; }

    public void Refresh()
    {
        var d = Download;
        var p = d.GetProgress();
        LastProgress = p;
        var state = d.State;
        Name = d.FileName.Length > 0 ? d.FileName : d.Record.Http?.Url ?? d.Record.Media?.PageUrl ?? "…";
        Glyph = GlyphFor(d);
        IsActive = state is DownloadState.Downloading or DownloadState.Starting or DownloadState.Seeding or DownloadState.Verifying;
        Indeterminate = IsActive && p.TotalBytes is null or 0;
        Progress = state == DownloadState.Completed ? 100 : p.Fraction * 100;
        SizeText = state == DownloadState.Completed || p.TotalBytes is null
            ? Loc.Bytes(p.TotalBytes ?? p.BytesDone)
            : $"{Loc.Bytes(p.BytesDone)} / {Loc.Bytes(p.TotalBytes)}";
        SpeedText = IsActive ? Loc.Speed(p.BytesPerSecond) : "";
        EtaText = state == DownloadState.Downloading ? Loc.Eta(p.Eta) : "";
        var error = Loc.Error(d.Record.Error);
        StatusText = error.Length > 0 ? $"{Loc.State(state)} · {error}" : state == DownloadState.Downloading && p.TotalBytes > 0
            ? $"{p.Fraction * 100:F1}%"
            : Loc.State(state);
        IsError = state is DownloadState.Failed or DownloadState.DiskFull or DownloadState.NeedsLinkRefresh;
        IsDone = state == DownloadState.Completed;
        IsSeeding = state == DownloadState.Seeding;
    }

    /// Segoe Fluent Icons code points.
    private static string GlyphFor(IDownload d)
    {
        if (d.Kind == DownloadKind.Torrent) return "";       // Download (torrent)
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
