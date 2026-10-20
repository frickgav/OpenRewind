using Microsoft.Data.Sqlite;

namespace OpenRewind;

/// BrowserUrl is the address of the browser tab in this frame, or "". Blocked: a blocked window or
/// site was in front (the frame keeps only the app name; Insights leaves it out).
/// Display names the screen it was taken on. A frame's picture is either the still file at SnapshotPath,
/// or (after Compress Older Recordings) frame VideoFrame of the video at VideoPath, VideoTime seconds in.
/// Read it with the app's FrameImages, never from the paths directly.
public record Frame(long Id, string Timestamp, string AppName, string WindowName, bool Focused,
                    string SnapshotPath, string Text, string TextSource, long Hash, string BrowserUrl = "", bool Blocked = false,
                    string Display = "", string VideoPath = "", int VideoFrame = 0, double VideoTime = 0)
{
    public FrameSource Source => new(SnapshotPath, VideoPath, VideoFrame, VideoTime);
}

public record AudioChunk(long Id, string FilePath, string Timestamp, string Device, bool IsInput, string Status);

public record Transcript(long Id, long ChunkId, string Timestamp, double StartTime, double EndTime,
                         string Text, string Device, bool IsInput, string FilePath);

public record Segment(double Start, double End, string Text);

public class SearchQuery
{
    public string? Q;
    public string? AppName;
    public string? WindowName;
    public string? StartTime;
    public string? EndTime;
    public int Limit = 20;
    public int Offset = 0;
}

/// Thin SQLite wrapper with the exact schema the Mac app uses:
/// `frames` + `frames_fts`, `audio_chunks` + `audio_transcriptions` + `audio_fts`.
/// One connection, one lock: the app is small and writes are rare.
public sealed partial class Database : IDisposable
{
    readonly SqliteConnection conn;
    readonly object gate = new();
    /// The frame columns to read. A read-only open (the MCP server) may see a database the recording app
    /// has not migrated yet; it then reads every frame as a still.
    readonly string cols = FullCols;

    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA busy_timeout=5000;");
        Migrate();
        FinishHomeMove(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    /// Read-only: no schema changes and no home move, which are the recording app's job (see OpenReadOnly).
    Database(string path, bool readOnly)
    {
        conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        Exec("PRAGMA busy_timeout=5000;");
        if (ScalarLong("SELECT COUNT(*) FROM pragma_table_info('frames') WHERE name IN ('display', 'video_path', 'video_frame', 'video_time')") < 4)
            cols = StillCols;
    }

    /// After the data folder moved from ~/.openscreen (see Config), point the stored screenshot
    /// and audio paths at the new folder. Runs again next launch if it is cut short.
    void FinishHomeMove(string home)
    {
        var marker = Path.Combine(home, Config.MovedFromFile);
        if (!File.Exists(marker)) return;
        var old = File.ReadAllText(marker);
        if (old.Length == 0) return;
        var fresh = home.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        lock (gate)
        {
            using var tx = conn.BeginTransaction();
            foreach (var (table, col) in new[] { ("frames", "snapshot_path"), ("frames", "video_path"), ("video_segments", "path"), ("audio_chunks", "file_path") })
            {
                using var cmd = Cmd($"UPDATE {table} SET {col} = ? || substr({col}, ?) WHERE substr({col}, 1, ?) = ?",
                                    fresh, old.EnumerateRunes().Count() + 1, old.EnumerateRunes().Count(), old);
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        File.Delete(marker);
        Log.Write("updated recording paths for the new data folder");
    }

    public void Dispose() => conn.Dispose();

    void Migrate()
    {
        Exec("""
        CREATE TABLE IF NOT EXISTS frames (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            app_name TEXT NOT NULL DEFAULT '',
            window_name TEXT NOT NULL DEFAULT '',
            focused INTEGER NOT NULL DEFAULT 1,
            snapshot_path TEXT NOT NULL DEFAULT '',
            text TEXT NOT NULL DEFAULT '',
            text_source TEXT NOT NULL DEFAULT 'ocr',
            hash INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_frames_app_ts ON frames(app_name, timestamp);
        CREATE VIRTUAL TABLE IF NOT EXISTS frames_fts USING fts5(
            text, app_name, window_name,
            content='frames', content_rowid='id', tokenize='unicode61'
        );
        CREATE TRIGGER IF NOT EXISTS frames_ai AFTER INSERT ON frames BEGIN
            INSERT INTO frames_fts(rowid, text, app_name, window_name)
            VALUES (new.id, new.text, new.app_name, new.window_name);
        END;
        CREATE TRIGGER IF NOT EXISTS frames_ad AFTER DELETE ON frames BEGIN
            INSERT INTO frames_fts(frames_fts, rowid, text, app_name, window_name)
            VALUES ('delete', old.id, old.text, old.app_name, old.window_name);
        END;

        CREATE TABLE IF NOT EXISTS audio_chunks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            file_path TEXT NOT NULL,
            timestamp TEXT NOT NULL,
            device TEXT NOT NULL DEFAULT '',
            is_input INTEGER NOT NULL DEFAULT 1,
            status TEXT NOT NULL DEFAULT 'pending'
                CHECK (status IN ('pending', 'transcribed', 'silent', 'failed')),
            attempts INTEGER NOT NULL DEFAULT 0,
            failure_reason TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_audio_chunks_status ON audio_chunks(status, timestamp);
        CREATE INDEX IF NOT EXISTS idx_audio_chunks_ts ON audio_chunks(timestamp);
        CREATE TABLE IF NOT EXISTS audio_transcriptions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            audio_chunk_id INTEGER NOT NULL REFERENCES audio_chunks(id),
            timestamp TEXT NOT NULL,
            start_time REAL NOT NULL DEFAULT 0,
            end_time REAL NOT NULL DEFAULT 0,
            transcription TEXT NOT NULL,
            device TEXT NOT NULL DEFAULT '',
            is_input INTEGER NOT NULL DEFAULT 1,
            engine TEXT NOT NULL DEFAULT 'whisper.cpp'
        );
        CREATE INDEX IF NOT EXISTS idx_audio_tx_ts ON audio_transcriptions(timestamp);
        CREATE VIRTUAL TABLE IF NOT EXISTS audio_fts USING fts5(
            transcription, device,
            content='audio_transcriptions', content_rowid='id', tokenize='unicode61'
        );
        CREATE TRIGGER IF NOT EXISTS audio_tx_ai AFTER INSERT ON audio_transcriptions BEGIN
            INSERT INTO audio_fts(rowid, transcription, device) VALUES (new.id, new.transcription, new.device);
        END;
        CREATE TRIGGER IF NOT EXISTS audio_tx_ad AFTER DELETE ON audio_transcriptions BEGIN
            INSERT INTO audio_fts(audio_fts, rowid, transcription, device)
            VALUES ('delete', old.id, old.transcription, old.device);
        END;
        """);
        // Added later: older databases do not have these columns yet. The Mac app adds the same ones.
        if (ScalarLong("SELECT COUNT(*) FROM pragma_table_info('frames') WHERE name = 'browser_url'") == 0)
            Exec("ALTER TABLE frames ADD COLUMN browser_url TEXT NOT NULL DEFAULT ''");
        MigrateJournal();
        // Time order. Replaced by idx_frames_insights, which starts with the time too (see BuildSlowIndexesInBackground);
        // only a database that does not have that one yet needs this.
        if (ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_frames_insights'") == 0)
            Exec("CREATE INDEX IF NOT EXISTS idx_frames_ts ON frames(timestamp)");
        // Frames saved while a blocked window or site was in front. Older frames say 0. Mac: the same column.
        if (ScalarLong("SELECT COUNT(*) FROM pragma_table_info('frames') WHERE name = 'blocked'") == 0)
            Exec("ALTER TABLE frames ADD COLUMN blocked INTEGER NOT NULL DEFAULT 0");
        MigrateVideo();
        MigrateMeaning();
        MigrateMeetings();
    }

    /// Indexes that take a while on a big database (the whole frames table is read, screen text and
    /// all: seconds, or about a minute on a slow disk). Built after launch on a connection of its own,
    /// off the UI thread, so the app starts at once and reads keep working meanwhile. A failure (full
    /// disk, another process writing) is logged and tried again next launch; Insights still works,
    /// only slower. The Mac app does the same.
    public static Task BuildSlowIndexesInBackground(string path) => Task.Run(() =>
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            c.Open();
            void Run(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            Run("PRAGMA busy_timeout=30000;");
            using (var has = c.CreateCommand())
            {
                has.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_frames_insights'";
                if ((long)has.ExecuteScalar()! > 0) return;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // Insights reads time, app, window, address and the blocked mark in time order; with this
            // index it never touches the screen text.
            Run("CREATE INDEX IF NOT EXISTS idx_frames_insights ON frames(timestamp, app_name, window_name, browser_url, blocked)");
            // The new index starts with the time, so the old time index only costs disk space and inserts.
            Run("DROP INDEX IF EXISTS idx_frames_ts");
            Run("DROP INDEX IF EXISTS idx_frames_activity");  // an earlier test build of Insights
            Log.Write($"built the Insights index in {watch.Elapsed.TotalSeconds:0.0} s");
        }
        catch (Exception e) { Log.Write($"could not build the Insights index (will try again next launch): {e.Message}"); }
    });

    // MARK: frames

    public long Insert(Frame f)
    {
        lock (gate)
        {
            using var cmd = Cmd("INSERT INTO frames (timestamp, app_name, window_name, focused, snapshot_path, text, text_source, hash, browser_url, blocked, display) VALUES (?,?,?,?,?,?,?,?,?,?,?); SELECT last_insert_rowid();",
                f.Timestamp, f.AppName, f.WindowName, f.Focused ? 1 : 0, f.SnapshotPath, f.Text, f.TextSource, f.Hash, f.BrowserUrl, f.Blocked ? 1 : 0, f.Display);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    const string FullCols = "f.id, f.timestamp, f.app_name, f.window_name, f.focused, f.snapshot_path, f.text, f.text_source, f.hash, f.browser_url, f.display, f.video_path, f.video_frame, f.video_time";
    const string StillCols = "f.id, f.timestamp, f.app_name, f.window_name, f.focused, f.snapshot_path, f.text, f.text_source, f.hash, f.browser_url, '', '', 0, 0.0";

    public Frame? FrameById(long id) => Frames($"SELECT {cols} FROM frames f WHERE f.id = ?", id).FirstOrDefault();

    public (List<Frame> Rows, int Total) Search(SearchQuery s)
    {
        var where = new List<string>();
        var args = new List<object>();
        var from = "frames f";
        if (s.Q != null && FtsQuery(s.Q) is string match)
        {
            from = "frames_fts JOIN frames f ON f.id = frames_fts.rowid";
            where.Add("frames_fts MATCH ?"); args.Add(match);
        }
        if (!string.IsNullOrEmpty(s.AppName)) { where.Add("f.app_name LIKE ?"); args.Add($"%{s.AppName}%"); }
        if (!string.IsNullOrEmpty(s.WindowName)) { where.Add("f.window_name LIKE ?"); args.Add($"%{s.WindowName}%"); }
        if (s.StartTime != null) { where.Add("f.timestamp >= ?"); args.Add(s.StartTime); }
        if (s.EndTime != null) { where.Add("f.timestamp <= ?"); args.Add(s.EndTime); }
        var whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        var total = (int)ScalarLong($"SELECT COUNT(*) FROM {from} {whereSql}", args.ToArray());
        var rows = Frames($"SELECT {cols} FROM {from} {whereSql} ORDER BY f.timestamp DESC LIMIT ? OFFSET ?",
                          args.Concat(new object[] { s.Limit, s.Offset }).ToArray());
        return (rows, total);
    }

    public List<(string App, int Frames)> Apps(string? since)
    {
        lock (gate)
        {
            using var cmd = since == null
                ? Cmd("SELECT app_name, COUNT(*) FROM frames GROUP BY app_name ORDER BY 2 DESC")
                : Cmd("SELECT app_name, COUNT(*) FROM frames WHERE timestamp >= ? GROUP BY app_name ORDER BY 2 DESC", since);
            using var r = cmd.ExecuteReader();
            var out_ = new List<(string, int)>();
            while (r.Read()) out_.Add((r.GetString(0), (int)r.GetInt64(1)));
            return out_;
        }
    }

    /// Web pages seen lately, newest first, one row per address: for picking sites to block.
    public List<RecentPage> RecentPages(int limit = 3000)
    {
        lock (gate)
        {
            // SQLite fills the bare window_name from the row that has the MAX(timestamp).
            using var cmd = Cmd("SELECT browser_url, window_name, MAX(timestamp) FROM frames WHERE browser_url LIKE 'http%' " +
                                "GROUP BY browser_url ORDER BY 3 DESC LIMIT ?", limit);
            using var r = cmd.ExecuteReader();
            var out_ = new List<RecentPage>();
            while (r.Read()) out_.Add(new RecentPage(r.GetString(0), r.GetString(1), r.GetString(2)));
            return out_;
        }
    }

    public (int Count, string? Last) Stats()
    {
        var n = (int)ScalarLong("SELECT COUNT(*) FROM frames");
        var last = Frames($"SELECT {cols} FROM frames f ORDER BY id DESC LIMIT 1").FirstOrDefault()?.Timestamp;
        return (n, last);
    }

    /// Turn free text into a safe FTS5 query: every word becomes a quoted prefix term.
    public static string? FtsQuery(string raw)
    {
        var words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Replace("\"", "")).Where(w => w.Length > 0).ToList();
        return words.Count == 0 ? null : string.Join(" ", words.Select(w => $"\"{w}\"*"));
    }

    // MARK: audio

    public long InsertChunk(string filePath, string timestamp, string device, bool isInput, string status = "pending")
    {
        lock (gate)
        {
            using var cmd = Cmd("INSERT INTO audio_chunks (file_path, timestamp, device, is_input, status) VALUES (?,?,?,?,?); SELECT last_insert_rowid();",
                filePath, timestamp, device, isInput ? 1 : 0, status);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// Oldest pending chunk that has not failed too often. Clips from the last 15 minutes go first,
    /// so a long Re-transcribe never holds up what was just recorded.
    public AudioChunk? NextPendingChunk(int maxAttempts = 3) =>
        Chunks("WHERE status = 'pending' AND attempts < ? ORDER BY timestamp >= ? DESC, timestamp ASC LIMIT 1",
               maxAttempts, Time.Iso(DateTime.UtcNow.AddMinutes(-15))).FirstOrDefault();

    public AudioChunk? ChunkById(long id) => Chunks("WHERE id = ?", id).FirstOrDefault();

    public void MarkChunk(long id, string status, string? reason = null)
    {
        lock (gate) MarkChunkLocked(id, status, reason);
    }

    void MarkChunkLocked(long id, string status, string? reason)
    {
        // A "pending" retry that has used up its attempts becomes "failed".
        using var cmd = Cmd("""
            UPDATE audio_chunks SET attempts = attempts + 1, failure_reason = ?,
                status = CASE WHEN ? = 'pending' AND attempts + 1 >= 3 THEN 'failed' ELSE ? END
            WHERE id = ?
            """, (object?)reason ?? DBNull.Value, status, status, id);
        cmd.ExecuteNonQuery();
    }

    /// Insert all segments of one chunk and mark it transcribed, in one transaction. Lines an earlier
    /// run left for the same chunk (Re-transcribe) are replaced. `engine` names the model that wrote them.
    /// Returns false (and saves nothing) when the clip was deleted while it was being transcribed.
    public bool SaveTranscripts(AudioChunk chunk, IReadOnlyList<Segment> segments, string engine = "whisper.cpp")
    {
        lock (gate)
        {
            using var tx = conn.BeginTransaction();
            using (var exists = Cmd("SELECT COUNT(*) FROM audio_chunks WHERE id = ?", chunk.Id))
            {
                exists.Transaction = tx;
                if (Convert.ToInt64(exists.ExecuteScalar()) == 0)
                {
                    // Deleted (Delete Recordings, or kept-for-days cleanup) after it was picked up: do not bring its words back.
                    tx.Rollback();
                    return false;
                }
            }
            using (var old = Cmd("DELETE FROM audio_transcriptions WHERE audio_chunk_id = ?", chunk.Id))
            {
                old.Transaction = tx;
                old.ExecuteNonQuery();
            }
            var start = Time.ParseIso(chunk.Timestamp) ?? DateTime.UtcNow;
            foreach (var s in segments)
            {
                using var cmd = Cmd("INSERT INTO audio_transcriptions (audio_chunk_id, timestamp, start_time, end_time, transcription, device, is_input, engine) VALUES (?,?,?,?,?,?,?,?)",
                    chunk.Id, Time.Iso(start.AddSeconds(s.Start)), s.Start, s.End, s.Text, chunk.Device, chunk.IsInput ? 1 : 0, engine);
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            using (var mark = Cmd("UPDATE audio_chunks SET attempts = attempts + 1, failure_reason = NULL, status = ? WHERE id = ?",
                                  segments.Count == 0 ? "silent" : "transcribed", chunk.Id))
            {
                mark.Transaction = tx;
                mark.ExecuteNonQuery();
            }
            tx.Commit();
            return true;
        }
    }

    public (List<Transcript> Rows, int Total) SearchAudio(SearchQuery s, int fetch)
    {
        var where = new List<string>();
        var args = new List<object>();
        var from = "audio_transcriptions t JOIN audio_chunks c ON c.id = t.audio_chunk_id";
        if (s.Q != null && FtsQuery(s.Q) is string match)
        {
            from = "audio_fts JOIN audio_transcriptions t ON t.id = audio_fts.rowid JOIN audio_chunks c ON c.id = t.audio_chunk_id";
            where.Add("audio_fts MATCH ?"); args.Add(match);
        }
        if (s.StartTime != null) { where.Add("t.timestamp >= ?"); args.Add(s.StartTime); }
        if (s.EndTime != null) { where.Add("t.timestamp <= ?"); args.Add(s.EndTime); }
        var whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        var total = (int)ScalarLong($"SELECT COUNT(*) FROM {from} {whereSql}", args.ToArray());
        lock (gate)
        {
            using var cmd = Cmd($"""
                SELECT t.id, t.audio_chunk_id, t.timestamp, t.start_time, t.end_time, t.transcription, t.device, t.is_input, c.file_path
                FROM {from} {whereSql} ORDER BY t.timestamp DESC LIMIT ?
                """, args.Append(fetch).ToArray());
            using var r = cmd.ExecuteReader();
            var rows = new List<Transcript>();
            while (r.Read())
                rows.Add(new Transcript(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4),
                                        r.GetString(5), r.GetString(6), r.GetInt64(7) != 0, r.GetString(8)));
            return (rows, total);
        }
    }

    /// Audio clips recorded since `from` that Re-transcribe would run again, and their total length in seconds
    /// (clips are 30 s or shorter, so this counts 30 s each).
    public (int Clips, double Seconds) RetranscribeCount(DateTime from, double clipSeconds)
    {
        var n = (int)ScalarLong($"SELECT COUNT(*) FROM audio_chunks WHERE timestamp >= ? AND status IN ({RedoStatuses})", Time.Iso(from));
        return (n, n * clipSeconds);
    }

    /// Clips Re-transcribe runs again: ones with words, and ones that failed. Clips found silent stay
    /// silent (a new model rarely finds speech where the old one found none, and a "pending" silent clip
    /// would look like speech to Meetings and Insights until it is done).
    const string RedoStatuses = "'transcribed', 'failed'";

    /// Re-transcribe: queue the clips recorded since `from` again. Their old lines stay searchable
    /// until the new ones replace them for good (SaveTranscripts). Meetings in that time keep the notes
    /// they have: the new lines do not make new notes (nor send the transcript to an AI provider again).
    /// Returns how many clips were queued.
    public int RequeueAudio(DateTime from)
    {
        lock (gate)
        {
            using var tx = conn.BeginTransaction();
            void InTx(string sql, params object[] args)
            {
                using var c = Cmd(sql, args);
                c.Transaction = tx;
                c.ExecuteNonQuery();
            }
            var since = Time.Iso(from);
            InTx("UPDATE meetings SET redone = 1 WHERE end_time >= ?", since);
            // Search by Meaning indexes each clip once, in id order: forget the vectors made from the old text
            // and move its audio cursor back, so the requeued clips are indexed again once they are redone.
            var first = $"(SELECT MIN(id) FROM audio_chunks WHERE timestamp >= ? AND status IN ({RedoStatuses}))";
            InTx($"DELETE FROM meaning_moments WHERE kind = 'audio' AND first_id >= {first}", since);
            InTx($"UPDATE meaning_state SET value = CAST({first} - 1 AS TEXT) WHERE key = '{ChunkCursor}' AND CAST(value AS INTEGER) >= {first}",
                 since, since);
            using var cmd = Cmd("UPDATE audio_chunks SET status = 'pending', attempts = 0, failure_reason = NULL " +
                                $"WHERE timestamp >= ? AND status IN ({RedoStatuses})", Time.Iso(from));
            cmd.Transaction = tx;
            var n = cmd.ExecuteNonQuery();
            tx.Commit();
            return n;
        }
    }

    public (int Chunks, int Pending, int Segments) AudioStats() =>
        ((int)ScalarLong("SELECT COUNT(*) FROM audio_chunks"),
         (int)ScalarLong("SELECT COUNT(*) FROM audio_chunks WHERE status = 'pending'"),
         (int)ScalarLong("SELECT COUNT(*) FROM audio_transcriptions"));

    List<AudioChunk> Chunks(string tail, params object[] args)
    {
        lock (gate)
        {
            using var cmd = Cmd($"SELECT id, file_path, timestamp, device, is_input, status FROM audio_chunks {tail}", args);
            using var r = cmd.ExecuteReader();
            var out_ = new List<AudioChunk>();
            while (r.Read())
                out_.Add(new AudioChunk(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetString(5)));
            return out_;
        }
    }

    // MARK: helpers

    public void Exec(string sql)
    {
        lock (gate)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// Positional "?" parameters, like the Swift wrapper. Call with `gate` held.
    SqliteCommand Cmd(string sql, params object?[] args)
    {
        var cmd = conn.CreateCommand();
        var parts = sql.Split('?');
        var sb = new System.Text.StringBuilder(parts[0]);
        for (int i = 1; i < parts.Length; i++) sb.Append("$p").Append(i).Append(parts[i]);
        cmd.CommandText = sb.ToString();
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$p" + (i + 1), args[i] ?? DBNull.Value);
        return cmd;
    }

    long ScalarLong(string sql, params object[] args)
    {
        lock (gate)
        {
            try
            {
                using var cmd = Cmd(sql, args);
                return cmd.ExecuteScalar() is long n ? n : 0;
            }
            catch (SqliteException e) { Log.Write($"sql error: {e.Message}"); return 0; }
        }
    }

    List<Frame> Frames(string sql, params object[] args)
    {
        lock (gate)
        {
            try
            {
                using var cmd = Cmd(sql, args);
                using var r = cmd.ExecuteReader();
                var out_ = new List<Frame>();
                while (r.Read())
                    out_.Add(new Frame(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0,
                                       r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt64(8), r.GetString(9),
                                       Display: r.GetString(10), VideoPath: r.GetString(11), VideoFrame: (int)r.GetInt64(12), VideoTime: r.GetDouble(13)));
                return out_;
            }
            catch (SqliteException e) { Log.Write($"sql error: {e.Message}"); return new(); }
        }
    }

    List<string> Strings(string sql, params object[] args)
    {
        lock (gate)
        {
            using var cmd = Cmd(sql, args);
            using var r = cmd.ExecuteReader();
            var out_ = new List<string>();
            while (r.Read()) out_.Add(r.IsDBNull(0) ? "" : r.GetString(0));
            return out_;
        }
    }

    void Run(string sql, params object[] args)
    {
        lock (gate)
        {
            try { using var cmd = Cmd(sql, args); cmd.ExecuteNonQuery(); }
            catch (SqliteException e) { Log.Write($"sql error: {e.Message}"); }
        }
    }
}
