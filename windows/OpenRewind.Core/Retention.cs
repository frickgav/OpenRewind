namespace OpenRewind;

/// How long to keep recordings. `Forever` means: only delete when the user asks.
public enum KeepFor { Day, Week, Month, Year, Forever }

public static class KeepForInfo
{
    public static string Title(this KeepFor k) => k switch
    {
        KeepFor.Day => "1 Day",
        KeepFor.Week => "1 Week",
        KeepFor.Month => "1 Month",
        KeepFor.Year => "1 Year",
        _ => "Until I Delete Them",
    };

    public static double? Seconds(this KeepFor k) => k switch
    {
        KeepFor.Day => 86_400,
        KeepFor.Week => 7 * 86_400,
        KeepFor.Month => 30 * 86_400,
        KeepFor.Year => 365 * 86_400,
        _ => null,
    };

    /// "day", "week", ... as the Mac app and the CLI spell them.
    public static string Raw(this KeepFor k) => k.ToString().ToLowerInvariant();

    public static KeepFor? FromRaw(string? s) =>
        Enum.TryParse<KeepFor>(s, ignoreCase: true, out var k) ? k : null;
}

/// Deletes screen and audio recordings (files + rows) inside a time range.
public sealed partial class Database
{
    /// Delete frames with `from <= timestamp < to`. null = open end. Returns frames deleted.
    /// A video left with no frames is deleted; one that still has frames outside the range is marked
    /// for the compactor to write again without the deleted ones (see VideoCompactor.RunOnce).
    /// The rows and the marks change in one transaction, so a crash can't leave deleted pictures in a
    /// video nobody will rewrite; files are deleted only after it commits.
    public int DeleteFrames(DateTime? from, DateTime? to)
    {
        var (where, args) = Range("timestamp", from, to);
        return DeleteFrames(where, args, from, to);
    }

    /// One screenshot (Delete on the timeline): its row, text, picture and Search by Meaning moment, and what
    /// was made from its moment (the time until the next screenshot): stars, meeting titles, the day's recap.
    public int DeleteFrame(long id)
    {
        var ts = Strings("SELECT timestamp FROM frames WHERE id = ?", id).FirstOrDefault();
        if (ts == null || Time.ParseIso(ts) is not DateTime t) return 0;
        var next = Time.ParseIso(Strings("SELECT MIN(timestamp) FROM frames WHERE timestamp > ?", ts).FirstOrDefault() ?? "");
        var now = DateTime.UtcNow;
        return DeleteFrames("WHERE id = ?", new object[] { id }, t, next ?? (now > t.AddSeconds(1) ? now : t.AddSeconds(1)));
    }

    /// Screenshots with `from <= timestamp < to`, in every app.
    public int CountFrames(DateTime from, DateTime to) =>
        (int)ScalarLong("SELECT COUNT(*) FROM frames WHERE timestamp >= ? AND timestamp < ?", Time.Iso(from), Time.Iso(to));

    /// Ids of every screenshot whose text, app or window title has all of `words` (as keyword search finds
    /// them), in every app, hidden and blocked ones included. Oldest first.
    public List<long> FrameIdsMatching(string words)
    {
        if (FtsQuery(words) is not string match) return new();
        return Rows("SELECT f.id FROM frames_fts JOIN frames f ON f.id = frames_fts.rowid WHERE frames_fts MATCH ? ORDER BY f.timestamp",
                    r => r.GetInt64(0), match);
    }

    /// The deleted rows are gone from the file itself, not only from search: SQLite overwrites what it frees
    /// (secure_delete, see Vault.Open), the search index drops their words (SecureDeleteSearchText, or a merge
    /// here when this SQLite can't), and this empties the write-ahead log, which still holds the old pages until
    /// a checkpoint. Can take a while on a big database: call it off the UI thread. Mac: the same.
    public void FlushDeleted()
    {
        if (!secureSearchDelete) PurgeDeletedSearchText();
        try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch (Exception e) { Log.Write($"checkpoint after delete failed: {e.Message}"); }
    }

    /// `where` picks the frames; `from`/`to` is the time they cover, for what is kept by time.
    int DeleteFrames(string where, object[] args, DateTime? from, DateTime? to)
    {
        var and = where.Length == 0 ? "WHERE" : where + " AND";
        List<string> stills, emptied = new();
        bool partial = false;
        int n;
        lock (gate)
        {
            // Stars of this time go with its recordings, even when no frame is left in it.
            DeleteStarsWith(from, to);
            // Meeting titles taken from these frames go first (outside the transaction: their own statement).
            if (ScalarLong($"SELECT COUNT(*) FROM frames {where}", args) > 0) ForgetMeetingWindows(from, to);
            using var tx = conn.BeginTransaction();
            n = (int)TxScalar(tx, $"SELECT COUNT(*) FROM frames {where}", args);
            if (n == 0) { tx.Rollback(); return 0; }
            stills = TxStrings(tx, $"SELECT snapshot_path FROM frames {where}", args);
            var videos = TxStrings(tx, $"SELECT DISTINCT video_path FROM frames {and} video_path != ''", args);
            TxRun(tx, $"DELETE FROM frames {where}", args);  // FTS rows go with it (trigger frames_ad)
            // Search by Meaning moments of these frames, in this transaction: none can outlive its frames.
            ForgetScreenMoments(tx, from, to);
            foreach (var v in videos)
            {
                if (TxScalar(tx, "SELECT COUNT(*) FROM frames WHERE video_path = ? AND video_path != ''", v) == 0)
                {
                    TxRun(tx, "DELETE FROM video_segments WHERE path = ?", v);
                    emptied.Add(v);
                }
                else
                {
                    TxRun(tx, "UPDATE video_segments SET needs_rewrite = 1 WHERE path = ?", v);
                    partial = true;
                }
            }
            tx.Commit();
        }
        if (emptied.Count > 0) ReleasingVideos?.Invoke(emptied);
        RemoveFiles(stills.Concat(emptied).ToList());
        if (partial) VideosNeedRewrite?.Invoke();
        // A recap is made from its day's recordings, so it goes with them.
        DeleteRecapsWith(from, to);
        return n;
    }

    /// Delete audio chunks with `from <= timestamp < to`, their transcripts and WAV files, and the
    /// meeting notes made from them.
    public int DeleteAudio(DateTime? from, DateTime? to)
    {
        DeleteMeetings(from, to);
        var (where, args) = Range("timestamp", from, to);
        var paths = Strings($"SELECT file_path FROM audio_chunks {where}", args);
        if (paths.Count == 0) return 0;
        ForgetAudioMoments(where, args);
        Run($"DELETE FROM audio_transcriptions WHERE audio_chunk_id IN (SELECT id FROM audio_chunks {where})", args);
        Run($"DELETE FROM audio_chunks {where}", args);
        RemoveFiles(paths);
        DeleteRecapsWith(from, to);
        return paths.Count;
    }

    /// Give freed space back to the disk (after big deletes).
    public void Compact()
    {
        try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); Exec("VACUUM;"); } catch (Exception e) { Log.Write($"compact failed: {e.Message}"); }
    }

    static (string, object[]) Range(string col, DateTime? from, DateTime? to)
    {
        var parts = new List<string>(); var args = new List<object>();
        if (from is DateTime f) { parts.Add($"{col} >= ?"); args.Add(Time.Iso(f)); }
        if (to is DateTime t) { parts.Add($"{col} < ?"); args.Add(Time.Iso(t)); }
        return (parts.Count == 0 ? "" : "WHERE " + string.Join(" AND ", parts), args.ToArray());
    }

    /// Remove files, then any day folders left empty.
    static void RemoveFiles(List<string> paths)
    {
        var dirs = new HashSet<string>();
        foreach (var p in paths.Where(p => p.Length > 0))
        {
            RecordingFile.Delete(p);
            if (Path.GetDirectoryName(p) is string d) dirs.Add(d);
        }
        foreach (var d in dirs)
        {
            try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
    }
}

/// Runs at launch and every hour: removes recordings older than the chosen limits.
public sealed class RetentionJob : IDisposable
{
    readonly Database db;
    public KeepFor ScreenKeep;
    public KeepFor AudioKeep;
    Timer? timer;

    public RetentionJob(Database db, KeepFor screen, KeepFor audio) { this.db = db; ScreenKeep = screen; AudioKeep = audio; }

    public void Start() => timer = new Timer(_ => RunNow(), null, TimeSpan.Zero, TimeSpan.FromHours(1));

    public void RunNow()
    {
        var screen = ScreenKeep; var audio = AudioKeep;
        Task.Run(() =>
        {
            int frames = 0, chunks = 0;
            if (screen.Seconds() is double s) frames = db.DeleteFrames(null, DateTime.UtcNow.AddSeconds(-s));
            if (audio.Seconds() is double a) chunks = db.DeleteAudio(null, DateTime.UtcNow.AddSeconds(-a));
            if (frames + chunks > 0) Log.Write($"auto-delete: removed {frames} screenshots, {chunks} audio clips");
        });
    }

    public void Dispose() => timer?.Dispose();
}
