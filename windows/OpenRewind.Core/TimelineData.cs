namespace OpenRewind;

/// Url is the browser tab's address in this frame, or "".
/// End is set only while the timeline shows one app: when the next screenshot (of any app) was taken.
/// Blocked: saved while a blocked window or site was in front; never copied or exported.
/// The picture is the still at Path, or a frame of the video at VideoPath (see Frame); load it with FrameImages.
public record TimelineFrame(long Id, DateTime Time, string AppName, string WindowName, string Path, string Url = "", DateTime? End = null, bool Blocked = false,
                            string VideoPath = "", int VideoFrame = 0, double VideoTime = 0)
{
    public FrameSource Source => new(Path, VideoPath, VideoFrame, VideoTime);
}

public record TimelineAudio(long Id, DateTime Start, double Duration, string Device, bool IsInput, string Path)
{
    public DateTime End => Start.AddSeconds(Duration);
}

public record TimelineCaption(double Start, double End, string Text);

/// Light reads for the timeline overlay. Rows come back oldest first.
public sealed partial class Database
{
    public List<TimelineFrame> TimelineFrames(DateTime since)
    {
        lock (gate)
        {
            using var cmd = Cmd("SELECT id, timestamp, app_name, window_name, snapshot_path, browser_url, blocked, video_path, video_frame, video_time FROM frames WHERE timestamp >= ? ORDER BY timestamp ASC", Time.Iso(since));
            using var r = cmd.ExecuteReader();
            var out_ = new List<TimelineFrame>();
            while (r.Read())
                if (Time.ParseIso(r.GetString(1)) is DateTime t)
                    out_.Add(new TimelineFrame(r.GetInt64(0), t, r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Blocked: r.GetInt64(6) != 0,
                                               VideoPath: r.GetString(7), VideoFrame: (int)r.GetInt64(8), VideoTime: r.GetDouble(9)));
            return out_;
        }
    }

    /// Screenshots taken from `from` to `to` (inclusive), plus the first one after `to`, which says how long the
    /// last one lasted. Export Clip reads only this, however much history there is.
    public List<TimelineFrame> TimelineFrames(DateTime from, DateTime to)
    {
        lock (gate)
        {
            var out_ = new List<TimelineFrame>();
            foreach (var (sql, args) in new[]
            {
                ("SELECT id, timestamp, app_name, window_name, snapshot_path, browser_url, blocked, video_path, video_frame, video_time FROM frames WHERE timestamp >= ? AND timestamp <= ? ORDER BY timestamp ASC", new object[] { Time.Iso(from), Time.Iso(to) }),
                ("SELECT id, timestamp, app_name, window_name, snapshot_path, browser_url, blocked, video_path, video_frame, video_time FROM frames WHERE timestamp > ? ORDER BY timestamp ASC LIMIT 1", new object[] { Time.Iso(to) }),
            })
            {
                using var cmd = Cmd(sql, args);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (Time.ParseIso(r.GetString(1)) is DateTime t)
                        out_.Add(new TimelineFrame(r.GetInt64(0), t, r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Blocked: r.GetInt64(6) != 0,
                                               VideoPath: r.GetString(7), VideoFrame: (int)r.GetInt64(8), VideoTime: r.GetDouble(9)));
            }
            return out_;
        }
    }

    public List<TimelineAudio> TimelineAudio(DateTime since)
    {
        lock (gate)
        {
            // Start a little earlier so a chunk that began before `since` but is still playing is included.
            using var cmd = Cmd("SELECT id, timestamp, device, is_input, file_path FROM audio_chunks WHERE timestamp >= ? ORDER BY timestamp ASC", Time.Iso(since.AddSeconds(-120)));
            using var r = cmd.ExecuteReader();
            var out_ = new List<TimelineAudio>();
            while (r.Read())
            {
                if (Time.ParseIso(r.GetString(1)) is not DateTime t) continue;
                var path = r.GetString(4);
                // 16 kHz mono 16-bit WAV: 32,000 bytes per second after the 44-byte header.
                long size = 0;
                try { size = new FileInfo(path).Length; } catch { }
                if (size <= 44) continue;
                out_.Add(new TimelineAudio(r.GetInt64(0), t, (size - 44) / 32_000.0, r.GetString(2), r.GetInt64(3) != 0, path));
            }
            return out_;
        }
    }

    public List<TimelineCaption> Captions(long chunkId)
    {
        lock (gate)
        {
            using var cmd = Cmd("SELECT start_time, end_time, transcription FROM audio_transcriptions WHERE audio_chunk_id = ? ORDER BY start_time", chunkId);
            using var r = cmd.ExecuteReader();
            var out_ = new List<TimelineCaption>();
            while (r.Read()) out_.Add(new TimelineCaption(r.GetDouble(0), r.GetDouble(1), r.GetString(2)));
            return out_;
        }
    }

    /// Time of the oldest screenshot or audio clip still stored.
    public DateTime? OldestRecording()
    {
        var s = Strings("SELECT MIN(t) FROM (SELECT MIN(timestamp) AS t FROM frames UNION ALL SELECT MIN(timestamp) FROM audio_chunks)").FirstOrDefault();
        return string.IsNullOrEmpty(s) ? null : Time.ParseIso(s);
    }

    /// Total size of everything in a folder (screenshots, audio, database).
    public static long FolderSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch { return 0; }
    }
}
