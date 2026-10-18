using Avalonia.Threading;
using Fluxer.Core.Downloads;
using Fluxer.Core.Platform.Windows;
using Serilog;

namespace Fluxer.App.Services;

/// Background intake: copied download links (clipboard monitoring), a watched folder for .torrent
/// files, and the antivirus hand-off after a download finishes.
public sealed partial class AppHost
{
    private uint _clipboardSeq;
    private bool _clipboardBusy;
    private readonly HashSet<string> _offeredLinks = new(StringComparer.Ordinal);
    private FileSystemWatcher? _folderWatcher;

    /// Settings changed or app start: (re)arm the clipboard baseline and the torrent folder watcher.
    public void ApplyWatchers()
    {
        // Whatever is on the clipboard when monitoring starts was copied before; only offer new copies.
        _clipboardSeq = ClipboardSequence.Current;

        _folderWatcher?.Dispose();
        _folderWatcher = null;
        if (Engine.Settings.WatchFolder is not { } dir) return;
        try
        {
            Directory.CreateDirectory(dir);
            _folderWatcher = new FileSystemWatcher(dir, "*.torrent") { IncludeSubdirectories = false };
            _folderWatcher.Created += (_, e) => Dispatcher.UIThread.Post(() => _ = AddWatchedTorrentAsync(e.FullPath));
            _folderWatcher.Renamed += (_, e) => Dispatcher.UIThread.Post(() => _ = AddWatchedTorrentAsync(e.FullPath));
            _folderWatcher.EnableRaisingEvents = true;
            foreach (var f in Directory.EnumerateFiles(dir, "*.torrent")) _ = AddWatchedTorrentAsync(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warning(e, "Watch folder {Dir} unavailable", dir);
        }
    }

    /// Adds a dropped .torrent without the add dialog (nobody is there to answer it) and moves it to
    /// "added" so it is not picked up again on the next start.
    private async Task AddWatchedTorrentAsync(string path)
    {
        if (!path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) return;
        byte[]? bytes = null;
        // The file is often still being written when the watcher fires.
        for (var i = 0; i < 10 && bytes is null; i++)
        {
            try { bytes = await File.ReadAllBytesAsync(path); }
            catch (IOException) { await Task.Delay(500); }
            catch (UnauthorizedAccessException) { return; }
        }
        if (bytes is null) return;
        try
        {
            var d = Engine.AddTorrent(bytes, null);
            Log.Information("Watch folder: {File} {Result}", Path.GetFileName(path), d is null ? "already in the list" : "added");
            var done = Path.Combine(Path.GetDirectoryName(path)!, "added");
            Directory.CreateDirectory(done);
            File.Move(path, Path.Combine(done, Path.GetFileName(path)), overwrite: true);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Watch folder: {File} is not a valid torrent", Path.GetFileName(path));
        }
    }

    /// Called from the 2 s tick. Reading the clipboard only happens after the sequence number moved.
    private void PollClipboard()
    {
        if (!Engine.Settings.WatchClipboard || _clipboardBusy) return;
        var seq = ClipboardSequence.Current;
        if (seq == _clipboardSeq) return;
        _clipboardSeq = seq;
        _ = OfferClipboardLinksAsync();
    }

    private async Task OfferClipboardLinksAsync()
    {
        _clipboardBusy = true;
        try
        {
            var text = await Task.Run(ClipboardSequence.ReadText);
            var known = Engine.Queue.Items.Select(d => d.Record.Http?.Url ?? d.Record.Torrent?.Magnet).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var links = LinkSniffer.DownloadLinks(text, Engine.Settings).Where(l => !known.Contains(l) && _offeredLinks.Add(l)).ToList();
            if (links.Count == 0) return;
            Log.Information("Clipboard: {Count} download link(s)", links.Count);
            await Flows.AddTextAsync(this, string.Join('\n', links));
        }
        catch (Exception e) { Log.Warning(e, "Clipboard monitoring failed"); }
        finally
        {
            _clipboardSeq = ClipboardSequence.Current; // ignore copies made while the dialog was open
            _clipboardBusy = false;
        }
    }

    /// Finished HTTP and video downloads go through Attachment Services so the antivirus sees them.
    private void ScanCompleted(IDownload d)
    {
        if (!Engine.Settings.ScanDownloads || d.Kind == DownloadKind.Torrent) return;
        var path = d.Record.FullPath;
        var source = d.Record.Http?.FinalUrl ?? d.Record.Http?.Url ?? d.Record.Media?.PageUrl;
        var referrer = d.Record.Http?.Referrer ?? d.Record.Media?.Referrer;
        _ = Task.Run(() =>
        {
            var result = AttachmentScanner.Scan(path, source, referrer);
            Log.Information("Scan {Id}: {Result}", d.Id, result);
            if (result == ScanResult.Blocked)
                Notifier.Show(Strings.ScanBlockedTitle, string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.ScanBlockedBody, d.FileName));
        });
    }
}
