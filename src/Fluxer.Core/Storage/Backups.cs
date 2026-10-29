using System.Globalization;

namespace Fluxer.Core.Storage;

/// Copies of the database in the data folder's "backups", made on a schedule (daily or weekly) and on demand.
/// Restoring one is a restart away (the app stages it, the next start puts it in place).
public static class Backups
{
    public const int Keep = 5;

    public static string Folder(string dataDirectory) => Path.Combine(dataDirectory, "backups");

    /// Newest first.
    public static IReadOnlyList<FileInfo> List(string dataDirectory)
    {
        var dir = new DirectoryInfo(Folder(dataDirectory));
        return dir.Exists ? [.. dir.EnumerateFiles("fluxer-*.db").OrderByDescending(f => f.Name, StringComparer.Ordinal)] : [];
    }

    public static string Create(Database db, string dataDirectory, DateTime now)
    {
        var dir = Folder(dataDirectory);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"fluxer-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.db");
        db.BackupTo(path);
        foreach (var old in List(dataDirectory).Skip(Keep))
            try { old.Delete(); } catch (IOException) { }
        return path;
    }

    /// "off" | "daily" | "weekly"; lastAt in Unix seconds (0 = never).
    public static bool Due(string interval, long lastAt, DateTimeOffset now) => interval switch
    {
        "daily" => now.ToUnixTimeSeconds() - lastAt >= 86400,
        "weekly" => now.ToUnixTimeSeconds() - lastAt >= 7 * 86400,
        _ => false,
    };
}
