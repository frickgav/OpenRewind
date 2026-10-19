using Microsoft.Data.Sqlite;

namespace OpenRewind;

/// Where a frame's picture is: a still file, or one frame of a video (see Compress Older Recordings).
/// VideoTime is the frame's time inside the video in seconds; VideoFrame its number (both start at 0).
public record FrameSource(string StillPath, string VideoPath = "", int VideoFrame = 0, double VideoTime = 0)
{
    public bool IsVideo => VideoPath.Length > 0;
}

/// One video file holding older frames of one screen and a few minutes. SourceBytes is what the
/// stills it replaced took, so SourceBytes - Bytes is the space it saves.
/// NeedsRewrite: some of its frames were deleted, so it must be written again without them.
public record VideoSegment(long Id, string Path, string Display, string StartTime, string EndTime, int Width, int Height,
                           int FrameCount, string Codec, long Bytes, long SourceBytes, bool NeedsRewrite = false);

/// A still frame old enough to go into a video.
public record CompactCandidate(long Id, DateTime Time, string Display, string StillPath);

/// The rows behind Compress Older Recordings. The Mac app has the same schema (VideoStore.swift).
public sealed partial class Database
{
    /// Videos are about to be deleted: close anything that has them open (Windows cannot delete an open file).
    public event Action<IReadOnlyList<string>>? ReleasingVideos;
    /// Frames of a video were deleted while others in it are kept: the compactor should write it again soon.
    public event Action? VideosNeedRewrite;

    void MigrateVideo()
    {
        foreach (var (name, type) in new[] { ("display", "TEXT NOT NULL DEFAULT ''"), ("video_path", "TEXT NOT NULL DEFAULT ''"),
                                             ("video_frame", "INTEGER NOT NULL DEFAULT 0"), ("video_time", "REAL NOT NULL DEFAULT 0") })
            if (ScalarLong($"SELECT COUNT(*) FROM pragma_table_info('frames') WHERE name = '{name}'") == 0)
                Exec($"ALTER TABLE frames ADD COLUMN {name} {type}");
        Exec("""
        CREATE INDEX IF NOT EXISTS idx_frames_video ON frames(video_path, video_frame) WHERE video_path != '';
        CREATE TABLE IF NOT EXISTS video_segments (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            path TEXT NOT NULL UNIQUE,
            display TEXT NOT NULL DEFAULT '',
            start_time TEXT NOT NULL,
            end_time TEXT NOT NULL,
            width INTEGER NOT NULL,
            height INTEGER NOT NULL,
            frame_count INTEGER NOT NULL,
            codec TEXT NOT NULL DEFAULT '',
            bytes INTEGER NOT NULL DEFAULT 0,
            source_bytes INTEGER NOT NULL DEFAULT 0,
            needs_rewrite INTEGER NOT NULL DEFAULT 0
        );
        """);
    }

    /// Still frames taken before `before` and after `after`, oldest first.
    public List<CompactCandidate> CompactCandidates(DateTime before, string after, int limit)
    {
        lock (gate)
        {
            using var cmd = Cmd("SELECT id, timestamp, display, snapshot_path FROM frames WHERE video_path = '' AND snapshot_path != '' " +
                                "AND timestamp < ? AND timestamp > ? ORDER BY timestamp ASC LIMIT ?", Time.Iso(before), after, limit);
            using var r = cmd.ExecuteReader();
            var out_ = new List<CompactCandidate>();
            while (r.Read())
                if (Time.ParseIso(r.GetString(1)) is DateTime t)
                    out_.Add(new CompactCandidate(r.GetInt64(0), t, r.GetString(2), r.GetString(3)));
            return out_;
        }
    }

    /// Point the frames at the new video, in one transaction. False (and nothing changed) when one of
    /// them was deleted or moved meanwhile: the video would then hold a picture the user deleted.
    public bool CommitSegment(VideoSegment seg, IReadOnlyList<long> ids)
    {
        lock (gate)
        {
            using var tx = conn.BeginTransaction();
            foreach (var id in ids)
                if (TxScalar(tx, "SELECT COUNT(*) FROM frames WHERE id = ? AND video_path = '' AND snapshot_path != ''", id) != 1)
                {
                    tx.Rollback();
                    return false;
                }
            TxRun(tx, "INSERT INTO video_segments (path, display, start_time, end_time, width, height, frame_count, codec, bytes, source_bytes, needs_rewrite) " +
                      "VALUES (?,?,?,?,?,?,?,?,?,?,0)",
                  seg.Path, seg.Display, seg.StartTime, seg.EndTime, seg.Width, seg.Height, seg.FrameCount, seg.Codec, seg.Bytes, seg.SourceBytes);
            for (int i = 0; i < ids.Count; i++)
                TxRun(tx, "UPDATE frames SET video_path = ?, video_frame = ?, video_time = ? WHERE id = ?", seg.Path, i, i / Compaction.Fps, ids[i]);
            tx.Commit();
            return true;
        }
    }

    /// The stills of these frames are deleted: forget their paths.
    public void ClearStills(IEnumerable<long> ids)
    {
        lock (gate)
        {
            using var tx = conn.BeginTransaction();
            foreach (var id in ids) TxRun(tx, "UPDATE frames SET snapshot_path = '' WHERE id = ? AND video_path != ''", id);
            tx.Commit();
        }
    }

    /// Frames already in a video whose still was not deleted yet (the app stopped in between).
    public List<(long Id, string Path)> LeftoverStills(int limit = 5000)
    {
        lock (gate)
        {
            using var cmd = Cmd("SELECT id, snapshot_path FROM frames WHERE video_path != '' AND snapshot_path != '' LIMIT ?", limit);
            using var r = cmd.ExecuteReader();
            var out_ = new List<(long, string)>();
            while (r.Read()) out_.Add((r.GetInt64(0), r.GetString(1)));
            return out_;
        }
    }

    public List<VideoSegment> Segments(bool onlyNeedingRewrite = false)
    {
        lock (gate)
        {
            using var cmd = Cmd("SELECT id, path, display, start_time, end_time, width, height, frame_count, codec, bytes, source_bytes, needs_rewrite " +
                                "FROM video_segments" + (onlyNeedingRewrite ? " WHERE needs_rewrite = 1" : "") + " ORDER BY start_time");
            using var r = cmd.ExecuteReader();
            var out_ = new List<VideoSegment>();
            while (r.Read())
                out_.Add(new VideoSegment(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), (int)r.GetInt64(5),
                                          (int)r.GetInt64(6), (int)r.GetInt64(7), r.GetString(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11) != 0));
            return out_;
        }
    }

    public HashSet<string> SegmentPaths() => Segments().Select(s => s.Path).ToHashSet(PathComparer);

    /// Frames still in a video, in video order.
    public List<(long Id, FrameSource Source, string Timestamp)> SegmentFrames(string path)
    {
        lock (gate)
        {
            using var cmd = Cmd("SELECT id, video_frame, video_time, timestamp FROM frames WHERE video_path = ? AND video_path != '' ORDER BY video_frame", path);
            using var r = cmd.ExecuteReader();
            var out_ = new List<(long, FrameSource, string)>();
            while (r.Read()) out_.Add((r.GetInt64(0), new FrameSource("", path, (int)r.GetInt64(1), r.GetDouble(2)), r.GetString(3)));
            return out_;
        }
    }

    /// Swap a video for its rewritten copy, in one transaction. False (nothing changed) when its frames
    /// changed meanwhile; it stays marked and is written again next time.
    public bool CommitRewrite(VideoSegment old, VideoSegment fresh, IReadOnlyList<long> ids)
    {
        lock (gate)
        {
            using var tx = conn.BeginTransaction();
            var now = new List<long>();
            using (var cmd = Cmd("SELECT id FROM frames WHERE video_path = ? AND video_path != '' ORDER BY video_frame", old.Path))
            {
                cmd.Transaction = tx;
                using var r = cmd.ExecuteReader();
                while (r.Read()) now.Add(r.GetInt64(0));
            }
            if (!now.SequenceEqual(ids) || TxScalar(tx, "SELECT COUNT(*) FROM video_segments WHERE id = ? AND path = ?", old.Id, old.Path) != 1)
            {
                tx.Rollback();
                return false;
            }
            for (int i = 0; i < ids.Count; i++)
                TxRun(tx, "UPDATE frames SET video_path = ?, video_frame = ?, video_time = ? WHERE id = ?", fresh.Path, i, i / Compaction.Fps, ids[i]);
            TxRun(tx, "UPDATE video_segments SET path = ?, start_time = ?, end_time = ?, width = ?, height = ?, frame_count = ?, codec = ?, " +
                      "bytes = ?, source_bytes = ?, needs_rewrite = 0 WHERE id = ?",
                  fresh.Path, fresh.StartTime, fresh.EndTime, fresh.Width, fresh.Height, fresh.FrameCount, fresh.Codec, fresh.Bytes, fresh.SourceBytes, old.Id);
            tx.Commit();
            return true;
        }
    }

    /// A video none of whose frames are left.
    public void DropSegment(long id) => Run("DELETE FROM video_segments WHERE id = ?", id);

    /// How many videos there are, the space they take, and the space they save compared with the stills.
    public (int Segments, long Bytes, long Saved) VideoStats()
    {
        lock (gate)
        {
            using var cmd = Cmd("SELECT COUNT(*), COALESCE(SUM(bytes), 0), COALESCE(SUM(source_bytes), 0) FROM video_segments");
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (0, 0, 0);
            long bytes = r.GetInt64(1), source = r.GetInt64(2);
            return ((int)r.GetInt64(0), bytes, Math.Max(0, source - bytes));
        }
    }

    public static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    long TxScalar(SqliteTransaction tx, string sql, params object?[] args)
    {
        using var cmd = Cmd(sql, args);
        cmd.Transaction = tx;
        return cmd.ExecuteScalar() is long n ? n : 0;
    }

    void TxRun(SqliteTransaction tx, string sql, params object?[] args)
    {
        using var cmd = Cmd(sql, args);
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }
}
