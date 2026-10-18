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
    public int DeleteFrames(DateTime? from, DateTime? to)
    {
        var (where, args) = Range("timestamp", from, to);
        var paths = Strings($"SELECT snapshot_path FROM frames {where}", args);
        if (paths.Count == 0) return 0;
        Run($"DELETE FROM frames {where}", args);  // FTS rows go with it (trigger frames_ad)
        RemoveFiles(paths);
        return paths.Count;
    }

    /// Delete audio chunks with `from <= timestamp < to`, their transcripts and WAV files.
    public int DeleteAudio(DateTime? from, DateTime? to)
    {
        var (where, args) = Range("timestamp", from, to);
        var paths = Strings($"SELECT file_path FROM audio_chunks {where}", args);
        if (paths.Count == 0) return 0;
        Run($"DELETE FROM audio_transcriptions WHERE audio_chunk_id IN (SELECT id FROM audio_chunks {where})", args);
        Run($"DELETE FROM audio_chunks {where}", args);
        RemoveFiles(paths);
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
            try { File.Delete(p); } catch { }
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
