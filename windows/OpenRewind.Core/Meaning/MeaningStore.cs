namespace OpenRewind;

/// A moment found by Search by Meaning, best first. Times are ISO (UTC).
public sealed record MeaningHit(long Id, bool IsAudio, string Timestamp, string End, long FirstId, string AppName,
                                string WindowName, string Url, string Text, float Score);

/// How far the index has got.
public sealed record MeaningProgress(long Moments, long Done, long Total, long Waiting)
{
    public bool UpToDate => Waiting == 0;
    public int Percent => Total == 0 ? 100 : (int)Math.Min(99, Done * 100 / Total);
}

/// One stored vector as a search reads it. `vector` is only valid during the call.
public delegate void MomentVisitor(long id, bool audio, string app, string window, string url, ReadOnlySpan<byte> vector);

/// Search by Meaning storage. Same schema as the Mac app (Meaning.swift):
/// - `meaning_moments`: one row per moment with its text, read only for the few results shown.
/// - `meaning_vectors`: the same ids with the vector and the columns a search filters on, kept narrow
///   so a search reads little, page by page.
/// - `meaning_state`: how far the indexer got, and with which model.
public sealed partial class Database
{
    /// Rows a search reads per page. The database lock is let go between pages, so recording never waits long.
    public const int ScanPage = 4000;

    void MigrateMeaning()
    {
        // A test build of this feature kept the vector in meaning_moments. Start that index over.
        if (ScalarLong("SELECT COUNT(*) FROM pragma_table_info('meaning_moments') WHERE name = 'vector'") > 0)
            Exec("DROP TABLE meaning_moments; DROP TABLE IF EXISTS meaning_state;");
        Exec("""
        CREATE TABLE IF NOT EXISTS meaning_moments (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL CHECK (kind IN ('screen', 'audio')),
            timestamp TEXT NOT NULL,
            end_time TEXT NOT NULL,
            first_id INTEGER NOT NULL,
            last_id INTEGER NOT NULL,
            app_name TEXT NOT NULL DEFAULT '',
            window_name TEXT NOT NULL DEFAULT '',
            browser_url TEXT NOT NULL DEFAULT '',
            text TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_meaning_ts ON meaning_moments(timestamp);
        CREATE INDEX IF NOT EXISTS idx_meaning_first ON meaning_moments(kind, first_id);
        CREATE TABLE IF NOT EXISTS meaning_vectors (
            id INTEGER PRIMARY KEY,
            kind TEXT NOT NULL,
            timestamp TEXT NOT NULL,
            end_time TEXT NOT NULL,
            app_name TEXT NOT NULL DEFAULT '',
            window_name TEXT NOT NULL DEFAULT '',
            browser_url TEXT NOT NULL DEFAULT '',
            model TEXT NOT NULL,
            vector BLOB NOT NULL
        );
        CREATE TRIGGER IF NOT EXISTS meaning_moments_ad AFTER DELETE ON meaning_moments BEGIN
            DELETE FROM meaning_vectors WHERE id = old.id;
        END;
        CREATE TABLE IF NOT EXISTS meaning_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """);
    }

    /// True when this database has the Search by Meaning tables (an older app may not have made them yet).
    public bool HasMeaningIndex() => ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meaning_vectors'") > 0;

    public string? MeaningState(string key) => Strings("SELECT value FROM meaning_state WHERE key = ?", key).FirstOrDefault();

    const string StateUpsert = "INSERT INTO meaning_state (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value";

    /// `keep`: checked under the database lock; false (the indexer was stopped meanwhile) writes nothing.
    public void SetMeaningState(string key, string value, Func<bool>? keep = null)
    {
        lock (gate)
        {
            if (keep != null && !keep()) return;
            using var cmd = Cmd(StateUpsert, key, value);
            cmd.ExecuteNonQuery();
        }
    }

    long Cursor(string key) => long.TryParse(MeaningState(key), out var n) ? n : 0;

    public const string FrameCursor = "frame_cursor", ChunkCursor = "chunk_cursor";

    /// Vectors from another model can't be compared with this one's: start over when it changed.
    public void MeaningUseModel(string model, Func<bool>? keep = null)
    {
        if (MeaningState("model") == model) return;
        lock (gate)
        {
            if (keep != null && !keep()) return;
            ClearMeaning();
            using var cmd = Cmd(StateUpsert, "model", model);
            cmd.ExecuteNonQuery();
        }
        Log.Write($"search by meaning: indexing with {model}");
    }

    /// Forget every vector and moment (Search by Meaning turned off, or a new model).
    public void ClearMeaning()
    {
        lock (gate)
        {
            Run("DELETE FROM meaning_vectors");
            Run("DELETE FROM meaning_moments");
            Run("DELETE FROM meaning_state");
        }
    }

    /// Screenshots after `after` (id) taken before `before` (ISO), oldest first, without the picture.
    public List<MomentFrame> MeaningFrames(long after, string before, int limit)
    {
        lock (gate)
        {
            using var cmd = Cmd("SELECT id, timestamp, app_name, window_name, browser_url, text, blocked FROM frames WHERE id > ? AND timestamp < ? ORDER BY id LIMIT ?",
                                after, before, limit);
            using var r = cmd.ExecuteReader();
            var out_ = new List<MomentFrame>();
            while (r.Read()) out_.Add(new MomentFrame(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6) != 0));
            return out_;
        }
    }

    /// Transcripts of the clips after `after` (id), up to the first clip still waiting for Whisper.
    /// `Done` is the last clip id they account for.
    public (List<MomentSegment> Segments, long Done) MeaningAudio(long after, int limit)
    {
        lock (gate)
        {
            var ready = new List<long>();
            long done = after;
            using (var cmd = Cmd("SELECT id, status FROM audio_chunks WHERE id > ? ORDER BY id LIMIT ?", after, limit))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    if (r.GetString(1) == "pending") break;
                    done = r.GetInt64(0);
                    if (r.GetString(1) == "transcribed") ready.Add(done);
                }
            var segs = new List<MomentSegment>();
            if (ready.Count == 0) return (segs, done);
            using (var cmd = Cmd($"""
                SELECT t.audio_chunk_id, t.timestamp, t.transcription, c.is_input FROM audio_transcriptions t
                JOIN audio_chunks c ON c.id = t.audio_chunk_id
                WHERE t.audio_chunk_id IN ({string.Join(",", ready)}) ORDER BY t.audio_chunk_id, t.start_time, t.id
                """))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) segs.Add(new MomentSegment(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0));
            return (segs, done);
        }
    }

    /// Store a moment and move the cursor past it, together. The moment is left out (the cursor still
    /// moves) when its screenshots or clip were deleted while the vector was being made: its first or
    /// last frame is gone, or fewer frames are left between them than it was made from. With `keep`
    /// false (the indexer was stopped meanwhile) nothing is written.
    public void SaveMoment(Moment m, string model, byte[] vector, string cursorKey, long cursor, Func<bool>? keep = null)
    {
        lock (gate)
        {
            if (keep != null && !keep()) return;
            using var tx = conn.BeginTransaction();
            var (exists, ids) = m.IsAudio
                ? ("EXISTS (SELECT 1 FROM audio_chunks WHERE id = ?)", new object[] { m.FirstId })
                : m.Frames > 0
                ? ("EXISTS (SELECT 1 FROM frames WHERE id = ?) AND EXISTS (SELECT 1 FROM frames WHERE id = ?) AND (SELECT COUNT(*) FROM frames WHERE id BETWEEN ? AND ?) = ?",
                   new object[] { m.FirstId, m.LastId, m.FirstId, m.LastId, m.Frames })
                : ("EXISTS (SELECT 1 FROM frames WHERE id = ?) AND EXISTS (SELECT 1 FROM frames WHERE id = ?)", new object[] { m.FirstId, m.LastId });
            var kind = m.IsAudio ? "audio" : "screen";
            int added;
            using (var cmd = Cmd($"""
                INSERT INTO meaning_moments (kind, timestamp, end_time, first_id, last_id, app_name, window_name, browser_url, text)
                SELECT ?, ?, ?, ?, ?, ?, ?, ?, ? WHERE {exists}
                """, new object[] { kind, m.Start, m.End, m.FirstId, m.LastId, m.AppName, m.WindowName, m.Url, m.Text }.Concat(ids).ToArray()))
            {
                cmd.Transaction = tx;
                added = cmd.ExecuteNonQuery();
            }
            if (added > 0)
                using (var cmd = Cmd("""
                    INSERT INTO meaning_vectors (id, kind, timestamp, end_time, app_name, window_name, browser_url, model, vector)
                    VALUES (last_insert_rowid(), ?, ?, ?, ?, ?, ?, ?, ?)
                    """, kind, m.Start, m.End, m.AppName, m.WindowName, m.Url, model, vector))
                {
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                }
            using (var cmd = Cmd(StateUpsert, cursorKey, cursor.ToString()))
            {
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// Every stored vector of `model` in the time range, without the text (that is read for the best
    /// few only). Read a page at a time; the lock is let go between pages and `each` runs outside it.
    public void ScanMoments(string model, string? start, string? end, MomentVisitor each)
    {
        var where = "id > ? AND model = ?";
        if (start != null) where += " AND end_time >= ?";
        if (end != null) where += " AND timestamp <= ?";
        var sql = $"SELECT id, kind, app_name, window_name, browser_url, vector FROM meaning_vectors WHERE {where} ORDER BY id LIMIT {ScanPage}";
        var ids = new long[ScanPage];
        var audio = new bool[ScanPage];
        var meta = new (string App, string Window, string Url)[ScanPage];
        var vecs = Array.Empty<byte>();
        var lens = new int[ScanPage];
        int dim = 0;
        long after = 0;
        while (true)
        {
            int n = 0;
            lock (gate)
            {
                var args = new List<object> { after, model };
                if (start != null) args.Add(start);
                if (end != null) args.Add(end);
                using var cmd = Cmd(sql, args.ToArray());
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var len = (int)r.GetBytes(5, 0, null, 0, 0);
                    if (dim == 0) { dim = Math.Max(1, len); vecs = new byte[ScanPage * dim]; }
                    ids[n] = r.GetInt64(0);
                    audio[n] = r.GetString(1) == "audio";
                    meta[n] = (r.GetString(2), r.GetString(3), r.GetString(4));
                    lens[n] = Math.Min(len, dim);
                    if (len == dim) r.GetBytes(5, 0, vecs, n * dim, dim);
                    else lens[n] = -1;   // not this model's size: never matches
                    n++;
                }
            }
            for (int i = 0; i < n; i++)
            {
                var v = lens[i] < 0 ? ReadOnlySpan<byte>.Empty : vecs.AsSpan(i * dim, lens[i]);
                each(ids[i], audio[i], meta[i].App, meta[i].Window, meta[i].Url, v);
            }
            if (n < ScanPage) return;
            after = ids[n - 1];
        }
    }

    public Dictionary<long, MeaningHit> MomentsById(IEnumerable<long> ids)
    {
        var list = ids.ToList();
        var out_ = new Dictionary<long, MeaningHit>();
        if (list.Count == 0) return out_;
        lock (gate)
        {
            using var cmd = Cmd($"SELECT id, kind, timestamp, end_time, first_id, app_name, window_name, browser_url, text FROM meaning_moments WHERE id IN ({string.Join(",", list)})");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                out_[r.GetInt64(0)] = new MeaningHit(r.GetInt64(0), r.GetString(1) == "audio", r.GetString(2), r.GetString(3), r.GetInt64(4),
                                                     r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), 0);
            return out_;
        }
    }

    public MeaningProgress MeaningProgress(DateTime nowUtc)
    {
        long fc = Cursor(FrameCursor), cc = Cursor(ChunkCursor);
        var settled = Time.Iso(nowUtc - MeaningService.Settle - TimeSpan.FromMinutes(1));
        // Done: everything up to the cursors.
        long total = ScalarLong("SELECT COUNT(*) FROM frames") + ScalarLong("SELECT COUNT(*) FROM audio_chunks");
        long left = ScalarLong("SELECT COUNT(*) FROM frames WHERE id > ?", fc) + ScalarLong("SELECT COUNT(*) FROM audio_chunks WHERE id > ?", cc);
        // Clips after one Whisper has not done yet wait for it, so they don't count as behind.
        long waiting = ScalarLong("SELECT COUNT(*) FROM frames WHERE id > ? AND timestamp < ?", fc, settled)
            + ScalarLong("""
                SELECT COUNT(*) FROM audio_chunks WHERE id > ? AND status = 'transcribed'
                AND id < COALESCE((SELECT MIN(id) FROM audio_chunks WHERE id > ? AND status = 'pending'), 9223372036854775807)
                """, cc, cc);
        return new MeaningProgress(ScalarLong("SELECT COUNT(*) FROM meaning_vectors"), total - left, total, waiting);
    }

    /// Retention and Delete Recordings: vectors go with the screenshots they were made from
    /// (the trigger meaning_moments_ad removes the vector rows).
    void ForgetScreenMoments(DateTime? from, DateTime? to)
    {
        var parts = new List<string> { "kind = 'screen'" }; var args = new List<object>();
        if (from is DateTime f) { parts.Add("end_time >= ?"); args.Add(Time.Iso(f)); }
        if (to is DateTime t) { parts.Add("timestamp < ?"); args.Add(Time.Iso(t)); }
        Run($"DELETE FROM meaning_moments WHERE {string.Join(" AND ", parts)}", args.ToArray());
    }

    /// ...and with the audio clips (`chunkWhere` selects the clips being deleted).
    void ForgetAudioMoments(string chunkWhere, object[] args) =>
        Run($"DELETE FROM meaning_moments WHERE kind = 'audio' AND first_id IN (SELECT id FROM audio_chunks {chunkWhere})", args);
}
