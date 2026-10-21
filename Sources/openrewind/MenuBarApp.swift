import AppKit
import AVFoundation
import Carbon.HIToolbox
import ServiceManagement

/// Menu bar app: status icon with separate screen / audio switches and the timeline overlay.
final class MenuBarApp: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let engine: Engine
    private var item: NSStatusItem!
    private var overlay: OverlayController!
    private var hotKey: HotKey?
    private let defaults = UserDefaults.standard
    private let screenKey = "recordScreen", audioKey = "recordAudio"
    private let keepScreenKey = "keepScreen", keepAudioKey = "keepAudio"
    /// Seconds between saved frames while only a few words change (2 = every capture).
    private let typingGapKey = "typingFrameGap"
    /// The local HTTP API + browser search page. Off unless the user turns it on in Settings.
    private let serverKey = "localSearchServer"
    /// Search by Meaning: index moments in the background to find them by meaning. Off unless the user turns it on.
    private let meaningKey = MeaningService.settingKey
    private var retention: RetentionJob!
    /// Compress Older Recordings: keep screenshots older than an hour as video. Off unless turned on.
    private let compressKey = "compressOlderRecordings"
    private var video: VideoJob!
    /// Last measured size of the data folder; measured in the background when the menu opens.
    private var dataSize: Int64?
    private var measuring = false
    private let pausedUntilKey = "pausedUntil"
    private var resumeTimer: Timer?
    /// When recording comes back on by itself. `.distantFuture` means paused until the user resumes.
    private var pausedUntil: Date? {
        get { (defaults.object(forKey: pausedUntilKey) as? Date).flatMap { $0 > Date() ? $0 : nil } }
        set { defaults.set(newValue, forKey: pausedUntilKey) }
    }
    private var isPaused: Bool { pausedUntil != nil }
    private let appLock = AppLock()
    /// Checks every 15 s whether the unlock has run out, so open history windows close.
    private var lockTimer: Timer?
    private var encryption: Encryption!
    /// The AI chat keeps its conversation while its window is closed.
    private lazy var aiChat: AIChatModel = {
        let m = AIChatModel(db: engine.db, meaning: engine.meaning)
        // A moment link goes through App Lock too: the chat window may have been left open.
        m.openMoment = { [weak self] d in self?.appLock.run("see your timeline") { self?.overlay.show(at: d) } }
        // AI Settings is a normal window, which would sit under the overlay.
        m.openSettings = { [weak self] in
            if self?.overlay.isVisible == true { self?.overlay.hide() }
            self?.showAISettings()
        }
        return m
    }()
    /// The journal keeps the day it shows while its window is closed.
    private lazy var journal: JournalModel = {
        let m = JournalModel(db: engine.db)
        m.openMoment = { [weak self] d in self?.appLock.run("see your timeline") { self?.overlay.show(at: d) } }
        m.openSettings = { [weak self] in self?.showAISettings() }
        return m
    }()
    /// What the Settings window shows; it keeps the tab it was on while closed.
    private lazy var settingsModel = SettingsModel(app: self)
    /// Checks every few minutes whether yesterday's recap is due (Write Each Day's Recap Automatically).
    private var recapTimer: Timer?
    private var writingRecap = false

    /// Finds meetings (and asks about notes) within seconds of a call; the Meetings window and the AI notes read what it stores.
    private lazy var meetings = MeetingCenter(db: engine.db, chunkSeconds: engine.config.audioChunkSeconds,
                                              transcribing: { [weak self] in self?.engine.transcriber != nil })

    init(engine: Engine) {
        self.engine = engine
        super.init()
        copyOldSettings()
        clearOldPermissions()
        defaults.register(defaults: [screenKey: true, audioKey: true, serverKey: false, meaningKey: false,
                                     keepScreenKey: KeepFor.forever.rawValue, keepAudioKey: KeepFor.forever.rawValue,
                                     typingGapKey: 2, compressKey: false])
    }

    /// The app used to be OpenScreen (bundle id ai.agilityresources.openscreen). Bring its menu
    /// settings over once. macOS permissions cannot be carried over: they are asked for again.
    private func copyOldSettings() {
        let done = "copiedOpenScreenSettings"
        guard !defaults.bool(forKey: done), let old = UserDefaults(suiteName: "ai.agilityresources.openscreen") else { return }
        for key in [screenKey, audioKey, keepScreenKey, keepAudioKey, serverKey] {
            if defaults.object(forKey: key) == nil, let v = old.object(forKey: key) { defaults.set(v, forKey: key) }
        }
        defaults.set(true, forKey: done)
    }

    /// The old OpenScreen app's entries in System Settings → Privacy & Security (Screen Recording,
    /// Microphone, …) look like a second app. Clear them once. tccutil only accepts a bundle id that
    /// macOS can still find, so this has to run while the old app is still on disk; once it is gone
    /// the user removes the row with the − button in System Settings.
    private func clearOldPermissions() {
        let done = "clearedOpenScreenPermissions", oldID = "ai.agilityresources.openscreen"
        guard !defaults.bool(forKey: done) else { return }
        guard NSWorkspace.shared.urlForApplication(withBundleIdentifier: oldID) != nil else {
            defaults.set(true, forKey: done)
            return
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/tccutil")
        p.arguments = ["reset", "All", oldID]
        p.terminationHandler = { [defaults] p in
            defaults.set(true, forKey: done)
            log("cleared old OpenScreen permissions (tccutil exit \(p.terminationStatus))")
        }
        do { try p.run() } catch { log("cannot run tccutil: \(error.localizedDescription)") }
    }

    func applicationDidFinishLaunching(_ note: Notification) {
        overlay = OverlayController(db: engine.db)
        overlay.chat = { [unowned self] in self.aiChat }
        appLock.onLock = { [weak self] in self?.hideHistory() }
        appLock.stateHome = engine.config.home
        let t = Timer(timeInterval: 15, repeats: true) { [weak self] _ in
            guard let self else { return }
            // In use: OpenRewind is in front with a window that shows history.
            let open = self.overlay.isVisible || InsightsWindow.shared.isOpen
                || ["ai", "journal", "meetings", "settings"].contains { SetupWindows.shared.isOpen($0) }
            self.appLock.tick(historyInUse: NSApp.isActive && open)
        }
        RunLoop.main.add(t, forMode: .common)
        lockTimer = t
        engine.serverLocked = { [appLock] in !appLock.isUnlocked }
        encryption = Encryption(engine: engine)
        encryption.resume()
        overlay.model.meaning = engine.meaning
        engine.meaning.pace = { MeaningPace.now() }
        if defaults.bool(forKey: meaningKey) { startMeaning() }
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu

        if defaults.bool(forKey: serverKey) { startServer() }
        engine.startTranscriber()
        engine.recorder.minorChangeGap = TimeInterval(defaults.integer(forKey: typingGapKey))
        if let until = pausedUntil { schedulePause(until) } else { applyRecordingPrefs() }
        retention = RetentionJob(db: engine.db, screen: keep(keepScreenKey), audio: keep(keepAudioKey))
        retention.start()
        video = VideoJob(db: engine.db, dataDir: engine.config.dataDir, enabled: defaults.bool(forKey: compressKey))
        video.start()
        meetings.start()
        Database.buildSlowIndexesInBackground(path: engine.config.dbPath)

        // ⌥⌘T (or the shortcut picked in Settings) opens the timeline from anywhere.
        registerHotKey(Shortcut.load(defaults))
        Updater.shared.start()
        updateIcon()
        if !BlocklistStore.shared.onboardingDone { showSetup() }

        DispatchQueue.main.asyncAfter(deadline: .now() + 60) { [weak self] in self?.writeDueRecap() }
        recapTimer = Timer.scheduledTimer(withTimeInterval: 600, repeats: true) { [weak self] _ in self?.writeDueRecap() }
    }

    /// The journal is history too: behind App Lock.
    @objc private func showJournal() {
        appLock.run("see your journal") { [weak self] in
            guard let self else { return }
            let j = self.journal
            j.reload()
            SetupWindows.shared.show("journal", title: "Journal", size: NSSize(width: 620, height: 800), resizable: true) {
                JournalView(model: j)
            }
        }
    }

    /// Write Each Day's Recap Automatically: yesterday's recap, the first time the app runs after
    /// the day ended, for the AI provider it was turned on for. A failed try is retried on the next
    /// check, up to `Journal.autoAttempts` times. Days with nothing recorded, or with a recap of the
    /// whole day already, are skipped; a recap written by hand partway through the day is replaced.
    private func writeDueRecap() {
        let s = AISettings.load()
        guard !writingRecap, s.isReady, JournalSettings.isOn(for: s),
              let key = Journal.autoRecapDue(now: Date(), lastDone: JournalSettings.lastAutoDay,
                                             attemptDay: JournalSettings.attemptDay, attempts: JournalSettings.attempts),
              let day = Journal.date(fromKey: key) else { return }
        let db = engine.db, j = journal
        guard !j.writing.contains(key), !db.isConverting else { return }  // held while Encrypt Recordings converts it
        if let r = db.recap(day: key), !r.partial { JournalSettings.lastAutoDay = key; return }
        writingRecap = true
        let tools = JournalModel.tools(db), price = AISettings.price(for: s.model)
        Task { @MainActor [weak self] in
            defer { self?.writingRecap = false }
            let stats = await Task.detached { db.journalStats(day: day, tools: tools) }.value
            guard !stats.isEmpty else { JournalSettings.lastAutoDay = key; return }
            // Counted before the request, so one that never comes back still counts.
            JournalSettings.attempts = JournalSettings.attemptDay == key ? JournalSettings.attempts + 1 : 1
            JournalSettings.attemptDay = key
            guard let r = await j.writeRecap(for: day, auto: true) else { return }
            if let e = r.error {
                log("automatic journal recap for \(key) failed (try \(JournalSettings.attempts) of \(Journal.autoAttempts)): \(e)")
                return
            }
            JournalSettings.lastAutoDay = key
            log("wrote the journal recap for \(key) (\(r.usage.describe(price)))")
        }
    }

    @objc private func toggleAutoRecap() {
        let s = AISettings.load()
        JournalSettings.set(!JournalSettings.isOn(for: s), for: s)
        journal.autoRecap = JournalSettings.isOn(for: s)
    }

    /// The first-run setup; ends in the guided tour if the user asks for it.
    @objc private func showSetup() {
        appLock.run("change OpenRewind's setup") { [weak self] in
            guard let self else { return }
            Onboarding.show(db: self.engine.db) { [weak self] tour in if tour { self?.overlay.showTutorial() } }
        }
    }

    @objc private func showTutorial() {
        appLock.run("see your timeline") { [weak self] in self?.overlay.showTutorial() }
    }

    @objc private func showAIChat() {
        appLock.run("ask about your history") { [weak self] in
            guard let self else { return }
            let chat = self.aiChat
            chat.reloadSettings()
            SetupWindows.shared.show("ai", title: "Ask OpenRewind", size: NSSize(width: 560, height: 720), resizable: true) {
                AIChatView(model: chat)
            }
        }
    }

    /// Meeting transcripts and notes are history too: behind App Lock, and so are its moment links.
    @objc private func showMeetings() {
        appLock.run("see your meetings") { [weak self] in
            guard let self else { return }
            let model = MeetingsModel(db: self.engine.db)
            model.openMoment = { [weak self] d in self?.appLock.run("see your timeline") { self?.overlay.show(at: d) } }
            model.openSettings = { [weak self] in self?.showAISettings() }
            let center = self.meetings
            SetupWindows.shared.show("meetings", title: "Meetings", size: NSSize(width: 1000, height: 700), resizable: true) {
                MeetingsView(model: model, center: center)
            }
        }
    }

    /// Turning it on says plainly what it sends and that it costs tokens.
    @objc private func toggleMeetingSummaries() {
        if MeetingCenter.autoSummarize {
            MeetingCenter.autoSummarize = false
            return
        }
        NSApp.activate(ignoringOtherApps: true)
        let ai = AISettings.load()
        guard ai.isReady else {
            let a = NSAlert()
            a.messageText = "Summarize Meetings with AI"
            a.informativeText = "Meeting summaries use your own AI key. Set up a provider in AI Settings first, then turn this on."
            a.addButton(withTitle: "Open AI Settings…")
            a.addButton(withTitle: "Cancel")
            if Dialogs.run(a) == .alertFirstButtonReturn { showAISettings() }
            return
        }
        let a = NSAlert()
        a.messageText = "Summarize meetings with AI?"
        a.informativeText = Self.meetingConsentText(ai.kind.title)
        a.addButton(withTitle: "Turn On")
        a.addButton(withTitle: "Cancel")
        guard Dialogs.run(a) == .alertFirstButtonReturn else { return }
        MeetingCenter.autoSummarize = true
        log("meeting summaries with AI on")
    }

    /// What turning on meeting summaries sends, and where. Same words as the Windows app.
    static func meetingConsentText(_ provider: String) -> String {
        "After each call you take notes for ends, OpenRewind sends its whole transcript (what was said and heard during the call) to \(provider) "
            + "using your key, to write a summary, decisions and action items.\n\n"
            + "This costs tokens on your account, and \(provider)'s privacy terms apply. Calls in blocked apps and sites never become "
            + "meetings, but the transcript is not filtered by app: anything said or played during a call is sent, even while a blocked "
            + "app or site was in front.\n\nOnly meetings from now on are summarized, and only to \(provider): if you switch providers "
            + "in AI Settings, OpenRewind asks again. You can still summarize any meeting yourself in the Meetings window."
    }

    /// After AI Settings change: summaries were agreed to for another provider, so ask before sending
    /// transcripts to this one.
    private func recheckMeetingConsent() {
        guard MeetingCenter.needsNewConsent else { return }
        let ai = AISettings.load()
        let a = NSAlert()
        a.messageText = "Summarize meetings with the new provider?"
        a.informativeText = "Meeting summaries were turned on for \(MeetingCenter.consentTitle). AI Settings now use \(ai.kind.title)"
            + (ai.kind == .custom ? " (\(ai.baseURL))" : "") + ".\n\nSend each meeting's transcript to \(ai.kind.title) from now on? "
            + "It uses your key and costs tokens. If you choose Turn Off, meeting summaries are turned off."
        a.addButton(withTitle: "Turn Off")
        a.addButton(withTitle: "Use \(ai.kind.title)")
        if Dialogs.run(a) == .alertSecondButtonReturn {
            MeetingCenter.consent = MeetingNotes.consentKey(ai)
            log("meeting summaries moved to the new provider")
        } else {
            MeetingCenter.autoSummarize = false
            log("meeting summaries off (provider changed)")
        }
    }

    /// Where the time went (app names and hours, so it is history too); a click opens the timeline.
    @objc private func showInsights() {
        appLock.run("see your insights") { [weak self] in
            guard let self else { return }
            InsightsWindow.shared.show(db: self.engine.db) { [weak self] date, app in
                self?.appLock.run("see your timeline") { self?.overlay.show(at: date, app: app) }
            }
        }
    }

    /// Open the timeline, after Touch ID or the password when App Lock is on; close it if it is open.
    private func toggleTimeline() {
        if overlay.isVisible { overlay.hide() } else { appLock.run("see your timeline") { [weak self] in self?.overlay.show() } }
    }

    /// The app locked (screen locked, Mac asleep): close everything that shows history. Recording goes on.
    private func hideHistory() {
        if overlay.isVisible { overlay.hide(restoreFocus: false) }
        // Settings shows recent sites (Blocked Apps & Sites) and changes what is recorded, so it closes too.
        for w in ["ai", "settings", "journal", "meetings", "mcp"] { SetupWindows.shared.close(w) }
        InsightsWindow.shared.close()
        FrameImages.shared.releaseAll()
    }

    /// Ask AI, the journal and meetings link here when no AI provider is set up.
    @objc private func showAISettings() { showSettings(.ai) }

    @objc private func openSettings() { showSettings(nil) }

    /// The Settings window, on `tab` (or the tab it was on). Behind App Lock: it shows recent sites,
    /// and can stop encryption, block lists or recording.
    private func showSettings(_ tab: SettingsTab?) {
        appLock.run("change OpenRewind's settings") { [weak self] in
            guard let self else { return }
            if let tab { self.settingsModel.tab = tab }
            self.settingsModel.refresh()
            SetupWindows.shared.show("settings", title: "Settings", size: NSSize(width: 820, height: 860), resizable: true,
                                     onClose: { [weak self] in self?.settingsModel.stop() }) {
                SettingsView(model: self.settingsModel)
            }
            self.settingsModel.start()
        }
    }

    /// After AI settings are saved.
    private func aiSettingsSaved() {
        aiChat.reloadSettings()
        journal.reload()
        recheckMeetingConsent()
    }

    /// Turning it on shows how to add OpenRewind to each AI app.
    /// Behind App Lock: AI apps read the whole history through it.
    @objc private func toggleMCP() {
        appLock.run("change whether AI apps can read your history") { [weak self] in
            MCPSetup.enabled.toggle()
            log("AI apps (MCP) \(MCPSetup.enabled ? "allowed" : "not allowed")")
            if MCPSetup.enabled { self?.openConnectAIApps() }
        }
    }

    @objc private func showConnectAIApps() {
        appLock.run("connect AI apps") { [weak self] in self?.openConnectAIApps() }
    }

    private func openConnectAIApps() {
        SetupWindows.shared.show("mcp", title: "Connect AI Apps", size: NSSize(width: 620, height: 860), resizable: true) {
            ConnectAIAppsView(home: engine.config.home)
        }
    }

    // Build the menu fresh each time it opens, so it always shows the live state.
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let status = NSMenuItem(title: statusLine(), action: nil, keyEquivalent: "")
        status.isEnabled = false
        menu.addItem(status)
        if let update = updateItem() { menu.addItem(update) }
        menu.addItem(.separator())

        if let until = pausedUntil {
            let resume = action("Resume Recording (\(pauseText(until)))", #selector(resumeRecording))
            resume.image = NSImage(systemSymbolName: "play.circle", accessibilityDescription: nil)
            menu.addItem(resume)
        } else {
            menu.addItem(pauseMenu())
        }
        menu.addItem(.separator())

        // While paused, the switches show and change what comes back on after the pause.
        let screenWanted = isPaused ? defaults.bool(forKey: screenKey) : engine.screenOn
        let audioWanted = isPaused ? defaults.bool(forKey: audioKey) : engine.audioOn
        menu.addItem(toggleItem("Record Screen", on: screenWanted, action: #selector(toggleScreen), symbol: "display"))
        if (engine.screenOn || engine.audioOn) && !Capture.hasScreenPermission {
            let fix = action("  ⚠︎ Screen permission needed — Open Settings…", #selector(openScreenSettings))
            menu.addItem(fix)
        }
        menu.addItem(toggleItem("Record Audio", on: audioWanted, action: #selector(toggleAudio), symbol: "waveform"))
        if engine.audioOn, engine.transcriber == nil {
            let picked = SpeechModels.byId(SpeechModels.picked)
            if picked.engine == .parakeet {
                let fix = action("  Transcription off: download \(picked.name)…", #selector(pickModel(_:)))
                fix.representedObject = picked.id
                menu.addItem(fix)
            } else {
                menu.addItem(action("  Transcription off: install whisper-cpp…", #selector(openWhisperHelp)))
            }
        }
        menu.addItem(.separator())

        let sc = Shortcut.load(defaults)
        let single = sc.key.count == 1 ? sc.key.lowercased() : ""
        let tl = NSMenuItem(title: single.isEmpty ? "Open Timeline  (\(sc.label))" : "Open Timeline",
                            action: #selector(openTimeline), keyEquivalent: single)
        tl.keyEquivalentModifierMask = sc.menuModifiers
        tl.target = self
        menu.addItem(tl)
        menu.addItem(action("Search…", #selector(openSearch)))
        menu.addItem(action("Ask AI…", #selector(showAIChat)))
        menu.addItem(action("Journal…", #selector(showJournal)))
        menu.addItem(action("Meetings…", #selector(showMeetings)))
        menu.addItem(action("Insights…", #selector(showInsights)))
        menu.addItem(action("Show Data Folder", #selector(openData)))
        for line in storageLines() {
            let i = NSMenuItem(title: "  " + line, action: nil, keyEquivalent: "")
            i.isEnabled = false
            menu.addItem(i)
        }
        measureStorage(menu)
        menu.addItem(.separator())

        let missing = Permission.allCases.filter { !$0.granted }.count
        let settings = action(missing > 0 ? "Settings…  ⚠︎" : "Settings…", #selector(openSettings), key: ",")
        settings.image = NSImage(systemSymbolName: "gearshape", accessibilityDescription: nil)
        if missing > 0 { settings.toolTip = "\(missing) permission\(missing == 1 ? "" : "s") needed: see Settings → General." }
        menu.addItem(settings)
        menu.addItem(action("Quit OpenRewind", #selector(quit), key: "q"))
    }

    // MARK: pause

    private func pauseMenu() -> NSMenuItem {
        let item = NSMenuItem(title: "Pause Recording", action: nil, keyEquivalent: "")
        item.image = NSImage(systemSymbolName: "pause.circle", accessibilityDescription: nil)
        let sub = NSMenu()
        for (title, minutes) in [("For 30 Minutes", 30), ("For 1 Hour", 60), ("For 1 Day", 24 * 60), ("Until I Resume", 0)] {
            let i = action(title, #selector(pauseRecording(_:)))
            i.tag = minutes
            sub.addItem(i)
        }
        item.submenu = sub
        return item
    }

    @objc private func pauseRecording(_ sender: NSMenuItem) {
        let until = sender.tag == 0 ? Date.distantFuture : Date().addingTimeInterval(Double(sender.tag) * 60)
        pausedUntil = until
        schedulePause(until)
        log("recording paused (\(sender.title.lowercased()))")
    }

    @objc private func resumeRecording() {
        pausedUntil = nil
        resumeTimer?.invalidate()
        resumeTimer = nil
        applyRecordingPrefs()
        log("recording resumed")
    }

    /// Stop both recorders now and turn them back on at `until`.
    private func schedulePause(_ until: Date) {
        engine.setScreen(false)
        engine.setAudio(false)
        resumeTimer?.invalidate()
        resumeTimer = nil
        if until != .distantFuture {
            let t = Timer(fire: until, interval: 0, repeats: false) { [weak self] _ in self?.resumeRecording() }
            RunLoop.main.add(t, forMode: .common)
            resumeTimer = t
        }
        updateIcon()
    }

    /// Turn screen and audio on or off to match the switches.
    private func applyRecordingPrefs() {
        engine.setScreen(defaults.bool(forKey: screenKey))
        engine.setAudio(defaults.bool(forKey: audioKey))
        updateIcon()
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in self?.updateIcon() }
    }

    private func pauseText(_ until: Date) -> String {
        if until == .distantFuture { return "paused" }
        let f = DateFormatter()
        f.dateStyle = Calendar.current.isDateInToday(until) ? .none : .short
        f.timeStyle = .short
        return "paused until \(f.string(from: until))"
    }

    // MARK: settings

    private func keep(_ key: String) -> KeepFor { KeepFor(rawValue: defaults.string(forKey: key) ?? "") ?? .forever }

    @objc private func toggleCompress() {
        let on = !defaults.bool(forKey: compressKey)
        defaults.set(on, forKey: compressKey)
        video.enabled = on
        log("compress older recordings \(on ? "on" : "off")")
        if on { video.kick() }
    }

    // MARK: transcription model

    private static let modelsHelp = URL(string: "https://github.com/frickgav/openscreen/blob/main/README.md#choosing-a-transcription-model")!

    /// "Using NVIDIA Parakeet v3", "Last clip 14:02: 30 s of audio in 3.1 s", "4 clips waiting".
    private func transcriptionStatus() -> [String] {
        guard let tx = engine.transcriber else {
            return ["Not transcribing: \(engine.transcriberProblem ?? "audio recording is off")"]
        }
        var lines = ["Using \(tx.displayName)"]
        let st = tx.status
        if let p = st.problem {
            lines.append("Last try failed: " + (p.count > 70 ? String(p.prefix(70)) + "…" : p))
        } else if let at = st.lastAt {
            let f = DateFormatter()
            f.timeStyle = .short
            lines.append("Last clip \(f.string(from: at)): \(Int(st.lastAudioSeconds.rounded())) s of audio in \(String(format: "%.1f", st.lastSeconds)) s")
        } else {
            lines.append("No clips transcribed since it started")
        }
        // While the database is being converted every query waits for it; don't hang the menu.
        let pending = engine.db.isConverting ? 0 : engine.db.audioStats().pending
        if pending > 0 { lines.append(pending == 1 ? "1 clip waiting" : "\(pending) clips waiting") }
        return lines
    }

    /// The "Transcription off: download …" line in the menu.
    @objc private func pickModel(_ sender: NSMenuItem) {
        if let id = sender.representedObject as? String { pickModel(id: id) }
    }

    /// Switch models; one that is not on this Mac yet is downloaded first.
    private func pickModel(id: String) {
        let m = SpeechModels.byId(id)
        if let warn = SpeechModels.languageWarning(m, language: engine.config.whisperLanguage),
           !confirm(m.name, warn + "\n\nUse it anyway?", button: "Use It", destructive: false) { return }
        if !m.files.isEmpty, !SpeechModels.isDownloaded(m, home: engine.config.home) {
            ModelDownload.show(m, home: engine.config.home) { [weak self] in self?.useModel(m) }
            return
        }
        useModel(m)
    }

    private func useModel(_ m: SpeechModel) {
        let changed = SpeechModels.picked != m.id
        SpeechModels.picked = m.id
        // Same model and it runs: nothing to do. Otherwise start it (again), which also retries a failed load.
        if !changed, let tx = engine.transcriber, tx.status.problem == nil { return }
        engine.restartTranscriber(model: m.id)
        log("transcription model set to \(m.name)")
        if engine.transcriber == nil, m.engine == .whisper {
            NSApp.activate(ignoringOtherApps: true)
            let a = NSAlert()
            a.messageText = "\(m.name) needs whisper-cli"
            a.informativeText = "Install it with Homebrew: brew install whisper-cpp. Then pick the model again.\n\nNVIDIA Parakeet v3 needs nothing else."
            Dialogs.run(a)
        }
    }

    /// Queue recorded audio again for the model picked now, after saying how long it may take.
    private func retranscribe(hours: Int) {
        guard let tx = engine.transcriber else { return }
        let what = [1: "the last hour", 24: "the last day", 24 * 7: "the last week"][hours] ?? "that time"
        let from = Date().addingTimeInterval(-Double(hours) * 3600)
        let (clips, seconds) = engine.db.retranscribeCount(from: from, clipSeconds: engine.config.audioChunkSeconds)
        guard clips > 0 else {
            NSApp.activate(ignoringOtherApps: true)
            let a = NSAlert()
            a.messageText = "There is no transcribed audio from \(what)."
            Dialogs.run(a)
            return
        }
        let st = tx.status
        let busy = st.lastAt != nil && st.lastAudioSeconds > 0
            ? "for \(SpeechModels.about(seconds * st.lastSeconds / st.lastAudioSeconds))"
            : "for a while"
        let length = SpeechModels.about(seconds).replacingOccurrences(of: "about ", with: "")
        guard confirm("Re-transcribe \(what)?",
                      "\(clips) clips (up to \(length) of audio) will be transcribed again with \(tx.displayName). This keeps the processor busy \(busy) and uses more battery. New recordings still go first.\n\n"
                        + "The new text replaces the old for good: the old text stays searchable only until each clip is redone. "
                        + "Clips with no speech are left as they are, and meeting notes already made are kept (not made or sent again)."
                        + (SpeechModels.languageWarning(tx.model, language: engine.config.whisperLanguage).map { "\n\n" + $0 } ?? ""),
                      button: "Re-transcribe", destructive: false) else { return }
        let db = engine.db, name = tx.displayName
        DispatchQueue.global(qos: .utility).async {
            log("re-transcribing \(db.requeueAudio(from: from)) audio clips from \(what) with \(name)")
        }
    }

    @objc private func openModelsHelp() { NSWorkspace.shared.open(Self.modelsHelp) }

    @objc private func openWhisperHelp() {
        NSWorkspace.shared.open(URL(string: "https://github.com/frickgav/openscreen/blob/main/README.md#whisper")!)
    }

    // MARK: shortcut

    @discardableResult
    private func registerHotKey(_ sc: Shortcut) -> Bool {
        hotKey = nil  // unregister the old one first
        hotKey = HotKey(keyCode: sc.keyCode, modifiers: sc.modifiers) { [weak self] in self?.toggleTimeline() }
        return hotKey?.registered == true
    }

    /// Small window that waits for the next key combination and saves it.
    @objc private func changeShortcut() {
        let old = Shortcut.load(defaults)
        hotKey = nil  // so pressing the old shortcut now does not open the timeline
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = "Press the new shortcut"
        a.informativeText = "Use ⌘, ⌥ or ⌃ with a key, for example ⌥⌘T. F-keys work alone.\nNow: \(old.label)"
        a.addButton(withTitle: "Cancel")
        var picked: Shortcut?
        let monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
            if e.keyCode == UInt16(kVK_Escape) { NSApp.abortModal(); return nil }
            guard let sc = Shortcut(event: e) else { NSSound.beep(); return nil }
            picked = sc
            NSApp.abortModal()
            return nil
        }
        Dialogs.run(a)
        if let monitor { NSEvent.removeMonitor(monitor) }

        guard let sc = picked, sc != old else { registerHotKey(old); return }
        if registerHotKey(sc) {
            sc.save(defaults)
            log("timeline shortcut set to \(sc.label)")
        } else {
            registerHotKey(old)
            let err = NSAlert()
            err.messageText = "\(sc.label) is already in use"
            err.informativeText = "Another app or macOS uses this shortcut. The shortcut stays \(old.label)."
            Dialogs.run(err)
        }
    }

    @objc private func resetShortcut() {
        Shortcut.standard.save(defaults)
        if !registerHotKey(.standard) { log("cannot register \(Shortcut.standard.label); another app may use it") }
    }

    // MARK: screenshots

    private func setTypingGap(_ secs: Int) {
        defaults.set(secs, forKey: typingGapKey)
        engine.recorder.minorChangeGap = TimeInterval(secs)
    }

    private func setResolution(_ scale: Double) {
        defaults.set(scale, forKey: Recorder.scaleKey)
        log("screenshot resolution set to \(Int(scale * 100))%")
    }

    private func setLook(_ look: OverlayLook) {
        defaults.set(look.rawValue, forKey: OverlayLook.key)
        log("timeline look set to \(look.rawValue)")
    }

    // MARK: encryption & lock

    @objc private func toggleEncryption() {
        if !encryption.on { encryption.turnOn() }
        else { appLock.run("stop encrypting recordings") { [weak self] in self?.encryption.turnOff() } }
    }

    @objc private func skipFailedFiles() { encryption.skipFailed() }

    @objc private func exportRecoveryKey() {
        appLock.run("save the recovery key") { [weak self] in _ = self?.encryption.exportKey() }
    }

    @objc private func toggleAppLock() {
        if appLock.enabled {
            // Turning it off from the menu asks first, so someone at the unlocked Mac can't just
            // switch it off. (A program running as you could still use `defaults write`; see AppLock.)
            appLock.unlock("stop asking for Touch ID or your password") { [weak self] ok in
                guard ok, let self else { return }
                self.appLock.enabled = false
                log("app lock off")
            }
            return
        }
        if let why = AppLock.unavailableReason() {
            NSApp.activate(ignoringOtherApps: true)
            let a = NSAlert()
            a.messageText = "Touch ID or a password is needed"
            a.informativeText = "\(why)\n\nSet a login password for your Mac account in System Settings → Users & Groups, then try again."
            Dialogs.run(a)
            return
        }
        appLock.enabled = true
        // Check it works now, so nobody is locked out by a prompt that never answers.
        appLock.unlock("check that unlocking works for OpenRewind") { [weak self] ok in
            guard let self else { return }
            if !ok { self.appLock.enabled = false; return }
            log("app lock on")
        }
    }

    // MARK: permissions

    @objc private func openPrivacySettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy")!)
    }

    // MARK: keep & delete

    /// Deleting is behind App Lock too: someone at the Mac could otherwise erase the history.
    private func setKeep(_ k: KeepFor, screen: Bool) {
        appLock.run("change how long recordings are kept") { [weak self] in
            guard let self else { return }
            let key = screen ? self.keepScreenKey : self.keepAudioKey
            let what = screen ? "screenshots" : "audio clips"
            if let secs = k.seconds, (self.keep(key).seconds ?? .infinity) > secs {
                guard self.confirm("Keep \(what) for \(k.title.lowercased())?",
                                   "OpenRewind will delete \(what) older than \(k.title.lowercased()) now, and keep doing it every hour. This cannot be undone.",
                                   button: "Delete Older \(what.capitalized)") else { return }
            }
            self.defaults.set(k.rawValue, forKey: key)
            if screen { self.retention.screenKeep = k } else { self.retention.audioKeep = k }
            self.retention.runNow()
        }
    }

    /// The last 15 minutes, the last hour or today.
    private func deleteRecent(_ what: String, from: Date) {
        appLock.run("delete recordings") { [weak self] in
            guard let self else { return }
            guard self.confirm("Delete \(what) of recordings?",
                               "Screenshots and audio from \(what) will be deleted. This cannot be undone.",
                               button: "Delete") else { return }
            let db = self.engine.db, now = Date()
            DispatchQueue.global(qos: .userInitiated).async {
                let f = db.deleteFrames(from: from, to: now), a = db.deleteAudio(from: from, to: now)
                log("deleted \(f) screenshots, \(a) audio clips (\(what))")
            }
        }
    }

    private func deleteAll(screen: Bool, audio: Bool) {
        appLock.run("delete recordings") { [weak self] in
            guard let self else { return }
            let what = screen && audio ? "all screenshots and audio" : (screen ? "all screenshots" : "all audio recordings")
            guard self.confirm("Delete \(what)?", "This removes \(what) from this Mac, and every journal recap. This cannot be undone.",
                               button: "Delete Everything") else { return }
            let db = self.engine.db, now = Date()
            DispatchQueue.global(qos: .userInitiated).async {
                let f = screen ? db.deleteFrames(from: nil, to: now) : 0
                let a = audio ? db.deleteAudio(from: nil, to: now) : 0
                db.deleteRecaps(from: nil, to: nil)  // each one was written from both
                db.compact()
                log("deleted \(f) screenshots, \(a) audio clips (all)")
            }
        }
    }

    private func confirm(_ title: String, _ text: String, button: String, destructive: Bool = true) -> Bool {
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.alertStyle = .warning
        a.addButton(withTitle: button)
        a.addButton(withTitle: "Cancel")
        a.buttons.first?.hasDestructiveAction = destructive
        return Dialogs.run(a) == .alertFirstButtonReturn
    }

    // MARK: updates

    /// A row under the status line while a newer version is out.
    private func updateItem() -> NSMenuItem? {
        let u = Updater.shared
        guard let r = u.available else { return nil }
        switch u.state {
        case .downloading(let p):
            let i = NSMenuItem(title: "Downloading OpenRewind \(r.version)… \(Int(p * 100))%", action: nil, keyEquivalent: "")
            i.isEnabled = false
            return i
        case .installing:
            let i = NSMenuItem(title: "Installing OpenRewind \(r.version)…", action: nil, keyEquivalent: "")
            i.isEnabled = false
            return i
        case .ready: return updateRow("Restart to Update to OpenRewind \(r.version)")
        case .idle, .failed: return updateRow(u.canInstall ? "Update to OpenRewind \(r.version)…" : "Download OpenRewind \(r.version)…")
        }
    }

    private func updateRow(_ title: String) -> NSMenuItem {
        let i = action(title, #selector(updateNow))
        i.image = NSImage(systemSymbolName: "arrow.down.circle", accessibilityDescription: nil)
        return i
    }

    @objc private func updateNow() { Updater.shared.update() }

    @objc private func toggleAutoDownload() { Updater.shared.autoDownload.toggle() }

    @objc private func checkForUpdates() {
        let u = Updater.shared
        u.check { error in
            NSApp.activate(ignoringOtherApps: true)
            let a = NSAlert()
            if let error {
                a.messageText = "Could not check for updates"
                a.informativeText = error
            } else if let r = u.available {
                a.messageText = "OpenRewind \(r.version) is out"
                a.informativeText = "You have \(u.current)."
                a.addButton(withTitle: u.canInstall ? "Update" : "Download")
                a.addButton(withTitle: "Later")
                if Dialogs.run(a) == .alertFirstButtonReturn { u.update() }
                return
            } else {
                a.messageText = "OpenRewind is up to date"
                a.informativeText = "You have the latest version, \(u.current)."
            }
            Dialogs.run(a)
        }
    }

    // MARK: storage

    private func storageLines() -> [String] {
        let size = dataSize.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) } ?? "measuring…"
        // Compressed recordings: how much less the videos take than the screenshots they replaced.
        // While the database is being converted every query waits for it; don't hang the menu.
        let converting = engine.db.isConverting
        let v = converting ? (segments: 0, bytes: Int64(0), saved: Int64(0)) : engine.db.videoStats()
        let saved = v.segments > 0 ? " · video saves " + ByteCountFormatter.string(fromByteCount: v.saved, countStyle: .file) : ""
        var lines = ["Using \(size)" + saved]
        if converting {
            lines.append("Encrypting or decrypting the database…")
        } else if let oldest = engine.db.oldestRecording() {
            let f = DateFormatter()
            f.dateStyle = .medium; f.timeStyle = .short
            let rel = RelativeDateTimeFormatter()
            rel.unitsStyle = .full
            lines.append("Goes back to \(f.string(from: oldest)) (\(rel.localizedString(for: oldest, relativeTo: Date())))")
        } else {
            lines.append("No recordings yet")
        }
        // Deleted frames that are still inside a video until it is written again (or turned back into pictures).
        let stuck = converting ? 0 : engine.db.stuckRewrites()
        if stuck > 0 { lines.append("\(stuck) video\(stuck == 1 ? "" : "s") still hold\(stuck == 1 ? "s" : "") deleted frames · being redone") }
        return lines
    }

    /// Measure the folder off the main thread, then refresh the open menu with the result.
    private func measureStorage(_ menu: NSMenu) {
        measureStorage { [weak self] in
            guard let self, let i = menu.items.firstIndex(where: { $0.title.hasPrefix("  Using ") }) else { return }
            menu.items[i].title = "  " + self.storageLines()[0]
        }
    }

    /// `changed` runs on the main thread when the size is new.
    private func measureStorage(changed: @escaping () -> Void) {
        guard !measuring else { return }
        measuring = true
        let home = engine.config.home
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let size = folderSize(home)
            DispatchQueue.main.async {
                guard let self else { return }
                self.measuring = false
                let isNew = self.dataSize != size
                self.dataSize = size
                if isNew { changed() }
            }
        }
    }

    private func statusLine() -> String {
        if engine.db.isConverting {
            return (encryption?.on == true ? "Encrypting" : "Decrypting") + " the database… (recording waits until it is done)"
        }
        let s = engine.db.stats(), a = engine.db.audioStats()
        var parts: [String] = []
        if let until = pausedUntil { let t = pauseText(until); return t.prefix(1).uppercased() + t.dropFirst() + "  —  \(s.count) frames, \(a.segments) audio" }
        parts.append(engine.screenOn ? "Screen on" : "Screen off")
        parts.append(engine.audioOn ? "Audio on" : "Audio off")
        return parts.joined(separator: " · ") + "  —  \(s.count) frames, \(a.segments) audio"
    }

    private func toggleItem(_ title: String, on: Bool, action: Selector, symbol: String) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: action, keyEquivalent: "")
        i.target = self
        i.state = on ? .on : .off
        i.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
        return i
    }

    private func action(_ title: String, _ sel: Selector, key: String = "") -> NSMenuItem {
        let i = NSMenuItem(title: title, action: sel, keyEquivalent: key)
        i.target = self
        return i
    }

    private func updateIcon() {
        let name: String
        switch (engine.screenOn, engine.audioOn) {
        case (true, true): name = "record.circle.fill"
        case (true, false): name = "record.circle"
        case (false, true): name = "waveform.circle"
        case (false, false): name = "pause.circle"
        }
        let img = NSImage(systemSymbolName: name, accessibilityDescription: "openrewind")
        img?.isTemplate = true
        item.button?.image = img
        item.button?.toolTip = "OpenRewind — \(statusLine())"
    }

    @objc private func toggleScreen() {
        let on = !(isPaused ? defaults.bool(forKey: screenKey) : engine.screenOn)
        defaults.set(on, forKey: screenKey)
        if !isPaused { engine.setScreen(on) }
        updateIcon()
    }

    @objc private func toggleAudio() {
        let on = !(isPaused ? defaults.bool(forKey: audioKey) : engine.audioOn)
        defaults.set(on, forKey: audioKey)
        if !isPaused { engine.setAudio(on) }
        // Mic start can finish a moment later (permission prompt), so refresh again shortly.
        updateIcon()
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in self?.updateIcon() }
    }

    @objc private func openScreenSettings() {
        PermissionHelper.shared.show(.screen)
    }

    @objc private func openTimeline() {
        appLock.run("see your timeline") { [weak self] in self?.overlay.show() }
    }

    /// The browser page when the local server is on, else the timeline's own search box.
    @objc private func openSearch() {
        appLock.run("search your history") { [weak self] in
            guard let self else { return }
            if self.engine.serverOn {
                NSWorkspace.shared.open(URL(string: "http://127.0.0.1:\(self.engine.config.port)/")!)
            } else {
                self.overlay.showSearch()
            }
        }
    }

    @objc private func toggleServer() {
        if engine.serverOn { engine.stopServer() } else { startServer() }
        defaults.set(engine.serverOn, forKey: serverKey)
    }

    private func startServer() {
        do { try engine.startServer(exitOnFailure: false) } catch { log("cannot start server: \(error)") }
    }

    @objc private func openData() {
        appLock.run("open its data folder") { [weak self] in
            guard let self else { return }
            NSWorkspace.shared.open(self.engine.config.home)
        }
    }

    // MARK: search by meaning

    /// Load Apple's sentence model (it comes with macOS) and start indexing in the background.
    private func startMeaning() {
        if engine.meaning.embedder == nil {
            engine.meaning.embedder = AppleEmbedder.make()
            if let e = engine.meaning.embedder { log("search by meaning: using \(e.modelId)") }
            else { log("search by meaning: macOS has no sentence model for this language") }
        }
        engine.meaning.start()
    }

    // The index counts run several COUNT(*) queries, so they are worked out in the background and
    // Settings shows the last answer, and picks up a new one on its next refresh.
    private var meaningCache: String?
    private var meaningCacheAt = Date.distantPast
    private var meaningRefreshing = false

    /// The line under the switch in Settings. Never touches the database on the main thread.
    private func meaningStatus() -> String {
        guard engine.meaning.embedder != nil else { return "⚠︎ Not available: macOS has no sentence model for your language" }
        if meaningCache == nil || Date().timeIntervalSince(meaningCacheAt) > 5 { refreshMeaningStatus() }
        return meaningCache ?? "Checking the index…"
    }

    private func refreshMeaningStatus() {
        guard !meaningRefreshing else { return }
        meaningRefreshing = true
        let service = engine.meaning
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let p = service.progress()
            let n = NumberFormatter.localizedString(from: NSNumber(value: p.moments), number: .decimal)
            let text = service.paused ? "Paused: could not save, will retry (see openrewind.log) · \(n) moments"
                : p.upToDate ? "Up to date · \(n) moments" : "Indexing in the background… \(p.percent)% · \(n) moments"
            DispatchQueue.main.async {
                guard let self else { return }
                self.meaningRefreshing = false
                guard self.defaults.bool(forKey: self.meaningKey) else { return }
                self.meaningCache = text
                self.meaningCacheAt = Date()
            }
        }
    }

    @objc private func toggleMeaning() {
        if !defaults.bool(forKey: meaningKey) {
            NSApp.activate(ignoringOtherApps: true)
            let a = NSAlert()
            a.messageText = "Turn on Search by Meaning?"
            a.informativeText = "Search by Meaning finds moments by what they were about, even when you don't remember the exact words. "
                + "For example, “the flight I was booking” finds the airline page.\n\n"
                + "OpenRewind will read your screen text and transcripts in the background to build an index on this Mac, "
                + "using the language model that comes with macOS. Nothing is downloaded and nothing leaves this Mac. "
                + "It works slowly while you use the Mac and on battery, and faster when you are away. "
                + "Blocked apps and sites are never indexed."
            a.addButton(withTitle: "Turn On")
            a.addButton(withTitle: "Cancel")
            guard Dialogs.run(a) == .alertFirstButtonReturn else { return }
            defaults.set(true, forKey: meaningKey)
            startMeaning()
            log("search by meaning on")
        } else {
            guard confirm("Turn off Search by Meaning?",
                          "The meaning index is deleted. Keyword search keeps working. Turning it back on builds the index again.",
                          button: "Turn Off") else { return }
            defaults.set(false, forKey: meaningKey)
            engine.meaning.stop()
            engine.meaning.embedder = nil
            engine.db.clearMeaning()
            meaningCache = nil
            overlay.model.setByMeaning(false)
            log("search by meaning off")
        }
    }

    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
            else { try SMAppService.mainApp.register() }
        } catch {
            log("launch at login failed: \(error.localizedDescription)")
        }
    }

    @objc private func quit() {
        appLock.forget()  // AI apps (MCP) get nothing once the app is gone
        encryption.stop()
        video?.stop()
        NSApp.terminate(nil)
    }

    /// Quit from the menu, logout or shutdown: delete the decrypted copies of open videos.
    func applicationWillTerminate(_ notification: Notification) {
        FrameImages.shared.releaseAll()
    }
}

// MARK: - Settings window

/// What the Settings window reads and changes. Each change goes through the same code the menu used,
/// so App Lock prompts, confirmations and logging stay the same.
extension MenuBarApp {
    // General
    var launchAtLogin: Bool { SMAppService.mainApp.status == .enabled }
    func toggleLaunchAtLogin() { toggleLogin() }
    func checkUpdatesNow() { checkForUpdates() }
    func openTutorial() { showTutorial() }
    func openSetup() { showSetup() }
    func openPrivacyPane() { openPrivacySettings() }
    var look: OverlayLook {
        get { OverlayLook.current }
        set { setLook(newValue) }
    }

    // Screen
    /// While paused, the switches show and change what comes back on after the pause.
    var recordsScreen: Bool { isPaused ? defaults.bool(forKey: screenKey) : engine.screenOn }
    func toggleRecordScreen() { toggleScreen() }
    var screenshotScale: Double {
        get { Double(Recorder.snapshotScale(default: engine.config.snapshotScale)) }
        set { setResolution(newValue) }
    }
    var typingGap: Int {
        get { defaults.integer(forKey: typingGapKey) }
        set { setTypingGap(newValue) }
    }
    var meaningOn: Bool { defaults.bool(forKey: meaningKey) }
    func toggleSearchByMeaning() { toggleMeaning() }
    var meaningLine: String { meaningStatus() }

    // Audio
    var recordsAudio: Bool { isPaused ? defaults.bool(forKey: audioKey) : engine.audioOn }
    func toggleRecordAudio() { toggleAudio() }
    var transcriberRunning: Bool { engine.transcriber != nil }
    var transcriptionLines: [String] { transcriptionStatus() }
    func useTranscriptionModel(_ id: String) { pickModel(id: id) }
    func retranscribeRecent(hours: Int) { retranscribe(hours: hours) }
    func openTranscriptionHelp() { openModelsHelp() }
    func openWhisperSetup() { openWhisperHelp() }

    // Meetings
    func toggleSummaries() { toggleMeetingSummaries() }

    // AI
    func savedAISettings() { aiSettingsSaved() }
    var autoRecapOn: Bool { JournalSettings.isOn(for: AISettings.load()) }
    func toggleDailyRecap() { toggleAutoRecap() }
    var mcpOn: Bool { MCPSetup.enabled }
    func toggleAIApps() { toggleMCP() }
    func connectAIApps() { showConnectAIApps() }

    // Privacy & security
    var serverOn: Bool { engine.serverOn }
    var serverPort: Int { Int(engine.config.port) }
    func toggleLocalServer() { toggleServer() }
    var encryptionOn: Bool { encryption.on }
    var encryptionStatus: String { encryption.status() }
    var encryptionFailed: Int { encryption.failed }
    var hasRecoveryKey: Bool { encryption.hasKey }
    func toggleEncrypt() { toggleEncryption() }
    func stopRetryingFiles() { skipFailedFiles() }
    func saveRecoveryKey() { exportRecoveryKey() }
    var appLockOn: Bool { appLock.enabled }
    func toggleLock() { toggleAppLock() }
    var lockMinutes: Int {
        get { appLock.minutes }
        set { appLock.minutes = newValue }
    }

    // Storage
    var storageSummary: [String] { storageLines() }
    func measureDataFolder(changed: @escaping () -> Void) { measureStorage(changed: changed) }
    func showDataFolder() { openData() }
    func keepFor(screen: Bool) -> KeepFor { keep(screen ? keepScreenKey : keepAudioKey) }
    func setKeepFor(_ k: KeepFor, screen: Bool) { setKeep(k, screen: screen) }
    var compressOn: Bool { defaults.bool(forKey: compressKey) }
    var compressUnavailable: String? { video.unavailable }
    func toggleCompression() { toggleCompress() }
    func deleteRecordings(_ what: String, from: Date) { deleteRecent(what, from: from) }
    func deleteAllRecordings(screen: Bool, audio: Bool) { deleteAll(screen: screen, audio: audio) }

    // Shortcuts
    var timelineShortcut: Shortcut { Shortcut.load(defaults) }
    func recordTimelineShortcut() { changeShortcut() }
    func restoreTimelineShortcut() { resetShortcut() }
}

/// The macOS privacy permissions OpenRewind uses.
enum Permission: String, CaseIterable {
    case screen, microphone, accessibility

    var title: String {
        switch self {
        case .screen: return "Screen Recording"
        case .microphone: return "Microphone"
        case .accessibility: return "Accessibility"
        }
    }

    var why: String {
        switch self {
        case .screen: return "Takes screenshots and records system audio"
        case .microphone: return "Records your voice"
        case .accessibility: return "Reads text and browser tabs from windows"
        }
    }

    var granted: Bool {
        switch self {
        case .screen: return Capture.hasScreenPermission
        case .microphone: return AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
        case .accessibility: return Accessibility.isTrusted
        }
    }

    /// Ask macOS to show its prompt, so OpenRewind is in the list in System Settings.
    func request() {
        switch self {
        case .screen: Capture.requestScreenPermission()
        case .microphone: AVCaptureDevice.requestAccess(for: .audio) { _ in }
        case .accessibility: Accessibility.promptForTrust()
        }
    }

    var settingsURL: URL {
        let pane: String
        switch self {
        case .screen: pane = "Privacy_ScreenCapture"
        case .microphone: pane = "Privacy_Microphone"
        case .accessibility: pane = "Privacy_Accessibility"
        }
        return URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)")!
    }
}
