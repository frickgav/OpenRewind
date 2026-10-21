using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// The tabs along the top of the Settings window, laid out like Rewind's.
enum SettingsTab { General, Screen, Audio, Meetings, Ai, Privacy, Security, Storage, Shortcuts }

/// Settings: every setting in one window with tabs, like Rewind's. Changes go through the tray app,
/// so they ask (Windows Hello, confirmations) and log exactly as the tray menu did. The window
/// re-reads its values every second, because a change can finish later (Windows Hello, a model
/// download) or come from the tray menu or the timeline.
sealed class SettingsForm : Form
{
    static SettingsForm? open;
    /// The tab to open on next time.
    static SettingsTab last = SettingsTab.General;
    readonly TrayApp app;
    readonly float k;
    readonly TabStrip strip;
    readonly Panel body;
    /// Re-read check boxes, lists and status lines without rebuilding the page.
    readonly List<Action> live = new();
    /// Like `live`, but these read the database, so every few seconds only.
    readonly List<Action> slow = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    int ticks;
    bool syncing;
    SettingsTab tab;

    /// One window at a time; a second click brings it to the front (on `tab`, when given).
    public static void ShowFor(TrayApp app, SettingsTab? tab)
    {
        if (open is { IsDisposed: false })
        {
            if (tab is SettingsTab t && t != open.tab) open.Select(t);
            open.Activate();
            return;
        }
        open = new SettingsForm(app, tab ?? last);
        open.Show();
        open.Activate();
    }

    /// The app locked (see AppLock): Blocked Apps & Sites lists recent pages, so close.
    public static void Release() { if (open is { IsDisposed: false }) open.Close(); }

    public static bool IsShown => open is { IsDisposed: false, Visible: true };

    /// Something changed outside the window (the meaning index, the data folder size): show it now.
    public static void Live() { if (open is { IsDisposed: false }) open.Sync(slowToo: true); }

    SettingsForm(TrayApp app, SettingsTab first)
    {
        this.app = app;
        k = DeviceDpi / 96f;
        Text = "Settings";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimumSize = new Size((int)(760 * k), (int)(560 * k));
        ClientSize = new Size((int)(840 * k), (int)(860 * k));
        // Never taller than the screen; pages scroll instead.
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        if (Height > work.Height - 40) Height = Math.Max(MinimumSize.Height, work.Height - 40);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(14 * k);
        HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); };

        body = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
        strip = new TabStrip(k) { Dock = DockStyle.Top, Height = (int)(96 * k) };
        strip.Picked += Select;
        Controls.Add(body);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = Math.Max(1, (int)k), BackColor = Ui.Stroke });
        Controls.Add(strip);

        // Text wraps to the width the page was built at, so build again after a resize.
        var builtFor = Size;
        ResizeEnd += (_, _) =>
        {
            if (Size == builtFor) return;  // only moved
            builtFor = Size;
            if (tab != SettingsTab.Privacy) Build();
        };
        timer.Tick += (_, _) => Sync(slowToo: ++ticks % 5 == 0);
        timer.Start();
        Select(first);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        open = null;
        base.OnFormClosed(e);
    }

    void Select(SettingsTab t)
    {
        tab = last = t;
        strip.Selected = t;
        Build();
    }

    void Sync(bool slowToo = false)
    {
        if (IsDisposed) return;
        syncing = true;
        try
        {
            foreach (var a in live) a();
            if (slowToo) foreach (var a in slow) a();
        }
        finally { syncing = false; }
    }

    /// Lay out the current tab from scratch, keeping the scroll position when it is the same tab.
    void Build()
    {
        var scroll = body.Controls.Count > 0 && body.Controls[0] is ScrollableControl old ? -old.AutoScrollPosition.Y : 0;
        body.SuspendLayout();
        foreach (var c in body.Controls.Cast<Control>().ToList()) { body.Controls.Remove(c); c.Dispose(); }
        live.Clear();
        slow.Clear();
        if (tab == SettingsTab.Privacy)
        {
            // Has its own scrolling lists, so it fills the tab instead of scrolling as a whole.
            var blocked = BlockedAppsForm.Embedded(app.AppSettings, app.Db, app.TimelineHiddenChanged);
            body.Controls.Add(blocked);
            blocked.Show();
        }
        else
        {
            var p = new Page(this);
            switch (tab)
            {
                case SettingsTab.General: General(p); break;
                case SettingsTab.Screen: ScreenTab(p); break;
                case SettingsTab.Audio: Audio(p); break;
                case SettingsTab.Meetings: Meetings(p); break;
                case SettingsTab.Ai: Ai(p); break;
                case SettingsTab.Security: Security(p); break;
                case SettingsTab.Storage: Storage(p); break;
                case SettingsTab.Shortcuts: Shortcuts(p); break;
            }
            p.Finish();
            body.Controls.Add(p.Panel);
            if (scroll > 0) p.Panel.AutoScrollPosition = new Point(0, scroll);
        }
        body.ResumeLayout();
        Sync(slowToo: true);
    }

    /// Build the page again after a change that adds or removes rows.
    void Rebuild() => BeginInvoke(Build);

    // MARK: General

    void General(Page p)
    {
        p.Heading("OpenRewind records your screen and audio on this PC. Nothing is sent to the cloud.");
        p.Row("Login");
        p.Check("Open OpenRewind when I sign in to Windows", () => TrayApp.LaunchAtLogin, TrayApp.ToggleLaunchAtLogin);
        p.Row("Updates");
        p.Check("Download updates automatically", () => app.AutoDownload, () => app.AutoDownload = !app.AutoDownload);
        p.Note("New versions download in the background. They are installed only when you click Update.");
        p.Buttons(("Check for Updates…", app.CheckUpdatesNow));
        p.Text($"Version {Updater.Current}", Ui.Faint, 12);
        p.Row("Timeline look");
        p.Combo(new[] { ("Match System", "system"), ("Light", "light"), ("Dark", "dark") }, () => app.Look, v => app.Look = v, 220);
        p.Row("Permissions");
        var mic = p.Text("", Ui.Text, 13.5f, bold: true);
        live.Add(() =>
        {
            var on = Permissions.MicrophoneOn;
            Set(mic, on ? "✓  Microphone — On" : "⚠  Microphone — Off");
            mic.ForeColor = on ? Ui.Good : Ui.Warn;
        });
        p.Note("The microphone records your voice. Screenshots and window text need no permission on Windows.");
        p.Buttons(("Microphone Settings…", () => TrayApp.OpenTarget(Permissions.MicrophoneSettings)),
                  ("Privacy & Security Settings…", () => TrayApp.OpenTarget("ms-settings:privacy")));
        p.Row("Help");
        p.Buttons(("Show Tutorial", app.OpenTutorial), ("Run Setup Again…", app.OpenSetupAgain));
    }

    // MARK: Screen

    void ScreenTab(Page p)
    {
        p.Heading("Screen recordings are stored on this PC and never sent to the cloud.");
        p.Row("Recording");
        p.Check("Record the screen", () => app.RecordsScreen, app.ToggleRecordScreen);
        p.Note("To stop for a while, use Pause Recording in the tray menu.");
        if (app.NoOcr)
            p.Text("No Windows OCR language is installed, so screen text cannot be read. Add one in Windows Settings → Time & language → Language.", Ui.Warn, 12.5f);
        p.Row("Exclude");
        p.Text("Blocked apps, sites, private windows and title words are blurred in every screenshot, and their text is never saved.", Ui.Text, 13.5f);
        p.Buttons(("Blocked Apps & Sites…", () => Select(SettingsTab.Privacy)));
        p.Row("Resolution");
        p.Combo(ScreenshotSizes.Choices.Select(pct => (TrayApp.ScreenshotTitle(pct), pct)).ToArray(), () => app.ScreenshotPercent, v => app.ScreenshotPercent = v, 340);
        p.Note("Higher looks sharper but uses more disk space. Applies to new screenshots.");
        p.Row("While typing");
        p.Combo(FrameDedup.GapChoices.Select(s => ($"Save a frame every {s} seconds" + (s == 2 ? " (default)" : ""), s)).ToArray(), () => app.TypingGap, v => app.TypingGap = v, 340);
        p.Note("While only a few words on screen change, save a frame this often. Longer uses less disk space; the screen you stop on is still saved.");
        p.Row("Search by meaning");
        p.Check("Find moments by what they were about, not only the exact words", () => app.MeaningOn, app.ToggleSearchByMeaning);
        p.LiveText(() => app.MeaningOn ? app.MeaningLine : "", Ui.Dim, 12.5f);
        var retry = p.Buttons(("Try Again", app.RetryMeaning))[0];
        live.Add(() => retry.Visible = app.MeaningOn && app.MeaningFailed);
        p.Note($"Downloads a language model once (about {MiniLm.DownloadSize / 1_000_000} MB) and builds the index on this PC. Your recordings are never uploaded.");
    }

    // MARK: Audio

    void Audio(Page p)
    {
        p.Heading("Audio recordings are stored on this PC and never sent to the cloud.");
        p.Row("Recording");
        p.Check("Record audio (your microphone and what the PC plays)", () => app.RecordsAudio, app.ToggleRecordAudio);
        var warn = p.LiveText(() => app.RecordsAudio && !Permissions.MicrophoneOn ? "The microphone is off in Windows Settings, so only the PC's sound is recorded." : "", Ui.Warn, 12.5f);
        p.Note("Audio is turned into text on this PC, so you can search what was said.");
        p.Row("Transcription");
        p.Combo(SpeechModels.All.Select(m => (ModelTitle(m), m.Id)).ToArray(), () => app.AppSettings.TranscriptionModel,
                id => app.UseTranscriptionModel(SpeechModels.ById(id)), 400);
        p.LiveText(() => SpeechModels.ById(app.AppSettings.TranscriptionModel).Detail, Ui.Faint, 11.5f, lines: 3);
        p.LiveText(() => string.Join("\n", app.TranscriptionLines), Ui.Dim, 12.5f, lines: 3);
        var whisper = p.Buttons(("Set Up Whisper…", TrayApp.OpenWhisperHelp))[0];
        live.Add(() => whisper.Visible = !app.TranscriberRunning && SpeechModels.ById(app.AppSettings.TranscriptionModel).Engine == SpeechEngine.Whisper);
        p.Link("About these models…", TrayApp.OpenModelsHelp);
        p.Row("Re-transcribe");
        var again = p.Buttons(("Last Hour…", () => app.RetranscribeRecent("the last hour", TimeSpan.FromHours(1))),
                              ("Last Day…", () => app.RetranscribeRecent("the last day", TimeSpan.FromDays(1))),
                              ("Last Week…", () => app.RetranscribeRecent("the last week", TimeSpan.FromDays(7))));
        live.Add(() => { foreach (var b in again) b.Enabled = app.TranscriberRunning; });
        p.Note("Run audio you already recorded through the model picked now.");
    }

    /// "NVIDIA Parakeet v3 (default) — downloaded", "Whisper Small — 488 MB download".
    string ModelTitle(SpeechModel m)
    {
        var size = m.Files.Count == 0 ? "" : SpeechModels.IsDownloaded(m, app.Home) ? "  —  downloaded" : $"  —  {SpeechModels.FormatSize(m.Size)} download";
        return m.Name + (m.Id == SpeechModels.DefaultId ? " (default)" : "") + size;
    }

    // MARK: Meetings

    void Meetings(Page p)
    {
        var s = app.AppSettings;
        p.Section("Recording Meetings", "When a Zoom, Teams, Google Meet, Webex or Slack huddle call starts, OpenRewind can take notes: the call's transcript, kept on this PC. Please ask for consent before recording others.");
        p.Row("When a call starts");
        p.Combo(new[] { ("Ask whether to take notes", true), ("Take notes for every call", false) }, () => s.AskBeforeMeetingNotes,
                v => { s.AskBeforeMeetingNotes = v; s.Save(); }, 300);
        p.Note("Apps you said Never for are always skipped.");
        p.Row("When a meeting ends");
        p.Combo(new[] { ("Keep the transcript", false), ("Summarize it with AI", true) }, () => s.SummarizeMeetings, _ => app.ToggleSummaries(), 300);
        p.Note("A summary sends the call's transcript to your AI provider (AI tab) with your key, for a summary, decisions and action items. It costs tokens. You can still summarize any meeting yourself in the Meetings window.");
        p.Row("Remembered apps");
        if (s.MeetingChoices.Count == 0) p.Text("None yet. When you pick Always or Never for an app, it shows here.", Ui.Faint, 13.5f);
        else
        {
            foreach (var c in s.MeetingChoices.OrderBy(c => c.Key))
                p.Text($"{c.Key}: {(c.Value == MeetingPrompt.Always ? "always take notes" : "never take notes")}", Ui.Text, 13.5f);
            p.Buttons(("Forget Remembered Choices", () => { s.MeetingChoices.Clear(); s.Save(); Rebuild(); }));
        }
    }

    // MARK: AI

    void Ai(Page p)
    {
        var form = AiSettingsForm.Embedded(app.AppSettings);
        p.Embed(form);
        p.Divider();
        p.Row("Daily recap");
        p.Check("Write each day's recap automatically", () => app.AutoRecapOn, app.ToggleDailyRecap);
        p.LiveText(() => Journal.AutoRecapInfo(app.AppSettings.Ai().Kind.Title()), Ui.Faint, 11.5f, lines: 4);
        p.Row("AI apps");
        p.Check("Allow AI apps to read my history (MCP)", () => app.AppSettings.McpEnabled, app.ToggleAiApps);
        p.Note("Lets Claude, ChatGPT, Cursor and other AI apps search your history on this PC. Blocked apps and sites are left out of screen text; audio transcripts are not filtered by app. Connect AI Apps has the details.");
        p.Buttons(("Connect AI Apps…", app.ConnectAiApps));
    }

    // MARK: Security

    void Security(Page p)
    {
        p.Row("Encryption");
        p.Check("Encrypt recordings on disk", () => app.EncryptionOn, app.ToggleEncrypt);
        p.LiveText(() => app.EncryptionStatus, Ui.Dim, 12.5f, lines: 2);
        var keys = p.Buttons(("Export Recovery Key…", app.SaveRecoveryKey), ("Stop Retrying Files…", app.StopRetryingFiles));
        live.Add(() =>
        {
            keys[0].Enabled = app.HasRecoveryKey;
            keys[1].Visible = app.EncryptionFailed > 0;
            Set(keys[1], $"Stop Retrying {app.EncryptionFailed:N0} Files…");
        });
        p.Note("Encrypts screenshots, audio and the database. The key stays with your Windows account; keep the recovery key somewhere safe.");
        p.Row("App Lock");
        p.Check("Require Windows Hello to open the timeline", () => app.AppLockOn, app.ToggleLock);
        p.Note("Asks for your face, fingerprint or PIN before showing the timeline, search, Ask AI, Settings and other history. Recording keeps going.");
        p.Text("Lock again after", Ui.Text, 13.5f);
        var after = p.Combo(AppLock.TimeoutChoices.Select(c => (c.Title + (c.Minutes == 15 ? " (default)" : ""), c.Minutes)).ToArray(),
                            () => app.LockMinutes, v => app.LockMinutes = v, 240);
        live.Add(() => after.Enabled = app.AppLockOn);
        p.Row("Local server");
        p.Check($"Local search server (127.0.0.1:{app.ServerPort})", () => app.ServerOn, app.ToggleLocalServer);
        p.Note("Search in the browser and let local scripts read your history through the API. Only this PC can connect.");
    }

    // MARK: Storage

    void Storage(Page p)
    {
        p.Row("Disk space used");
        var used = p.Text("", Ui.Text, 13.5f, bold: true, lines: 4);
        slow.Add(() => Set(used, string.Join("\n", app.StorageSummary)));
        app.MeasureDataFolder();
        p.Buttons(("Show Data Folder", app.ShowDataFolder));
        p.Row("Keep screen recordings");
        p.Combo(Enum.GetValues<KeepFor>().Select(v => (v.Title(), v)).ToArray(), () => app.KeepScreen, v => app.SetKeepFor(true, v), 240);
        p.Row("Keep audio recordings");
        p.Combo(Enum.GetValues<KeepFor>().Select(v => (v.Title(), v)).ToArray(), () => app.KeepAudio, v => app.SetKeepFor(false, v), 240);
        p.Note("Older recordings are deleted every hour. A shorter time saves space, and also limits how far back you can search.");
        p.Row("Compress");
        var why = app.CompressUnavailable;
        var compress = p.Check(why == null ? "Compress older recordings" : "Compress older recordings (not available on this PC)", () => app.CompressOn, app.ToggleCompression);
        live.Add(() => compress.Enabled = why == null || app.CompressOn);  // can still be turned off
        p.Note(why != null ? $"This PC has no working video encoder: {why}"
            : "Keep screenshots older than an hour as video instead of one picture each: about 5 to 10 times less disk space. Videos already made keep playing when it is off.");
        p.Row("Delete recordings");
        p.Buttons(("Last 15 Minutes…", () => app.DeleteRecordings("last 15 minutes", DateTime.UtcNow.AddMinutes(-15))),
                  ("Last Hour…", () => app.DeleteRecordings("last hour", DateTime.UtcNow.AddHours(-1))),
                  ("Today…", () => app.DeleteRecordings("today", DateTime.Today.ToUniversalTime())));
        p.Buttons(("All Screen Recordings…", () => app.DeleteAllRecordings(screen: true, audio: false)),
                  ("All Audio Recordings…", () => app.DeleteAllRecordings(screen: false, audio: true)));
        p.Link("Delete Everything…", () => app.DeleteAllRecordings(screen: true, audio: true), Ui.Warn);
    }

    // MARK: Shortcuts

    void Shortcuts(Page p)
    {
        p.Heading("Shortcuts work in every app. Click a shortcut to change it.");
        p.Row("Open Timeline");
        var row = p.Buttons((app.TimelineShortcut.Label, app.RecordTimelineShortcut), ("Restore Default", app.RestoreTimelineShortcut));
        live.Add(() =>
        {
            Set(row[0], app.TimelineShortcut.Label);
            row[1].Enabled = app.TimelineShortcut != HotkeyShortcut.Standard;
        });
        p.Row("Star Moment");
        p.Note($"{HotkeyShortcut.Star.Label} stars the moment from any app. On the open timeline, S stars the moment shown.");
        p.Row("Scroll to rewind");
        p.Check("Open the timeline with Ctrl+Shift + scroll", () => app.ScrollOpensTimeline, app.ToggleScrollOpensTimeline);
        p.Note("Hold Ctrl and Shift and turn the mouse wheel in any app to open the timeline and go back in time.");
    }

    static void Set(Control c, string text) { if (c.Text != text) c.Text = text; }

    /// Lays out one tab top to bottom: a bold label on the left, right-aligned, and its controls on
    /// the right, like Rewind's settings.
    sealed class Page
    {
        public readonly Panel Panel;
        readonly SettingsForm form;
        readonly float k;
        readonly int margin, labelW, x, w;
        int y, rowTop = -1;

        public Page(SettingsForm form)
        {
            this.form = form;
            k = form.k;
            Panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Ui.Bg };
            var width = Math.Min(form.body.ClientSize.Width, (int)(820 * k)) - SystemInformation.VerticalScrollBarWidth;
            margin = (int)(32 * k) + Math.Max(0, (form.body.ClientSize.Width - (int)(820 * k)) / 2);
            labelW = (int)(180 * k);
            x = margin + labelW + (int)(16 * k);
            w = Math.Max((int)(260 * k), margin + width - (int)(32 * k) - x);
            y = (int)(26 * k);
        }

        T Add<T>(T c, int left, int width, int gap = 6) where T : Control
        {
            c.Location = new Point(left, y);
            c.Width = width;
            Panel.Controls.Add(c);
            y += c.Height + (int)(gap * k);
            return c;
        }

        int Measure(string text, Font font, int width, int lines) =>
            Math.Max(TextRenderer.MeasureText(text.Length == 0 ? " " : text, font, new Size(width, 0), TextFormatFlags.WordBreak).Height,
                     (int)(font.Height * lines * 1.05f)) + (int)(2 * k);

        Label MakeLabel(string text, Color color, float size, bool bold, int width, int lines, ContentAlignment align = ContentAlignment.TopLeft) =>
            new() { Text = text, Font = Ui.F(size * k, bold), ForeColor = color, BackColor = Ui.Bg, AutoSize = false, TextAlign = align,
                    Height = Measure(text, Ui.F(size * k, bold), width, lines), UseMnemonic = false };

        /// The bold line at the top of a tab.
        public void Heading(string text)
        {
            int full = x + w - margin;
            Add(MakeLabel(text, Ui.Text, 14, true, full, 1, ContentAlignment.TopCenter), margin, full, 18);
        }

        /// A heading with a paragraph under it, across the page.
        public void Section(string title, string text)
        {
            int full = x + w - margin;
            Add(MakeLabel(title, Ui.Text, 17, true, full, 1), margin, full, 4);
            Add(MakeLabel(text, Ui.Dim, 13.5f, false, full, 1), margin, full, 18);
        }

        /// Starts a row: the label on the left; what follows goes on the right.
        public void Row(string label)
        {
            EndRow();
            var l = MakeLabel(label + ":", Ui.Text, 14, true, labelW, 1, ContentAlignment.TopRight);
            l.Location = new Point(margin, y + (int)(3 * k));
            l.Width = labelW;
            Panel.Controls.Add(l);
            rowTop = y + l.Height;
        }

        void EndRow()
        {
            if (rowTop < 0) return;
            y = Math.Max(y, rowTop) + (int)(16 * k);
            rowTop = -1;
        }

        public CheckBox Check(string text, Func<bool> get, Action flip)
        {
            var c = new CheckBox { Text = text, Checked = get(), Font = Ui.F(14 * k), ForeColor = Ui.Text, BackColor = Ui.Bg, Height = (int)(28 * k), AutoEllipsis = true, UseMnemonic = false };
            c.CheckedChanged += (_, _) =>
            {
                if (form.syncing) return;
                flip();
                form.Sync();
            };
            form.live.Add(() => { var on = get(); if (c.Checked != on) c.Checked = on; });
            return Add(c, x, w, 4);
        }

        public Label Text(string text, Color color, float size, bool bold = false, int lines = 1) => Add(MakeLabel(text, color, size, bold, w, lines), x, w, 4);

        /// Text read again every second; room for `lines` lines.
        public Label LiveText(Func<string> get, Color color, float size, int lines = 1)
        {
            var l = Text(get(), color, size, lines: lines);
            form.live.Add(() => Set(l, get()));
            return l;
        }

        /// A gray line under a control.
        public void Note(string text) => Add(MakeLabel(text, Ui.Faint, 12, false, w, 1), x, w, 8);

        public List<PillButton> Buttons(params (string Title, Action Act)[] buttons)
        {
            var list = new List<PillButton>();
            int left = x, h = (int)(34 * k);
            foreach (var (title, act) in buttons)
            {
                var b = new PillButton(title) { Font = Ui.F(13 * k, true), Height = h };
                b.Width = TextRenderer.MeasureText(title, b.Font).Width + (int)(36 * k);
                b.Location = new Point(left, y);
                b.Click += (_, _) => { act(); form.Sync(); };
                Panel.Controls.Add(b);
                left += b.Width + (int)(8 * k);
                list.Add(b);
            }
            y += h + (int)(8 * k);
            return list;
        }

        public LinkLabel Link(string text, Action act, Color? color = null)
        {
            var l = new LinkLabel
            {
                Text = text, Font = Ui.F(13 * k), BackColor = Ui.Bg, Height = (int)(24 * k), AutoSize = false, UseMnemonic = false,
                LinkColor = color ?? Color.FromArgb(115, 165, 255), ActiveLinkColor = color ?? Color.FromArgb(115, 165, 255),
            };
            l.LinkClicked += (_, _) => act();
            return Add(l, x, w, 6);
        }

        /// A pop-up list showing `get`; picking another calls `set` (which may ask first).
        public ComboBox Combo<T>((string Title, T Value)[] choices, Func<T> get, Action<T> set, int width)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Ui.Card, ForeColor = Ui.Text, Font = Ui.F(14 * k) };
            foreach (var choice in choices) c.Items.Add(choice.Title);
            int Index() => Array.FindIndex(choices, ch => EqualityComparer<T>.Default.Equals(ch.Value, get()));
            c.SelectedIndex = Index();
            c.SelectedIndexChanged += (_, _) =>
            {
                if (form.syncing || c.SelectedIndex < 0) return;
                var v = choices[c.SelectedIndex].Value;
                if (!EqualityComparer<T>.Default.Equals(v, get())) set(v);
                form.Sync();
            };
            form.live.Add(() => { var i = Index(); if (c.SelectedIndex != i && !c.DroppedDown) c.SelectedIndex = i; });
            return Add(c, x, Math.Min((int)(width * k), w), 6);
        }

        /// Another window's content (AI Settings), at its own size.
        public void Embed(Form f)
        {
            EndRow();
            f.Location = new Point(Math.Max(0, margin - (int)(24 * k)), y - (int)(14 * k));
            Panel.Controls.Add(f);
            f.Show();
            y = f.Bottom + (int)(8 * k);
        }

        public void Divider()
        {
            EndRow();
            Add(new Panel { BackColor = Ui.Stroke, Height = Math.Max(1, (int)k) }, margin, x + w - margin, 20);
        }

        public void Finish()
        {
            EndRow();
            // Room under the last row when scrolled to the end.
            Panel.Controls.Add(new Panel { Location = new Point(0, y), Size = new Size(1, (int)(20 * k)), BackColor = Ui.Bg });
        }
    }

    /// The row of tabs: an icon over a name, the picked one on a lighter card in the accent color.
    sealed class TabStrip : Control
    {
        public event Action<SettingsTab>? Picked;
        readonly float k;
        SettingsTab selected;
        int hover = -1;
        static readonly SettingsTab[] Tabs = Enum.GetValues<SettingsTab>();
        // Segoe MDL2 Assets (Windows 10 and 11): Setting, TVMonitor, Microphone, Calendar, ChatBubbles,
        // Shield, Lock, HardDrive, KeyboardClassic.
        static readonly string[] Glyphs = { "", "", "", "", "", "", "", "", "" };
        readonly Font glyphFont;

        public TabStrip(float k)
        {
            this.k = k;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Cursor = Cursors.Hand;
            glyphFont = new Font("Segoe MDL2 Assets", 22 * k, GraphicsUnit.Pixel);
        }

        public SettingsTab Selected { get => selected; set { selected = value; Invalidate(); } }

        static string Title(SettingsTab t) => t == SettingsTab.Ai ? "AI" : t.ToString();

        Rectangle Cell(int i)
        {
            int cw = (int)(84 * k), ch = (int)(70 * k);
            int left = (Width - cw * Tabs.Length) / 2;
            return new Rectangle(left + i * cw, (Height - ch) / 2, cw, ch);
        }

        int HitTest(Point p) { for (int i = 0; i < Tabs.Length; i++) if (Cell(i).Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e) { var h = HitTest(e.Location); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var i = HitTest(e.Location);
            if (i >= 0 && Tabs[i] != selected) Picked?.Invoke(Tabs[i]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.Smooth(g);
            for (int i = 0; i < Tabs.Length; i++)
            {
                var r = Cell(i);
                bool on = Tabs[i] == selected;
                if (on || i == hover) Ui.Fill(g, new RectangleF(r.X + 2 * k, r.Y, r.Width - 4 * k, r.Height), 9 * k, on ? Ui.CardHi : Ui.Card);
                var color = on ? Ui.AccentHi : Ui.Dim;
                TextRenderer.DrawText(g, Glyphs[i], glyphFont, new Rectangle(r.X, r.Y + (int)(8 * k), r.Width, (int)(30 * k)), color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Title(Tabs[i]), Ui.F(12.5f * k, on), new Rectangle(r.X, r.Y + (int)(42 * k), r.Width, (int)(20 * k)), on ? Ui.Text : Ui.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) glyphFont.Dispose();
            base.Dispose(disposing);
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
