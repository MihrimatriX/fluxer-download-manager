using System.Globalization;
using System.Text.Json;
using Fluxer.Core.Downloads;
using Fluxer.Core.Http;
using Fluxer.Core.Platform.Windows;
using Microsoft.Data.Sqlite;

namespace Fluxer.Core.Storage;

/// Plain SQL over one connection. Calls are short and serialized by a lock; the engine calls
/// them from thread-pool threads (Microsoft.Data.Sqlite's async API is synchronous underneath anyway).
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly Lock _lock = new();

    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE downloads (
          id INTEGER PRIMARY KEY, kind INTEGER NOT NULL, state INTEGER NOT NULL,
          file_name TEXT NOT NULL, save_dir TEXT NOT NULL, category TEXT,
          total_bytes INTEGER, done_bytes INTEGER NOT NULL DEFAULT 0,
          speed_limit INTEGER NOT NULL DEFAULT 0, queue_order INTEGER NOT NULL,
          error TEXT, created_at INTEGER NOT NULL, completed_at INTEGER);
        CREATE INDEX ix_downloads_state ON downloads(state, queue_order);
        CREATE TABLE http_downloads (
          download_id INTEGER PRIMARY KEY REFERENCES downloads(id) ON DELETE CASCADE,
          url TEXT NOT NULL, final_url TEXT, max_connections INTEGER NOT NULL DEFAULT 8,
          resumable INTEGER NOT NULL, etag TEXT, last_modified TEXT,
          referrer TEXT, user_agent TEXT, cookies BLOB, hash_algo TEXT, hash_expected TEXT);
        CREATE TABLE segments (
          download_id INTEGER NOT NULL REFERENCES downloads(id) ON DELETE CASCADE,
          idx INTEGER NOT NULL, start INTEGER NOT NULL, "end" INTEGER NOT NULL, position INTEGER NOT NULL,
          PRIMARY KEY (download_id, idx)) WITHOUT ROWID;
        CREATE TABLE torrent_downloads (
          download_id INTEGER PRIMARY KEY REFERENCES downloads(id) ON DELETE CASCADE,
          info_hash TEXT NOT NULL UNIQUE, magnet TEXT, torrent_file BLOB, fast_resume BLOB,
          sequential INTEGER NOT NULL DEFAULT 0, seed_ratio REAL, seed_minutes INTEGER,
          uploaded_bytes INTEGER NOT NULL DEFAULT 0, seeding_started_at INTEGER NOT NULL DEFAULT 0,
          deselected TEXT);
        CREATE TABLE media_downloads (
          download_id INTEGER PRIMARY KEY REFERENCES downloads(id) ON DELETE CASCADE,
          page_url TEXT NOT NULL, format_spec TEXT NOT NULL, title TEXT, cookies BLOB,
          audio_only INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
        """,
        """
        ALTER TABLE http_downloads ADD COLUMN reject_html INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE torrent_downloads ADD COLUMN upload_limit INTEGER NOT NULL DEFAULT 0;
        """,
        // 3: page a captured stream was playing on (sent to yt-dlp as Referer)
        "ALTER TABLE media_downloads ADD COLUMN referrer TEXT;",
        // 4: mirror addresses of an HTTP download (JSON array)
        "ALTER TABLE http_downloads ADD COLUMN mirrors TEXT;",
        // 5: user tags (JSON array)
        "ALTER TABLE downloads ADD COLUMN tags TEXT;",
        // 6: named queue
        "ALTER TABLE downloads ADD COLUMN queue_id TEXT;",
    ];

    public Database(string path)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
        for (var v = version; v < Migrations.Length; v++)
        {
            using var tx = _conn.BeginTransaction();
            Exec(Migrations[v], tx);
            Exec($"PRAGMA user_version = {v + 1}", tx);
            tx.Commit();
        }
    }

    // ---------- downloads ----------

    public void Insert(DownloadRecord r)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            r.QueueOrder = Convert.ToInt64(Scalar("SELECT COALESCE(MAX(queue_order), 0) + 1 FROM downloads", tx), CultureInfo.InvariantCulture);
            r.Id = Convert.ToInt64(Scalar("""
                INSERT INTO downloads (kind, state, file_name, save_dir, category, total_bytes, done_bytes,
                  speed_limit, queue_order, error, created_at, completed_at, tags, queue_id)
                VALUES ($kind, $state, $file, $dir, $cat, $total, $done, $limit, $order, $err, $created, $completed, $tags, $queue);
                SELECT last_insert_rowid();
                """, tx, CommonParams(r)), CultureInfo.InvariantCulture);
            WriteDetails(r, tx);
            tx.Commit();
        }
    }

    public void Update(DownloadRecord r)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            Exec("""
                UPDATE downloads SET kind=$kind, state=$state, file_name=$file, save_dir=$dir, category=$cat,
                  total_bytes=$total, done_bytes=$done, speed_limit=$limit, queue_order=$order, error=$err,
                  created_at=$created, completed_at=$completed, tags=$tags, queue_id=$queue WHERE id=$id
                """, tx, [.. CommonParams(r), ("$id", r.Id)]);
            WriteDetails(r, tx);
            tx.Commit();
        }
    }

    public void Delete(long id)
    {
        lock (_lock) Exec("DELETE FROM downloads WHERE id=$id", null, ("$id", id));
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    public List<DownloadRecord> LoadAll()
    {
        lock (_lock)
        {
            var list = new Dictionary<long, DownloadRecord>();
            using (var rd = Reader("SELECT * FROM downloads ORDER BY queue_order"))
                while (rd.Read())
                {
                    var r = new DownloadRecord
                    {
                        Id = rd.GetInt64(rd.GetOrdinal("id")),
                        Kind = (DownloadKind)rd.GetInt32(rd.GetOrdinal("kind")),
                        State = (DownloadState)rd.GetInt32(rd.GetOrdinal("state")),
                        FileName = rd.GetString(rd.GetOrdinal("file_name")),
                        SaveDirectory = rd.GetString(rd.GetOrdinal("save_dir")),
                        Category = Str(rd, "category"),
                        TotalBytes = Long(rd, "total_bytes"),
                        DoneBytes = rd.GetInt64(rd.GetOrdinal("done_bytes")),
                        SpeedLimit = rd.GetInt64(rd.GetOrdinal("speed_limit")),
                        QueueOrder = rd.GetInt64(rd.GetOrdinal("queue_order")),
                        Error = Str(rd, "error"),
                        CreatedAt = rd.GetInt64(rd.GetOrdinal("created_at")),
                        CompletedAt = Long(rd, "completed_at"),
                        Tags = Str(rd, "tags") is { } tg ? JsonSerializer.Deserialize<List<string>>(tg) ?? [] : [],
                        QueueId = Str(rd, "queue_id"),
                    };
                    list[r.Id] = r;
                }

            using (var rd = Reader("SELECT * FROM http_downloads"))
                while (rd.Read())
                    if (list.TryGetValue(rd.GetInt64(0), out var r))
                        r.Http = new HttpInfo
                        {
                            Url = rd.GetString(rd.GetOrdinal("url")),
                            FinalUrl = Str(rd, "final_url"),
                            MaxConnections = rd.GetInt32(rd.GetOrdinal("max_connections")),
                            Resumable = rd.GetInt64(rd.GetOrdinal("resumable")) != 0,
                            ETag = Str(rd, "etag"),
                            LastModified = Str(rd, "last_modified") is { } lm ? DateTimeOffset.Parse(lm, CultureInfo.InvariantCulture) : null,
                            Referrer = Str(rd, "referrer"),
                            UserAgent = Str(rd, "user_agent"),
                            Cookies = Dpapi.Unprotect(Blob(rd, "cookies")),
                            HashAlgorithm = Str(rd, "hash_algo"),
                            HashExpected = Str(rd, "hash_expected"),
                            RejectHtml = rd.GetInt64(rd.GetOrdinal("reject_html")) != 0,
                            Mirrors = Str(rd, "mirrors") is { } mi ? JsonSerializer.Deserialize<List<string>>(mi) ?? [] : [],
                        };

            using (var rd = Reader("SELECT * FROM torrent_downloads"))
                while (rd.Read())
                    if (list.TryGetValue(rd.GetInt64(0), out var r))
                        r.Torrent = new TorrentInfo
                        {
                            InfoHash = rd.GetString(rd.GetOrdinal("info_hash")),
                            Magnet = Str(rd, "magnet"),
                            TorrentFile = Blob(rd, "torrent_file"),
                            FastResume = Blob(rd, "fast_resume"),
                            Sequential = rd.GetInt64(rd.GetOrdinal("sequential")) != 0,
                            SeedRatio = rd.IsDBNull(rd.GetOrdinal("seed_ratio")) ? null : rd.GetDouble(rd.GetOrdinal("seed_ratio")),
                            SeedMinutes = (int?)Long(rd, "seed_minutes"),
                            UploadedBytes = rd.GetInt64(rd.GetOrdinal("uploaded_bytes")),
                            SeedingStartedAt = rd.GetInt64(rd.GetOrdinal("seeding_started_at")),
                            UploadLimit = rd.GetInt64(rd.GetOrdinal("upload_limit")),
                            DeselectedFiles = Str(rd, "deselected") is { } d ? JsonSerializer.Deserialize<HashSet<int>>(d) : null,
                        };

            using (var rd = Reader("SELECT * FROM media_downloads"))
                while (rd.Read())
                    if (list.TryGetValue(rd.GetInt64(0), out var r))
                        r.Media = new MediaInfo
                        {
                            PageUrl = rd.GetString(rd.GetOrdinal("page_url")),
                            FormatSpec = rd.GetString(rd.GetOrdinal("format_spec")),
                            Title = Str(rd, "title"),
                            Cookies = Dpapi.Unprotect(Blob(rd, "cookies")),
                            AudioOnly = rd.GetInt64(rd.GetOrdinal("audio_only")) != 0,
                            Referrer = Str(rd, "referrer"),
                        };

            return [.. list.Values];
        }
    }

    // ---------- segments ----------

    public void SaveSegments(long downloadId, long doneBytes, IReadOnlyList<SegmentRow> rows)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            Exec("DELETE FROM segments WHERE download_id=$id", tx, ("$id", downloadId));
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """INSERT INTO segments (download_id, idx, start, "end", position) VALUES ($d, $i, $s, $e, $p)""";
            var pd = cmd.Parameters.Add("$d", SqliteType.Integer);
            var pi = cmd.Parameters.Add("$i", SqliteType.Integer);
            var ps = cmd.Parameters.Add("$s", SqliteType.Integer);
            var pe = cmd.Parameters.Add("$e", SqliteType.Integer);
            var pp = cmd.Parameters.Add("$p", SqliteType.Integer);
            foreach (var r in rows)
            {
                pd.Value = downloadId; pi.Value = r.Index; ps.Value = r.Start; pe.Value = r.End; pp.Value = r.Position;
                cmd.ExecuteNonQuery();
            }
            Exec("UPDATE downloads SET done_bytes=$b WHERE id=$id", tx, ("$b", doneBytes), ("$id", downloadId));
            tx.Commit();
        }
    }

    public List<SegmentRow> LoadSegments(long downloadId)
    {
        lock (_lock)
        {
            var list = new List<SegmentRow>();
            using var rd = Reader("""SELECT idx, start, "end", position FROM segments WHERE download_id=$id ORDER BY idx""", ("$id", downloadId));
            while (rd.Read()) list.Add(new SegmentRow(rd.GetInt32(0), rd.GetInt64(1), rd.GetInt64(2), rd.GetInt64(3)));
            return list;
        }
    }

    // ---------- settings ----------

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    public T GetSetting<T>(string key) where T : new()
    {
        lock (_lock)
            return Scalar("SELECT value FROM settings WHERE key=$k", null, ("$k", key)) is string json
                ? JsonSerializer.Deserialize<T>(json) ?? new T()
                : new T();
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    public void SetSetting<T>(string key, T value)
    {
        lock (_lock)
            Exec("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                null, ("$k", key), ("$v", JsonSerializer.Serialize(value)));
    }

    /// A consistent copy of the whole database while it stays open (SQLite online backup).
    public void BackupTo(string path)
    {
        lock (_lock)
        {
            using var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            dest.Open();
            _conn.BackupDatabase(dest);
        }
    }

    public void Dispose() => _conn.Dispose();

    // ---------- helpers ----------

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    private void WriteDetails(DownloadRecord r, SqliteTransaction tx)
    {
        if (r.Http is { } h)
            Exec("""
                INSERT INTO http_downloads (download_id, url, final_url, max_connections, resumable, etag, last_modified,
                  referrer, user_agent, cookies, hash_algo, hash_expected, reject_html, mirrors)
                VALUES ($id, $url, $final, $conns, $res, $etag, $lm, $ref, $ua, $cookies, $ha, $he, $rh, $mir)
                ON CONFLICT(download_id) DO UPDATE SET url=excluded.url, final_url=excluded.final_url,
                  max_connections=excluded.max_connections, resumable=excluded.resumable, etag=excluded.etag,
                  last_modified=excluded.last_modified, referrer=excluded.referrer, user_agent=excluded.user_agent,
                  cookies=excluded.cookies, hash_algo=excluded.hash_algo, hash_expected=excluded.hash_expected,
                  reject_html=excluded.reject_html, mirrors=excluded.mirrors
                """, tx, ("$id", r.Id), ("$url", h.Url), ("$final", h.FinalUrl), ("$conns", h.MaxConnections),
                ("$res", h.Resumable ? 1 : 0), ("$etag", h.ETag), ("$lm", h.LastModified?.ToString("O", CultureInfo.InvariantCulture)),
                ("$ref", h.Referrer), ("$ua", h.UserAgent), ("$cookies", Dpapi.Protect(h.Cookies)),
                ("$ha", h.HashAlgorithm), ("$he", h.HashExpected), ("$rh", h.RejectHtml ? 1 : 0),
                ("$mir", h.Mirrors.Count == 0 ? null : JsonSerializer.Serialize(h.Mirrors)));

        if (r.Torrent is { } t)
            Exec("""
                INSERT INTO torrent_downloads (download_id, info_hash, magnet, torrent_file, fast_resume, sequential,
                  seed_ratio, seed_minutes, uploaded_bytes, seeding_started_at, deselected, upload_limit)
                VALUES ($id, $ih, $mag, $tf, $fr, $seq, $sr, $sm, $up, $ss, $des, $ul)
                ON CONFLICT(download_id) DO UPDATE SET info_hash=excluded.info_hash, magnet=excluded.magnet,
                  torrent_file=excluded.torrent_file, fast_resume=excluded.fast_resume, sequential=excluded.sequential,
                  seed_ratio=excluded.seed_ratio, seed_minutes=excluded.seed_minutes, uploaded_bytes=excluded.uploaded_bytes,
                  seeding_started_at=excluded.seeding_started_at, deselected=excluded.deselected, upload_limit=excluded.upload_limit
                """, tx, ("$id", r.Id), ("$ih", t.InfoHash), ("$mag", t.Magnet), ("$tf", t.TorrentFile),
                ("$fr", t.FastResume), ("$seq", t.Sequential ? 1 : 0), ("$sr", t.SeedRatio), ("$sm", t.SeedMinutes),
                ("$up", t.UploadedBytes), ("$ss", t.SeedingStartedAt),
                ("$des", t.DeselectedFiles is null ? null : JsonSerializer.Serialize(t.DeselectedFiles)), ("$ul", t.UploadLimit));

        if (r.Media is { } m)
            Exec("""
                INSERT INTO media_downloads (download_id, page_url, format_spec, title, cookies, audio_only, referrer)
                VALUES ($id, $url, $fmt, $title, $cookies, $audio, $ref)
                ON CONFLICT(download_id) DO UPDATE SET page_url=excluded.page_url, format_spec=excluded.format_spec,
                  title=excluded.title, cookies=excluded.cookies, audio_only=excluded.audio_only, referrer=excluded.referrer
                """, tx, ("$id", r.Id), ("$url", m.PageUrl), ("$fmt", m.FormatSpec), ("$title", m.Title),
                ("$cookies", Dpapi.Protect(m.Cookies)), ("$audio", m.AudioOnly ? 1 : 0), ("$ref", m.Referrer));
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = TrimNotes.OwnTypesKept)]
    private static (string, object?)[] CommonParams(DownloadRecord r) =>
    [
        ("$kind", (int)r.Kind), ("$state", (int)r.State), ("$file", r.FileName), ("$dir", r.SaveDirectory),
        ("$cat", r.Category), ("$total", r.TotalBytes), ("$done", r.DoneBytes), ("$limit", r.SpeedLimit),
        ("$order", r.QueueOrder), ("$err", r.Error), ("$created", r.CreatedAt), ("$completed", r.CompletedAt),
        ("$tags", r.Tags.Count == 0 ? null : JsonSerializer.Serialize(r.Tags)), ("$queue", r.QueueId),
    ];

    private SqliteCommand Command(string sql, SqliteTransaction? tx, (string, object?)[] ps)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    private void Exec(string sql, SqliteTransaction? tx = null, params (string, object?)[] ps)
    {
        using var cmd = Command(sql, tx, ps);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, SqliteTransaction? tx = null, params (string, object?)[] ps)
    {
        using var cmd = Command(sql, tx, ps);
        return cmd.ExecuteScalar();
    }

    private SqliteDataReader Reader(string sql, params (string, object?)[] ps) => Command(sql, null, ps).ExecuteReader();

    private static string? Str(SqliteDataReader rd, string col) { var i = rd.GetOrdinal(col); return rd.IsDBNull(i) ? null : rd.GetString(i); }
    private static long? Long(SqliteDataReader rd, string col) { var i = rd.GetOrdinal(col); return rd.IsDBNull(i) ? null : rd.GetInt64(i); }
    private static byte[]? Blob(SqliteDataReader rd, string col) { var i = rd.GetOrdinal(col); return rd.IsDBNull(i) ? null : (byte[])rd[i]; }
}
