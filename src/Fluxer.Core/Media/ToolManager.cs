using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fluxer.Core.Http;
using Serilog;

namespace Fluxer.Core.Media;

/// Installs/updates yt-dlp, deno (JS runtime yt-dlp needs for YouTube) and ffmpeg from their
/// official GitHub releases into %LOCALAPPDATA%\Fluxer\tools, verifying the publisher's SHA-256.
/// yt-dlp.exe finds deno.exe automatically when it sits in the same folder.
public sealed partial class ToolManager(string directory, HttpEngine http)
{
    internal sealed record Tool(string Name, string Url, string SumsUrl, string AssetName, string[] Extract);

    private static readonly Tool YtDlp = new("yt-dlp",
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS", "yt-dlp.exe", []);

    private static readonly Tool Deno = new("deno",
        "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip",
        "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip.sha256sum",
        "deno-x86_64-pc-windows-msvc.zip", ["deno.exe"]);

    private static readonly Tool Ffmpeg = new("ffmpeg",
        "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
        "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256",
        "ffmpeg-master-latest-win64-gpl.zip", ["ffmpeg.exe", "ffprobe.exe"]);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Directory => directory;
    public string YtDlpPath => Path.Combine(directory, "yt-dlp.exe");
    public bool IsInstalled => File.Exists(YtDlpPath) && File.Exists(Path.Combine(directory, "deno.exe")) && File.Exists(Path.Combine(directory, "ffmpeg.exe"));

    /// (tool name, fraction 0..1)
    public event Action<string, double>? Progress;

    public async Task InstallAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            if (!File.Exists(YtDlpPath)) await InstallAsync(YtDlp, ct);
            if (!File.Exists(Path.Combine(directory, "deno.exe"))) await InstallAsync(Deno, ct);
            if (!File.Exists(Path.Combine(directory, "ffmpeg.exe"))) await InstallAsync(Ffmpeg, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetYtDlpVersionAsync(CancellationToken ct)
    {
        if (!File.Exists(YtDlpPath)) return null;
        var psi = new ProcessStartInfo(YtDlpPath, "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var v = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
        await p.WaitForExitAsync(ct);
        return v;
    }

    /// Returns true if a newer yt-dlp was installed.
    public async Task<bool> UpdateYtDlpAsync(CancellationToken ct)
    {
        var current = await GetYtDlpVersionAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest");
        req.Headers.UserAgent.ParseAdd(HttpEngine.DefaultUserAgent);
        using var resp = await http.Client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var latest = doc.RootElement.GetProperty("tag_name").GetString();
        if (latest is null || latest == current) return false;

        await _gate.WaitAsync(ct);
        try { await InstallAsync(YtDlp, ct); }
        finally { _gate.Release(); }
        Log.Information("yt-dlp updated {Old} → {New}", current, latest);
        return true;
    }

    internal async Task InstallAsync(Tool tool, CancellationToken ct)
    {
        var expected = await ExpectedHashAsync(tool, ct);
        var temp = Path.Combine(directory, tool.AssetName + ".download");
        try
        {
            using (var resp = await http.Client.GetAsync(tool.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(temp);
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) Progress?.Invoke(tool.Name, done / (double)total);
                }
            }

            string actual;
            await using (var fs = File.OpenRead(temp)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new ToolVerificationException(tool.Name);

            if (tool.Extract.Length == 0)
            {
                File.Move(temp, Path.Combine(directory, tool.AssetName), overwrite: true);
            }
            else
            {
                using var zip = ZipFile.OpenRead(temp);
                foreach (var name in tool.Extract)
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidDataException($"{name} missing in {tool.AssetName}");
                    entry.ExtractToFile(Path.Combine(directory, name), overwrite: true);
                }
            }
            Log.Information("Installed {Tool}", tool.Name);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private async Task<string> ExpectedHashAsync(Tool tool, CancellationToken ct)
    {
        var sums = await http.Client.GetStringAsync(tool.SumsUrl, ct);
        return ParseHash(sums, tool.AssetName) ?? throw new ToolVerificationException(tool.Name);
    }

    /// Accepts sha256sum-style lists ("HASH  name") and files that describe a single asset
    /// (a bare hash, or PowerShell Get-FileHash output as deno publishes it).
    internal static string? ParseHash(string sums, string assetName)
    {
        var lines = sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var l in lines)
        {
            var parts = l.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && HexHash().IsMatch(parts[0])
                && parts[^1].TrimStart('*').Equals(assetName, StringComparison.OrdinalIgnoreCase))
                return parts[0];
        }
        var hashes = HexHash().Matches(sums).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return hashes.Count == 1 ? hashes[0] : null; // several unrelated hashes → refuse to guess
    }

    [GeneratedRegex("\\b[0-9a-fA-F]{64}\\b")]
    private static partial Regex HexHash();
}

public sealed class ToolVerificationException(string tool) : Exception($"tool-hash-mismatch:{tool}")
{
    public string Tool { get; } = tool;
}
