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

/// The `meaning_moments` table: one row per moment with its vector, and `meaning_state` for how
/// far the indexer got. Same schema as the Mac app (Meaning.swift).
public sealed partial class Database
{
    const string MeaningCols = "id, kind, timestamp, end_time, first_id, app_name, window_name, browser_url, text";

    void MigrateMeaning()
    {
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
            text TEXT NOT NULL DEFAULT '',
            model TEXT NOT NULL,
            vector BLOB NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_meaning_ts ON meaning_moments(timestamp);
        CREATE INDEX IF NOT EXISTS idx_meaning_first ON meaning_moments(kind, first_id);
        CREATE TABLE IF NOT EXISTS meaning_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """);
    }

    public string? MeaningState(string key) => Strings("SELECT value FROM meaning_state WHERE key = ?", key).FirstOrDefault();

    public void SetMeaningState(string key, string value) =>
        Run("INSERT INTO meaning_state (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, value);

    long Cursor(string key) => long.TryParse(MeaningState(key), out var n) ? n : 0;

    public const string FrameCursor = "frame_cursor", ChunkCursor = "chunk_cursor";

    /// Vectors from another model can't be compared with this one's: start over when it changed.
    public void MeaningUseModel(string model)
    {
        if (MeaningState("model") == model) return;
        ClearMeaning();
        SetMeaningState("model", model);
        Log.Write($"search by meaning: indexing with {model}");
    }

    /// Forget every vector (Search by Meaning turned off, or a new model).
    public void ClearMeaning()
    {
        Run("DELETE FROM meaning_moments");
        Run("DELETE FROM meaning_state");
    }

    /// Screenshots after `after` (id) taken before `before` (ISO), oldest first, without the picture.
    public List<MomentFrame> MeaningFrames(long after, string before, int limit)
    {
        lock (gate)
        {
            using var cmd = Cmd("SELECT id, timestamp, app_name, window_name, browser_url, text FROM frames WHERE id > ? AND timestamp < ? ORDER BY id LIMIT ?",
                                after, before, limit);
            using var r = cmd.ExecuteReader();
            var out_ = new List<MomentFrame>();
            while (r.Read()) out_.Add(new MomentFrame(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));
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

    /// Store a moment and move the cursor past it, together. Nothing is stored when its
    /// screenshots or clip were deleted while the vector was being made.
    public void SaveMoment(Moment m, string model, byte[] vector, string cursorKey, long cursor)
    {
        lock (gate)
        {
            using var tx = conn.BeginTransaction();
            var (exists, ids) = m.IsAudio
                ? ("EXISTS (SELECT 1 FROM audio_chunks WHERE id = ?)", new object[] { m.FirstId })
                : ("EXISTS (SELECT 1 FROM frames WHERE id = ?) AND EXISTS (SELECT 1 FROM frames WHERE id = ?)", new object[] { m.FirstId, m.LastId });
            var values = new object[] { m.IsAudio ? "audio" : "screen", m.Start, m.End, m.FirstId, m.LastId, m.AppName, m.WindowName, m.Url, m.Text, model, vector };
            using (var cmd = Cmd($"""
                INSERT INTO meaning_moments (kind, timestamp, end_time, first_id, last_id, app_name, window_name, browser_url, text, model, vector)
                SELECT ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ? WHERE {exists}
                """, values.Concat(ids).ToArray()))
            {
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            using (var cmd = Cmd("INSERT INTO meaning_state (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                                 cursorKey, cursor.ToString()))
            {
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// Every stored vector of `model` in the time range, without the text (that is read for the best few only).
    public void ScanMoments(string model, string? start, string? end, Action<long, bool, string, string, string, string, byte[]> each)
    {
        var where = new List<string> { "model = ?" };
        var args = new List<object> { model };
        if (start != null) { where.Add("end_time >= ?"); args.Add(start); }
        if (end != null) { where.Add("timestamp <= ?"); args.Add(end); }
        lock (gate)
        {
            using var cmd = Cmd($"SELECT id, kind, app_name, window_name, browser_url, timestamp, vector FROM meaning_moments WHERE {string.Join(" AND ", where)}",
                                args.ToArray());
            using var r = cmd.ExecuteReader();
            while (r.Read())
                each(r.GetInt64(0), r.GetString(1) == "audio", r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), (byte[])r.GetValue(6));
        }
    }

    public Dictionary<long, MeaningHit> MomentsById(IEnumerable<long> ids)
    {
        var list = ids.ToList();
        var out_ = new Dictionary<long, MeaningHit>();
        if (list.Count == 0) return out_;
        lock (gate)
        {
            using var cmd = Cmd($"SELECT {MeaningCols} FROM meaning_moments WHERE id IN ({string.Join(",", list)})");
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
        long total = ScalarLong("SELECT COUNT(*) FROM frames") + ScalarLong("SELECT COUNT(*) FROM audio_chunks");
        long done = ScalarLong("SELECT COUNT(*) FROM frames WHERE id <= ?", fc) + ScalarLong("SELECT COUNT(*) FROM audio_chunks WHERE id <= ?", cc);
        // Clips after one Whisper has not done yet wait for it, so they don't count as behind.
        long waiting = ScalarLong("SELECT COUNT(*) FROM frames WHERE id > ? AND timestamp < ?", fc, settled)
            + ScalarLong("""
                SELECT COUNT(*) FROM audio_chunks WHERE id > ? AND status = 'transcribed'
                AND id < COALESCE((SELECT MIN(id) FROM audio_chunks WHERE id > ? AND status = 'pending'), 9223372036854775807)
                """, cc, cc);
        return new MeaningProgress(ScalarLong("SELECT COUNT(*) FROM meaning_moments"), done, total, waiting);
    }

    /// Retention and Delete Recordings: vectors go with the screenshots they were made from.
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
