using Fluxer.Ipc;
using Serilog;

namespace Fluxer.App;

/// Where the database, logs, tools and torrent state live. Normally %LOCALAPPDATA%\Fluxer; with a portable.txt
/// next to the app (the folder that holds Update.exe and current\), a "data" folder beside it, so the whole
/// thing runs from a USB stick. Velopack replaces current\ on update, so data never goes in there.
public static class PortableData
{
    public const string MarkerName = "portable.txt";
    private const string RestoreName = "restore.db";

    public static string AppRoot
    {
        get
        {
            var b = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(b).Equals("current", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(b)! : b;
        }
    }

    public static string LocalDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Protocol.Instance.Length > 0 ? "Fluxer-" + Protocol.Instance : "Fluxer");

    public static string PortableDirectory => Path.Combine(AppRoot, "data");

    public static bool IsPortable => File.Exists(Path.Combine(AppRoot, MarkerName));

    /// An installed copy already lives inside %LOCALAPPDATA%\Fluxer; only a copy elsewhere can carry its data.
    public static bool CanSwitch => IsPortable || !AppRoot.StartsWith(LocalDirectory, StringComparison.OrdinalIgnoreCase);

    public static string Resolve() => IsPortable ? PortableDirectory : LocalDirectory;

    /// Copies the data to the other place and flips the marker; the caller restarts. The database goes through
    /// SQLite's backup API (consistent while open); tools and torrent state are copied best effort.
    public static void SwitchTo(bool portable, Fluxer.Core.Storage.Database db, string current)
    {
        var target = portable ? PortableDirectory : LocalDirectory;
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(target);
        var dbPath = Path.Combine(target, "fluxer.db");
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(f)) File.Move(f, f + ".old", overwrite: true);
        db.BackupTo(dbPath);
        foreach (var dir in new[] { "tools", "torrent" })
            CopyTree(Path.Combine(current, dir), Path.Combine(target, dir));
        var marker = Path.Combine(AppRoot, MarkerName);
        if (portable) File.WriteAllText(marker, "Fluxer keeps its data in the \"data\" folder next to this file. Delete this file to use %LOCALAPPDATA%\\Fluxer again.\r\n");
        else File.Delete(marker);
    }

    /// Settings → Advanced → "Restore": the chosen backup replaces the database at the next start.
    public static void StageRestore(string dataDirectory, string backup) => File.Copy(backup, Path.Combine(dataDirectory, RestoreName), overwrite: true);

    /// Before the engine opens the database: put a staged backup in place (the old one stays as .before-restore).
    public static void ApplyPending(string dataDirectory)
    {
        var restore = Path.Combine(dataDirectory, RestoreName);
        if (!File.Exists(restore)) return;
        try
        {
            var db = Path.Combine(dataDirectory, "fluxer.db");
            if (File.Exists(db)) File.Move(db, db + ".before-restore", overwrite: true);
            foreach (var f in new[] { db + "-wal", db + "-shm" }) File.Delete(f);
            File.Move(restore, db);
            Log.Information("Database restored from backup");
        }
        catch (Exception e) { Log.Error(e, "Restoring the backup failed"); }
    }

    private static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
            catch (IOException e) { Log.Warning("Not copied {File}: {Error}", file, e.Message); }
        }
    }
}
