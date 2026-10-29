using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Fluxer.Core.Automation;

/// Unpacks a finished archive next to it: zip, rar, 7z and tar (also .tar.gz/.bz2/.xz), including sets split
/// into parts (name.part1.rar, name.rar + .r00, name.zip + .z01, name.7z.001) and encrypted ones, where the
/// user's passwords are tried in turn. Everything goes to a hidden folder first and is moved into place only
/// when the whole archive came out, so a wrong password or a missing part never leaves half the files behind.
public static partial class Extractor
{
    public enum Outcome { Done, NeedsPassword, Failed }

    /// Folder: where the files are (a single folder in the archive lands as itself, otherwise a folder named
    /// after the archive is made). Files: how many came out.
    public sealed record Result(Outcome Outcome, string? Folder = null, int Files = 0, string? Error = null);

    /// The files of one archive, first part first, in the order the library reads them. Key is the same for
    /// every part, so a part still downloading can be recognised.
    public sealed record ArchiveSet(string Key, IReadOnlyList<string> Parts, bool Complete);

    [GeneratedRegex(@"^(?<base>.+)\.part(?<n>\d+)\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex PartRar();
    [GeneratedRegex(@"^(?<base>.+)\.r(?<n>\d{2,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex OldRarPart();
    [GeneratedRegex(@"^(?<base>.+)\.z(?<n>\d{2,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex ZipPart();
    [GeneratedRegex(@"^(?<base>.+\.(?:7z|zip|rar|tar))\.(?<n>\d{3,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex Numbered();

    private static readonly string[] Single = [".zip", ".7z", ".rar", ".tar", ".tgz", ".tbz2", ".txz", ".tar.gz", ".tar.bz2", ".tar.xz"];

    /// The set a file belongs to, from its name alone; null when it is not an archive this can open.
    public static string? KeyOf(string fileName)
    {
        if (PartRar().Match(fileName) is { Success: true } a) return a.Groups["base"].Value + ".rar";
        if (Numbered().Match(fileName) is { Success: true } b) return b.Groups["base"].Value;
        if (OldRarPart().Match(fileName) is { Success: true } c) return c.Groups["base"].Value + ".rar";
        if (ZipPart().Match(fileName) is { Success: true } d) return d.Groups["base"].Value + ".zip";
        return Single.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? fileName : null;
    }

    /// Null when the file is not an archive this can open.
    public static ArchiveSet? Describe(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileName(path);
        if (PartRar().Match(name) is { Success: true } pr)
            return Numbered(dir, pr.Groups["base"].Value, $@"\.part(\d+)\.rar", start: 1, key: pr.Groups["base"].Value + ".rar");
        if (Numbered().Match(name) is { Success: true } nm)
            return Numbered(dir, nm.Groups["base"].Value, @"\.(\d{3,4})", start: 1, key: nm.Groups["base"].Value);
        var or = OldRarPart().Match(name);
        if (or.Success || name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            var b = or.Success ? or.Groups["base"].Value : name[..^4];
            var rest = Numbered(dir, b, @"\.r(\d{2,3})", start: 0, key: b + ".rar");
            return Lead(dir, b + ".rar", rest);
        }
        var zp = ZipPart().Match(name);
        if (zp.Success || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // A split zip ends with the .zip, but it is the part the library opens first.
            var b = zp.Success ? zp.Groups["base"].Value : name[..^4];
            var rest = Numbered(dir, b, @"\.z(\d{2,3})", start: 1, key: b + ".zip");
            return Lead(dir, b + ".zip", rest);
        }
        return Single.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? new ArchiveSet(name, [path], File.Exists(path)) : null;
    }

    /// name.rar / name.zip followed by its numbered parts (if any).
    private static ArchiveSet Lead(string dir, string leadName, ArchiveSet rest)
    {
        var lead = Path.Combine(dir, leadName);
        return new ArchiveSet(leadName, [lead, .. rest.Parts], File.Exists(lead) && (rest.Parts.Count == 0 || rest.Complete));
    }

    /// Parts base + suffix with a number, sorted; complete when they run from `start` without a gap.
    private static ArchiveSet Numbered(string dir, string baseName, string suffix, int start, string key)
    {
        var re = new Regex("^" + Regex.Escape(baseName) + suffix + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var parts = (Directory.Exists(dir) ? Directory.EnumerateFiles(dir) : [])
            .Select(f => (Path: f, M: re.Match(Path.GetFileName(f))))
            .Where(x => x.M.Success)
            .Select(x => (x.Path, N: int.Parse(x.M.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .OrderBy(x => x.N).ToList();
        var complete = parts.Count > 0 && parts.Select((p, i) => p.N == start + i).All(ok => ok);
        return new ArchiveSet(key, [.. parts.Select(p => p.Path)], complete);
    }

    private static bool IsTar(string path) =>
        new[] { ".tar", ".tgz", ".tbz2", ".txz", ".tar.gz", ".tar.bz2", ".tar.xz" }.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static ExtractionOptions Options => new()
    {
        ExtractFullPath = true,
        Overwrite = true,
        PreserveFileTime = true,
        SymbolicLinkHandler = (_, _) => { },     // links could point anywhere; they are left out
    };

    /// Unpacks the set next to its first part. `passwords` are tried in order when the archive is encrypted.
    public static Result Extract(ArchiveSet set, IReadOnlyList<string> passwords, CancellationToken ct)
    {
        if (!set.Complete) return new Result(Outcome.Failed, Error: "missing part");
        var first = set.Parts[0];
        var dir = Path.GetDirectoryName(first)!;
        var encrypted = false;
        foreach (var password in new string?[] { null }.Concat(passwords.Where(p => p.Length > 0)))
        {
            if (password is not null && !encrypted) break;
            var temp = Path.Combine(dir, ".fluxer-extract-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var dirInfo = Directory.CreateDirectory(temp);
                dirInfo.Attributes |= FileAttributes.Hidden;
                var files = Unpack(set, password, temp, ct);
                return new Result(Outcome.Done, MoveIntoPlace(temp, dir, ArchiveName(set)), files);
            }
            catch (OperationCanceledException) { TryDelete(temp); throw; }
            catch (Exception e)
            {
                TryDelete(temp);
                if (password is not null) continue;        // a wrong password fails in many ways; try the next one
                encrypted = e is SharpCompress.Common.CryptographicException || IsEncrypted(set);
                if (!encrypted) return new Result(Outcome.Failed, Error: e.Message);
            }
        }
        return new Result(encrypted ? Outcome.NeedsPassword : Outcome.Failed);
    }

    /// Whether the entries are encrypted (when only the contents are, the file list still opens without a password).
    private static bool IsEncrypted(ArchiveSet set)
    {
        if (IsTar(set.Parts[0])) return false;
        try
        {
            using var archive = set.Parts.Count == 1
                ? ArchiveFactory.OpenArchive(set.Parts[0], new ReaderOptions())
                : ArchiveFactory.OpenArchive([.. set.Parts.Select(p => new FileInfo(p))], new ReaderOptions());
            return archive.IsEncrypted || archive.Entries.Any(e => e.IsEncrypted);
        }
        catch (SharpCompress.Common.CryptographicException) { return true; }
        catch (Exception) { return false; }
    }

    private static int Unpack(ArchiveSet set, string? password, string temp, CancellationToken ct)
    {
        var options = new ReaderOptions { Password = password, LookForHeader = false };
        var files = 0;
        if (IsTar(set.Parts[0]))
        {
            using var reader = ReaderFactory.OpenReader(set.Parts[0], options);
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory) continue;
                reader.WriteEntryToDirectory(temp, Options);
                files++;
            }
            return files;
        }
        using var archive = set.Parts.Count == 1
            ? ArchiveFactory.OpenArchive(set.Parts[0], options)
            : ArchiveFactory.OpenArchive([.. set.Parts.Select(p => new FileInfo(p))], options);
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            // Solid data is one stream; reading it in order is the only fast way through.
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory) continue;
                reader.WriteEntryToDirectory(temp, Options);
                files++;
            }
            return files;
        }
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            entry.WriteToDirectory(temp, Options);
            files++;
        }
        return files;
    }

    /// "Holiday.part1.rar" → "Holiday"; "data.tar.gz" → "data".
    public static string ArchiveName(ArchiveSet set)
    {
        var name = set.Key;
        foreach (var ext in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".7z", ".zip", ".rar", ".tar", ".tgz", ".tbz2", ".txz" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        return name;
    }

    /// One folder (or file) at the top of the archive lands as itself; anything else goes into a folder named
    /// after the archive. Nothing that is already there is replaced: a free name with " (2)" is used instead.
    private static string MoveIntoPlace(string temp, string dir, string archiveName)
    {
        var top = Directory.EnumerateFileSystemEntries(temp).ToList();
        if (top.Count == 1)
        {
            var item = top[0];
            var isDir = Directory.Exists(item);
            var target = Free(Path.Combine(dir, Path.GetFileName(item)), isDir);
            if (isDir) Directory.Move(item, target); else File.Move(item, target);
            Directory.Delete(temp);
            return isDir ? target : dir;
        }
        var folder = Free(Path.Combine(dir, archiveName), isDir: true);
        Directory.Move(temp, folder);
        new DirectoryInfo(folder).Attributes &= ~FileAttributes.Hidden;
        return folder;
    }

    private static string Free(string path, bool isDir)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
