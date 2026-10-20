using System.Diagnostics;
using System.Drawing;

namespace OpenRewind;

/// Tray app: icon with separate screen / audio switches and the timeline overlay.
/// Same menu as the Mac menu bar app.
sealed class TrayApp : ApplicationContext
{
    readonly Engine engine;
    readonly Settings settings;
    readonly NotifyIcon icon;
    readonly ContextMenuStrip menu = new();
    readonly OverlayForm overlay;
    readonly TimelineModel timeline;
    HotKey hotKey;
    /// Turns recording back on when a timed pause ends.
    readonly System.Windows.Forms.Timer resumeTimer = new();
    readonly RetentionJob retention;
    readonly Updater updater;
    /// Last measured size of the data folder; measured in the background when the menu opens.
    long? dataSize;
    bool measuring;
    /// The AI chat keeps its conversation while its window is closed.
    readonly AiConversation aiConversation = new();
    readonly AiTools aiTools;
    readonly MeaningController meaning;
    /// The Search by Meaning line in the open menu, updated while the model downloads.
    ToolStripMenuItem? meaningStatus;
    /// The journal's own lookups, so it never offers screenshots the chat allowed.
    readonly AiTools journalTools;
    /// Checks every few minutes whether yesterday's recap is due (Write Each Day's Recap Automatically).
    readonly System.Windows.Forms.Timer recapTimer = new() { Interval = 60_000 };
    bool writingRecap;
    readonly MeetingWatcher meetings;

    public TrayApp(Engine engine)
    {
        this.engine = engine;
        settings = Settings.Load(engine.Config.Home);
        Program.RefreshIconIfNew(settings);
        Blocklist.Current = settings.ToBlocklist();
        timeline = new TimelineModel(engine.Db);
        aiTools = new AiTools(engine.Db) { Meaning = engine.Meaning };
        timeline.Meaning = engine.Meaning;
        journalTools = new AiTools(engine.Db);
        overlay = new OverlayForm(timeline) { Look = settings.TimelineLook };
        _ = overlay.Handle;  // create it on the UI thread now
        meaning = new MeaningController(engine);
        meaning.Changed += () => overlay.BeginInvoke(() =>
        {
            if (meaningStatus != null && settings.SearchByMeaning) meaningStatus.Text = "   " + meaning.StatusLine();
            if (overlay.Visible) overlay.Invalidate();
        });
        if (settings.SearchByMeaning) meaning.Start();

        if (settings.LocalSearchServer) StartServer();
        engine.StartTranscriber();
        engine.Recorder.MinorChangeGap = settings.TypingFrameGap;
        engine.Config.SnapshotScale = ScreenshotSizes.Scale(settings.ScreenshotPercent);
        resumeTimer.Tick += (_, _) => CheckPause();
        retention = new RetentionJob(engine.Db, settings.Screen, settings.Audio);
        retention.Start();
        _ = Database.BuildSlowIndexesInBackground(engine.Config.DbPath);

        updater = new Updater(settings) { QuitApp = Quit };
        updater.Changed += () => { if (overlay.Visible) overlay.Invalidate(); };
        overlay.Updater = updater;
        overlay.Chat = AiChat;
        updater.Start();

        // Finds meetings (and asks about notes) within seconds of a call; the window and the AI notes read what it stores.
        meetings = new MeetingWatcher(engine.Db, settings, engine.Config.AudioChunkSeconds, () => engine.Transcriber != null, a => overlay.BeginInvoke(a));
        meetings.Start();

        // Ctrl+Alt+T (or the shortcut picked in Settings) opens the timeline from anywhere.
        hotKey = new HotKey(Shortcut, overlay.Toggle);

        menu.Opening += (_, _) => BuildMenu();
        icon = new NotifyIcon { Icon = Program.AppIcon, ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) overlay.Toggle(); };

        // Start recording, or re-arm a pause saved last time, only once `icon` exists:
        // both paths end in UpdateIcon(), which sets the icon's tooltip and picture.
        if (PausedUntil is DateTime until) SchedulePause(until); else ApplyRecordingPrefs();

        recapTimer.Tick += (_, _) => { recapTimer.Interval = 10 * 60_000; WriteDueRecap(); };
        recapTimer.Start();

        BuildMenu();
        UpdateIcon();
        if (!settings.OnboardingDone) ShowSetup();
    }

    /// The first-run setup; ends in the guided tour if the user asks for it.
    void ShowSetup()
    {
        if (setup is { IsDisposed: false }) { setup.Activate(); return; }
        OnboardingForm.Hotkey = Shortcut.Label;
        setup = new OnboardingForm(settings, engine.Db, tour => { if (tour) ShowTutorial(); });
        setup.Show();
    }
    OnboardingForm? setup;

    void ShowJournal() => JournalForm.ShowFor(settings, engine.Db, journalTools, OpenMoment, OpenAiSettings);

    /// Write Each Day's Recap Automatically: yesterday's recap, the first time the app runs after
    /// the day ended, for the AI provider it was turned on for. A failed try is retried on the next
    /// check, up to Journal.AutoAttempts times. Days with nothing recorded, or with a recap of the
    /// whole day already, are skipped; a recap written by hand partway through the day is replaced.
    async void WriteDueRecap()
    {
        var ai = settings.Ai();
        if (writingRecap || !ai.IsReady || !settings.JournalAutoRecapOn(ai)) return;
        if (Journal.AutoRecapDue(DateTime.Now, settings.JournalLastAutoDay, settings.JournalAutoAttemptDay, settings.JournalAutoAttempts) is not string key
            || Journal.FromKey(key) is not DateTime day || JournalJobs.IsWriting(key)) return;
        void Done() { settings.JournalLastAutoDay = key; settings.Save(); }
        if (engine.Db.Recap(key) is { Partial: false }) { Done(); return; }
        writingRecap = true;
        try
        {
            var stats = await Task.Run(() => engine.Db.JournalStats(day, journalTools));
            if (stats.IsEmpty) { Done(); return; }
            // Counted before the request, so one that never comes back still counts.
            settings.JournalAutoAttempts = settings.JournalAutoAttemptDay == key ? settings.JournalAutoAttempts + 1 : 1;
            settings.JournalAutoAttemptDay = key;
            settings.Save();
            var r = await JournalJobs.Write(day, settings, engine.Db, journalTools, auto: true);
            if (r == null) return;
            if (r.Error == null) { Done(); Log.Write($"wrote the journal recap for {key} ({r.Usage.Describe(settings.AiPriceFor(ai.Model))})"); }
            else Log.Write($"automatic journal recap for {key} failed (try {settings.JournalAutoAttempts} of {Journal.AutoAttempts}): {r.Error}");
        }
        catch (Exception e) { Log.Write($"automatic journal recap for {key} failed: {e.Message}"); }
        finally { writingRecap = false; }
    }

    void ShowMeetings() => MeetingsForm.ShowFor(engine.Db, settings, meetings, OpenMoment, OpenAiSettings);

    /// What turning on meeting summaries sends, and where. Same words as the Mac app.
    static string MeetingConsentText(string provider) =>
        $"After each call you take notes for ends, OpenRewind sends its whole transcript (what was said and heard during the call) to {provider} "
        + "using your key, to write a summary, decisions and action items.\n\n"
        + $"This costs tokens on your account, and {provider}'s privacy terms apply. Calls in blocked apps and sites never become "
        + "meetings, but the transcript is not filtered by app: anything said or played during a call is sent, even while a blocked "
        + $"app or site was in front.\n\nOnly meetings from now on are summarized, and only to {provider}: if you switch providers "
        + "in AI Settings, OpenRewind asks again. You can still summarize any meeting yourself in the Meetings window.";

    /// Turning it on says plainly what it sends and that it costs tokens.
    void ToggleMeetingSummaries()
    {
        if (settings.SummarizeMeetings)
        {
            settings.SummarizeMeetings = false;
            settings.SummarizeMeetingsSince = null;
            settings.SummarizeMeetingsProvider = null;
            settings.Save();
            return;
        }
        var ai = settings.Ai();
        if (!ai.IsReady)
        {
            if (MessageBox.Show("Meeting summaries use your own AI key. Set up a provider in AI Settings first, then turn this on.",
                                "Summarize Meetings with AI", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK) OpenAiSettings();
            return;
        }
        var text = MeetingConsentText(ai.Kind.Title());
        if (MessageBox.Show(text, "Summarize meetings with AI?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        settings.SummarizeMeetings = true;
        settings.SummarizeMeetingsSince = DateTime.UtcNow;
        settings.SummarizeMeetingsProvider = MeetingNotes.ConsentKey(ai);
        settings.Save();
        Log.Write("meeting summaries with AI on");
    }

    /// Turning it on shows how to add OpenRewind to each AI app.
    void ToggleMcp()
    {
        settings.McpEnabled = !settings.McpEnabled;
        settings.Save();
        Log.Write($"AI apps (MCP) {(settings.McpEnabled ? "allowed" : "not allowed")}");
        if (settings.McpEnabled) ShowConnectAiApps(); else ConnectAiAppsForm.SettingsChanged(settings);
    }

    void ShowConnectAiApps() => ConnectAiAppsForm.ShowFor(settings, engine.Config);

    void ShowAiChat() => AiChatForm.ShowFor(settings, aiTools, aiConversation, OpenMoment, OpenAiSettings);

    /// The chat for the overlay; the same one the Ask AI window shows.
    AiChatPanel AiChat() => AiChatPanel.Get(settings, aiTools, aiConversation, OpenMoment, OpenAiSettings);

    /// Where the time went; a click shows that app or moment in the timeline.
    void ShowInsights() => InsightsForm.ShowFor(settings, engine.Db, (at, app) =>
    {
        if (!overlay.Visible) overlay.ShowOverlay();
        timeline.OpenAt(at, app);
    });

    void OpenMoment(DateTime at)
    {
        if (!overlay.Visible) overlay.ShowOverlay();
        timeline.Jump(at);
    }

    /// AI Settings is a normal window, which would sit under the overlay.
    void OpenAiSettings()
    {
        if (overlay.Visible) overlay.HideOverlay();
        AiSettingsForm.ShowFor(settings);
    }

    void ShowTutorial()
    {
        OnboardingForm.Hotkey = Shortcut.Label;
        TutorialCoach.Start(overlay, timeline);
    }

    // Build the menu fresh each time it opens, so it always shows the live state.
    void BuildMenu()
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem(StatusLine()) { Enabled = false });
        if (UpdateItem() is ToolStripMenuItem update) menu.Items.Add(update);
        menu.Items.Add(new ToolStripSeparator());

        if (PausedUntil is DateTime until)
            menu.Items.Add(Item($"Resume Recording ({Pause.Text(until, DateTime.UtcNow)})", ResumeRecording));
        else
        {
            var pause = new ToolStripMenuItem("Pause Recording");
            foreach (var (title, minutes) in Pause.Choices) pause.DropDownItems.Add(Item(title, () => PauseRecording(minutes)));
            menu.Items.Add(pause);
        }
        menu.Items.Add(new ToolStripSeparator());

        // While paused, the switches show and change what comes back on after the pause.
        bool paused = PausedUntil != null;
        menu.Items.Add(Toggle("Record Screen", paused ? settings.RecordScreen : engine.ScreenOn, ToggleScreen));
        if (engine.ScreenOn && !Ocr.Available && !engine.Config.UseAccessibility)
            menu.Items.Add(new ToolStripMenuItem("   No Windows OCR language installed") { Enabled = false });
        menu.Items.Add(Toggle("Record Audio", paused ? settings.RecordAudio : engine.AudioOn, ToggleAudio));
        if (engine.AudioOn && !Permissions.MicrophoneOn)
            menu.Items.Add(Item("   ⚠ Microphone is off in Windows — Open Settings…", () => Open(Permissions.MicrophoneSettings)));
        if (engine.AudioOn && engine.Transcriber == null)
            menu.Items.Add(Item("   Transcription off: set up Whisper…", () => Open("https://github.com/frickgav/openscreen/blob/main/windows/README.md#whisper")));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Open Timeline", null, (_, _) => overlay.ShowOverlay()) { ShortcutKeyDisplayString = Shortcut.Label });
        // The browser page when the local server is on, else the timeline's own search box.
        menu.Items.Add(Item("Search…", () => { if (engine.ServerOn) Open($"http://127.0.0.1:{engine.Config.Port}/"); else overlay.ShowSearch(); }));
        menu.Items.Add(Item("Ask AI…", ShowAiChat));
        menu.Items.Add(Item("Journal…", ShowJournal));
        menu.Items.Add(Item("Meetings…", ShowMeetings));
        menu.Items.Add(Item("Insights…", ShowInsights));
        menu.Items.Add(Item("Show Data Folder", () => Open(engine.Config.Home)));
        foreach (var line in StorageLines()) menu.Items.Add(new ToolStripMenuItem("   " + line) { Enabled = false });
        MeasureStorage();
        menu.Items.Add(new ToolStripSeparator());

        var s = new ToolStripMenuItem(Permissions.MicrophoneOn ? "Settings" : "Settings  ⚠");
        s.DropDownItems.Add(Item("Blocked Apps & Sites…", () => BlockedAppsForm.ShowFor(settings, engine.Db)));
        s.DropDownItems.Add(Item("AI Settings…", () => AiSettingsForm.ShowFor(settings)));
        var ai = settings.Ai();
        var autoRecap = Toggle("Write Each Day's Recap Automatically", settings.JournalAutoRecapOn(ai), () =>
        {
            settings.SetJournalAutoRecap(!settings.JournalAutoRecapOn(settings.Ai()));
            JournalForm.Reload();
        });
        autoRecap.ToolTipText = Journal.AutoRecapInfo(ai.Kind.Title());
        s.DropDownItems.Add(autoRecap);
        var ask = Toggle("Ask Before Taking Meeting Notes", settings.AskBeforeMeetingNotes, () =>
        {
            settings.AskBeforeMeetingNotes = !settings.AskBeforeMeetingNotes;
            settings.Save();
        });
        ask.ToolTipText = "When a call starts, ask whether to take notes for it. Off: notes are taken for every call (except apps you said Never for).";
        s.DropDownItems.Add(ask);
        var summaries = Toggle("Summarize Meetings with AI", settings.SummarizeMeetings, ToggleMeetingSummaries);
        summaries.ToolTipText = "Off by default. When on, the transcript of each call you take notes for is sent to your AI provider after it ends, for a summary and action items. This uses your key and costs tokens.";
        s.DropDownItems.Add(summaries);
        if (settings.MeetingChoices.Count > 0)
        {
            var remembered = string.Join(", ", settings.MeetingChoices.OrderBy(c => c.Key)
                .Select(c => $"{c.Key}: {(c.Value == MeetingPrompt.Always ? "always" : "never")}"));
            var forget = Item("Forget Remembered Meeting Choices", () => { settings.MeetingChoices.Clear(); settings.Save(); });
            forget.ToolTipText = $"Ask again for every app. Remembered now: {remembered}.";
            s.DropDownItems.Add(forget);
        }
        s.DropDownItems.Add(new ToolStripSeparator());
        s.DropDownItems.Add(ShortcutMenu());
        s.DropDownItems.Add(ScreenshotSizeMenu());
        s.DropDownItems.Add(LookMenu());
        s.DropDownItems.Add(new ToolStripSeparator());
        s.DropDownItems.Add(KeepMenu("Keep Screen Recordings For", screen: true));
        s.DropDownItems.Add(KeepMenu("Keep Audio Recordings For", screen: false));
        s.DropDownItems.Add(TypingGapMenu());
        s.DropDownItems.Add(new ToolStripSeparator());
        var del = new ToolStripMenuItem("Delete Recordings");
        del.DropDownItems.Add(Item("Last 15 Minutes", () => DeleteRecent("last 15 minutes", DateTime.UtcNow.AddMinutes(-15))));
        del.DropDownItems.Add(Item("Last Hour", () => DeleteRecent("last hour", DateTime.UtcNow.AddHours(-1))));
        del.DropDownItems.Add(Item("Today", () => DeleteRecent("today", DateTime.Today.ToUniversalTime())));
        del.DropDownItems.Add(new ToolStripSeparator());
        del.DropDownItems.Add(Item("All Screen Recordings…", () => DeleteAll(screen: true, audio: false)));
        del.DropDownItems.Add(Item("All Audio Recordings…", () => DeleteAll(screen: false, audio: true)));
        del.DropDownItems.Add(Item("Everything…", () => DeleteAll(screen: true, audio: true)));
        s.DropDownItems.Add(del);
        s.DropDownItems.Add(new ToolStripSeparator());
        foreach (var item in MeaningItems()) s.DropDownItems.Add(item);
        s.DropDownItems.Add(new ToolStripSeparator());
        var server = Toggle($"Local Search Server (127.0.0.1:{engine.Config.Port})", engine.ServerOn, ToggleServer);
        server.ToolTipText = "Search in the browser and let local scripts read your history through the API. Only this PC can connect.";
        s.DropDownItems.Add(server);
        var mcp = Toggle("Allow AI Apps to Read My History (MCP)", settings.McpEnabled, ToggleMcp);
        mcp.ToolTipText = "Let Claude, Cursor and other AI apps search your history on this PC. Blocked apps and sites are left out of screen text; audio transcripts are not filtered by app. See Connect AI Apps for details.";
        s.DropDownItems.Add(mcp);
        s.DropDownItems.Add(Item("Connect AI Apps…", ShowConnectAiApps));
        s.DropDownItems.Add(new ToolStripSeparator());
        var auto = Toggle("Download Updates Automatically", updater.AutoDownload, () => updater.AutoDownload = !updater.AutoDownload);
        auto.ToolTipText = "Download new versions in the background. They are installed only when you click Update.";
        s.DropDownItems.Add(auto);
        s.DropDownItems.Add(Item($"Check for Updates… (version {Updater.Current})", CheckForUpdates));
        s.DropDownItems.Add(new ToolStripSeparator());
        s.DropDownItems.Add(PermissionsMenu());
        s.DropDownItems.Add(new ToolStripSeparator());
        s.DropDownItems.Add(Toggle("Launch at Login", Settings.LaunchAtLogin, () =>
        {
            try { Settings.LaunchAtLogin = !Settings.LaunchAtLogin; } catch (Exception e) { Log.Write($"launch at login failed: {e.Message}"); }
        }));
        s.DropDownItems.Add(new ToolStripSeparator());
        s.DropDownItems.Add(Item("Show Tutorial", ShowTutorial));
        s.DropDownItems.Add(Item("Run Setup Again…", ShowSetup));
        menu.Items.Add(s);
        menu.Items.Add(Item("Quit OpenRewind", Quit));
    }

    static ToolStripMenuItem Item(string title, Action act) => new(title, null, (_, _) => act());

    static ToolStripMenuItem Toggle(string title, bool on, Action act) => new(title, null, (_, _) => act()) { Checked = on };

    /// Fewer saved frames while typing means less disk space; the screen you stop on is always saved.
    ToolStripMenuItem TypingGapMenu()
    {
        var item = new ToolStripMenuItem("Save Frames While Typing")
        {
            ToolTipText = "While only a few words on screen change, save a frame this often. Longer uses less disk space; the screen you stop on is still saved."
        };
        foreach (var secs in FrameDedup.GapChoices)
            item.DropDownItems.Add(Toggle($"Every {secs} Seconds" + (secs == 2 ? " (Default)" : ""), secs == settings.TypingFrameGap, () =>
            {
                settings.TypingFrameGap = secs;
                engine.Recorder.MinorChangeGap = secs;
                settings.Save();
            }));
        return item;
    }

    /// Bigger screenshots are sharper to read back in the timeline and use more disk space.
    ToolStripMenuItem ScreenshotSizeMenu()
    {
        var item = new ToolStripMenuItem("Screenshot Resolution")
        {
            ToolTipText = "How big each saved screenshot is compared with your screen. Applies to new screenshots."
        };
        // Size of the main display in real pixels, to show what each choice gives.
        var screen = Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
        var current = ScreenshotSizes.Nearest(settings.ScreenshotPercent);
        foreach (var pct in ScreenshotSizes.Choices)
            item.DropDownItems.Add(Toggle(ScreenshotSizes.Title(pct, screen.Width, screen.Height), pct == current, () =>
            {
                settings.ScreenshotPercent = pct;
                engine.Config.SnapshotScale = ScreenshotSizes.Scale(pct);
                settings.Save();
                Log.Write($"screenshot resolution set to {pct}%");
            }));
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Higher looks sharper but uses more disk space") { Enabled = false });
        return item;
    }

    // MARK: search by meaning

    IEnumerable<ToolStripItem> MeaningItems()
    {
        var toggle = Toggle("Search by Meaning", settings.SearchByMeaning, ToggleMeaning);
        toggle.ToolTipText = "Find moments by what they were about, not only the exact words. Works on this PC; nothing is sent anywhere.";
        yield return toggle;
        meaningStatus = null;
        if (!settings.SearchByMeaning) yield break;
        meaningStatus = new ToolStripMenuItem("   " + meaning.StatusLine()) { Enabled = false };
        yield return meaningStatus;
        if (meaning.State == MeaningController.Phase.Failed) yield return Item("   Try Again", meaning.Retry);
    }

    void ToggleMeaning()
    {
        if (!settings.SearchByMeaning)
        {
            var download = meaning.ModelPresent ? ""
                : $"\n\nTo do this it downloads a language model once (about {MiniLm.DownloadSize / 1_000_000} MB) from Hugging Face. " +
                  "The model is checked before it is used. Your recordings are never uploaded.";
            if (MessageBox.Show(
                    "Search by Meaning finds moments by what they were about, even when you don't remember the exact words. " +
                    "For example, \u201cthe flight I was booking\u201d finds the airline page.\n\n" +
                    "OpenRewind will read your screen text and transcripts in the background to build an index on this PC. " +
                    "It works slowly while you use the computer and on battery, and faster when you are away. " +
                    "Blocked apps and sites are never indexed." + download,
                    "Turn on Search by Meaning?", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            settings.SearchByMeaning = true;
            settings.Save();
            meaning.Start();
            Log.Write("search by meaning on");
        }
        else
        {
            if (!Confirm("Turn off Search by Meaning?", "The meaning index is deleted. Keyword search keeps working. Turning it back on builds the index again.")) return;
            settings.SearchByMeaning = false;
            settings.Save();
            meaning.Stop(deleteIndex: true);
            timeline.SetByMeaning(false);
            Log.Write("search by meaning off");
        }
    }

    // MARK: timeline look

    ToolStripMenuItem LookMenu()
    {
        var item = new ToolStripMenuItem("Timeline Look");
        foreach (var (raw, title) in new[] { ("system", "Match System"), ("light", "Light"), ("dark", "Dark") })
            item.DropDownItems.Add(Toggle(title, settings.TimelineLook == raw, () =>
            {
                settings.TimelineLook = raw;
                overlay.Look = raw;
                settings.Save();
                Log.Write($"timeline look set to {raw}");
            }));
        return item;
    }

    // MARK: shortcut

    HotkeyShortcut Shortcut => settings.TimelineShortcut ?? HotkeyShortcut.Standard;

    ToolStripMenuItem ShortcutMenu()
    {
        var item = new ToolStripMenuItem($"Timeline Shortcut: {Shortcut.Label}");
        item.DropDownItems.Add(Item("Change Shortcut…", ChangeShortcut));
        item.DropDownItems.Add(new ToolStripMenuItem($"Reset to {HotkeyShortcut.Standard.Label}", null, (_, _) => SetShortcut(HotkeyShortcut.Standard))
        {
            Enabled = Shortcut != HotkeyShortcut.Standard
        });
        return item;
    }

    void ChangeShortcut()
    {
        var old = Shortcut;
        hotKey.Dispose();  // so pressing the old shortcut now does not open the timeline
        using var picker = new ShortcutPicker(old);
        var picked = picker.ShowDialog() == DialogResult.OK ? picker.Picked : null;
        hotKey = new HotKey(old, overlay.Toggle);
        if (picked != null && picked != old) SetShortcut(picked);
    }

    /// Switch to a new shortcut; keeps the old one when Windows refuses the new one.
    void SetShortcut(HotkeyShortcut sc)
    {
        var old = Shortcut;
        hotKey.Dispose();
        var next = new HotKey(sc, overlay.Toggle);
        if (next.Registered)
        {
            hotKey = next;
            settings.TimelineShortcut = sc == HotkeyShortcut.Standard ? null : sc;
            settings.Save();
            Log.Write($"timeline shortcut set to {sc.Label}");
            return;
        }
        next.Dispose();
        hotKey = new HotKey(old, overlay.Toggle);
        MessageBox.Show($"Another app or Windows uses this shortcut. The shortcut stays {old.Label}.", $"{sc.Label} is already in use",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // MARK: permissions

    /// One line per Windows permission with its state. Only the microphone has a switch on Windows.
    ToolStripMenuItem PermissionsMenu()
    {
        bool mic = Permissions.MicrophoneOn;
        var item = new ToolStripMenuItem(mic ? "Permissions  ✓ All on" : "Permissions  ⚠ 1 needed");
        item.DropDownItems.Add(Item(mic ? "✓  Microphone — On" : "⚠  Microphone — Off, click to turn on", () => Open(Permissions.MicrophoneSettings)));
        item.DropDownItems.Add(new ToolStripMenuItem("      Records your voice") { Enabled = false });
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Screenshots and window text need no permission on Windows") { Enabled = false });
        item.DropDownItems.Add(Item("Open Privacy & Security Settings…", () => Open("ms-settings:privacy")));
        return item;
    }

    // MARK: pause

    /// A pause that is still running; a pause that ended while the app was closed counts as over.
    DateTime? PausedUntil => Pause.Active(settings.PausedUntil, DateTime.UtcNow);

    void PauseRecording(int minutes)
    {
        var until = Pause.Until(minutes, DateTime.UtcNow);
        settings.PausedUntil = until;
        settings.Save();
        SchedulePause(until);
        Log.Write($"recording paused ({Pause.Text(until, DateTime.UtcNow)})");
    }

    void ResumeRecording()
    {
        settings.PausedUntil = null;
        settings.Save();
        resumeTimer.Stop();
        ApplyRecordingPrefs();
        Log.Write("recording resumed");
    }

    /// Stop both recorders now; the timer turns them back on at `until`.
    void SchedulePause(DateTime until)
    {
        engine.SetScreen(false);
        engine.SetAudio(false);
        // Check every 15 s rather than one long timer, so sleep and clock changes cannot make it late.
        resumeTimer.Interval = 15_000;
        if (until != DateTime.MaxValue) resumeTimer.Start(); else resumeTimer.Stop();
        UpdateIcon();
    }

    void CheckPause()
    {
        if (PausedUntil == null) ResumeRecording();
    }

    /// Turn screen and audio on or off to match the switches.
    void ApplyRecordingPrefs()
    {
        engine.SetScreen(settings.RecordScreen);
        engine.SetAudio(settings.RecordAudio);
        UpdateIcon();
    }

    ToolStripMenuItem KeepMenu(string title, bool screen)
    {
        var item = new ToolStripMenuItem(title);
        var current = screen ? settings.Screen : settings.Audio;
        foreach (var k in Enum.GetValues<KeepFor>())
            item.DropDownItems.Add(Toggle(k.Title(), k == current, () => SetKeep(screen, k)));
        return item;
    }

    void SetKeep(bool screen, KeepFor k)
    {
        var what = screen ? "screenshots" : "audio clips";
        var old = screen ? settings.Screen : settings.Audio;
        if (k.Seconds() is double secs && (old.Seconds() ?? double.PositiveInfinity) > secs)
        {
            if (!Confirm($"Keep {what} for {k.Title().ToLower()}?",
                         $"OpenRewind will delete {what} older than {k.Title().ToLower()} now, and keep doing it every hour. This cannot be undone.")) return;
        }
        if (screen) { settings.KeepScreen = k.Raw(); retention.ScreenKeep = k; }
        else { settings.KeepAudio = k.Raw(); retention.AudioKeep = k; }
        settings.Save();
        retention.RunNow();
    }

    void DeleteRecent(string what, DateTime from)
    {
        if (!Confirm($"Delete {what} of recordings?", $"Screenshots and audio from {what} will be deleted. This cannot be undone.")) return;
        var db = engine.Db;
        var now = DateTime.UtcNow;
        Task.Run(() =>
        {
            int f = db.DeleteFrames(from, now), a = db.DeleteAudio(from, now);
            Log.Write($"deleted {f} screenshots, {a} audio clips ({what})");
        });
    }

    void DeleteAll(bool screen, bool audio)
    {
        var what = screen && audio ? "all screenshots and audio" : screen ? "all screenshots" : "all audio recordings";
        if (!Confirm($"Delete {what}?", $"This removes {what} from this PC, and every journal recap. This cannot be undone.")) return;
        var db = engine.Db;
        var now = DateTime.UtcNow;
        Task.Run(() =>
        {
            int f = screen ? db.DeleteFrames(null, now) : 0;
            int a = audio ? db.DeleteAudio(null, now) : 0;
            db.DeleteRecaps(null, null);  // each one was written from both
            db.Compact();
            Log.Write($"deleted {f} screenshots, {a} audio clips (all)");
        });
    }

    static bool Confirm(string title, string text) =>
        MessageBox.Show(text, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;

    // MARK: updates

    /// A row under the status line while a newer version is out.
    ToolStripMenuItem? UpdateItem()
    {
        if (updater.Available is not ReleaseInfo r) return null;
        return updater.State switch
        {
            Updater.Phase.Downloading => new ToolStripMenuItem($"Downloading OpenRewind {r.Version}… {(int)(updater.Progress * 100)}%") { Enabled = false },
            Updater.Phase.Installing => new ToolStripMenuItem($"Installing OpenRewind {r.Version}…") { Enabled = false },
            Updater.Phase.Ready => Item($"Restart to Update to OpenRewind {r.Version}", updater.Update),
            _ => Item(Updater.CanInstall ? $"Update to OpenRewind {r.Version}…" : $"Download OpenRewind {r.Version}…", updater.Update),
        };
    }

    void CheckForUpdates()
    {
        updater.Check(error =>
        {
            const string title = "Check for Updates";
            if (error != null)
                MessageBox.Show($"Could not check for updates: {error}", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (updater.Available is ReleaseInfo r)
            {
                var verb = Updater.CanInstall ? "Update" : "Download";
                if (MessageBox.Show($"OpenRewind {r.Version} is out. You have {Updater.Current}.\n\n{verb} now?", title,
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) updater.Update();
            }
            else
                MessageBox.Show($"You have the latest version, {Updater.Current}.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    // MARK: storage

    List<string> StorageLines()
    {
        var size = dataSize is long n ? FormatBytes(n) : "measuring…";
        var lines = new List<string> { $"Using {size}" };
        if (engine.Db.OldestRecording() is DateTime oldest)
        {
            var local = oldest.ToLocalTime();
            lines.Add($"Goes back to {local:g} ({Ago(oldest)})");
        }
        else lines.Add("No recordings yet");
        return lines;
    }

    static string Ago(DateTime utc)
    {
        var s = DateTime.UtcNow - utc;
        if (s.TotalDays >= 2) return $"{(int)s.TotalDays} days ago";
        if (s.TotalHours >= 2) return $"{(int)s.TotalHours} hours ago";
        return $"{Math.Max(1, (int)s.TotalMinutes)} minutes ago";
    }

    static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : $"{b / 1024} KB";

    /// Measure the folder off the UI thread, then refresh the open menu with the result.
    void MeasureStorage()
    {
        if (measuring) return;
        measuring = true;
        var home = engine.Config.Home;
        Task.Run(() =>
        {
            var size = Database.FolderSize(home);
            menu.BeginInvoke(() =>
            {
                measuring = false;
                var changed = dataSize != size;
                dataSize = size;
                if (changed && menu.Items.OfType<ToolStripItem>().FirstOrDefault(i => i.Text?.StartsWith("   Using ") == true) is ToolStripItem item)
                    item.Text = "   " + StorageLines()[0];
            });
        });
    }

    string StatusLine()
    {
        var s = engine.Db.Stats();
        var a = engine.Db.AudioStats();
        var counts = $"  —  {s.Count} frames, {a.Segments} audio";
        if (PausedUntil is DateTime until) { var t = Pause.Text(until, DateTime.UtcNow); return char.ToUpper(t[0]) + t[1..] + counts; }
        return $"{(engine.ScreenOn ? "Screen on" : "Screen off")} · {(engine.AudioOn ? "Audio on" : "Audio off")}" + counts;
    }

    void UpdateIcon()
    {
        // The constructor turns recording on before the tray icon exists; it calls this again once it does.
        if (icon is null) return;
        // NotifyIcon tooltips are limited to 127 characters.
        var tip = PausedUntil is DateTime until ? $"OpenRewind — {Pause.Text(until, DateTime.UtcNow)}"
            : $"OpenRewind — {(engine.ScreenOn ? "Screen on" : "Screen off")} · {(engine.AudioOn ? "Audio on" : "Audio off")}";
        icon.Text = tip.Length > 127 ? tip[..127] : tip;
        icon.Icon = engine.ScreenOn || engine.AudioOn ? Program.AppIcon : Program.PausedIcon;
    }

    void ToggleServer()
    {
        if (engine.ServerOn) engine.StopServer(); else StartServer();
        settings.LocalSearchServer = engine.ServerOn;
        settings.Save();
    }

    void StartServer()
    {
        try { engine.StartServer(); }
        catch (Exception e) { Log.Write($"cannot start server: {e.Message}"); }
    }

    void ToggleScreen()
    {
        bool paused = PausedUntil != null;
        var on = !(paused ? settings.RecordScreen : engine.ScreenOn);
        if (!paused) engine.SetScreen(on);
        settings.RecordScreen = on;
        settings.Save();
        UpdateIcon();
    }

    void ToggleAudio()
    {
        bool paused = PausedUntil != null;
        var on = !(paused ? settings.RecordAudio : engine.AudioOn);
        if (!paused) engine.SetAudio(on);
        settings.RecordAudio = on;
        settings.Save();
        UpdateIcon();
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write($"cannot open {target}: {e.Message}"); }
    }

    void Quit()
    {
        icon.Visible = false;
        overlay.HideOverlay();
        hotKey.Dispose();
        resumeTimer.Dispose();
        recapTimer.Dispose();
        retention.Dispose();
        meaning.Dispose();
        updater.Dispose();
        meetings.Dispose();
        engine.Shutdown();
        ExitThread();
    }
}
