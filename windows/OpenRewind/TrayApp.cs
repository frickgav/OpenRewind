using System.Diagnostics;
using System.Drawing;

namespace OpenRewind;

/// Tray app: icon with separate screen / audio switches and the timeline overlay.
/// Same menu as the Mac menu bar app.
sealed partial class TrayApp : ApplicationContext
{
    readonly Engine engine;
    readonly Settings settings;
    readonly NotifyIcon icon;
    readonly ContextMenuStrip menu = new();
    readonly OverlayForm overlay;
    readonly TimelineModel timeline;
    HotKey hotKey;
    /// Ctrl+Alt+S stars the moment from anywhere.
    readonly HotKey starKey;
    readonly ScrollOpener scrollOpener;
    /// Turns recording back on when a timed pause ends.
    readonly System.Windows.Forms.Timer resumeTimer = new();
    /// Checks App Lock's "Lock Again After" every 15 s and closes history windows once it has passed.
    readonly System.Windows.Forms.Timer lockTimer = new() { Interval = 15_000 };
    readonly RetentionJob retention;
    /// Compress Older Recordings, and rewriting videos after deletes.
    readonly VideoJob video;
    readonly Updater updater;
    /// Last measured size of the data folder; measured in the background when the menu opens.
    long? dataSize;
    bool measuring;
    /// The AI chat keeps its conversation while its window is closed.
    readonly AiConversation aiConversation = new();
    readonly AiTools aiTools;
    readonly AppLock appLock;
    readonly Encryption encryption;
    readonly MeaningController meaning;
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
        timeline = new TimelineModel(engine.Db) { Settings = settings };
        aiTools = new AiTools(engine.Db) { Meaning = engine.Meaning };
        timeline.Meaning = engine.Meaning;
        journalTools = new AiTools(engine.Db);
        overlay = new OverlayForm(timeline) { Look = settings.TimelineLook };
        _ = overlay.Handle;  // create it on the UI thread now
        appLock = new AppLock(settings) { StateHome = engine.Config.Home };
        appLock.Locked += () => overlay.BeginInvoke(HideHistory);
        engine.ServerLocked = () => !appLock.IsUnlocked;
        encryption = new Encryption(engine, settings);
        encryption.Resume();
        lockTimer.Tick += (_, _) => appLock.Tick(historyInUse: HistoryShown && Form.ActiveForm != null);
        lockTimer.Start();
        meaning = new MeaningController(engine);
        meaning.Changed += () => overlay.BeginInvoke(() =>
        {
            SettingsForm.Live();
            if (overlay.Visible) overlay.Invalidate();
        });
        if (settings.SearchByMeaning) meaning.Start();

        if (settings.LocalSearchServer) StartServer();
        engine.StartTranscriber();
        engine.Recorder.MinorChangeGap = settings.TypingFrameGap;
        engine.Recorder.RedactSecrets = Database.RedactSecrets = settings.RedactSecrets;
        engine.Config.SnapshotScale = ScreenshotSizes.Scale(settings.ScreenshotPercent);
        resumeTimer.Tick += (_, _) => CheckPause();
        retention = new RetentionJob(engine.Db, settings.Screen, settings.Audio);
        retention.Start();
        video = new VideoJob(engine.Db, engine.Config, settings.CompressOlderRecordings);
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
        hotKey = new HotKey(Shortcut, ToggleTimeline);
        starKey = new HotKey(HotkeyShortcut.Star, StarMoment);
        // Ctrl+Shift + wheel anywhere opens it too, and that first turn of the wheel already moves back in time.
        scrollOpener = new ScrollOpener(overlay, () => overlay.Visible, delta =>
            appLock.Run("Unlock OpenRewind to see your timeline", () =>
            {
                if (overlay.Visible) return;
                overlay.ShowOverlay();
                overlay.ScrollTime(delta);
            }));
        scrollOpener.Enable(settings.ScrollOpensTimeline);

        menu.Opening += (_, _) => BuildMenu();
        icon = new NotifyIcon { Icon = Program.AppIcon, ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleTimeline(); };

        // Start recording, or re-arm a pause saved last time, only once `icon` exists:
        // both paths end in UpdateIcon(), which sets the icon's tooltip and picture.
        if (PausedUntil is DateTime until) SchedulePause(until); else ApplyRecordingPrefs();

        recapTimer.Tick += (_, _) => { recapTimer.Interval = 10 * 60_000; WriteDueRecap(); };
        recapTimer.Start();

        BuildMenu();
        UpdateIcon();
        if (!settings.OnboardingDone) ShowSetup();
    }

    /// Open the timeline, after Windows Hello when App Lock is on; close it if it is open.
    /// Ctrl+Alt+S: star now. On the open timeline it stars (or unstars) the moment shown instead.
    /// Needs no App Lock: it only saves the time, and shows nothing from history.
    void StarMoment()
    {
        if (overlay.Visible) { timeline.ToggleStar(); return; }
        try { engine.Db.AddStar(DateTime.UtcNow); }
        catch (Exception e) { Log.Write($"star: {e.Message}"); icon.ShowBalloonTip(2000, "OpenRewind", "Could not star this moment", ToolTipIcon.Warning); return; }
        icon.ShowBalloonTip(1500, "OpenRewind", PausedUntil != null ? "★ Moment starred (recording is paused)" : "★ Moment starred", ToolTipIcon.None);
    }

    void ToggleTimeline()
    {
        if (overlay.Visible) overlay.HideOverlay();
        else appLock.Run("Unlock OpenRewind to see your timeline", overlay.ShowOverlay);
    }

    /// The app locked (screen locked, PC asleep): close everything that shows history. Recording goes on.
    void HideHistory()
    {
        if (overlay.Visible) overlay.HideOverlay(restoreFocus: false);
        AiChatForm.Release();
        // Settings shows recent sites (Blocked Apps & Sites) and changes what is recorded, so it closes too.
        SettingsForm.Release();
        JournalForm.Release();
        MeetingsForm.Release();
        InsightsForm.Release();
        ConnectAiAppsForm.Release();
        Task.Run(FrameImages.ReleaseAll);
    }

    /// A window that shows history is open (App Lock's "Lock Again After" waits while one is in use).
    bool HistoryShown => overlay.Visible || AiChatForm.IsShown || JournalForm.IsShown || MeetingsForm.IsShown || InsightsForm.IsShown || SettingsForm.IsShown;

    /// The first-run setup; ends in the guided tour if the user asks for it.
    void ShowSetup() => appLock.Run("Unlock OpenRewind to change its setup", OpenSetup);

    void OpenSetup()
    {
        if (setup is { IsDisposed: false }) { setup.Activate(); return; }
        OnboardingForm.Hotkey = Shortcut.Label;
        setup = new OnboardingForm(settings, engine.Db, tour => { if (tour) ShowTutorial(); });
        setup.Show();
    }
    OnboardingForm? setup;

    void ShowJournal() => appLock.Run("Unlock OpenRewind to see your journal",
                                      () => JournalForm.ShowFor(settings, engine.Db, journalTools, OpenMoment, OpenAiSettings));

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
        if (engine.Db.IsConverting) return;  // the database is held while Encrypt Recordings converts it
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

    void ShowMeetings() => appLock.Run("Unlock OpenRewind to see your meetings",
                                       () => MeetingsForm.ShowFor(engine.Db, settings, meetings, OpenMoment, OpenAiSettings));

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
    /// Behind App Lock: AI apps read the whole history through it.
    void ToggleMcp() => appLock.Run("Unlock OpenRewind to change whether AI apps can read your history", () =>
    {
        settings.McpEnabled = !settings.McpEnabled;
        settings.Save();
        Log.Write($"AI apps (MCP) {(settings.McpEnabled ? "allowed" : "not allowed")}");
        if (settings.McpEnabled) ConnectAiAppsForm.ShowFor(settings, engine.Config); else ConnectAiAppsForm.SettingsChanged(settings);
    });

    void ShowConnectAiApps() => appLock.Run("Unlock OpenRewind to connect AI apps", () => ConnectAiAppsForm.ShowFor(settings, engine.Config));

    void ShowAiChat() => appLock.Run("Unlock OpenRewind to ask about your history",
                                     () => AiChatForm.ShowFor(settings, aiTools, aiConversation, OpenMoment, OpenAiSettings));

    /// The chat for the overlay; the same one the Ask AI window shows.
    AiChatPanel AiChat() => AiChatPanel.Get(settings, aiTools, aiConversation, OpenMoment, OpenAiSettings);

    /// Where the time went (app names and hours, so it is history too); a click shows that app or
    /// moment in the timeline, behind App Lock as well.
    void ShowInsights() => appLock.Run("Unlock OpenRewind to see your insights", () => InsightsForm.ShowFor(settings, engine.Db, (at, app) =>
        appLock.Run("Unlock OpenRewind to see your timeline", () =>
        {
            if (!overlay.Visible) overlay.ShowOverlay();
            timeline.OpenAt(at, app);
        })));

    /// A moment link in Ask AI. Goes through App Lock too: the chat window may have been left open.
    void OpenMoment(DateTime at) => appLock.Run("Unlock OpenRewind to see your timeline", () =>
    {
        if (!overlay.Visible) overlay.ShowOverlay();
        timeline.Jump(at);
    });

    /// Ask AI, the journal and meetings link here when no AI provider is set up. Settings is a
    /// normal window, which would sit under the overlay.
    void OpenAiSettings()
    {
        if (overlay.Visible) overlay.HideOverlay();
        ShowSettings(SettingsTab.Ai);
    }

    /// The Settings window, on `tab` (or the tab it was on). Behind App Lock: it shows recent sites,
    /// and can stop encryption, block lists or recording.
    void ShowSettings(SettingsTab? tab) => appLock.Run("Unlock OpenRewind to change its settings", () => SettingsForm.ShowFor(this, tab));

    void ShowTutorial() => appLock.Run("Unlock OpenRewind to see your timeline", () =>
    {
        OnboardingForm.Hotkey = Shortcut.Label;
        TutorialCoach.Start(overlay, timeline);
    });

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
        {
            var picked = SpeechModels.ById(settings.TranscriptionModel);
            menu.Items.Add(picked.Engine == SpeechEngine.Parakeet
                ? Item($"   Transcription off: download {picked.Name}…", () => PickModel(picked))
                : Item("   Transcription off: set up Whisper…", () => Open(WhisperHelp)));
        }
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Open Timeline", null, (_, _) => appLock.Run("Unlock OpenRewind to see your timeline", overlay.ShowOverlay))
        {
            ShortcutKeyDisplayString = Shortcut.Label
        });
        // The browser page when the local server is on, else the timeline's own search box.
        menu.Items.Add(Item("Search…", () => appLock.Run("Unlock OpenRewind to search your history", () =>
        {
            if (engine.ServerOn) Open($"http://127.0.0.1:{engine.Config.Port}/"); else overlay.ShowSearch();
        })));
        menu.Items.Add(new ToolStripMenuItem("Star This Moment", null, (_, _) => StarMoment()) { ShortcutKeyDisplayString = HotkeyShortcut.Star.Label });
        menu.Items.Add(Item("Ask AI…", ShowAiChat));
        menu.Items.Add(Item("Journal…", ShowJournal));
        menu.Items.Add(Item("Meetings…", ShowMeetings));
        menu.Items.Add(Item("Insights…", ShowInsights));
        menu.Items.Add(Item("Show Data Folder", () => appLock.Run("Unlock OpenRewind to open its data folder", () => Open(engine.Config.Home))));
        foreach (var line in StorageLines()) menu.Items.Add(new ToolStripMenuItem("   " + line) { Enabled = false });
        MeasureStorage();
        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = Item(Permissions.MicrophoneOn ? "Settings…" : "Settings…  ⚠", () => ShowSettings(null));
        if (!Permissions.MicrophoneOn) settingsItem.ToolTipText = "The microphone is off in Windows: see Settings → General.";
        menu.Items.Add(settingsItem);
        menu.Items.Add(Item("Quit OpenRewind", Quit));
    }

    static ToolStripMenuItem Item(string title, Action act) => new(title, null, (_, _) => act());

    static ToolStripMenuItem Toggle(string title, bool on, Action act) => new(title, null, (_, _) => act()) { Checked = on };

    // MARK: transcription model

    const string WhisperHelp = "https://github.com/frickgav/openscreen/blob/main/windows/README.md#whisper";
    const string ModelsHelp = "https://github.com/frickgav/openscreen/blob/main/README.md#choosing-a-transcription-model";

    /// "Using NVIDIA Parakeet v3", "Last clip 14:02: 30 s of audio in 3.1 s", "4 clips waiting".
    List<string> TranscriptionStatus()
    {
        var lines = new List<string>();
        if (engine.Transcriber is not Transcriber tx)
        {
            lines.Add($"Not transcribing: {engine.TranscriberProblem ?? "audio recording is off"}");
            return lines;
        }
        lines.Add($"Using {tx.DisplayName}");
        var st = tx.Status;
        if (st.Problem != null) lines.Add("Last try failed: " + (st.Problem.Length > 70 ? st.Problem[..70] + "…" : st.Problem));
        else if (st.LastAt is DateTime at)
            lines.Add($"Last clip {at.ToLocalTime():t}: {st.LastAudioSeconds:0} s of audio in {st.LastSeconds:0.0} s");
        else lines.Add("No clips transcribed since it started");
        // Converting holds the database for a while (Encrypt Recordings); don't wait for it here.
        var pending = engine.Db.IsConverting ? 0 : engine.Db.AudioStats().Pending;
        if (pending > 0) lines.Add(pending == 1 ? "1 clip waiting" : $"{pending} clips waiting");
        return lines;
    }

    /// Switch models; one that is not on this PC yet is downloaded first.
    void PickModel(SpeechModel m)
    {
        if (SpeechModels.LanguageWarning(m, engine.Config.WhisperLanguage) is string warn &&
            MessageBox.Show(warn + "\n\nUse it anyway?", m.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        if (m.Files.Count > 0 && !SpeechModels.IsDownloaded(m, engine.Config.Home))
        {
            ModelDownloadForm.ShowFor(m, engine.Config.Home, () => UseModel(m));
            return;
        }
        UseModel(m);
    }

    void UseModel(SpeechModel m)
    {
        var changed = settings.TranscriptionModel != m.Id;
        settings.TranscriptionModel = m.Id;
        settings.Save();
        // Same model and it runs: nothing to do. Otherwise start it (again), which also retries a failed load.
        if (!changed && engine.Transcriber != null && engine.Transcriber.Status.Problem == null) return;
        engine.RestartTranscriber(m.Id);
        Log.Write($"transcription model set to {m.Name}");
        if (engine.Transcriber == null && m.Engine == SpeechEngine.Whisper &&
            MessageBox.Show($"{m.Name} needs whisper-cli.exe, which is not on this PC yet ({engine.TranscriberProblem}). Open the setup steps?\n\n"
                            + "NVIDIA Parakeet v3 needs nothing else.", "Set Up Whisper", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            Open(WhisperHelp);
    }

    /// Queue recorded audio again for the model picked now, after saying how long it may take.
    void Retranscribe(string what, TimeSpan span)
    {
        if (engine.Transcriber is not Transcriber tx) return;
        var from = DateTime.UtcNow - span;
        var (clips, seconds) = engine.Db.RetranscribeCount(from, engine.Config.AudioChunkSeconds);
        if (clips == 0)
        {
            MessageBox.Show($"There is no transcribed audio from {what}.", "Re-transcribe", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var st = tx.Status;
        var busy = st.LastAt != null && st.LastAudioSeconds > 0
            ? $"for {SpeechModels.About(seconds * st.LastSeconds / st.LastAudioSeconds)}"
            : "for a while";
        if (!Confirm($"Re-transcribe {what}?",
                     $"{clips} clips (up to {SpeechModels.About(seconds).Replace("about ", "")} of audio) will be transcribed again with {tx.DisplayName}. "
                     + $"This keeps the processor busy {busy} and uses more battery. New recordings still go first.\n\n"
                     + "The new text replaces the old for good: the old text stays searchable only until each clip is redone. "
                     + "Clips with no speech are left as they are, and meeting notes already made are kept (not made or sent again)."
                     + (SpeechModels.LanguageWarning(tx.Model, engine.Config.WhisperLanguage) is string warn ? "\n\n" + warn : ""))) return;
        var db = engine.Db;
        Task.Run(() => Log.Write($"re-transcribing {db.RequeueAudio(from)} audio clips from {what} with {tx.DisplayName}"));
    }

    // MARK: search by meaning

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

    // MARK: shortcut

    HotkeyShortcut Shortcut => settings.TimelineShortcut ?? HotkeyShortcut.Standard;

    void ChangeShortcut()
    {
        var old = Shortcut;
        hotKey.Dispose();  // so pressing the old shortcut now does not open the timeline
        using var picker = new ShortcutPicker(old);
        var picked = picker.ShowDialog() == DialogResult.OK ? picker.Picked : null;
        hotKey = new HotKey(old, ToggleTimeline);
        if (picked != null && picked != old) SetShortcut(picked);
    }

    /// Switch to a new shortcut; keeps the old one when Windows refuses the new one.
    void SetShortcut(HotkeyShortcut sc)
    {
        var old = Shortcut;
        hotKey.Dispose();
        var next = new HotKey(sc, ToggleTimeline);
        if (next.Registered)
        {
            hotKey = next;
            settings.TimelineShortcut = sc == HotkeyShortcut.Standard ? null : sc;
            settings.Save();
            Log.Write($"timeline shortcut set to {sc.Label}");
            return;
        }
        next.Dispose();
        hotKey = new HotKey(old, ToggleTimeline);
        MessageBox.Show($"Another app or Windows uses this shortcut. The shortcut stays {old.Label}.", $"{sc.Label} is already in use",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // MARK: encryption & lock

    async void ToggleAppLock()
    {
        if (settings.RequireUnlock)
        {
            // Turning it off from the menu takes Windows Hello (or the Windows password when Hello is
            // gone). It is not a wall against programs running as you, which could edit settings.json.
            if (!await appLock.Unlock("Unlock OpenRewind to stop asking for Windows Hello")) return;
            settings.RequireUnlock = false;
        }
        else
        {
            if (!await AppLock.Available())
            {
                MessageBox.Show($"Windows Hello is not set up on this PC. {AppLock.SetUpHello}", "Require Windows Hello",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            settings.RequireUnlock = true;
            appLock.Forget();
            // Check it works now, so nobody is locked out by a Windows Hello that never answers.
            if (!await appLock.Unlock("Check that Windows Hello works for OpenRewind")) { settings.RequireUnlock = false; return; }
        }
        settings.Save();
        Log.Write($"app lock {(settings.RequireUnlock ? "on" : "off")}");
    }

    // MARK: permissions

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
        // While the database is being encrypted it is not read (the conversion holds it).
        var converting = engine.Db.IsConverting;
        // Compressed recordings: how much less the videos take than the screenshots they replaced.
        var v = converting ? default : engine.Db.VideoStats();
        var lines = new List<string> { $"Using {size}" + (converting ? "  (encrypting the database…)" : v.Segments > 0 ? $" · video saves {FormatBytes(v.Saved)}" : "") };
        if (!converting && engine.Db.OldestRecording() is DateTime oldest)
        {
            var local = oldest.ToLocalTime();
            lines.Add($"Goes back to {local:g} ({Ago(oldest)})");
        }
        else if (!converting) lines.Add("No recordings yet");
        // Deleted frames that are still inside a video until it is written again (or turned back into pictures).
        if (!converting && engine.Db.StuckRewrites() is int stuck and > 0)
            lines.Add($"{stuck} video{(stuck == 1 ? "" : "s")} still hold{(stuck == 1 ? "s" : "")} deleted frames · being redone");
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

    /// Measure the folder off the UI thread, then refresh the open menu (and Settings → Storage) with the result.
    void MeasureStorage()
    {
        if (measuring) return;
        measuring = true;
        var home = engine.Config.Home;
        Task.Run(() =>
        {
            var size = Database.FolderSize(home);
            // The overlay's handle exists from the start; the menu's only once it has been shown.
            overlay.BeginInvoke(() =>
            {
                measuring = false;
                var changed = dataSize != size;
                dataSize = size;
                if (!changed) return;
                if (menu.Items.OfType<ToolStripItem>().FirstOrDefault(i => i.Text?.StartsWith("   Using ") == true) is ToolStripItem item)
                    item.Text = "   " + StorageLines()[0];
                SettingsForm.Live();
            });
        });
    }

    string StatusLine()
    {
        // Converting holds the database for a while (Encrypt Recordings); don't wait for it here.
        if (engine.Db.IsConverting) return "Encrypting the database… recording resumes when it is done";
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
        appLock.Forget();  // AI apps (MCP) get nothing once the app is gone
        encryption.Stop();
        icon.Visible = false;
        overlay.HideOverlay();
        hotKey.Dispose();
        starKey.Dispose();
        scrollOpener.Dispose();
        resumeTimer.Dispose();
        lockTimer.Dispose();
        recapTimer.Dispose();
        retention.Dispose();
        video.Dispose();
        meaning.Dispose();
        updater.Dispose();
        meetings.Dispose();
        engine.Shutdown();
        // Deletes the decrypted copies of open videos.
        FrameImages.ReleaseAll();
        ExitThread();
    }
}
