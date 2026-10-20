using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenRewind;

/// What the AI can look up in the local database. A whole day of screen text is far too much to
/// send, so the model asks for summaries and searches instead, and every answer is capped in size.
/// Frames of apps, sites and windows on the block list are left out of every answer, including
/// ones saved before the user blocked them. The Mac app has the same tools in AITools.swift.
public sealed class AiTools
{
    readonly Database db;

    public AiTools(Database db) { this.db = db; }

    /// What to leave out; null means the block list recording uses right now.
    public Blocklist? Blocked;

    /// True for a frame the block list covers now, by app name, private-window title, blocked site, or a blocked
    /// word in the title or web address.
    public bool Hides(string app, string window, string url)
    {
        var list = Blocked ?? Blocklist.Current;
        return list.BlocksWindow(app, app, window) || list.BlocksUrl(url) || list.HasKeyword(url);
    }

    /// One summary covers at most this many days, so a question about a whole year does not load
    /// every frame of it (set for AI apps outside OpenRewind, see McpServer).
    public int? MaxSummaryDays;

    /// What the activity summary calls a blocked app: its time still counts, but not its name.
    public const string BlockedAppLabel = "(blocked app)";

    /// True when the app itself is on the block list (not just one of its windows or sites).
    public bool BlocksAppName(string app) => (Blocked ?? Blocklist.Current).BlocksApp(app, app);

    /// Reads a frame's screenshot as a small JPEG. Set only while the user has turned on
    /// "Let AI look at screenshots"; null means view_screenshot is not offered at all.
    public Func<Frame, byte[]?>? Screenshots;

    /// The tools to send with a request.
    public IReadOnlyList<AiToolSpec> Offered => Screenshots == null ? Specs : Specs.Append(ScreenshotSpec).ToArray();

    public static readonly AiToolSpec ScreenshotSpec = new("view_screenshot",
        "The screenshot saved closest to a moment, as an image. Use only when the screen text is not enough (charts, pictures, layout, what something looked like). Each image costs far more than text, so look at few.",
        Schema(new() { ["time"] = TimeArg("The moment to look at.") }, "time"));

    /// Most characters one tool answer may send to the provider.
    public const int ResultLimit = 14_000;

    public static readonly AiToolSpec[] Specs =
    {
        new("activity_summary",
            "Time spent per app, window and website between two times, plus a timeline of what was in front. Use first for questions like 'what did I work on today'.",
            Schema(new() { ["start"] = TimeArg("Start time. Default: start of today."), ["end"] = TimeArg("End time. Default: now.") })),
        new("search_screen",
            "Full-text search over text that was on screen (read from screenshots), app names and window titles. Returns matching moments with a snippet.",
            Schema(new()
            {
                ["query"] = Str("Words to look for. Every word must match (prefix match)."),
                ["start"] = TimeArg("Only after this time (optional)."),
                ["end"] = TimeArg("Only before this time (optional)."),
                ["app"] = Str("Only this app, e.g. 'chrome' (optional)."),
                ["limit"] = Int("Results to return, up to 30. Default 15."),
            }, "query")),
        new("search_audio",
            "Full-text search over transcripts of the microphone (the user) and system audio (calls, videos). Leave query empty to list everything said in a time range.",
            Schema(new()
            {
                ["query"] = Str("Words to look for (optional)."),
                ["start"] = TimeArg("Only after this time (optional)."),
                ["end"] = TimeArg("Only before this time (optional)."),
                ["limit"] = Int("Results to return, up to 60. Default 25."),
            })),
        new("get_screen_text",
            "The text that was on screen during a short time range (best under 30 minutes), sampled evenly and trimmed. Use to read details after a summary or search.",
            Schema(new() { ["start"] = TimeArg("Start time."), ["end"] = TimeArg("End time."), ["app"] = Str("Only this app (optional).") }, "start", "end")),
    };

    static JsonObject Schema(Dictionary<string, JsonNode> props, params string[] required)
    {
        var p = new JsonObject();
        foreach (var (k, v) in props) p[k] = v;
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()) };
    }

    static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
    static JsonObject Int(string d) => new() { ["type"] = "integer", ["description"] = d };
    static JsonObject TimeArg(string d) => Str(d + " Local time like '2026-10-18 14:30', or 'today', 'yesterday', '2h ago'.");

    /// A short line for the chat window: what was looked up.
    public static string Describe(AiToolCall call)
    {
        var a = call.Args;
        var range = string.Join(" to ", new[] { S(a, "start"), S(a, "end") }.Where(x => x.Length > 0));
        var when = range.Length == 0 ? "" : $" ({range})";
        var q = S(a, "query");
        return call.Name switch
        {
            "activity_summary" => "Summarized activity" + (range.Length == 0 ? " today" : when),
            "search_screen" => $"Searched screen for “{q}”{when}",
            "search_audio" => (q.Length == 0 ? "Read transcripts" : $"Searched audio for “{q}”") + when,
            "get_screen_text" => "Read screen text" + when,
            "view_screenshot" => $"Looked at the screenshot from {S(a, "time")}",
            _ => call.Name,
        };
    }

    /// The arguments as the model sent them, for the chat window.
    public static string Arguments(AiToolCall call) =>
        call.Name + " " + call.Args.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// Runs a lookup. Only view_screenshot returns an image.
    public (string Text, byte[]? Image) Execute(AiToolCall call)
    {
        if (call.Name != "view_screenshot") return (Run(call), null);
        if (Screenshots == null) return ("Error: the user has not allowed screenshots. Use the text tools.", null);
        if (AiTime.Parse(call.Args["time"]) is not DateTime t) return ("Error: time is required, e.g. '2026-10-18 14:30'.", null);
        if (db.FrameNear(t) is not Frame f) return ("No screenshot was saved near that time.", null);
        if (Hides(f.AppName, f.WindowName, f.BrowserUrl)) return ("That moment is from a blocked app or site, so its screenshot can't be shown.", null);
        var image = Screenshots(f);
        var what = $"Screenshot from {AiTime.Label(f.Timestamp)} {f.AppName}" + (f.WindowName.Length > 0 ? $" — “{f.WindowName}”" : "")
                   + $" (link: {AiTime.Link(f.Timestamp)})";
        return image == null ? ($"{what}: the image file is missing (it may have been deleted).", null) : (what + ". The image is attached.", image);
    }

    public string Run(AiToolCall call)
    {
        var a = call.Args;
        string out_;
        switch (call.Name)
        {
            case "activity_summary":
                var start = AiTime.Parse(a["start"]) ?? DateTime.Now.Date.ToUniversalTime();
                var end = AiTime.Parse(a["end"]) ?? DateTime.UtcNow;
                var note = "";
                if (MaxSummaryDays is int days && end - start > TimeSpan.FromDays(days))
                {
                    start = end.AddDays(-days);
                    note = $"Only the last {days} days of that range are summarized. Ask again with an earlier end for older days.\n";
                }
                // Time in a blocked app still counts, as on the timeline, but not its name, window titles or addresses.
                var rows = db.ActivityRows(start, end)
                    .Select(r => Hides(r.App, r.Window, r.Url) ? r with { Window = "", Url = "" } : r)
                    .Select(r => BlocksAppName(r.App) ? r with { App = BlockedAppLabel } : r)
                    .ToList();
                out_ = note + AiActivity.Summary(rows, start, end);
                break;
            case "search_screen": out_ = SearchScreen(a); break;
            case "search_audio": out_ = SearchAudio(a); break;
            case "get_screen_text":
                if (AiTime.Parse(a["start"]) is not DateTime s || AiTime.Parse(a["end"]) is not DateTime e)
                    return "Error: start and end are required, e.g. '2026-10-18 14:00'.";
                out_ = ScreenText(s, e, S(a, "app"));
                break;
            default: out_ = $"Error: unknown tool {call.Name}."; break;
        }
        return Cap(out_);
    }

    public static string Cap(string s, int limit = ResultLimit) =>
        s.Length <= limit ? s : s[..limit] + "\n[cut to fit; narrow the time range or search for more]";

    string SearchScreen(JsonObject a)
    {
        var q = S(a, "query").Trim();
        if (q.Length == 0) return "Error: query is required.";
        var limit = Math.Clamp(I(a, "limit") ?? 15, 1, 30);
        var s = new SearchQuery { Q = q, AppName = S(a, "app"), StartTime = IsoArg(a, "start"), EndTime = IsoArg(a, "end"), Limit = limit * 4 };
        var (rows, total) = db.Search(s);
        var seen = new HashSet<string>();
        var lines = new List<string>();
        foreach (var f in rows)
        {
            if (Hides(f.AppName, f.WindowName, f.BrowserUrl)) continue;
            var snip = Snippet(f.Text, q);
            // While typing, many frames in a row show the same thing.
            if (!seen.Add($"{f.AppName}|{f.WindowName}|{(snip.Length > 80 ? snip[..80] : snip)}")) continue;
            var line = new StringBuilder($"- {AiTime.Label(f.Timestamp)} {f.AppName}");
            if (f.WindowName.Length > 0) line.Append($" — “{f.WindowName}”");
            if (f.BrowserUrl.Length > 0) line.Append($" <{f.BrowserUrl}>");
            line.Append($" (link: {AiTime.Link(f.Timestamp)})\n  {snip}");
            lines.Add(line.ToString());
            if (lines.Count >= limit) break;
        }
        if (lines.Count == 0) return $"No screen text matches “{q}” in that range.";
        return $"{total} matching frames; newest first, near-duplicates skipped:\n" + string.Join("\n", lines);
    }

    string SearchAudio(JsonObject a)
    {
        var q = S(a, "query").Trim();
        var limit = Math.Clamp(I(a, "limit") ?? 25, 1, 60);
        var s = new SearchQuery { Q = q.Length == 0 ? null : q, StartTime = IsoArg(a, "start"), EndTime = IsoArg(a, "end") };
        var (rows, total) = db.SearchAudio(s, limit);
        if (rows.Count == 0) return q.Length == 0 ? "Nothing was transcribed in that range." : $"No transcript matches “{q}”.";
        var lines = Enumerable.Reverse(rows).Select(t =>
            $"- {AiTime.Label(t.Timestamp)} {(t.IsInput ? "Microphone (the user)" : "System audio")} (link: {AiTime.Link(t.Timestamp)}): {t.Text.Trim()}");
        return $"{total} transcript segments, showing {rows.Count} (oldest first):\n" + string.Join("\n", lines);
    }

    string ScreenText(DateTime start, DateTime end, string app)
    {
        var s = new SearchQuery { AppName = app, StartTime = Time.Iso(start), EndTime = Time.Iso(end), Limit = 400 };
        var rows = Enumerable.Reverse(db.Search(s).Rows).Where(f => f.Text.Length > 0 && !Hides(f.AppName, f.WindowName, f.BrowserUrl));
        // Skip frames whose text did not change.
        var unique = new List<Frame>();
        foreach (var f in rows) if (unique.Count == 0 || unique[^1].Text != f.Text) unique.Add(f);
        if (unique.Count == 0) return "No screen text was recorded in that range.";
        var picks = Evenly(unique, 12);
        var each = Math.Max(400, (ResultLimit - 400) / picks.Count);
        var out_ = new StringBuilder($"{unique.Count} different screens in this range; showing {picks.Count}:\n");
        foreach (var f in picks)
        {
            out_.Append($"\n## {AiTime.Label(f.Timestamp)} {f.AppName}");
            if (f.WindowName.Length > 0) out_.Append($" — “{f.WindowName}”");
            if (f.BrowserUrl.Length > 0) out_.Append($" <{f.BrowserUrl}>");
            var text = Squash(f.Text);
            out_.Append($" (link: {AiTime.Link(f.Timestamp)})\n").Append(text.Length > each ? text[..each] + "…" : text).Append('\n');
        }
        return out_.ToString();
    }

    // MARK: helpers

    static string S(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue(out string? s) ? s : "";

    static int? I(JsonObject a, string key) => a[key] switch
    {
        JsonValue v when v.TryGetValue(out int i) => i,
        JsonValue v when v.TryGetValue(out double d) => (int)d,
        JsonValue v when v.TryGetValue(out string? s) && int.TryParse(s, out var n) => n,
        _ => null,
    };

    static string? IsoArg(JsonObject a, string key) => AiTime.Parse(a[key]) is DateTime d ? Time.Iso(d) : null;

    public static List<T> Evenly<T>(List<T> items, int n)
    {
        if (items.Count <= n || n < 2) return items;
        return Enumerable.Range(0, n).Select(i => items[i * (items.Count - 1) / (n - 1)]).ToList();
    }

    /// OCR text has lots of line breaks and spacing; one space is enough for the model.
    public static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// About 300 characters around the first query word found.
    public static string Snippet(string text, string query)
    {
        var flat = Squash(text);
        var at = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => flat.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(-1).First();
        if (at < 0) return flat.Length > 300 ? flat[..300] : flat;
        var from = Math.Max(0, at - 140);
        var to = Math.Min(flat.Length, from + 300);
        return (from > 0 ? "…" : "") + flat[from..to] + (to < flat.Length ? "…" : "");
    }
}

/// Times in tool answers are local and short; links open that moment in the timeline.
public static class AiTime
{
    static readonly string[] LocalFormats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm" };

    public static string Label(string iso) => Time.ParseIso(iso) is DateTime d ? Label(d) : iso;
    public static string Label(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Link(string iso) => Link(Time.ParseIso(iso) ?? DateTime.UtcNow);
    public static string Link(DateTime utc) => $"openrewind://t/{new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds()}";

    /// The moment (UTC) in an openrewind://t/<seconds> link.
    public static DateTime? FromLink(string url)
    {
        const string prefix = "openrewind://t/";
        if (!url.StartsWith(prefix) || !long.TryParse(url[prefix.Length..].TrimEnd('/'), out var secs)) return null;
        return DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;
    }

    /// Local times the model writes ("2026-10-18 14:30"), plus everything Time.Parse takes. Returns UTC.
    public static DateTime? Parse(JsonNode? v)
    {
        if (v is not JsonValue jv || !jv.TryGetValue(out string? raw) || string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (DateTime.TryParseExact(raw, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var d))
            return DateTime.SpecifyKind(d, DateTimeKind.Utc);
        return Time.Parse(raw) is string iso ? Time.ParseIso(iso) : null;
    }
}

/// One row per saved frame, for the activity summary and Insights. Blocked: saved while a blocked
/// window or site was in front.
public sealed record ActivityRow(DateTime Time, string App, string Window, string Url, bool Blocked = false);

/// Turns frames into "time per app" and a timeline of what was in front.
public static class AiActivity
{
    /// A frame counts until the next one, but never longer than this: the recorder saves at least
    /// once a minute while the screen is on, so a longer gap means the PC was idle or off.
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(120);

    sealed class Block { public DateTime Start, End; public string App = "", Window = ""; public double Secs; }

    public static string Summary(IReadOnlyList<ActivityRow> rows, DateTime start, DateTime end)
    {
        if (rows.Count == 0) return $"Nothing was recorded between {AiTime.Label(start)} and {AiTime.Label(end)}.";
        var apps = new Dictionary<string, double>();
        var windows = new Dictionary<string, Dictionary<string, double>>();
        var sites = new Dictionary<string, double>();
        var blocks = new List<Block>();
        double total = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var next = i + 1 < rows.Count ? rows[i + 1].Time : (r.Time.AddSeconds(30) < end ? r.Time.AddSeconds(30) : end);
            var secs = Math.Max(0, Math.Min(MaxGap.TotalSeconds, (next - r.Time).TotalSeconds));
            total += secs;
            apps[r.App] = apps.GetValueOrDefault(r.App) + secs;
            if (r.Window.Length > 0)
            {
                if (!windows.TryGetValue(r.App, out var w)) windows[r.App] = w = new();
                w[r.Window] = w.GetValueOrDefault(r.Window) + secs;
            }
            if (Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && u.Host.Length > 0)
            {
                var host = u.Host.StartsWith("www.") ? u.Host[4..] : u.Host;
                sites[host] = sites.GetValueOrDefault(host) + secs;
            }
            if (blocks.Count > 0 && blocks[^1] is var b && b.App == r.App && b.Window == r.Window && r.Time - b.End <= MaxGap)
            {
                b.End = r.Time.AddSeconds(secs); b.Secs += secs;
            }
            else blocks.Add(new Block { Start = r.Time, End = r.Time.AddSeconds(secs), App = r.App, Window = r.Window, Secs = secs });
        }

        var out_ = new StringBuilder($"Activity from {AiTime.Label(start)} to {AiTime.Label(end)} (local time). About {Duration(total)} recorded.\n\nTime by app:\n");
        foreach (var (app, secs) in apps.OrderByDescending(x => x.Value).Take(15).Where(x => x.Value >= 60))
        {
            out_.Append($"- {(app.Length == 0 ? "(unknown)" : app)}: {Duration(secs)}\n");
            foreach (var (w, ws) in windows.GetValueOrDefault(app, new()).OrderByDescending(x => x.Value).Take(5).Where(x => x.Value >= 60))
                out_.Append($"  - “{Cut(w, 120)}”: {Duration(ws)}\n");
        }
        var topSites = sites.OrderByDescending(x => x.Value).Take(15).Where(x => x.Value >= 60).ToList();
        if (topSites.Count > 0)
            out_.Append("\nWebsites:\n").Append(string.Join("\n", topSites.Select(x => $"- {x.Key}: {Duration(x.Value)}"))).Append('\n');
        // Short switches are noise; raise the bar until the timeline fits.
        double minSecs = 120;
        var shown = blocks.Where(x => x.Secs >= minSecs).ToList();
        while (shown.Count > 70) { minSecs *= 2; shown = blocks.Where(x => x.Secs >= minSecs).ToList(); }
        out_.Append($"\nTimeline (stretches of {(int)(minSecs / 60)}+ minutes):\n");
        foreach (var b in shown)
        {
            out_.Append($"- {b.Start.ToLocalTime():HH:mm}–{b.End.ToLocalTime():HH:mm} {b.App}");
            if (b.Window.Length > 0) out_.Append($" — “{Cut(b.Window, 100)}”");
            out_.Append($" (link: {AiTime.Link(b.Start)})\n");
        }
        return out_.ToString();
    }

    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    public static string Duration(double secs)
    {
        var m = (int)(secs / 60);
        return m >= 60 ? $"{m / 60}h {m % 60}m" : $"{Math.Max(1, m)}m";
    }
}

public sealed partial class Database
{
    /// The saved frame closest to `t` (UTC), within 10 minutes.
    public Frame? FrameNear(DateTime t)
    {
        var iso = Time.Iso(t);
        var before = Frames($"SELECT {cols} FROM frames f WHERE f.timestamp <= ? ORDER BY f.timestamp DESC LIMIT 1", iso).FirstOrDefault();
        var after = Frames($"SELECT {cols} FROM frames f WHERE f.timestamp > ? ORDER BY f.timestamp ASC LIMIT 1", iso).FirstOrDefault();
        double Gap(Frame? f) => f != null && Time.ParseIso(f.Timestamp) is DateTime d ? Math.Abs((d - t).TotalSeconds) : double.MaxValue;
        var best = Gap(before) <= Gap(after) ? before : after;
        return Gap(best) <= 600 ? best : null;
    }

    /// Light rows (no text) for the activity summary, oldest first. `start` and `end` are UTC.
    public List<ActivityRow> ActivityRows(DateTime start, DateTime end)
    {
        lock (gate)
        {
            using var cmd = Cmd("SELECT timestamp, app_name, window_name, browser_url FROM frames WHERE timestamp >= ? AND timestamp <= ? ORDER BY timestamp ASC",
                                Time.Iso(start), Time.Iso(end));
            using var r = cmd.ExecuteReader();
            var out_ = new List<ActivityRow>();
            while (r.Read())
                if (Time.ParseIso(r.GetString(0)) is DateTime t)
                    out_.Add(new ActivityRow(t, r.GetString(1), r.GetString(2), r.GetString(3)));
            return out_;
        }
    }
}
