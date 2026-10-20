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
    private var retention: RetentionJob!
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
    /// The AI chat keeps its conversation while its window is closed.
    private lazy var aiChat: AIChatModel = {
        let m = AIChatModel(db: engine.db)
        m.openMoment = { [weak self] d in self?.overlay.show(at: d) }
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
        m.openMoment = { [weak self] d in self?.overlay.show(at: d) }
        m.openSettings = { [weak self] in self?.showAISettings() }
        return m
    }()
    /// Checks every few minutes whether yesterday's recap is due (Write Each Day's Recap Automatically).
    private var recapTimer: Timer?
    private var writingRecap = false

    /// Finds meetings once a minute; the Meetings window and the AI notes read what it stores.
    private lazy var meetings = MeetingCenter(db: engine.db, chunkSeconds: engine.config.audioChunkSeconds,
                                              transcribing: { [weak self] in self?.engine.transcriber != nil })

    init(engine: Engine) {
        self.engine = engine
        super.init()
        copyOldSettings()
        clearOldPermissions()
        defaults.register(defaults: [screenKey: true, audioKey: true, serverKey: false,
                                     keepScreenKey: KeepFor.forever.rawValue, keepAudioKey: KeepFor.forever.rawValue,
                                     typingGapKey: 2])
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

    @objc private func showJournal() {
        let j = journal
        j.reload()
        SetupWindows.shared.show("journal", title: "Journal", size: NSSize(width: 620, height: 800), resizable: true) {
            JournalView(model: j)
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
        guard !j.writing.contains(key) else { return }
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
        Onboarding.show(db: engine.db) { [weak self] tour in if tour { self?.overlay.showTutorial() } }
    }

    @objc private func showTutorial() { overlay.showTutorial() }

    @objc private func showBlockedApps() {
        SetupWindows.shared.show("blocked", title: "Blocked Apps & Sites", size: NSSize(width: 600, height: 860), resizable: true) {
            BlockedAppsView(db: engine.db)
        }
    }

    @objc private func showAIChat() {
        let chat = aiChat
        chat.reloadSettings()
        SetupWindows.shared.show("ai", title: "Ask OpenRewind", size: NSSize(width: 560, height: 720), resizable: true) {
            AIChatView(model: chat)
        }
    }

    @objc private func showMeetings() {
        let model = MeetingsModel(db: engine.db)
        model.openMoment = { [weak self] d in self?.overlay.show(at: d) }
        model.openSettings = { [weak self] in self?.showAISettings() }
        let center = meetings
        SetupWindows.shared.show("meetings", title: "Meetings", size: NSSize(width: 1000, height: 700), resizable: true) {
            MeetingsView(model: model, center: center)
        }
    }

    @objc private func toggleAskBeforeMeetingNotes() {
        MeetingCenter.askFirst.toggle()
    }

    @objc private func forgetMeetingChoices() {
        MeetingCenter.choices = [:]
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
            if a.runModal() == .alertFirstButtonReturn { showAISettings() }
            return
        }
        let a = NSAlert()
        a.messageText = "Summarize meetings with AI?"
        a.informativeText = Self.meetingConsentText(ai.kind.title)
        a.addButton(withTitle: "Turn On")
        a.addButton(withTitle: "Cancel")
        guard a.runModal() == .alertFirstButtonReturn else { return }
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
        if a.runModal() == .alertSecondButtonReturn {
            MeetingCenter.consent = MeetingNotes.consentKey(ai)
            log("meeting summaries moved to the new provider")
        } else {
            MeetingCenter.autoSummarize = false
            log("meeting summaries off (provider changed)")
        }
    }

    /// Where the time went; a click shows that app or moment in the timeline.
    @objc private func showInsights() {
        InsightsWindow.shared.show(db: engine.db) { [weak self] date, app in self?.overlay.show(at: date, app: app) }
    }

    @objc private func showAISettings() {
        SetupWindows.shared.show("aiSettings", title: "AI Settings", size: NSSize(width: 560, height: 840)) {
            AISettingsView { [weak self] in
                self?.aiChat.reloadSettings()
                self?.journal.reload()
                self?.recheckMeetingConsent()
            }
        }
    }

    /// Turning it on shows how to add OpenRewind to each AI app.
    @objc private func toggleMCP() {
        MCPSetup.enabled.toggle()
        log("AI apps (MCP) \(MCPSetup.enabled ? "allowed" : "not allowed")")
        if MCPSetup.enabled { showConnectAIApps() }
    }

    @objc private func showConnectAIApps() {
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
            let warn = NSMenuItem(title: "  Transcription off: install whisper-cpp", action: nil, keyEquivalent: "")
            warn.isEnabled = false
            menu.addItem(warn)
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
        let settings = NSMenuItem(title: missing > 0 ? "Settings  ⚠︎" : "Settings", action: nil, keyEquivalent: "")
        settings.image = NSImage(systemSymbolName: "gearshape", accessibilityDescription: nil)
        settings.submenu = settingsMenu()
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

    private func settingsMenu() -> NSMenu {
        let m = NSMenu()
        m.addItem(action("Blocked Apps & Sites…", #selector(showBlockedApps)))
        m.addItem(action("AI Settings…", #selector(showAISettings)))
        let autoRecap = action("Write Each Day's Recap Automatically", #selector(toggleAutoRecap))
        let ai = AISettings.load()
        autoRecap.state = JournalSettings.isOn(for: ai) ? .on : .off
        autoRecap.toolTip = Journal.autoRecapInfo(ai.kind.title)
        m.addItem(autoRecap)
        let ask = action("Ask Before Taking Meeting Notes", #selector(toggleAskBeforeMeetingNotes))
        ask.state = MeetingCenter.askFirst ? .on : .off
        ask.toolTip = "When a call starts, ask whether to take notes for it. Off: notes are taken for every call (except apps you said Never for)."
        m.addItem(ask)
        let summaries = action("Summarize Meetings with AI", #selector(toggleMeetingSummaries))
        summaries.state = MeetingCenter.autoSummarize ? .on : .off
        summaries.toolTip = "Off by default. When on, the transcript of each call you take notes for is sent to your AI provider after it ends, for a summary and action items. This uses your key and costs tokens."
        m.addItem(summaries)
        let remembered = MeetingCenter.choices
        if !remembered.isEmpty {
            let forget = action("Forget Remembered Meeting Choices", #selector(forgetMeetingChoices))
            forget.toolTip = "Ask again for every app. Remembered now: "
                + remembered.sorted { $0.key < $1.key }.map { "\($0.key): \($0.value == MeetingPrompt.always ? "always" : "never")" }
                    .joined(separator: ", ") + "."
            m.addItem(forget)
        }
        m.addItem(.separator())
        let sc = Shortcut.load(defaults)
        let shortcut = NSMenuItem(title: "Timeline Shortcut: \(sc.label)", action: nil, keyEquivalent: "")
        shortcut.image = NSImage(systemSymbolName: "keyboard", accessibilityDescription: nil)
        let sm = NSMenu()
        sm.addItem(action("Change Shortcut…", #selector(changeShortcut)))
        let reset = action("Reset to \(Shortcut.standard.label)", #selector(resetShortcut))
        reset.isEnabled = sc != .standard
        sm.addItem(reset)
        shortcut.submenu = sm
        m.addItem(shortcut)
        m.addItem(resolutionMenu())
        m.addItem(lookMenu())
        m.addItem(.separator())
        m.addItem(keepMenu("Keep Screen Recordings For", key: keepScreenKey))
        m.addItem(keepMenu("Keep Audio Recordings For", key: keepAudioKey))
        m.addItem(typingGapMenu())
        m.addItem(.separator())

        let del = NSMenuItem(title: "Delete Recordings", action: nil, keyEquivalent: "")
        let dm = NSMenu()
        for (title, tag) in [("Last 15 Minutes", 1), ("Last Hour", 2), ("Today", 3)] {
            let i = action(title, #selector(deleteRecent(_:))); i.tag = tag; dm.addItem(i)
        }
        dm.addItem(.separator())
        for (title, tag) in [("All Screen Recordings…", 1), ("All Audio Recordings…", 2), ("Everything…", 3)] {
            let i = action(title, #selector(deleteAll(_:))); i.tag = tag; dm.addItem(i)
        }
        del.submenu = dm
        m.addItem(del)
        m.addItem(.separator())

        let server = action("Local Search Server (127.0.0.1:\(engine.config.port))", #selector(toggleServer))
        server.state = engine.serverOn ? .on : .off
        server.toolTip = "Search in the browser and let local scripts read your history through the API. Only this Mac can connect."
        m.addItem(server)
        let mcp = action("Allow AI Apps to Read My History (MCP)", #selector(toggleMCP))
        mcp.state = MCPSetup.enabled ? .on : .off
        mcp.toolTip = "Let Claude, Cursor and other AI apps search your history on this Mac. Blocked apps and sites are left out of screen text; audio transcripts are not filtered by app. See Connect AI Apps for details."
        m.addItem(mcp)
        m.addItem(action("Connect AI Apps…", #selector(showConnectAIApps)))
        m.addItem(.separator())

        let auto = action("Download Updates Automatically", #selector(toggleAutoDownload))
        auto.state = Updater.shared.autoDownload ? .on : .off
        auto.toolTip = "Download new versions in the background. They are installed only when you click Update."
        m.addItem(auto)
        m.addItem(action("Check for Updates… (version \(Updater.shared.current))", #selector(checkForUpdates)))
        m.addItem(.separator())

        m.addItem(permissionsMenu())
        m.addItem(.separator())

        let login = action("Launch at Login", #selector(toggleLogin))
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        m.addItem(login)
        m.addItem(.separator())
        m.addItem(action("Show Tutorial", #selector(showTutorial)))
        m.addItem(action("Run Setup Again…", #selector(showSetup)))
        return m
    }

    /// Fewer saved frames while typing means less disk space; the screen you stop on is always saved.
    private func typingGapMenu() -> NSMenuItem {
        let item = NSMenuItem(title: "Save Frames While Typing", action: nil, keyEquivalent: "")
        item.toolTip = "While only a few words on screen change, save a frame this often. Longer uses less disk space; the screen you stop on is still saved."
        let sub = NSMenu()
        let current = defaults.integer(forKey: typingGapKey)
        for secs in FrameDedup.gapChoices {
            let i = action("Every \(secs) Seconds" + (secs == 2 ? " (Default)" : ""), #selector(setTypingGap(_:)))
            i.tag = secs
            i.state = secs == current ? .on : .off
            sub.addItem(i)
        }
        item.submenu = sub
        return item
    }

    @objc private func setTypingGap(_ sender: NSMenuItem) {
        defaults.set(sender.tag, forKey: typingGapKey)
        engine.recorder.minorChangeGap = TimeInterval(sender.tag)
    }

    // MARK: shortcut

    @discardableResult
    private func registerHotKey(_ sc: Shortcut) -> Bool {
        hotKey = nil  // unregister the old one first
        hotKey = HotKey(keyCode: sc.keyCode, modifiers: sc.modifiers) { [weak self] in self?.overlay.toggle() }
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
        a.runModal()
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
            err.runModal()
        }
    }

    @objc private func resetShortcut() {
        Shortcut.standard.save(defaults)
        if !registerHotKey(.standard) { log("cannot register \(Shortcut.standard.label); another app may use it") }
    }

    // MARK: resolution

    private func resolutionMenu() -> NSMenuItem {
        let current = Recorder.snapshotScale(default: engine.config.snapshotScale)
        let item = NSMenuItem(title: "Screenshot Resolution", action: nil, keyEquivalent: "")
        item.image = NSImage(systemSymbolName: "photo", accessibilityDescription: nil)
        let sub = NSMenu()
        // Size of the main display in real pixels, to show what each choice gives.
        let screen = NSScreen.main
        let px = screen.map { ($0.frame.width * $0.backingScaleFactor, $0.frame.height * $0.backingScaleFactor) }
        for (title, scale) in [("Full", 1.0), ("High", 0.75), ("Medium", 0.5), ("Low", 0.33)] {
            var t = "\(title) (\(Int(scale * 100))%)"
            if let (w, h) = px { t += "  —  \(Int(w * scale)) × \(Int(h * scale))" }
            let i = action(t, #selector(setResolution(_:)))
            i.representedObject = scale
            i.state = abs(Double(current) - scale) < 0.01 ? .on : .off
            sub.addItem(i)
        }
        sub.addItem(.separator())
        let note = NSMenuItem(title: "Higher looks sharper but uses more disk space", action: nil, keyEquivalent: "")
        note.isEnabled = false
        sub.addItem(note)
        item.submenu = sub
        return item
    }

    @objc private func setResolution(_ sender: NSMenuItem) {
        guard let scale = sender.representedObject as? Double else { return }
        defaults.set(scale, forKey: Recorder.scaleKey)
        log("screenshot resolution set to \(Int(scale * 100))%")
    }

    // MARK: look

    private func lookMenu() -> NSMenuItem {
        let current = OverlayLook.current
        let item = NSMenuItem(title: "Timeline Look", action: nil, keyEquivalent: "")
        item.image = NSImage(systemSymbolName: "circle.lefthalf.filled", accessibilityDescription: nil)
        let sub = NSMenu()
        for look in OverlayLook.allCases {
            let i = action(look.title, #selector(setLook(_:)))
            i.representedObject = look.rawValue
            i.state = look == current ? .on : .off
            sub.addItem(i)
        }
        item.submenu = sub
        return item
    }

    @objc private func setLook(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String else { return }
        defaults.set(raw, forKey: OverlayLook.key)
        log("timeline look set to \(raw)")
    }

    // MARK: permissions

    /// One line per macOS permission with its state. Clicking a line opens its page in System Settings.
    private func permissionsMenu() -> NSMenuItem {
        let missing = Permission.allCases.filter { !$0.granted }.count
        let item = NSMenuItem(title: missing > 0 ? "Permissions  ⚠︎ \(missing) needed" : "Permissions  ✓ All on",
                              action: nil, keyEquivalent: "")
        item.image = NSImage(systemSymbolName: "lock.shield", accessibilityDescription: nil)
        let sub = NSMenu()
        for p in Permission.allCases {
            let i = action("\(p.title) — \(p.granted ? "On" : "Off, click to turn on")", #selector(openPermission(_:)))
            i.representedObject = p.rawValue
            i.image = NSImage(systemSymbolName: p.granted ? "checkmark.circle.fill" : "exclamationmark.triangle.fill",
                              accessibilityDescription: p.granted ? "on" : "off")
            sub.addItem(i)
            let why = NSMenuItem(title: "    " + p.why, action: nil, keyEquivalent: "")
            why.isEnabled = false
            sub.addItem(why)
        }
        sub.addItem(.separator())
        sub.addItem(action("Open Privacy & Security Settings…", #selector(openPrivacySettings)))
        item.submenu = sub
        return item
    }

    @objc private func openPermission(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let p = Permission(rawValue: raw) else { return }
        PermissionHelper.shared.show(p)
    }

    @objc private func openPrivacySettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy")!)
    }

    private func keepMenu(_ title: String, key: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        let sub = NSMenu()
        let current = keep(key)
        for k in KeepFor.allCases {
            let i = action(k.title, #selector(setKeep(_:)))
            i.representedObject = [key, k.rawValue]
            i.state = k == current ? .on : .off
            sub.addItem(i)
        }
        item.submenu = sub
        return item
    }

    @objc private func setKeep(_ sender: NSMenuItem) {
        guard let pair = sender.representedObject as? [String], pair.count == 2,
              let k = KeepFor(rawValue: pair[1]) else { return }
        let key = pair[0]
        let what = key == keepScreenKey ? "screenshots" : "audio clips"
        if let secs = k.seconds, (keep(key).seconds ?? .infinity) > secs {
            guard confirm("Keep \(what) for \(k.title.lowercased())?",
                          "OpenRewind will delete \(what) older than \(k.title.lowercased()) now, and keep doing it every hour. This cannot be undone.",
                          button: "Delete Older \(what.capitalized)") else { return }
        }
        defaults.set(k.rawValue, forKey: key)
        if key == keepScreenKey { retention.screenKeep = k } else { retention.audioKeep = k }
        retention.runNow()
    }

    @objc private func deleteRecent(_ sender: NSMenuItem) {
        let now = Date()
        let from: Date
        switch sender.tag {
        case 1: from = now.addingTimeInterval(-15 * 60)
        case 2: from = now.addingTimeInterval(-3600)
        default: from = Calendar.current.startOfDay(for: now)
        }
        guard confirm("Delete \(sender.title.lowercased()) of recordings?",
                      "Screenshots and audio from \(sender.title.lowercased()) will be deleted. This cannot be undone.",
                      button: "Delete") else { return }
        let db = engine.db
        DispatchQueue.global(qos: .userInitiated).async {
            let f = db.deleteFrames(from: from, to: now), a = db.deleteAudio(from: from, to: now)
            log("deleted \(f) screenshots, \(a) audio clips (\(sender.title))")
        }
    }

    @objc private func deleteAll(_ sender: NSMenuItem) {
        let screen = sender.tag != 2, audio = sender.tag != 1
        let what = screen && audio ? "all screenshots and audio" : (screen ? "all screenshots" : "all audio recordings")
        guard confirm("Delete \(what)?", "This removes \(what) from this Mac, and every journal recap. This cannot be undone.",
                      button: "Delete Everything") else { return }
        let db = engine.db, now = Date()
        DispatchQueue.global(qos: .userInitiated).async {
            let f = screen ? db.deleteFrames(from: nil, to: now) : 0
            let a = audio ? db.deleteAudio(from: nil, to: now) : 0
            db.deleteRecaps(from: nil, to: nil)  // each one was written from both
            db.compact()
            log("deleted \(f) screenshots, \(a) audio clips (all)")
        }
    }

    private func confirm(_ title: String, _ text: String, button: String) -> Bool {
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.alertStyle = .warning
        a.addButton(withTitle: button)
        a.addButton(withTitle: "Cancel")
        a.buttons.first?.hasDestructiveAction = true
        return a.runModal() == .alertFirstButtonReturn
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
                if a.runModal() == .alertFirstButtonReturn { u.update() }
                return
            } else {
                a.messageText = "OpenRewind is up to date"
                a.informativeText = "You have the latest version, \(u.current)."
            }
            a.runModal()
        }
    }

    // MARK: storage

    private func storageLines() -> [String] {
        let size = dataSize.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) } ?? "measuring…"
        var lines = ["Using \(size)"]
        if let oldest = engine.db.oldestRecording() {
            let f = DateFormatter()
            f.dateStyle = .medium; f.timeStyle = .short
            let rel = RelativeDateTimeFormatter()
            rel.unitsStyle = .full
            lines.append("Goes back to \(f.string(from: oldest)) (\(rel.localizedString(for: oldest, relativeTo: Date())))")
        } else {
            lines.append("No recordings yet")
        }
        return lines
    }

    /// Measure the folder off the main thread, then refresh the open menu with the result.
    private func measureStorage(_ menu: NSMenu) {
        guard !measuring else { return }
        measuring = true
        let home = engine.config.home
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let size = folderSize(home)
            DispatchQueue.main.async {
                guard let self else { return }
                self.measuring = false
                let changed = self.dataSize != size
                self.dataSize = size
                if changed, let i = menu.items.firstIndex(where: { $0.title.hasPrefix("  Using ") }) {
                    menu.items[i].title = "  " + self.storageLines()[0]
                }
            }
        }
    }

    private func statusLine() -> String {
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

    @objc private func openTimeline() { overlay.show() }

    /// The browser page when the local server is on, else the timeline's own search box.
    @objc private func openSearch() {
        if engine.serverOn {
            NSWorkspace.shared.open(URL(string: "http://127.0.0.1:\(engine.config.port)/")!)
        } else {
            overlay.showSearch()
        }
    }

    @objc private func toggleServer() {
        if engine.serverOn { engine.stopServer() } else { startServer() }
        defaults.set(engine.serverOn, forKey: serverKey)
    }

    private func startServer() {
        do { try engine.startServer(exitOnFailure: false) } catch { log("cannot start server: \(error)") }
    }

    @objc private func openData() { NSWorkspace.shared.open(engine.config.home) }

    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
            else { try SMAppService.mainApp.register() }
        } catch {
            log("launch at login failed: \(error.localizedDescription)")
        }
    }

    @objc private func quit() { NSApp.terminate(nil) }
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
