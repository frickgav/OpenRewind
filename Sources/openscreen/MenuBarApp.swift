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
    private var retention: RetentionJob!

    init(engine: Engine) {
        self.engine = engine
        defaults.register(defaults: [screenKey: true, audioKey: true,
                                     keepScreenKey: KeepFor.forever.rawValue, keepAudioKey: KeepFor.forever.rawValue])
    }

    func applicationDidFinishLaunching(_ note: Notification) {
        overlay = OverlayController(db: engine.db)
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu

        do { try engine.startServer() } catch { log("cannot start server: \(error)") }
        engine.startTranscriber()
        engine.setScreen(defaults.bool(forKey: screenKey))
        engine.setAudio(defaults.bool(forKey: audioKey))
        retention = RetentionJob(db: engine.db, screen: keep(keepScreenKey), audio: keep(keepAudioKey))
        retention.start()

        // ⌥⌘T opens the timeline from anywhere.
        hotKey = HotKey(keyCode: kVK_ANSI_T, modifiers: optionKey | cmdKey) { [weak self] in self?.overlay.toggle() }
        updateIcon()
    }

    // Build the menu fresh each time it opens, so it always shows the live state.
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let status = NSMenuItem(title: statusLine(), action: nil, keyEquivalent: "")
        status.isEnabled = false
        menu.addItem(status)
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
        menu.addItem(.separator())

        let settings = NSMenuItem(title: "Settings", action: nil, keyEquivalent: "")
        settings.image = NSImage(systemSymbolName: "gearshape", accessibilityDescription: nil)
        settings.submenu = settingsMenu()
        menu.addItem(settings)
        menu.addItem(action("Quit OpenScreen", #selector(quit), key: "q"))
    }

    // MARK: settings

    private func keep(_ key: String) -> KeepFor { KeepFor(rawValue: defaults.string(forKey: key) ?? "") ?? .forever }

    private func settingsMenu() -> NSMenu {
        let m = NSMenu()
        m.addItem(keepMenu("Keep Screen Recordings For", key: keepScreenKey))
        m.addItem(keepMenu("Keep Audio Recordings For", key: keepAudioKey))
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

        let login = action("Launch at Login", #selector(toggleLogin))
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        m.addItem(login)
        return m
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
                          "OpenScreen will delete \(what) older than \(k.title.lowercased()) now, and keep doing it every hour. This cannot be undone.",
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
        let img = NSImage(systemSymbolName: name, accessibilityDescription: "openscreen")
        img?.isTemplate = true
        item.button?.image = img
        item.button?.toolTip = "OpenScreen — \(statusLine())"
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

    @objc private func openSearch() {
        NSWorkspace.shared.open(URL(string: "http://127.0.0.1:\(engine.config.port)/")!)
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
