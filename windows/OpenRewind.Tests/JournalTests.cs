using System.Text.Json.Nodes;
using OpenRewind;

public class JournalTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-journal-test-" + Guid.NewGuid());
    readonly Database db;
    /// A local day well in the past, so "now" never cuts it short.
    static readonly DateTime Day = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Local);

    public JournalTests()
    {
        var config = Config.Load(new[] { "--home", home });
        db = new Database(config.DbPath);
    }

    public void Dispose()
    {
        db.Dispose();
        try { Directory.Delete(home, true); } catch { }
    }

    /// UTC time of `h:m` local on Day.
    static DateTime At(int h, int m, int s = 0) => Day.AddHours(h).AddMinutes(m).AddSeconds(s).ToUniversalTime();

    void AddFrame(DateTime utc, string app, string window, string url = "", string text = "") =>
        db.Insert(new Frame(0, Time.Iso(utc), app, window, true, "", text, "ocr", 0, url));

    /// One frame every 30 s from `from` for `minutes`.
    void AddStretch(DateTime from, int minutes, string app, string window, string url = "", string text = "")
    {
        for (int i = 0; i < minutes * 2; i++) AddFrame(from.AddSeconds(30 * i), app, window, url, text);
    }

    static AiTools Tools(Database db) => new(db)
    {
        Blocked = new Blocklist(new[] { new BlockedApp("1password", "1Password") }, new[] { "Chase" }, true, sites: new[] { "bank.example.com" }),
    };

    static List<ActivityRow> Rows(params (DateTime T, string App, string Window, string Url)[] r) =>
        r.Select(x => new ActivityRow(x.T, x.App, x.Window, x.Url)).ToList();

    [Fact]
    public void CountsTimeUntilTheNextFrameButNotAcrossIdleGaps()
    {
        var rows = Rows((At(9, 0), "Code", "main.cs", ""), (At(9, 10), "Code", "main.cs", ""), (At(9, 11), "Mail", "Inbox", ""), (At(9, 12), "Mail", "Inbox", ""));
        var s = Journal.Compute(Day, rows, new List<(DateTime, bool)>(), _ => false, _ => false, At(23, 59));
        // 9:00 → 9:10 is longer than the idle gap, so it counts 2 minutes; then 1 minute each; the last frame 30 s.
        Assert.Equal(120 + 60 + 60 + 30, s.ActiveSeconds);
        Assert.Equal(At(9, 0), s.First);
        Assert.Equal(At(9, 12), s.Last);
        Assert.Equal(new[] { "Code", "Mail" }, s.Apps.Select(a => a.Name));
        Assert.Equal(180, s.Apps[0].Seconds);
    }

    [Fact]
    public void ListsAreSortedCappedAndSkipShortVisits()
    {
        var rows = new List<ActivityRow>();
        var t = At(8, 0);
        for (int app = 0; app < 12; app++)
            for (int i = 0; i < 4 + app; i++) { rows.Add(new(t, $"App{app}", $"Doc {app}", $"https://www.site{app}.com/x")); t = t.AddSeconds(30); }
        rows.Add(new(t, "Blink", "Quick", "https://quick.com"));
        var s = Journal.Compute(Day, rows, new List<(DateTime, bool)>(), _ => false, _ => false, At(23, 0));
        Assert.Equal(Journal.TopCount, s.Apps.Count);
        Assert.Equal("App11", s.Apps[0].Name);
        Assert.DoesNotContain(s.Apps, a => a.Name == "Blink");
        Assert.Equal("site11.com", s.Sites[0].Name);
        Assert.Equal(("Doc 11", "App11"), (s.Titles[0].Name, s.Titles[0].App));
    }

    [Fact]
    public void BlockedAppsSitesAndWindowsAreNeverListed()
    {
        AddStretch(At(9, 0), 10, "Code", "journal.cs");
        AddStretch(At(9, 10), 10, "1Password", "Vault — bank logins");
        AddStretch(At(9, 20), 10, "chrome", "Chase Online", "https://www.chase.com/");
        AddStretch(At(9, 30), 10, "chrome", "Statement", "https://bank.example.com/statement");
        AddStretch(At(9, 40), 10, "chrome", "Private — Incognito", "https://secret.example.org/");
        AddStretch(At(9, 50), 10, "chrome", "GitHub", "https://github.com/frickgav/OpenRewind");
        var s = db.JournalStats(Day, Tools(db));
        var page = string.Join("\n", Journal.Sections(s).SelectMany(x => x.Lines).Select(l => l.Text)) + Journal.Markdown(s, null);
        foreach (var hidden in new[] { "1Password", "Vault", "Chase", "chase.com", "bank.example.com", "Statement", "Incognito", "secret.example.org" })
            Assert.DoesNotContain(hidden, page);
        Assert.Contains("github.com", page);
        Assert.Contains("“GitHub” (chrome)", page);
        Assert.Contains("journal.cs", page);
        // Blocked time still counts as time at the computer.
        Assert.Equal(60 * 60, s.ActiveSeconds, 0);
        Assert.Equal(40 * 60, s.Apps.Single(a => a.Name == "chrome").Seconds, 0);
    }

    [Fact]
    public void OnlyTheDaysOwnRecordingsCount()
    {
        AddStretch(At(-1, 50), 5, "Late", "yesterday");
        AddStretch(At(10, 0), 5, "Code", "today");
        AddStretch(At(24, 0), 5, "Early", "tomorrow");
        db.InsertChunk("a.wav", Time.Iso(At(24, 1)), "mic", true);
        var s = db.JournalStats(Day, Tools(db));
        Assert.Equal(new[] { "Code" }, s.Apps.Select(a => a.Name));
        Assert.Equal(At(10, 0), s.First);
        Assert.Empty(s.Audio);
    }

    [Fact]
    public void AudioClipsCloseTogetherMakeOneStretch()
    {
        var clips = new List<(DateTime, bool)>
        {
            (At(14, 0), true), (At(14, 0, 30), false), (At(14, 1), true), (At(14, 3), true),
            (At(16, 0), false),
        };
        var spans = Journal.AudioSpans(clips, 30, At(23, 0));
        Assert.Equal(2, spans.Count);
        Assert.Equal((At(14, 0), At(14, 3, 30), true, true), (spans[0].Start, spans[0].End, spans[0].Mic, spans[0].System));
        Assert.Equal((At(16, 0), At(16, 0, 30), false, true), (spans[1].Start, spans[1].End, spans[1].Mic, spans[1].System));
        var lines = Journal.Sections(new JournalStats(Day, null, null, 0, new(), new(), new(), spans)).Single(x => x.Title == "Audio recorded").Lines;
        Assert.EndsWith("microphone and system audio", lines[0].Text);
        Assert.EndsWith("· system audio", lines[1].Text);
        Assert.Equal(At(14, 0), lines[0].At);
    }

    [Fact]
    public void AnEmptyDaySaysSo()
    {
        var s = db.JournalStats(Day, Tools(db));
        Assert.True(s.IsEmpty);
        var sections = Journal.Sections(s);
        Assert.Single(sections);
        Assert.Equal("Nothing was recorded on this day.", sections[0].Lines.Single().Text);
    }

    [Fact]
    public void FirstAndLastActivityLinkToTheTimeline()
    {
        var s = Journal.Compute(Day, Rows((At(8, 58), "Code", "", ""), (At(18, 12), "Code", "", "")), new List<(DateTime, bool)>(), _ => false, _ => false, At(23, 0));
        var day = Journal.Sections(s)[0];
        Assert.Equal("The day", day.Title);
        Assert.Equal(At(8, 58), day.Lines[0].At);
        Assert.Equal(At(18, 12), day.Lines[1].At);
        Assert.Null(day.Lines[2].At);
        Assert.StartsWith("Active time: ", day.Lines[2].Text);
    }

    [Fact]
    public void MarkdownHasTheRecapWithoutAppLinksAndEscapesTitles()
    {
        var s = Journal.Compute(Day, Rows((At(9, 0), "Code", "notes_[draft]*.md", ""), (At(9, 2), "Code", "notes_[draft]*.md", "")),
                                new List<(DateTime, bool)>(), _ => false, _ => false, At(23, 0));
        var recap = new JournalRecap(Journal.Key(Day), "**What I worked on**\n- Notes at [9:00 AM](openrewind://t/123)", DateTime.UtcNow, "anthropic", "m", default, null);
        var md = Journal.Markdown(s, recap);
        Assert.StartsWith($"# {Journal.DayTitle(Day)}\n", md);
        Assert.Contains("## Recap\n\n**What I worked on**\n- Notes at 9:00 AM\n", md);
        Assert.DoesNotContain("openrewind://", md);
        Assert.Contains(@"“notes\_\[draft\]\*.md” (Code)", md);
        Assert.Contains("## Top apps\n\n- Code — 2m\n", md);
    }

    [Fact]
    public void AutomaticRecapIsForYesterdayOnce()
    {
        var now = new DateTime(2026, 10, 19, 0, 5, 0, DateTimeKind.Local);
        Assert.Equal("2026-10-18", Journal.AutoRecapDay(now, null));
        Assert.Equal("2026-10-18", Journal.AutoRecapDay(now, "2026-10-17"));
        Assert.Null(Journal.AutoRecapDay(now.AddHours(20), "2026-10-18"));
        Assert.Equal("2026-10-19", Journal.AutoRecapDay(now.AddDays(1), "2026-10-18"));
    }

    [Fact]
    public void RecapsAreSavedReplacedAndDeletedWithTheirRecordings()
    {
        var key = Journal.Key(Day);
        Assert.Null(db.Recap(key));
        db.SaveRecap(new JournalRecap(key, "first", At(23, 0), "openai", "gpt-x", new AiUsage(100, 10), 0.5));
        db.SaveRecap(new JournalRecap(key, "second", At(23, 30), "anthropic", "claude-x", new AiUsage(200, 20), null));
        var r = db.Recap(key)!;
        Assert.Equal(("second", "anthropic", "claude-x", new AiUsage(200, 20), (double?)null), (r.Text, r.Provider, r.Model, r.Usage, r.Cost));
        Assert.Equal(At(23, 30), r.Created);

        var other = Journal.Key(Day.AddDays(3));
        db.SaveRecap(new JournalRecap(other, "later", At(23, 0), "", "", default, 0.01));
        // Deleting part of a day's recordings removes that day's recap, and only that day's.
        db.DeleteFrames(At(12, 0), At(12, 15));
        Assert.Null(db.Recap(key));
        Assert.Equal(0.01, db.Recap(other)!.Cost);
        db.DeleteAudio(null, DateTime.UtcNow);
        Assert.Null(db.Recap(other));
    }

    [Fact]
    public void RecapRangeCoversEveryDayTouched()
    {
        Assert.Equal(("2026-03-10", "2026-03-10"), Journal.RecapRange(At(12, 0), At(12, 15)));
        Assert.Equal(("2026-03-10", "2026-03-10"), Journal.RecapRange(At(0, 0), At(24, 0)));
        Assert.Equal(("0000-00-00", "2026-03-11"), Journal.RecapRange(null, At(24, 1)));
        Assert.Equal("9999-12-31", Journal.RecapRange(At(1, 0), null).To);
    }

    sealed class RecordingClient : IAiClient
    {
        public readonly Queue<AiReply> Replies = new();
        public readonly List<string> Sent = new();
        public string System = "";
        public Task<AiReply> Send(string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolSpec> tools, CancellationToken ct)
        {
            System = system;
            Sent.Add(string.Join("\n", messages.Select(m => m.Text)));
            return Replies.Count > 0 ? Task.FromResult(Replies.Dequeue()) : throw new AiException("no more replies");
        }
    }

    [Fact]
    public async Task RecapSendsTheDaysLookupsWithoutBlockedContent()
    {
        AddStretch(At(9, 0), 10, "Code", "journal.cs");
        AddStretch(At(9, 10), 5, "1Password", "Vault");
        AddStretch(At(9, 15), 5, "chrome", "Chase Online", "https://www.chase.com/");
        AddStretch(At(9, 20), 5, "chrome", "Notes", "https://notes.example.com/", "budget for Q3");
        var chunk = db.InsertChunk("a.wav", Time.Iso(At(11, 0)), "mic", true);
        db.SaveTranscripts(db.ChunkById(chunk)!, new[] { new Segment(0, 3, "Let's ship it Friday, Sam.") });
        var client = new RecordingClient();
        client.Replies.Enqueue(new AiReply("", new() { new("c1", "search_screen", new JsonObject { ["query"] = "budget" }) }, new AiUsage(1000, 50)));
        client.Replies.Enqueue(new AiReply("**What I worked on**\n- The journal", new(), new AiUsage(2000, 100)));

        var r = await JournalWriter.Write(Day, client, Tools(db), new AiPrice(1, 10));

        Assert.Null(r.Error);
        Assert.Equal("**What I worked on**\n- The journal", r.Text);
        Assert.Equal(new AiUsage(3000, 150), r.Usage);
        Assert.Equal((3000 * 1 + 150 * 10) / 1_000_000.0, r.Cost!.Value, 9);
        var first = client.Sent[0];
        Assert.Contains("journal.cs", first);
        Assert.Contains("Let's ship it Friday, Sam.", first);
        Assert.DoesNotContain("Vault", first);
        Assert.DoesNotContain("Chase", first);
        Assert.DoesNotContain("chase.com", first);
        Assert.Contains("budget for Q3", client.Sent[1]);
        Assert.Equal(new[] { "activity_summary", "search_audio", "search_screen" }, r.Lookups.Select(l => l.Args.Split(' ')[0]));
        Assert.Contains("**Open loops**", client.System);
    }

    [Fact]
    public async Task RecapErrorsKeepTheTokensUsed()
    {
        var client = new RecordingClient();
        client.Replies.Enqueue(new AiReply("", new() { new("c1", "search_audio", new JsonObject()) }, new AiUsage(500, 5)));
        var r = await JournalWriter.Write(Day, client, Tools(db), null);
        Assert.Equal("no more replies", r.Error);
        Assert.Equal("", r.Text);
        Assert.Equal(new AiUsage(500, 5), r.Usage);
        Assert.Null(r.Cost);
    }
}
