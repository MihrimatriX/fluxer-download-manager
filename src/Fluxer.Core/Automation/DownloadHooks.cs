using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using Fluxer.Core.Downloads;

namespace Fluxer.Core.Automation;

public enum HookEvent { Added, Completed, Failed }

/// What the webhook receives (JSON, camelCase).
public sealed record HookPayload(
    string Event, long Id, string Name, string Path, string Directory, string? Category,
    long? Size, string? Url, string? Error, string Kind, string App, string Version);

/// "When a download finishes": the user's program and/or a webhook. The program gets the download's details
/// as placeholders in its arguments and as FLUXER_* environment variables; it never goes through cmd.exe, so a
/// file name such as "a&b.zip" cannot become a command.
public static class DownloadHooks
{
    public static readonly (string Token, string Meaning)[] Placeholders =
    [
        ("%F", "path"), ("%N", "name"), ("%D", "folder"), ("%K", "category"), ("%Z", "size"), ("%U", "url"), ("%E", "event"), ("%I", "id"),
    ];

    public static string EventName(HookEvent e) => e switch { HookEvent.Added => "added", HookEvent.Failed => "failed", _ => "completed" };

    public static string? SourceUrl(DownloadRecord r) =>
        r.Http?.FinalUrl ?? r.Http?.Url ?? r.Torrent?.Magnet ?? r.Media?.PageUrl;

    /// Splits a command line the way most Windows programs read theirs: spaces separate, double quotes group.
    public static List<string> Tokenize(string commandLine)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) list.Add(cur.ToString());
                cur.Clear();
                any = false;
                continue;
            }
            cur.Append(c);
            any = true;
        }
        if (any) list.Add(cur.ToString());
        return list;
    }

    public static string Expand(string token, DownloadRecord r, HookEvent e) => token
        .Replace("%F", r.FullPath, StringComparison.Ordinal)
        .Replace("%N", r.FileName, StringComparison.Ordinal)
        .Replace("%D", r.SaveDirectory, StringComparison.Ordinal)
        .Replace("%K", r.Category ?? "", StringComparison.Ordinal)
        .Replace("%Z", (r.TotalBytes ?? r.DoneBytes).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("%U", SourceUrl(r) ?? "", StringComparison.Ordinal)
        .Replace("%E", EventName(e), StringComparison.Ordinal)
        .Replace("%I", r.Id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// Characters cmd.exe acts on. A batch file receives its arguments through cmd.exe, so a value carrying
    /// one of these could run a command; such a run is refused (the script can read FLUXER_* instead).
    private static readonly char[] CmdMeta = ['&', '|', '<', '>', '^', '%', '!', '"', '\r', '\n'];

    /// The process to start, or null with the reason it was refused.
    public static ProcessStartInfo? Build(string commandLine, DownloadRecord r, HookEvent e, out string? refused)
    {
        refused = null;
        var tokens = Tokenize(commandLine);
        if (tokens.Count == 0) { refused = "empty"; return null; }
        var exe = Environment.ExpandEnvironmentVariables(tokens[0]);
        var args = tokens.Skip(1).Select(t => Expand(t, r, e)).ToList();
        var ext = Path.GetExtension(exe).ToLowerInvariant();
        if (ext is ".bat" or ".cmd" && args.Any(a => a.IndexOfAny(CmdMeta) >= 0)) { refused = "unsafe-batch-argument"; return null; }

        var psi = ext == ".ps1"
            ? new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", exe } }
            : new ProcessStartInfo(exe);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        if (Directory.Exists(r.SaveDirectory)) psi.WorkingDirectory = r.SaveDirectory;
        var env = psi.Environment;
        env["FLUXER_EVENT"] = EventName(e);
        env["FLUXER_ID"] = r.Id.ToString(CultureInfo.InvariantCulture);
        env["FLUXER_FILE"] = r.FullPath;
        env["FLUXER_NAME"] = r.FileName;
        env["FLUXER_DIR"] = r.SaveDirectory;
        env["FLUXER_CATEGORY"] = r.Category ?? "";
        env["FLUXER_SIZE"] = (r.TotalBytes ?? r.DoneBytes).ToString(CultureInfo.InvariantCulture);
        env["FLUXER_URL"] = SourceUrl(r) ?? "";
        env["FLUXER_ERROR"] = r.Error ?? "";
        return psi;
    }

    public static HookPayload Payload(DownloadRecord r, HookEvent e, string version) => new(
        EventName(e), r.Id, r.FileName, r.FullPath, r.SaveDirectory, r.Category, r.TotalBytes ?? r.DoneBytes,
        SourceUrl(r), r.Error, r.Kind.ToString().ToLowerInvariant(), "Fluxer", version);

    /// Status code and round trip; throws on network errors (the caller logs, or shows it for "Test").
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    public static async Task<(int Status, long Ms)> PostAsync(HttpClient http, string url, HookPayload payload, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var res = await http.PostAsJsonAsync(url, payload, Json, timeout.Token);
        return ((int)res.StatusCode, sw.ElapsedMilliseconds);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static bool IsWebhookUrl(string? s) =>
        Uri.TryCreate(s?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}
