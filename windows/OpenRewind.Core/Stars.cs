using System.Text;
using System.Text.Json.Nodes;

namespace OpenRewind;

/// A moment the user starred: with Ctrl+Alt+S from anywhere, or S / the star button on the timeline.
/// Only the time (UTC) and an optional note are stored. What was on screen comes from the screenshot
/// saved at or just before it, so a star never keeps a window title or address the recordings
/// don't, and the block list applies to it like to any other moment. Mac: Stars.swift.
public sealed record Star(long Id, DateTime Time, string Note);

/// A star with the screenshot it points at (null when none was saved near it).
public sealed record StarredMoment(Star Star, Frame? Frame);

public sealed partial class Database
{
    /// The Mac app creates the same table.
    void MigrateStars()
    {
        Exec("""
        CREATE TABLE IF NOT EXISTS stars (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            note TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_stars_ts ON stars(timestamp);
        """);
    }

    /// Stars from `from` to `to` (UTC, either open), newest first. Empty when the table is missing (an AI app's
    /// read-only open of a database the recording app has not migrated yet).
    public List<Star> Stars(DateTime? from = null, DateTime? to = null, int limit = 500)
    {
        var parts = new List<string>(); var args = new List<object>();
        if (from is DateTime f) { parts.Add("timestamp >= ?"); args.Add(Time.Iso(f)); }
        if (to is DateTime t) { parts.Add("timestamp <= ?"); args.Add(Time.Iso(t)); }
        args.Add(limit);
        lock (gate)
        {
            if (ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'stars'") == 0) return new();
            return Rows($"SELECT id, timestamp, note FROM stars {(parts.Count == 0 ? "" : "WHERE " + string.Join(" AND ", parts))} ORDER BY timestamp DESC LIMIT ?",
                        r => (Id: r.GetInt64(0), Time: Time.ParseIso(r.GetString(1)), Note: r.GetString(2)), args.ToArray())
                .Where(x => x.Time != null).Select(x => new Star(x.Id, x.Time!.Value, x.Note)).ToList();
        }
    }

    /// `time` is UTC.
    public long AddStar(DateTime time, string note = "")
    {
        lock (gate)
        {
            using var cmd = Cmd("INSERT INTO stars (timestamp, note, created_at) VALUES (?, ?, ?); SELECT last_insert_rowid();", Time.Iso(time), note, Time.Now());
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void RemoveStar(long id) => Run("DELETE FROM stars WHERE id = ?", id);

    /// Stars go with the recordings of their time (Delete Recordings and Keep Screen Recordings For).
    public void DeleteStarsWith(DateTime? from, DateTime? to)
    {
        var (where, args) = Range("timestamp", from, to);
        Run($"DELETE FROM stars {where}", args);
    }

    /// The screenshot a star points at: the last one saved at or before it, or, when there is none in the
    /// minute before, the first one in the minute after (a star pressed just as recording started).
    public Frame? FrameForStar(DateTime t)
    {
        string iso = Time.Iso(t), lo = Time.Iso(t.AddSeconds(-60)), hi = Time.Iso(t.AddSeconds(60));
        return Frames($"SELECT {cols} FROM frames f WHERE f.timestamp <= ? AND f.timestamp >= ? ORDER BY f.timestamp DESC LIMIT 1", iso, lo).FirstOrDefault()
            ?? Frames($"SELECT {cols} FROM frames f WHERE f.timestamp > ? AND f.timestamp <= ? ORDER BY f.timestamp ASC LIMIT 1", iso, hi).FirstOrDefault();
    }

    public List<StarredMoment> StarredMoments(DateTime? from = null, DateTime? to = null, int limit = 500) =>
        Stars(from, to, limit).Select(s => new StarredMoment(s, FrameForStar(s.Time))).ToList();
}

public sealed partial class AiTools
{
    string ListStarred(JsonObject a)
    {
        var limit = Math.Clamp(I(a, "limit") ?? 20, 1, 50);
        var lines = new List<string>();
        foreach (var m in db.StarredMoments(AiTime.Parse(a["start"]), AiTime.Parse(a["end"]), 200))
        {
            var f = m.Frame;
            // A star on a blocked app or site is left out, like the moment itself.
            if (f != null && (f.Blocked || Hides(f.AppName, f.WindowName, f.BrowserUrl))) continue;
            var line = new StringBuilder($"- {AiTime.Label(m.Star.Time)}");
            if (f != null)
            {
                line.Append(' ').Append(f.AppName);
                if (f.WindowName.Length > 0) line.Append($" — “{f.WindowName}”");
                if (f.BrowserUrl.Length > 0) line.Append($" <{f.BrowserUrl}>");
            }
            else line.Append(" (no screenshot saved near this moment)");
            line.Append($" (link: {AiTime.Link(m.Star.Time)})");
            if (m.Star.Note.Length > 0) line.Append($"\n  Note: {m.Star.Note}");
            var text = f == null ? "" : Squash(f.Text);
            if (text.Length > 0) line.Append("\n  On screen: ").Append(text.Length > 400 ? text[..400] + "…" : text);
            lines.Add(line.ToString());
            if (lines.Count >= limit) break;
        }
        return lines.Count == 0 ? "No starred moments in that range." : $"{lines.Count} starred moments, newest first:\n" + string.Join("\n", lines);
    }
}

public static partial class SearchHits
{
    /// Starred moments for the timeline's Starred filter, newest first. With words in `q`, only stars whose app,
    /// window, address, note or screen text has every word; with `app`, only stars on that app. A star on an app
    /// or site blocked now shows the app name only, as the timeline does.
    public static List<SearchHit> Starred(Database db, string q, string? app, Blocklist list)
    {
        var words = q.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var out_ = new List<SearchHit>();
        foreach (var m in db.StarredMoments())
        {
            var f = m.Frame;
            if (app != null && f?.AppName != app) continue;
            var hidden = f != null && list.HidesFrame(f);
            var title = f == null ? "Starred moment" : f.AppName.Length == 0 ? "Screen" : f.AppName;
            var detail = hidden ? "" : string.Join(" · ", new[] { m.Star.Note, f?.WindowName ?? "", f?.BrowserUrl ?? "" }.Where(x => x.Length > 0));
            var text = hidden ? "" : f?.Text ?? "";
            if (words.Length > 0)
            {
                var hay = $"{title} {detail} {text}".ToLowerInvariant();
                if (!words.All(hay.Contains)) continue;
            }
            out_.Add(new SearchHit($"s{m.Star.Id}", false, m.Star.Time, title, words.Length > 0 && text.Length > 0 ? Snippet(text, q) : detail, null, IsStar: true));
        }
        return out_;
    }
}
