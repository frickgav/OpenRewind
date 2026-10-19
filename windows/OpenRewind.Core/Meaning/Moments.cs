using System.Text;

namespace OpenRewind;

/// A stored screenshot as the indexer sees it.
public sealed record MomentFrame(long Id, string Timestamp, string AppName, string WindowName, string Url, string Text);

/// One transcript line as the indexer sees it.
public sealed record MomentSegment(long ChunkId, string Timestamp, string Text, bool IsInput);

/// What Search by Meaning finds: a stretch of one window, or one audio clip. Screen moments run
/// from FirstId to LastId (frame ids); audio moments are one clip (FirstId = LastId = chunk id).
public sealed record Moment(bool IsAudio, string Start, string End, long FirstId, long LastId,
                            string AppName, string WindowName, string Url, string Text);

/// Cuts recordings into moments worth one vector each. The screen holds the same text for many
/// frames, so a moment is a stretch of one app window (until the window, page or app changes,
/// a 2-minute break, 10 minutes, or about 1,500 characters of new text), and only lines it has
/// not shown before in that stretch are added. Audio: one moment per transcribed clip.
/// The Mac app cuts them the same way (Meaning.swift).
public static class MomentChunker
{
    /// Characters of text per moment; the model reads about 1,000 characters anyway.
    public const int TextBudget = 1500;
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(10);

    sealed class Stretch
    {
        public required MomentFrame First;
        public required MomentFrame Last;
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        public readonly StringBuilder Text = new();
    }

    /// Frames in id order. `complete`: these are all the frames there are for now, so the last
    /// stretch can be closed; otherwise it is held back for the next batch (unless it starts the
    /// batch, which would get nowhere). `blocked` frames are never part of a moment. Returns the moments and the id of the
    /// last frame they account for (the next batch starts after it).
    public static (List<Moment> Moments, long Done) Screen(IReadOnlyList<MomentFrame> frames, bool complete, Func<MomentFrame, bool> blocked)
    {
        var moments = new List<Moment>();
        if (frames.Count == 0) return (moments, 0);
        Stretch? cur = null;
        void Close()
        {
            if (cur != null && cur.Text.Length > 0) moments.Add(ToMoment(cur));
            cur = null;
        }
        foreach (var f in frames)
        {
            if (blocked(f)) { Close(); continue; }
            var lines = Lines(f.Text);
            if (lines.Count == 0) continue;
            if (cur != null && Breaks(cur, f)) Close();
            var fresh = cur == null ? lines : lines.Where(l => !cur.Seen.Contains(l)).ToList();
            if (cur != null && cur.Text.Length > 0 && cur.Text.Length + fresh.Sum(l => l.Length + 1) > TextBudget)
            {
                Close();
                fresh = lines;
            }
            cur ??= new Stretch { First = f, Last = f };
            cur.Last = f;
            foreach (var l in fresh)
            {
                if (!cur.Seen.Add(l)) continue;
                if (cur.Text.Length >= TextBudget) continue;
                var room = TextBudget - cur.Text.Length;
                cur.Text.Append(l.Length < room ? l : l[..room]).Append('\n');
            }
        }
        long done = frames[^1].Id;
        if (cur != null && !complete && cur.First.Id > frames[0].Id)
        {
            done = cur.First.Id - 1;   // still going: read it again with the next batch
            cur = null;
        }
        Close();
        return (moments, done);
    }

    static bool Breaks(Stretch s, MomentFrame f)
    {
        if (f.AppName != s.Last.AppName || f.WindowName != s.Last.WindowName || f.Url != s.Last.Url) return true;
        if (Time.ParseIso(f.Timestamp) is not DateTime t || Time.ParseIso(s.Last.Timestamp) is not DateTime last
            || Time.ParseIso(s.First.Timestamp) is not DateTime first) return true;
        return t - last > MaxGap || t - first > MaxLength;
    }

    static Moment ToMoment(Stretch s) =>
        new(false, s.First.Timestamp, s.Last.Timestamp, s.First.Id, s.Last.Id, s.First.AppName, s.First.WindowName, s.First.Url,
            s.Text.ToString().TrimEnd('\n'));

    /// Trimmed lines with single spaces, without empty ones and stray single characters.
    public static List<string> Lines(string text)
    {
        var out_ = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var l = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (l.Length >= 2) out_.Add(l);
        }
        return out_;
    }

    /// Transcript lines in clip order (as stored), one moment per clip.
    public static List<Moment> Audio(IReadOnlyList<MomentSegment> segments)
    {
        var moments = new List<Moment>();
        foreach (var clip in segments.GroupBy(s => s.ChunkId))
        {
            var parts = clip.Select(s => string.Join(' ', s.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                            .Where(t => t.Length > 0).ToList();
            var text = string.Join(' ', parts);
            if (text.Length < 2) continue;
            if (text.Length > TextBudget) text = text[..TextBudget];
            var first = clip.First();
            moments.Add(new Moment(true, first.Timestamp, clip.Last().Timestamp, clip.Key, clip.Key,
                                   first.IsInput ? "Microphone" : "System audio", "", "", text));
        }
        return moments;
    }

    /// What the model reads: the window title and app give the screen text its context.
    public static string EmbedText(Moment m) =>
        m.IsAudio || (m.WindowName.Length == 0 && m.AppName.Length == 0) ? m.Text
        : $"{(m.WindowName.Length > 0 ? m.WindowName + " — " : "")}{m.AppName}\n{m.Text}";
}
