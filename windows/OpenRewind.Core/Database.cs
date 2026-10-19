using Microsoft.Data.Sqlite;

namespace OpenRewind;

/// BrowserUrl is the address of the browser tab in this frame, or "".
public record Frame(long Id, string Timestamp, string AppName, string WindowName, bool Focused,
                    string SnapshotPath, string Text, string TextSource, long Hash, string BrowserUrl = "");

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
            foreach (var (table, col) in new[] { ("frames", "snapshot_path"), ("audio_chunks", "file_path") })
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
        CREATE INDEX IF NOT EXISTS idx_frames_ts ON frames(timestamp);
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
        // Added later: older databases do not have this column yet.
        if (ScalarLong("SELECT COUNT(*) FROM pragma_table_info('frames') WHERE name = 'browser_url'") == 0)
            Exec("ALTER TABLE frames ADD COLUMN browser_url TEXT NOT NULL DEFAULT ''");
        // Insights reads app, window and address in time order; with this index it never touches the
        // screen text. Created once (a few seconds on a big database). The Mac app adds the same index.
        Exec("CREATE INDEX IF NOT EXISTS idx_frames_activity ON frames(timestamp, app_name, window_name, browser_url)");
    }

    // MARK: frames

    public long Insert(Frame f)
    {
        lock (gate)
        {
            using var cmd = Cmd("INSERT INTO frames (timestamp, app_name, window_name, focused, snapshot_path, text, text_source, hash, browser_url) VALUES (?,?,?,?,?,?,?,?,?); SELECT last_insert_rowid();",
                f.Timestamp, f.AppName, f.WindowName, f.Focused ? 1 : 0, f.SnapshotPath, f.Text, f.TextSource, f.Hash, f.BrowserUrl);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    const string Cols = "f.id, f.timestamp, f.app_name, f.window_name, f.focused, f.snapshot_path, f.text, f.text_source, f.hash, f.browser_url";

    public Frame? FrameById(long id) => Frames($"SELECT {Cols} FROM frames f WHERE f.id = ?", id).FirstOrDefault();

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
        var rows = Frames($"SELECT {Cols} FROM {from} {whereSql} ORDER BY f.timestamp DESC LIMIT ? OFFSET ?",
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
        var last = Frames($"SELECT {Cols} FROM frames f ORDER BY id DESC LIMIT 1").FirstOrDefault()?.Timestamp;
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

    /// Oldest pending chunk that has not failed too often.
    public AudioChunk? NextPendingChunk(int maxAttempts = 3) =>
        Chunks("WHERE status = 'pending' AND attempts < ? ORDER BY timestamp ASC LIMIT 1", maxAttempts).FirstOrDefault();

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

    /// Insert all segments of one chunk and mark it transcribed, in one transaction.
    public void SaveTranscripts(AudioChunk chunk, IReadOnlyList<Segment> segments)
    {
        lock (gate)
        {
            using var tx = conn.BeginTransaction();
            var start = Time.ParseIso(chunk.Timestamp) ?? DateTime.UtcNow;
            foreach (var s in segments)
            {
                using var cmd = Cmd("INSERT INTO audio_transcriptions (audio_chunk_id, timestamp, start_time, end_time, transcription, device, is_input) VALUES (?,?,?,?,?,?,?)",
                    chunk.Id, Time.Iso(start.AddSeconds(s.Start)), s.Start, s.End, s.Text, chunk.Device, chunk.IsInput ? 1 : 0);
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
                                       r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt64(8), r.GetString(9)));
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
