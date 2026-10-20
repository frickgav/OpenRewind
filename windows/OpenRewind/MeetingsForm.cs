using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Finds meetings once a minute (MeetingTracker) and, when the user turned on
/// "Summarize Meetings with AI", asks their provider for notes after each one ends.
/// The Mac app does the same in MeetingCenter (Meetings.swift).
sealed class MeetingWatcher : IDisposable
{
    readonly Database db;
    readonly Settings settings;
    readonly double chunkSeconds;
    /// Whether Whisper is set up (clips waiting for it are speech still to come).
    readonly Func<bool> transcribing;
    /// Runs an action on the UI thread.
    readonly Action<Action> ui;
    System.Threading.Timer? timer;
    int running;
    readonly HashSet<long> summarizing = new();

    /// Raised on the UI thread when meetings were added, changed or summarized.
    public static event Action? Changed;

    public MeetingWatcher(Database db, Settings settings, double chunkSeconds, Func<bool> transcribing, Action<Action> ui)
    {
        this.db = db; this.settings = settings; this.chunkSeconds = chunkSeconds; this.transcribing = transcribing; this.ui = ui;
    }

    public void Start() => timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1));

    void Tick()
    {
        if (Interlocked.Exchange(ref running, 1) == 1) return;
        try
        {
            var now = DateTime.UtcNow;
            var ended = MeetingTracker.Update(db, Blocklist.Current, now, chunkSeconds, transcribing());
            foreach (var m in ended) Log.Write($"meeting ended: {m.App}, {m.Start.ToLocalTime():t}–{m.End.ToLocalTime():t}");
            var late = settings.SummarizeMeetings ? MeetingTracker.Late(db, now) : new();
            var recent = db.MeetingsBetween(now - TimeSpan.FromDays(1), now);
            ui(() =>
            {
                // Take or skip what the user already chose for; ask about calls going on now.
                var plan = MeetingPrompt.PlanFor(recent, settings.AskBeforeMeetingNotes, settings.MeetingChoices, Blocklist.Current);
                foreach (var (id, choice) in plan.Decided) db.SetMeetingChoice(id, choice);
                foreach (var m in plan.Ask) Ask(m);
                foreach (var (id, form) in prompts.ToList())
                    if (MeetingPrompt.Closes(db.MeetingById(id), now)) { prompts.Remove(id); form.Close(); }
                Changed?.Invoke();
                foreach (var m in ended)
                    if (db.MeetingById(m.Id) is Meeting fresh && AutoSummarizes(fresh)) _ = Summarize(fresh);
                // Transcripts that came in after the notes were made: make them again, once.
                foreach (var m in late)
                    if (!summarizing.Contains(m.Id) && AutoSummarizes(m))
                    {
                        db.MarkMeetingRedone(m.Id);
                        Log.Write($"meeting {m.Id}: more of it was transcribed, summarizing again");
                        _ = Summarize(m);
                    }
            });
        }
        catch (Exception e) { Log.Write($"meeting check failed: {e.Message}"); }
        finally { Interlocked.Exchange(ref running, 0); }
    }

    readonly Dictionary<long, MeetingPromptForm> prompts = new();

    /// Shows "Meeting detected. Take notes?" once per meeting, without taking focus from the call.
    void Ask(Meeting m)
    {
        if (prompts.ContainsKey(m.Id)) return;
        var ai = settings.Ai();
        var summarizer = settings.SummarizeMeetings && settings.SummarizeMeetingsProvider == MeetingNotes.ConsentKey(ai) && ai.IsReady
            ? ai.Kind.Title() : null;
        var form = new MeetingPromptForm(m, summarizer, (take, remember) => Answer(m, take, remember));
        prompts[m.Id] = form;
        Log.Write($"meeting detected: {m.App}, asking about notes");
        form.ShowNear(prompts.Count - 1);
    }

    /// The user answered the popup. Call on the UI thread.
    void Answer(Meeting m, bool take, bool remember)
    {
        if (prompts.Remove(m.Id, out var form)) form.Close();
        db.SetMeetingChoice(m.Id, take ? MeetingChoice.Take : MeetingChoice.Skip);
        if (remember)
        {
            settings.MeetingChoices[m.App] = take ? MeetingPrompt.Always : MeetingPrompt.Never;
            settings.Save();
        }
        Log.Write($"meeting {m.Id}: {(take ? "take notes" : "not this time")}{(remember ? $", remembered for {m.App}" : "")}");
        Changed?.Invoke();
        // Answered after the call ended: summarize now, as it would have been when it ended.
        if (take && db.MeetingById(m.Id) is Meeting fresh && !fresh.IsLive && fresh.Status == MeetingStatus.Ended && AutoSummarizes(fresh))
            _ = Summarize(fresh);
    }

    /// Only meetings that ended after the user turned the setting on (so turning it on does not spend
    /// tokens on older ones), and only to the provider they agreed to.
    bool AutoSummarizes(Meeting m) =>
        MeetingNotes.AutoSummarizes(m, settings.SummarizeMeetings, settings.SummarizeMeetingsSince, settings.SummarizeMeetingsProvider,
                                    settings.Ai(), Blocklist.Current);

    /// After AI Settings change: summaries were agreed to for another provider, so ask before sending
    /// transcripts to this one. Call on the UI thread.
    public static void RecheckConsent(Settings settings, IWin32Window? owner)
    {
        if (!settings.SummarizeMeetings) return;
        var ai = settings.Ai();
        var key = MeetingNotes.ConsentKey(ai);
        if (settings.SummarizeMeetingsProvider == key || !ai.IsReady) return;
        var was = settings.SummarizeMeetingsProvider is string old ? AiProviders.FromId(old.Split(' ')[0]).Title() : "another provider";
        var text = $"Meeting summaries were turned on for {was}. AI Settings now use {ai.Kind.Title()}"
                 + (ai.Kind == AiProviderKind.Custom ? $" ({ai.BaseUrl})" : "") + ".\n\n"
                 + $"Send each meeting's transcript to {ai.Kind.Title()} from now on? It uses your key and costs tokens. "
                 + "If you choose No, meeting summaries are turned off.";
        var yes = MessageBox.Show(owner, text, "Summarize meetings with the new provider?", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                                  MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        if (yes) settings.SummarizeMeetingsProvider = key;
        else { settings.SummarizeMeetings = false; settings.SummarizeMeetingsSince = null; settings.SummarizeMeetingsProvider = null; }
        settings.Save();
        Log.Write(yes ? "meeting summaries moved to the new provider" : "meeting summaries off (provider changed)");
    }

    public bool IsSummarizing(long id) => summarizing.Contains(id);

    /// Call on the UI thread. Returns an error message, or null when the notes were saved.
    public async Task<string?> Summarize(Meeting m)
    {
        var ai = settings.Ai();
        if (!ai.IsReady) return "Set up a provider in AI Settings first.";
        if (!summarizing.Add(m.Id)) return null;
        Changed?.Invoke();
        var price = settings.AiPriceFor(ai.Model);
        var segments = 0;
        try
        {
            m = db.MeetingById(m.Id) ?? m;
            var segs = await Task.Run(() => db.MeetingSegments(m.Start, m.End));
            segments = segs.Count;
            // AiClient gives up after 180 seconds, so a request that hangs ends as a failed summary.
            var r = await MeetingNotes.Summarize(new AiClient(ai), m, MeetingTranscript.Build(segs));
            var cost = r.Usage.Cost(price);
            db.SaveMeetingNotes(m.Id, r.Notes, ai.Model, r.Usage, cost, segments);
            settings.AddAiUsage(ai.Kind, r.Usage, cost);
            Log.Write($"meeting {m.Id} summarized with {ai.Model}: {r.Usage.Describe(price)}");
            return null;
        }
        catch (Exception e)
        {
            var msg = e is AiException ? e.Message : $"Something went wrong: {e.Message}";
            db.MeetingFailed(m.Id, msg, segments);
            Log.Write($"meeting {m.Id} summary failed: {msg}");
            return msg;
        }
        finally
        {
            summarizing.Remove(m.Id);
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        timer?.Dispose();
        foreach (var f in prompts.Values) f.Close();
        prompts.Clear();
    }
}

/// "Meeting detected in Zoom. Take notes?" in the corner above the tray. It never takes focus from
/// the call: it shows without activating, and clicking it does not activate it either.
sealed class MeetingPromptForm : Form
{
    readonly Action<bool, bool> answer;
    readonly CheckBox remember;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x00000008 /* WS_EX_TOPMOST */;
            return p;
        }
    }

    public MeetingPromptForm(Meeting m, string? summarizer, Action<bool, bool> answer)
    {
        this.answer = answer;
        var k = DeviceDpi / 96f;
        Text = MeetingPrompt.Title(m);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        Font = Ui.F(13 * k);
        Padding = new Padding((int)(16 * k));
        ClientSize = new Size((int)(380 * k), (int)(214 * k));

        var title = new Label { Text = MeetingPrompt.Title(m), Font = Ui.F(15 * k, true), AutoSize = false, AutoEllipsis = true,
                                Location = new Point((int)(16 * k), (int)(14 * k)), Size = new Size((int)(348 * k), (int)(26 * k)) };
        var body = new Label { Text = MeetingPrompt.Body(summarizer), Font = Ui.F(12.5f * k), ForeColor = Ui.Text,
                               Location = new Point((int)(16 * k), (int)(44 * k)), Size = new Size((int)(348 * k), (int)(54 * k)) };
        var help = new Label { Text = MeetingPrompt.Help, Font = Ui.F(11 * k), ForeColor = Ui.Dim,
                               Location = new Point((int)(16 * k), (int)(100 * k)), Size = new Size((int)(348 * k), (int)(36 * k)) };
        remember = new CheckBox { Text = MeetingPrompt.Remember(m), Font = Ui.F(12 * k), ForeColor = Ui.Text, AutoSize = true,
                                  Location = new Point((int)(16 * k), (int)(140 * k)) };
        var take = new PillButton("Take Notes", true) { Font = Ui.F(12.5f * k, true), Size = new Size((int)(120 * k), (int)(32 * k)) };
        var skip = new PillButton("Not This Time", false) { Font = Ui.F(12.5f * k, true), Size = new Size((int)(130 * k), (int)(32 * k)) };
        take.Location = new Point(ClientSize.Width - (int)(16 * k) - take.Width, (int)(170 * k));
        skip.Location = new Point(take.Left - (int)(8 * k) - skip.Width, (int)(170 * k));
        take.Click += (_, _) => answer(true, remember.Checked);
        skip.Click += (_, _) => answer(false, remember.Checked);
        Controls.AddRange(new Control[] { title, body, help, remember, skip, take });
        Paint += (_, e) => { using var pen = new Pen(Ui.Stroke); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); };
    }

    /// Bottom right of the screen with the tray, above any taskbar; later popups stack upwards.
    public void ShowNear(int index)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var gap = (int)(12 * DeviceDpi / 96f);
        Location = new Point(area.Right - Width - gap, area.Bottom - (Height + gap) * (index + 1));
        Show();
    }
}

/// Meetings: calls by day on the left; the selected one's notes and transcript on the right.
/// Same window as MeetingsView on the Mac.
sealed class MeetingsForm : Form
{
    static MeetingsForm? open;
    readonly Database db;
    readonly Settings settings;
    readonly MeetingWatcher watcher;
    readonly Action<DateTime> openMoment;
    readonly Action openSettings;
    readonly float k;
    readonly ListBox list;
    readonly Panel detail, empty;
    readonly Label title, when, status;
    readonly PillButton timelineButton, copyButton, saveButton, summarizeButton;
    readonly RichTextBox body;
    Meeting? shown;

    public static void ShowFor(Database db, Settings settings, MeetingWatcher watcher, Action<DateTime> openMoment, Action openSettings)
    {
        if (open is { IsDisposed: false }) { open.Reload(); open.Activate(); return; }
        open = new MeetingsForm(db, settings, watcher, openMoment, openSettings);
        open.Show();
        open.Activate();
    }

    MeetingsForm(Database db, Settings settings, MeetingWatcher watcher, Action<DateTime> openMoment, Action openSettings)
    {
        this.db = db; this.settings = settings; this.watcher = watcher; this.openMoment = openMoment; this.openSettings = openSettings;
        k = DeviceDpi / 96f;
        Text = "Meetings";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size((int)(720 * k), (int)(480 * k));
        ClientSize = new Size((int)(1000 * k), (int)(700 * k));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(14 * k);
        HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); };

        list = new ListBox
        {
            Dock = DockStyle.Left, Width = (int)(300 * k), BorderStyle = BorderStyle.None, BackColor = Ui.Card, ForeColor = Ui.Text,
            DrawMode = DrawMode.OwnerDrawVariable, IntegralHeight = false,
        };
        list.MeasureItem += (_, e) => e.ItemHeight = list.Items[e.Index] is Meeting ? (int)(52 * k) : (int)(34 * k);
        list.DrawItem += DrawRow;
        list.SelectedIndexChanged += (_, _) => ShowSelected();

        // Right side: title, time, buttons, then the notes and transcript.
        detail = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding((int)(24 * k), (int)(16 * k), (int)(24 * k), (int)(16 * k)) };
        var head = new Panel { Dock = DockStyle.Top, Height = (int)(150 * k), BackColor = Ui.Bg };
        title = new Label { Font = Ui.F(20 * k, true), AutoEllipsis = true, Location = new Point(0, 0), Height = (int)(32 * k) };
        when = new Label { Font = Ui.F(13 * k), ForeColor = Ui.Dim, Location = new Point(0, (int)(34 * k)), Height = (int)(22 * k) };
        timelineButton = Button("Open in Timeline", 150);
        copyButton = Button("Copy as Markdown", 160);
        saveButton = Button("Save as Markdown…", 170);
        summarizeButton = Button("Summarize", 130, primary: true);
        status = new Label { Font = Ui.F(12 * k), ForeColor = Ui.Faint, Location = new Point(0, (int)(110 * k)), Height = (int)(36 * k) };
        var x = 0;
        foreach (var b in new[] { timelineButton, copyButton, saveButton, summarizeButton })
        {
            b.Location = new Point(x, (int)(66 * k));
            x += b.Width + (int)(8 * k);
        }
        head.Resize += (_, _) => { title.Width = when.Width = status.Width = head.Width; };
        head.Controls.AddRange(new Control[] { title, when, timelineButton, copyButton, saveButton, summarizeButton, status });
        timelineButton.Click += (_, _) => { if (shown != null) openMoment(shown.Start); };
        copyButton.Click += (_, _) => { if (shown != null) { Clipboard.SetText(Markdown(shown)); status.Text = "Copied as Markdown."; } };
        saveButton.Click += (_, _) => SaveMarkdown();
        summarizeButton.Click += async (_, _) => await SummarizeShown();
        body = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Ui.Bg, ForeColor = Ui.Text,
            Font = Ui.F(14 * k), DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical,
        };
        detail.Controls.Add(body);
        detail.Controls.Add(head);

        empty = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding((int)(48 * k)), Visible = false };
        empty.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Ui.Dim, Font = Ui.F(14 * k),
            Text = "No meetings yet.\n\nWhen a Zoom, Teams, Google Meet, Webex or Slack huddle call starts, OpenRewind asks whether to take "
                 + "notes. Calls you take notes for show up here with their transcript.\n\nTurn on Settings → Summarize Meetings with AI to "
                 + "also get a summary and action items after each one, using your own AI key.",
        });

        Controls.Add(detail);
        Controls.Add(empty);
        Controls.Add(new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Ui.Stroke });
        Controls.Add(list);
        MeetingWatcher.Changed += Reload;
        Reload();
    }

    PillButton Button(string text, int width, bool primary = false) =>
        new(text, primary) { Font = Ui.F(12.5f * k, true), Size = new Size((int)(width * k), (int)(34 * k)) };

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        MeetingWatcher.Changed -= Reload;
        open = null;
        base.OnFormClosed(e);
    }

    /// Fills the list by day, newest first, and keeps the selection.
    void Reload()
    {
        if (IsDisposed) return;
        var keep = shown?.Id;
        var blocked = Blocklist.Current;
        var meetings = db.ShownMeetings().Where(m => !MeetingDetector.Hides(blocked, m)).ToList();
        list.BeginUpdate();
        list.Items.Clear();
        DateTime? day = null;
        foreach (var m in meetings)
        {
            var d = m.Start.ToLocalTime().Date;
            if (d != day) { list.Items.Add(DayTitle(d)); day = d; }
            list.Items.Add(m);
        }
        list.EndUpdate();
        empty.Visible = meetings.Count == 0;
        detail.Visible = meetings.Count > 0;
        var pick = meetings.FirstOrDefault(m => m.Id == keep) ?? meetings.FirstOrDefault();
        if (pick != null) list.SelectedIndex = list.Items.IndexOf(list.Items.OfType<Meeting>().First(m => m.Id == pick.Id));
        else shown = null;
    }

    static string DayTitle(DateTime d) =>
        d == DateTime.Today ? "Today" : d == DateTime.Today.AddDays(-1) ? "Yesterday" : d.ToString("dddd d MMMM yyyy");

    void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var g = e.Graphics;
        Ui.Smooth(g);
        var item = list.Items[e.Index];
        var r = e.Bounds;
        using (var back = new SolidBrush(Ui.Card)) g.FillRectangle(back, r);
        int pad = (int)(14 * k);
        if (item is Meeting m)
        {
            if ((e.State & DrawItemState.Selected) != 0)
                Ui.Fill(g, new RectangleF(r.X + 6 * k, r.Y + 2 * k, r.Width - 12 * k, r.Height - 4 * k), 8 * k, Ui.Accent);
            var time = $"{m.Start.ToLocalTime():t}–{m.End.ToLocalTime():t}";
            var line2 = m.App + " · " + (m.IsLive ? "In progress" : watcher.IsSummarizing(m.Id) ? "Summarizing…"
                : m.HasSummary ? "Summarized" : m.Status == MeetingStatus.Failed ? "Summary failed" : Duration(m));
            TextRenderer.DrawText(g, m.Name, Ui.F(13.5f * k, true), new Rectangle(r.X + pad, r.Y + (int)(7 * k), r.Width - 2 * pad - (int)(110 * k), (int)(22 * k)),
                                  Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, time, Ui.F(12 * k), new Rectangle(r.Right - pad - (int)(110 * k), r.Y + (int)(8 * k), (int)(110 * k), (int)(20 * k)),
                                  Ui.Dim, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, line2, Ui.F(12 * k), new Rectangle(r.X + pad, r.Y + (int)(28 * k), r.Width - 2 * pad, (int)(20 * k)),
                                  Ui.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        else
            TextRenderer.DrawText(g, item.ToString(), Ui.F(12 * k, true), new Rectangle(r.X + pad, r.Y + (int)(10 * k), r.Width - 2 * pad, (int)(20 * k)),
                                  Ui.Faint, TextFormatFlags.NoPrefix);
    }

    static string Duration(Meeting m) => AiActivity.Duration((m.End - m.Start).TotalSeconds);

    int lastIndex = -1;

    void ShowSelected()
    {
        // Day titles are not selectable: skip to the next meeting in the direction the user went.
        var i = list.SelectedIndex;
        if (list.SelectedItem is string)
        {
            var step = i < lastIndex && i > 0 ? -1 : 1;
            if (i + step >= 0 && i + step < list.Items.Count) list.SelectedIndex = i + step;
            return;
        }
        lastIndex = i;
        if (list.SelectedItem is not Meeting m) return;
        // The list reloads every minute; leave the text (and where it is scrolled to) alone if nothing changed.
        var fresh = db.MeetingById(m.Id) ?? m;
        var busy = watcher.IsSummarizing(fresh.Id);
        if (fresh == shown && busy == shownBusy) return;
        var same = shown?.Id == fresh.Id;
        shown = fresh;
        shownBusy = busy;
        // A meeting in progress changes every minute: keep the reader's place in it.
        var at = new ScrollPoint();
        if (same) SendMessage(body.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref at);
        body.SuspendLayout();
        Display(fresh);
        if (same) SendMessage(body.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref at);
        else { body.SelectionStart = 0; body.ScrollToCaret(); }
        body.ResumeLayout();
    }

    bool shownBusy;

    const int EM_GETSCROLLPOS = 0x0400 + 221, EM_SETSCROLLPOS = 0x0400 + 222;
    struct ScrollPoint { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref ScrollPoint lParam);

    void Display(Meeting m)
    {
        title.Text = m.Name;
        var start = m.Start.ToLocalTime();
        when.Text = $"{start:dddd d MMMM yyyy}, {start:t}–{m.End.ToLocalTime():t} · {m.App}" + (m.IsLive ? " · In progress" : $" · {Duration(m)}");
        var ai = settings.Ai();
        var busy = watcher.IsSummarizing(m.Id);
        summarizeButton.Text = busy ? "Summarizing…" : m.HasSummary ? "Summarize Again" : "Summarize";
        summarizeButton.Enabled = !busy && !m.IsLive;
        status.Text = busy ? $"Sending the transcript to {ai.Kind.Title()}…"
            : m.IsLive ? "Notes can be made once the meeting ends."
            : m.Status == MeetingStatus.Failed ? "The summary did not work: " + m.Error
            : m.HasSummary ? m.UsageText
            : ai.IsReady ? $"Summarize sends this transcript to {ai.Kind.Title()} using your key."
            : "To get a summary and action items, set up a provider in AI Settings.";

        var lines = db.MeetingTranscriptLines(m);
        body.Clear();
        if (m.HasSummary)
        {
            Section("Summary", m.Summary.Length > 0 ? m.Summary : "None");
            Section("Decisions", m.Decisions.Length > 0 ? m.Decisions : "None");
            Section("Action items", m.ActionItems.Length > 0 ? m.ActionItems : "None");
        }
        Heading("Transcript");
        if (lines.Count == 0) Para(m.IsLive ? "Nothing transcribed yet." : "Nothing was transcribed. Transcripts need Whisper (see the tray menu).", Ui.Dim);
        foreach (var l in lines)
        {
            Append($"{l.Time.ToLocalTime():t}  ", Ui.Faint, Ui.F(12.5f * k));
            Append(l.Speaker + ": ", l.Speaker == MeetingTranscript.Me ? Ui.AccentHi : Ui.Good, Ui.F(14 * k, true));
            Append(l.Text + "\n\n", Ui.Text, Ui.F(14 * k));
        }
    }

    void Section(string heading, string text)
    {
        Heading(heading);
        Para(text.Replace("**", ""), Ui.Text);
    }

    void Heading(string text) => Append(text + "\n", Ui.Text, Ui.F(16 * k, true));
    void Para(string text, Color color) => Append(text.Trim() + "\n\n", color, Ui.F(14 * k));

    void Append(string text, Color color, Font font)
    {
        body.SelectionStart = body.TextLength;
        body.SelectionLength = 0;
        body.SelectionColor = color;
        body.SelectionFont = font;
        body.AppendText(text.Replace("\r\n", "\n"));
    }

    string Markdown(Meeting m) => MeetingNotes.Markdown(m, db.MeetingTranscriptLines(m));

    void SaveMarkdown()
    {
        if (shown is not Meeting m) return;
        using var dlg = new SaveFileDialog { FileName = MeetingNotes.FileName(m), Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*", DefaultExt = "md" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dlg.FileName, Markdown(m)); status.Text = $"Saved to {dlg.FileName}."; }
        catch (Exception e) { status.Text = $"Could not save: {e.Message}"; }
    }

    async Task SummarizeShown()
    {
        if (shown is not Meeting m) return;
        if (!settings.Ai().IsReady) { openSettings(); return; }
        var error = await watcher.Summarize(m);
        if (IsDisposed) return;
        Reload();
        if (error != null && shown?.Id == m.Id) status.Text = "The summary did not work: " + error;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
