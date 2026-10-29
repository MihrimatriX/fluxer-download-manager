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
            await RunHooksAsync(record, HookEvent.Completed);
        });
    }

    private void OnFailedEvent(IDownload d) => _ = Task.Run(() => RunHooksAsync(d.Record, HookEvent.Failed));

    public void OnAddedEvent(IDownload d)
    {
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
