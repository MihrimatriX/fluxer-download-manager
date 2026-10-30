using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fluxer.Core.Http;
using Serilog;

namespace Fluxer.Core.Media;

/// Installs/updates yt-dlp, deno (JS runtime yt-dlp needs for YouTube) and ffmpeg from their
/// official GitHub releases into %LOCALAPPDATA%\Fluxer\tools, verifying the publisher's SHA-256
/// and, where the publisher signs its checksum file (yt-dlp), that signature against a pinned key.
/// tools.json records the hash each tool was installed from; updates compare it with the published one.
public sealed partial class ToolManager(string directory, HttpEngine http)
{
    /// SingleAssetSums: the checksum file describes only this asset and may not name it.
    /// MinUpdateAge: don't replace an install younger than this.
    /// SignatureUrl + PublicKey: detached OpenPGP signature over the checksum file and the key it must verify with.
    internal sealed record Tool(string Name, string Url, string SumsUrl, string AssetName, string[] Extract,
        bool SingleAssetSums = false, TimeSpan? MinUpdateAge = null, string? SignatureUrl = null, string? PublicKey = null);

    private static readonly Tool YtDlp = new("yt-dlp",
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS", "yt-dlp.exe", [],
        SignatureUrl: "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS.sig", PublicKey: OpenPgp.YtDlpKey);

    // deno and FFmpeg-Builds publish no signatures; their hash only proves an intact download.

    // deno publishes PowerShell Get-FileHash output, which names the file by its build path.
    private static readonly Tool Deno = new("deno",
        "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip",
        "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip.sha256sum",
        "deno-x86_64-pc-windows-msvc.zip", ["deno.exe"], SingleAssetSums: true);

    // FFmpeg-Builds rebuilds "latest" every day, so its hash always differs; a ~200 MB download each
    // week for that isn't worth it.
    private static readonly Tool Ffmpeg = new("ffmpeg",
        "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
        "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256",
        "ffmpeg-master-latest-win64-gpl.zip", ["ffmpeg.exe", "ffprobe.exe"], MinUpdateAge: TimeSpan.FromDays(30));

    private static readonly Tool[] All = [YtDlp, Deno, Ffmpeg];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _updateLock = new();
    private Task<IReadOnlyList<string>>? _updating;
    private DateTimeOffset _lastYtDlpCheck;

    public string Directory => directory;
    public string YtDlpPath => Path.Combine(directory, "yt-dlp.exe");
    public string DenoPath => Path.Combine(directory, "deno.exe");
    public bool IsInstalled => All.All(IsComplete);

    /// HttpEngine never times out (downloads can legitimately run for hours), so every request here
    /// is abandoned once no bytes have arrived for this long.
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal TimeSpan VerifyRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    private string ManifestPath => Path.Combine(directory, "tools.json");

    /// (tool name, fraction 0..1)
    public event Action<string, double>? Progress;

    public async Task InstallAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            DeleteSetAside();
            foreach (var tool in All)
                if (!IsComplete(tool)) await InstallAsync(tool, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetYtDlpVersionAsync(CancellationToken ct)
    {
        if (!File.Exists(YtDlpPath)) return null;
        var psi = new ProcessStartInfo(YtDlpPath, "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        Platform.Windows.ChildProcessJob.Assign(p);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var v = (await p.StandardOutput.ReadToEndAsync(timeout.Token)).Trim();
            await p.WaitForExitAsync(timeout.Token);
            return v;
        }
        catch (OperationCanceledException) { p.Kill(entireProcessTree: true); throw; }
    }

    /// Returns true if a newer yt-dlp was installed. notWithin skips the check when one ran that
    /// recently (failed media downloads ask after every failure).
    public async Task<bool> UpdateYtDlpAsync(CancellationToken ct, TimeSpan? notWithin = null)
    {
        if (notWithin is { } w && DateTimeOffset.UtcNow - _lastYtDlpCheck < w) return false;
        return (await UpdateAsync([YtDlp], ct)).Contains(YtDlp.Name);
    }

    /// Updates every tool whose published hash differs from the installed one; returns their names.
    public Task<IReadOnlyList<string>> UpdateAllAsync(CancellationToken ct) => UpdateAsync(All, ct);

    internal Task<IReadOnlyList<string>> UpdateAsync(Tool[] tools, CancellationToken ct)
    {
        lock (_updateLock)
        {
            // Callers arriving while a check runs (several media downloads failing together) share it
            // instead of each downloading the same release.
            if (_updating is { IsCompleted: false } running) return running;
            return _updating = UpdateCoreAsync(tools, ct);
        }
    }

    private async Task<IReadOnlyList<string>> UpdateCoreAsync(Tool[] tools, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            DeleteSetAside();
            var manifest = LoadManifest();
            var updated = new List<string>();
            foreach (var tool in tools)
            {
                if (tool == YtDlp) _lastYtDlpCheck = DateTimeOffset.UtcNow;
                var known = manifest.GetValueOrDefault(tool.Name);
                if (known is not null && IsComplete(tool) && tool.MinUpdateAge is { } age && DateTimeOffset.UtcNow - known.InstalledAt < age)
                    continue;
                var expected = await ExpectedHashAsync(tool, ct);
                // Installs from before tools.json existed: a plain exe can be hashed; an extracted zip can't,
                // so it is reinstalled once (which also repairs one cut short by a crash).
                var installed = !IsComplete(tool) ? null
                    : known?.Sha256 ?? (tool.Extract.Length == 0 ? await HashFileAsync(Path.Combine(directory, tool.AssetName), ct) : null);
                if (installed is not null && installed.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    if (known is null) Record(tool, installed);
                    continue;
                }
                await InstallAsync(tool, ct);
                updated.Add(tool.Name);
                Log.Information("{Tool} updated", tool.Name);
            }
            return updated;
        }
        finally { _gate.Release(); }
    }

    internal async Task InstallAsync(Tool tool, CancellationToken ct)
    {
        try { await InstallOnceAsync(tool, ct); }
        catch (ToolVerificationException)
        {
            // The asset and its checksum file are separate "latest" downloads; a release published
            // between the two makes them disagree once. A second mismatch is real.
            Log.Warning("{Tool} failed verification, retrying once", tool.Name);
            await Task.Delay(VerifyRetryDelay, ct);
            await InstallOnceAsync(tool, ct);
        }
    }

    private async Task InstallOnceAsync(Tool tool, CancellationToken ct)
    {
        var temp = Path.Combine(directory, tool.AssetName + ".download");
        try
        {
            string expected;
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                stall.CancelAfter(StallTimeout);
                try
                {
                    expected = await ExpectedHashAsync(tool, ct, stall);
                    stall.CancelAfter(StallTimeout);
                    using var resp = await http.Client.GetAsync(tool.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength;
                    await using var src = await resp.Content.ReadAsStreamAsync(stall.Token);
                    await using var dst = File.Create(temp);
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buffer, stall.Token)) > 0)
                    {
                        stall.CancelAfter(StallTimeout);
                        await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                        done += n;
                        if (total > 0) Progress?.Invoke(tool.Name, done / (double)total);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ToolStalledException(tool.Name); }
            }

            string actual;
            await using (var fs = File.OpenRead(temp)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new ToolVerificationException(tool.Name);

            // Everything is written next to its final name first and only then renamed into place, so a
            // crash or a full disk leaves a missing file (reinstalled on next use), never a truncated exe.
            var staged = new List<(string From, string To)>();
            if (tool.Extract.Length == 0)
            {
                staged.Add((temp, Path.Combine(directory, tool.AssetName)));
            }
            else
            {
                using var zip = ZipFile.OpenRead(temp);
                foreach (var name in tool.Extract)
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidDataException($"{name} missing in {tool.AssetName}");
                    var part = Path.Combine(directory, name + ".new");
                    entry.ExtractToFile(part, overwrite: true);
                    staged.Add((part, Path.Combine(directory, name)));
                }
            }
            foreach (var (from, to) in staged) Replace(from, to);
            Record(tool, expected);
            Log.Information("Installed {Tool}", tool.Name);
        }
        finally
        {
            TryDelete(temp);
            foreach (var name in tool.Extract) TryDelete(Path.Combine(directory, name + ".new"));
        }
    }

    private bool IsComplete(Tool tool) =>
        (tool.Extract.Length == 0 ? [tool.AssetName] : tool.Extract).All(f => File.Exists(Path.Combine(directory, f)));

    /// A running exe can't be overwritten on Windows, but it can be renamed: the old one is moved
    /// aside and deleted now or, if yt-dlp/ffmpeg still runs it, on the next install or update.
    private static void Replace(string from, string to)
    {
        if (File.Exists(to))
        {
            var aside = $"{to}.{Guid.NewGuid():N}.old";
            File.Move(to, aside);
            try { File.Delete(aside); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        File.Move(from, to);
    }

    private void DeleteSetAside()
    {
        foreach (var f in System.IO.Directory.EnumerateFiles(directory, "*.old"))
            try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed record ToolRecord(string Sha256, DateTimeOffset InstalledAt);

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    private Dictionary<string, ToolRecord> LoadManifest()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, ToolRecord>>(File.ReadAllText(ManifestPath)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException) { return []; } // missing or damaged: treated as unknown
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    private void Record(Tool tool, string sha256)
    {
        var manifest = LoadManifest();
        manifest[tool.Name] = new ToolRecord(sha256.ToUpperInvariant(), DateTimeOffset.UtcNow);
        var temp = ManifestPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest));
        File.Move(temp, ManifestPath, overwrite: true);
    }

    private static async Task<string?> HashFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var fs = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
    }

    /// Cleanup must not hide the exception that got us here (an antivirus scan can hold the file).
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning(e, "Could not delete {Path}", path); }
    }

    private async Task<string> ExpectedHashAsync(Tool tool, CancellationToken ct, CancellationTokenSource? stall = null)
    {
        byte[] sums;
        byte[]? signature = null;
        using var own = stall is null ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        own?.CancelAfter(StallTimeout);
        var token = (stall ?? own)!.Token;
        try
        {
            sums = await http.Client.GetByteArrayAsync(tool.SumsUrl, token);
            if (tool.SignatureUrl is not null) signature = await http.Client.GetByteArrayAsync(tool.SignatureUrl, token);
        }
        catch (OperationCanceledException) when (own is not null && !ct.IsCancellationRequested) { throw new ToolStalledException(tool.Name); }

        if (tool.PublicKey is not null && (signature is null || !OpenPgp.VerifyDetached(sums, signature, tool.PublicKey)))
        {
            Log.Error("{Tool} checksum file has no valid signature", tool.Name);
            throw new ToolVerificationException(tool.Name);
        }
        return ParseHash(Encoding.UTF8.GetString(sums), tool.AssetName, tool.SingleAssetSums) ?? throw new ToolVerificationException(tool.Name);
    }

    /// Accepts sha256sum-style lists ("HASH  name"). With singleAsset, also a file that describes only
    /// this asset without naming it (a bare hash, or PowerShell Get-FileHash output).
    internal static string? ParseHash(string sums, string assetName, bool singleAsset = false)
    {
        var lines = sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var l in lines)
        {
            var parts = l.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && HexHash().IsMatch(parts[0])
                && parts[^1].TrimStart('*').Equals(assetName, StringComparison.OrdinalIgnoreCase))
                return parts[0];
        }
        if (!singleAsset) return null;
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

/// No data arrived for ToolManager.StallTimeout; distinct from the user cancelling.
public sealed class ToolStalledException(string tool) : Exception($"tool-stalled:{tool}")
{
    public string Tool { get; } = tool;
}
