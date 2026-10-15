using System.Diagnostics;
using System.Text.Json;

namespace Fluxer.Core.Media;

public sealed record MediaEntry(string Url, string Title, double? Duration);

public sealed record MediaProbe(string Title, bool IsPlaylist, IReadOnlyList<MediaEntry> Entries, IReadOnlyList<int> Heights);

/// Metadata extraction (`yt-dlp -J`). Downloading itself is MediaDownload.
public static class YtDlp
{
    public static async Task<MediaProbe> ProbeAsync(ToolManager tools, string url, string? cookieFile, CancellationToken ct, string? referrer = null)
    {
        var psi = BaseStartInfo(tools, cookieFile, referrer);
        foreach (var a in new[] { "-J", "--flat-playlist", "--no-warnings", url }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        Platform.Windows.ChildProcessJob.Assign(p);
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { p.Kill(entireProcessTree: true); throw; }
        if (p.ExitCode != 0) throw new MediaException(MediaErrors.Classify(await stderr), await stderr);

        using var doc = JsonDocument.Parse(await stdout);
        var root = doc.RootElement;
        var title = Str(root, "title") ?? url;

        if (Str(root, "_type") == "playlist" && root.TryGetProperty("entries", out var entries))
        {
            var list = new List<MediaEntry>();
            foreach (var e in entries.EnumerateArray())
            {
                var u = Str(e, "url") ?? Str(e, "webpage_url");
                if (u is null) continue;
                list.Add(new MediaEntry(u, Str(e, "title") ?? u, e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null));
            }
            return new MediaProbe(title, true, list, []);
        }

        var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        if (root.TryGetProperty("formats", out var formats))
            foreach (var f in formats.EnumerateArray())
                if (f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && Str(f, "vcodec") is { } vc && vc != "none")
                    heights.Add(h.GetInt32());

        var duration = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetDouble() : (double?)null;
        return new MediaProbe(title, false, [new MediaEntry(Str(root, "webpage_url") ?? url, title, duration)], [.. heights]);
    }

    /// yt-dlp -f expression for a max height (null = best) or audio only.
    public static string FormatFor(int? maxHeight, bool audioOnly) =>
        audioOnly ? "ba/b"
        : maxHeight is { } h ? $"bv*[height<={h}]+ba/b[height<={h}]"
        : "bv*+ba/b";

    internal static ProcessStartInfo BaseStartInfo(ToolManager tools, string? cookieFile, string? referrer = null)
    {
        var psi = new ProcessStartInfo(tools.YtDlpPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = tools.Directory,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in new[] { "--ignore-config", "--no-colors", "--encoding", "utf-8", "--ffmpeg-location", tools.Directory })
            psi.ArgumentList.Add(a);
        if (cookieFile is not null) { psi.ArgumentList.Add("--cookies"); psi.ArgumentList.Add(cookieFile); }
        // Stream URLs captured in the browser usually only answer with the page they play on as Referer.
        if (referrer is not null) { psi.ArgumentList.Add("--referer"); psi.ArgumentList.Add(referrer); }
        return psi;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public static class MediaErrors
{
    /// Maps yt-dlp stderr to an error code the UI can translate.
    public static string Classify(string stderr) =>
        stderr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ? "media-signin"
        : stderr.Contains("DRM", StringComparison.Ordinal) ? "media-drm"
        : stderr.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase) ? "media-unsupported"
        : stderr.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase) ? "media-format"
        : stderr.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase) ? "media-forbidden"
        : stderr.Contains("Private video", StringComparison.OrdinalIgnoreCase) ? "media-private"
        : "media-failed";
}

public sealed class MediaException(string code, string details) : Exception(code)
{
    public string Code { get; } = code;
    public string Details { get; } = details;
}
