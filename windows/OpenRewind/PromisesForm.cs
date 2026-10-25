using System.Drawing;

namespace OpenRewind;

/// "You promised" reminders: looks for promises after each meeting the user took notes for and every
/// 3 hours in chat and mail screen text, and shows a balloon when one is due. Runs on the UI thread;
/// the AI requests run in the background. Mac: PromiseCenter (Promises.swift).
sealed class PromiseJobs : IDisposable
{
    readonly Database db;
    readonly Settings settings;
    readonly Action<string, string> notify;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 60_000 };
    bool scanning;
    public static readonly TimeSpan ScanEvery = TimeSpan.FromHours(3);

    /// Raised on the UI thread when promises were found or changed.
    public static event Action? Changed;
    /// The last thing it did, for the list window.
    public string Status { get; private set; } = "";
    /// App Lock is locked: reminders then say only that one is due, not what it is.
    public Func<bool> Locked { get; set; } = () => false;

    public PromiseJobs(Database db, Settings settings, Action<string, string> notify)
    {
        this.db = db; this.settings = settings; this.notify = notify;
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    /// The settings to send with, when it is on and AI Settings still point at the provider agreed to.
    AiSettings? ReadyAi()
    {
        if (!settings.PromisesOn) return null;
        var ai = settings.Ai();
        return ai.IsReady && settings.PromisesProvider == MeetingNotes.ConsentKey(ai) ? ai : null;
    }

    public bool Paused => settings.PromisesOn && settings.PromisesProvider != MeetingNotes.ConsentKey(settings.Ai());

    void Tick()
    {
        var list = Blocklist.Current;
        bool locked = Locked(), told = false;
        foreach (var p in db.DuePromises(DateTime.UtcNow))
        {
            db.MarkPromiseNotified(p.Id);
            if (Promises.Hidden(p, list)) continue;
            if (!locked) notify("You promised", Promises.Describe(p));
            else if (!told) { notify("You promised", "Something you promised is due. Unlock OpenRewind to see it."); told = true; }
        }
        if (DateTime.UtcNow - (settings.PromisesLastScan ?? DateTime.MinValue) >= ScanEvery) ScanScreen();
    }

    /// A meeting ended (MeetingWatcher): its transcript, if the user took notes for it.
    public void MeetingEnded(Meeting m)
    {
        if (m.Choice != MeetingChoice.Take || ReadyAi() is not AiSettings ai || MeetingDetector.Hides(Blocklist.Current, m)) return;
        var lines = db.MeetingTranscriptLines(m).Select(l => l with { Text = Secrets.Redact(l.Text) }).ToList();
        Run(ai, lines, $"meeting: {m.Name}");
    }

    /// Chat and mail screen text since the last scan (at most the last 3 hours). `force` is Scan Now.
    public void ScanScreen(bool force = false)
    {
        if (scanning) return;
        if (ReadyAi() is not AiSettings ai)
        {
            if (force) Status = settings.PromisesOn ? "AI Settings point at another provider. Turn reminders off and on to use it." : "Turn on promise reminders in Settings → AI.";
            return;
        }
        var now = DateTime.UtcNow;
        var from = new[] { settings.PromisesLastScan ?? DateTime.MinValue, now - ScanEvery }.Max();
        settings.PromisesLastScan = now;
        settings.Save();
        var list = Blocklist.Current;
        scanning = true;
        var ctx = SynchronizationContext.Current;
        Task.Run(() =>
        {
            var byApp = Promises.ScreenLines(db.FramesForPromises(from, now), list).GroupBy(l => l.Speaker).ToList();
            ctx?.Post(_ =>
            {
                scanning = false;
                if (byApp.Count == 0) { if (force) Status = "Nothing new from chat or mail apps since the last scan."; Changed?.Invoke(); return; }
                foreach (var g in byApp) Run(ai, g.ToList(), $"screen: {g.Key}");
            }, null);
        });
    }

    void Run(AiSettings ai, List<TranscriptLine> lines, string source)
    {
        if (lines.Count == 0) return;
        Status = "Looking for promises…";
        var ctx = SynchronizationContext.Current;
        _ = Task.Run(async () =>
        {
            string status;
            try
            {
                var (found, usage) = await Promises.Extract(new AiClient(ai), lines, source);
                var saved = found.Count(f => db.AddPromise(f, source) != null);
                Log.Write($"promises: {saved} new from {RecordingFile.ForLog(source)}");
                ctx?.Post(_ => settings.AddAiUsage(ai.Kind, usage, usage.Cost(settings.AiPriceFor(ai.Model))), null);
                status = saved == 0 ? "No new promises found." : $"Found {saved} new promise{(saved == 1 ? "" : "s")}.";
            }
            catch (Exception e)
            {
                Log.Write($"promises: {e.Message}");
                status = $"Could not look for promises: {e.Message}";
            }
            ctx?.Post(_ => { Status = status; Changed?.Invoke(); }, null);
        });
    }

    /// What turning reminders on sends, and where. Same words as the Mac app.
    public static string ConsentText(string provider) =>
        $"OpenRewind sends to {provider}, using your key: the transcript of each meeting you take notes for, after it ends, and every " +
        "3 hours the text read off your screen in chat and mail apps (Slack, Teams, Messages, Mail, Outlook, WhatsApp, Discord, web mail…) " +
        "since the last look, up to about 12,000 characters each time.\n\n" +
        "Blocked apps, sites and words are never sent, and passwords, keys and card numbers are taken out first. This costs tokens " +
        $"on your account and {provider}'s privacy terms apply. If you switch providers in AI Settings, it pauses until you turn it on again.";

    public void Dispose() => timer.Dispose();
}

/// You Promised: open promises by when they are due (or all), with Done, Snooze, Dismiss and a jump to
/// the moment it was said. Mac: PromiseListView.
sealed class PromisesForm : Form
{
    static PromisesForm? open;
    readonly Database db;
    readonly PromiseJobs jobs;
    readonly Action<DateTime> openMoment;
    readonly ListView list;
    readonly CheckBox all;
    readonly Label status;
    List<Promise> items = new();

    public static void Open(Database db, PromiseJobs jobs, Action<DateTime> openMoment)
    {
        if (open is { IsDisposed: false }) { open.Activate(); open.Reload(); return; }
        open = new PromisesForm(db, jobs, openMoment);
        open.Show();
    }

    /// Closes it (App Lock locked).
    public static void CloseIfOpen() { if (open is { IsDisposed: false }) open.Close(); }

    PromisesForm(Database db, PromiseJobs jobs, Action<DateTime> openMoment)
    {
        this.db = db; this.jobs = jobs; this.openMoment = openMoment;
        var k = DeviceDpi / 96f;
        Text = "You Promised";
        Icon = Program.AppIcon;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(13 * k);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size((int)(720 * k), (int)(520 * k));
        MinimumSize = new Size((int)(560 * k), (int)(360 * k));

        var top = new Panel { Dock = DockStyle.Top, Height = (int)(84 * k), BackColor = Ui.Bg, Padding = new Padding((int)(16 * k), (int)(12 * k), (int)(16 * k), 0) };
        var title = new Label { Text = "You promised", Font = Ui.F(19 * k, true), AutoSize = true, Location = new Point((int)(16 * k), (int)(12 * k)) };
        all = new CheckBox { Text = "Show done and dismissed", AutoSize = true, ForeColor = Ui.Dim, Location = new Point((int)(200 * k), (int)(18 * k)) };
        all.CheckedChanged += (_, _) => Reload();
        var scan = new PillButton("Scan Now") { Font = Ui.F(12.5f * k, true), Size = new Size((int)(110 * k), (int)(30 * k)) };
        scan.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        scan.Location = new Point(ClientSize.Width - (int)(16 * k) - scan.Width, (int)(12 * k));
        scan.Click += (_, _) => { jobs.ScanScreen(force: true); status!.Text = jobs.Status; };
        status = new Label { AutoSize = false, ForeColor = Ui.Faint, Font = Ui.F(12 * k), Location = new Point((int)(16 * k), (int)(52 * k)),
                             Size = new Size(ClientSize.Width - (int)(32 * k), (int)(24 * k)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        top.Controls.AddRange(new Control[] { title, all, scan, status });

        list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, BorderStyle = BorderStyle.None,
            BackColor = Ui.Card, ForeColor = Ui.Text, Font = Ui.F(13 * k), HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        list.Columns.Add("Promise", (int)(300 * k));
        list.Columns.Add("For", (int)(110 * k));
        list.Columns.Add("Due", (int)(140 * k));
        list.Columns.Add("From", (int)(150 * k));
        list.DoubleClick += (_, _) => Jump();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = (int)(52 * k), BackColor = Ui.Bg, Padding = new Padding((int)(12 * k), (int)(9 * k), 0, 0) };
        void Btn(string t, Action a, bool primary = false)
        {
            var b = new PillButton(t, primary) { Font = Ui.F(12.5f * k, true), Size = new Size(TextRenderer.MeasureText(t, Ui.F(12.5f * k, true)).Width + (int)(32 * k), (int)(32 * k)),
                                                Margin = new Padding(0, 0, (int)(8 * k), 0) };
            b.Click += (_, _) => a();
            bar.Controls.Add(b);
        }
        Btn("Done", () => Set(Promises.Done), true);
        Btn("Snooze 1 Hour", () => Snooze(TimeSpan.FromHours(1)));
        Btn("Snooze to Tomorrow", () => Snooze(TimeSpan.FromDays(1)));
        Btn("Dismiss", () => Set(Promises.Dismissed));
        Btn("Reopen", () => Set(Promises.Open));
        Btn("Go to Moment", Jump);

        Controls.Add(list);
        Controls.Add(bar);
        Controls.Add(top);
        PromiseJobs.Changed += OnChanged;
        FormClosed += (_, _) => { PromiseJobs.Changed -= OnChanged; open = null; };
        Reload();
    }

    void OnChanged() { if (!IsDisposed) Reload(); }

    void Reload()
    {
        var bl = Blocklist.Current;
        items = db.PromiseList(all.Checked ? null : Promises.Open).Where(p => !Promises.Hidden(p, bl)).ToList();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var p in items)
        {
            var when = p.RemindAt is DateTime d ? (p.SnoozedUntil != null ? "remind " : "") + d.ToLocalTime().ToString("ddd d MMM, t") : "";
            var it = new ListViewItem(new[] { p.Status == Promises.Open ? p.What : $"[{p.Status}] {p.What}", p.Who, when, p.Source });
            if (p.Status == Promises.Open && p.RemindAt is DateTime r && r <= DateTime.UtcNow) it.ForeColor = Ui.Warn;
            if (p.Status != Promises.Open) it.ForeColor = Ui.Faint;
            if (p.Quote.Length > 0) it.ToolTipText = $"“{p.Quote}”";
            list.Items.Add(it);
        }
        list.ShowItemToolTips = true;
        list.EndUpdate();
        status.Text = items.Count == 0
            ? (jobs.Status.Length > 0 ? jobs.Status + " " : "") + "No promises here yet. They show up after meetings and chats."
            : jobs.Status;
    }

    Promise? Selected => list.SelectedIndices.Count == 1 ? items[list.SelectedIndices[0]] : null;

    void Set(string s) { if (Selected is Promise p) { db.SetPromiseStatus(p.Id, s); Reload(); } }
    void Snooze(TimeSpan t) { if (Selected is Promise p) { db.SnoozePromise(p.Id, DateTime.UtcNow + t); Reload(); } }
    void Jump() { if (Selected is Promise p) openMoment(p.Time); }
}
