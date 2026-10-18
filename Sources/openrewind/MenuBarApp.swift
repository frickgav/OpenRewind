import AppKit
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
    /// Saved screenshot size as a percent of the screen.
    private let screenshotKey = "screenshotPercent"
    private static let screenshotChoices = [100, 75, 50, 25]
    /// The local HTTP API + browser search page. Off unless the user turns it on in Settings.
    private let serverKey = "localSearchServer"
    private var retention: RetentionJob!
    /// Last measured size of the data folder; measured in the background when the menu opens.
    private var dataSize: Int64?
    private var measuring = false

    init(engine: Engine) {
        self.engine = engine
        super.init()
        copyOldSettings()
        defaults.register(defaults: [screenKey: true, audioKey: true, serverKey: false,
                                     keepScreenKey: KeepFor.forever.rawValue, keepAudioKey: KeepFor.forever.rawValue,
                                     typingGapKey: 2, screenshotKey: 50])
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

    func applicationDidFinishLaunching(_ note: Notification) {
        overlay = OverlayController(db: engine.db)
        overlay.model.openInBrowser = { [weak self] in self?.openBrowserSearch() }
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu

        if defaults.bool(forKey: serverKey) { startServer() }
        engine.startTranscriber()
        engine.recorder.minorChangeGap = TimeInterval(defaults.integer(forKey: typingGapKey))
        engine.recorder.snapshotScale = screenshotScale(defaults.integer(forKey: screenshotKey))
        engine.setScreen(defaults.bool(forKey: screenKey))
        engine.setAudio(defaults.bool(forKey: audioKey))
        retention = RetentionJob(db: engine.db, screen: keep(keepScreenKey), audio: keep(keepAudioKey))
        retention.start()

        // ⌥⌘T opens the timeline from anywhere.
        hotKey = HotKey(keyCode: kVK_ANSI_T, modifiers: optionKey | cmdKey) { [weak self] in self?.overlay.toggle() }
        Updater.shared.start()
        updateIcon()
    }

    // Build the menu fresh each time it opens, so it always shows the live state.
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let status = NSMenuItem(title: statusLine(), action: nil, keyEquivalent: "")
        status.isEnabled = false
        menu.addItem(status)
        if let update = updateItem() { menu.addItem(update) }
        menu.addItem(.separator())

        menu.addItem(toggleItem("Record Screen", on: engine.screenOn, action: #selector(toggleScreen), symbol: "display"))
        if (engine.screenOn || engine.audioOn) && !Capture.hasScreenPermission {
            let fix = action("  ⚠︎ Screen permission needed — Open Settings…", #selector(openScreenSettings))
            menu.addItem(fix)
        }
        menu.addItem(toggleItem("Record Audio", on: engine.audioOn, action: #selector(toggleAudio), symbol: "waveform"))
        if engine.audioOn, engine.transcriber == nil {
            let warn = NSMenuItem(title: "  Transcription off: install whisper-cpp", action: nil, keyEquivalent: "")
            warn.isEnabled = false
            menu.addItem(warn)
        }
        menu.addItem(.separator())

        let tl = NSMenuItem(title: "Open Timeline", action: #selector(openTimeline), keyEquivalent: "t")
        tl.keyEquivalentModifierMask = [.option, .command]
        tl.target = self
        menu.addItem(tl)
        menu.addItem(action("Search…", #selector(openSearch)))
        menu.addItem(action("Show Data Folder", #selector(openData)))
        for line in storageLines() {
            let i = NSMenuItem(title: "  " + line, action: nil, keyEquivalent: "")
            i.isEnabled = false
            menu.addItem(i)
        }
        measureStorage(menu)
        menu.addItem(.separator())

        let settings = NSMenuItem(title: "Settings", action: nil, keyEquivalent: "")
        settings.image = NSImage(systemSymbolName: "gearshape", accessibilityDescription: nil)
        settings.submenu = settingsMenu()
        menu.addItem(settings)
        menu.addItem(action("Quit OpenRewind", #selector(quit), key: "q"))
    }

    // MARK: settings

    private func keep(_ key: String) -> KeepFor { KeepFor(rawValue: defaults.string(forKey: key) ?? "") ?? .forever }

    private func settingsMenu() -> NSMenu {
        let m = NSMenu()
        m.addItem(keepMenu("Keep Screen Recordings For", key: keepScreenKey))
        m.addItem(keepMenu("Keep Audio Recordings For", key: keepAudioKey))
        m.addItem(typingGapMenu())
        m.addItem(screenshotMenu())
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
        m.addItem(.separator())

        let auto = action("Download Updates Automatically", #selector(toggleAutoDownload))
        auto.state = Updater.shared.autoDownload ? .on : .off
        auto.toolTip = "Download new versions in the background. They are installed only when you click Update."
        m.addItem(auto)
        m.addItem(action("Check for Updates… (version \(Updater.shared.current))", #selector(checkForUpdates)))
        m.addItem(.separator())

        let login = action("Launch at Login", #selector(toggleLogin))
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        m.addItem(login)
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

    /// Bigger screenshots are sharper to read back in the timeline and use more disk space.
    private func screenshotMenu() -> NSMenuItem {
        let item = NSMenuItem(title: "Screenshot Resolution", action: nil, keyEquivalent: "")
        item.toolTip = "How big each saved screenshot is compared with your screen. Higher is sharper in the timeline but uses more disk space. Applies to new screenshots."
        let sub = NSMenu()
        let current = defaults.integer(forKey: screenshotKey)
        for pct in Self.screenshotChoices {
            let title = pct == 100 ? "Full (100%)" : pct == 50 ? "Half (50%, Default)" : pct == 25 ? "Quarter (25%)" : "\(pct)%"
            let i = action(title, #selector(setScreenshotSize(_:)))
            i.tag = pct
            i.state = pct == current ? .on : .off
            sub.addItem(i)
        }
        item.submenu = sub
        return item
    }

    @objc private func setScreenshotSize(_ sender: NSMenuItem) {
        defaults.set(sender.tag, forKey: screenshotKey)
        engine.recorder.snapshotScale = screenshotScale(sender.tag)
    }

    /// An unknown value falls back to half size, the default.
    private func screenshotScale(_ pct: Int) -> CGFloat {
        CGFloat(Self.screenshotChoices.contains(pct) ? pct : 50) / 100
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
        guard confirm("Delete \(what)?", "This removes \(what) from this Mac. This cannot be undone.",
                      button: "Delete Everything") else { return }
        let db = engine.db, now = Date()
        DispatchQueue.global(qos: .userInitiated).async {
            let f = screen ? db.deleteFrames(from: nil, to: now) : 0
            let a = audio ? db.deleteAudio(from: nil, to: now) : 0
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
        let on = !engine.screenOn
        engine.setScreen(on)
        defaults.set(on, forKey: screenKey)
        updateIcon()
    }

    @objc private func toggleAudio() {
        let on = !engine.audioOn
        engine.setAudio(on)
        defaults.set(on, forKey: audioKey)
        // Mic start can finish a moment later (permission prompt), so refresh again shortly.
        updateIcon()
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in self?.updateIcon() }
    }

    @objc private func openScreenSettings() {
        Capture.requestScreenPermission()
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
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

    /// The browser search page, with the timeline's query filled in. It needs the local server,
    /// so pressing O turns the server on (and remembers that, like the Settings switch).
    private func openBrowserSearch() {
        let query = overlay.model.query.trimmingCharacters(in: .whitespaces)
        overlay.hide()
        if !engine.serverOn {
            startServer()
            defaults.set(engine.serverOn, forKey: serverKey)
        }
        guard engine.serverOn, var url = URLComponents(string: "http://127.0.0.1:\(engine.config.port)/") else { return }
        if !query.isEmpty { url.queryItems = [URLQueryItem(name: "q", value: query)] }
        if let u = url.url { NSWorkspace.shared.open(u) }
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
