using System.Diagnostics;
using System.Globalization;
using Fluxer.Core.Automation;
using Fluxer.Core.Downloads;
using Fluxer.Core.Platform.Windows;
using Serilog;

namespace Fluxer.App.Services;

/// What happens around a download event: the notification and sound the user picked, then (after the antivirus
/// hand-off) their program and webhook. Everything after the notification runs off the UI thread.
public sealed partial class AppHost
{
    private string AppVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// Notification (behind the master switch) and sound for one event.
    private void Feedback(EventFeedback f, string evt, string title, string body)
    {
        if (Engine.Settings.Notifications && f.Notify) Notifier.Show(title, body);
        if (f.Sound) Sounds.Play(f.SoundFile, evt);
    }

    private void OnFinished(IDownload d)
    {
        var record = d.Record;
        _ = Task.Run(async () =>
        {
            ScanCompleted(d);
            // Before the hooks, so a program run on finish already sees the unpacked files.
            if (Engine.Settings.Extract.Auto && d.Kind == DownloadKind.Http) await ExtractAsync(d, manual: false);
            Plugins.Raise("completed", record);
            await RunHooksAsync(record, HookEvent.Completed);
        });
    }

    /// One archive at a time: several at once only make the disk seek.
    private readonly SemaphoreSlim _extracting = new(1, 1);

    /// Unpacks a finished archive (auto after download, or "Extract" from the list). For a set split into parts,
    /// the last part to finish does it.
    public async Task ExtractAsync(IDownload d, bool manual)
    {
        var path = d.Record.FullPath;
        if (Extractor.KeyOf(d.Record.FileName) is not { } key) return;
        var dir = Path.GetDirectoryName(path);
        var pending = Engine.Queue.Items.Any(o => o.Id != d.Id && o.State != DownloadState.Completed
            && string.Equals(Extractor.KeyOf(o.Record.FileName), key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(o.Record.SaveDirectory.TrimEnd('\\', '/'), dir?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (pending && !manual) return;
        var name = Extractor.ArchiveName(new Extractor.ArchiveSet(key, [], false));
        void Tell(string title, string format, params object?[] args)
        {
            if (Engine.Settings.Notifications || manual) Notifier.Show(title, string.Format(CultureInfo.CurrentCulture, format, args));
        }
        if (Extractor.Describe(path) is not { Complete: true } set)
        {
            if (manual) Tell(Strings.ExtractFailedTitle, Strings.ExtractMissingParts, name);
            return;
        }
        await _extracting.WaitAsync();
        try
        {
            Log.Information("Extracting {Archive} ({Parts} part(s))", key, set.Parts.Count);
            var r = await Task.Run(() => Extractor.Extract(set, Engine.Settings.Extract.Passwords, CancellationToken.None));
            switch (r.Outcome)
            {
                case Extractor.Outcome.Done:
                    Log.Information("Extracted {Archive}: {Files} file(s) → {Folder}", key, r.Files, r.Folder);
                    if (Engine.Settings.Extract.DeleteAfter)
                        foreach (var part in set.Parts)
                            try { File.Delete(part); }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning("Could not delete {Part}: {Error}", part, e.Message); }
                    Tell(Strings.ExtractDoneTitle, Strings.ExtractDoneBody, name, r.Files);
                    break;
                case Extractor.Outcome.NeedsPassword:
                    Log.Information("Extract {Archive}: no password fits", key);
                    Tell(Strings.ExtractFailedTitle, Strings.ExtractNeedsPassword, name);
                    break;
                default:
                    Log.Warning("Extract {Archive} failed: {Error}", key, r.Error);
                    Tell(Strings.ExtractFailedTitle, Strings.ExtractFailedBody, name, r.Error);
                    break;
            }
        }
        catch (Exception e) { Log.Warning(e, "Extract {Archive} failed", key); }
        finally { _extracting.Release(); }
    }

    private void OnFailedEvent(IDownload d)
    {
        Plugins.Raise("failed", d.Record);
        _ = Task.Run(() => RunHooksAsync(d.Record, HookEvent.Failed));
    }

    public void OnAddedEvent(IDownload d)
    {
        Plugins.Raise("added", d.Record);
        if (Engine.Settings.Automation.WebhookOnAdded) _ = Task.Run(() => RunHooksAsync(d.Record, HookEvent.Added));
    }

    private async Task RunHooksAsync(DownloadRecord r, HookEvent e)
    {
        var a = Engine.Settings.Automation;
        if (e != HookEvent.Added && a.Program is { Length: > 0 } cmd && (e == HookEvent.Completed || a.ProgramOnFailed))
        {
            try
            {
                if (DownloadHooks.Build(cmd, r, e, out var refused) is { } psi) Process.Start(psi)?.Dispose();
                else Log.Warning("Program after download {Id} not started: {Reason}", r.Id, refused);
            }
            catch (Exception ex) { Log.Warning(ex, "Program after download {Id} failed", r.Id); }
        }
        var want = e switch { HookEvent.Added => a.WebhookOnAdded, HookEvent.Failed => a.WebhookOnFailed, _ => a.WebhookOnCompleted };
        if (want && DownloadHooks.IsWebhookUrl(a.WebhookUrl))
        {
            try
            {
                var (status, ms) = await DownloadHooks.PostAsync(Engine.Http.Client, a.WebhookUrl!.Trim(), DownloadHooks.Payload(r, e, AppVersion), CancellationToken.None);
                Log.Information("Webhook {Event} {Id}: HTTP {Status} in {Ms} ms", DownloadHooks.EventName(e), r.Id, status, ms);
            }
            catch (Exception ex) { Log.Warning("Webhook {Event} {Id} failed: {Error}", DownloadHooks.EventName(e), r.Id, ex.Message); }
        }
    }

    /// Settings → Automation → "Test": a made-up finished download, the answer shown next to the button.
    public async Task<string> TestWebhookAsync(string url)
    {
        if (!DownloadHooks.IsWebhookUrl(url)) return Strings.WebhookInvalid;
        var sample = new DownloadRecord
        {
            Id = 0, FileName = "fluxer-test.zip", SaveDirectory = Engine.Settings.DownloadDirectory, Category = "Archives",
            TotalBytes = 1_048_576, State = DownloadState.Completed, Http = new HttpInfo { Url = "https://example.org/fluxer-test.zip" },
        };
        try
        {
            var (status, ms) = await DownloadHooks.PostAsync(Engine.Http.Client, url.Trim(), DownloadHooks.Payload(sample, HookEvent.Completed, AppVersion), CancellationToken.None);
            return string.Format(CultureInfo.CurrentCulture, Strings.WebhookResult, status, ms);
        }
        catch (Exception e) { return Loc.Error(e.Message); }
    }
}
