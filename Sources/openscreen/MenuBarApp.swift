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

    init(engine: Engine) {
        self.engine = engine
        defaults.register(defaults: [screenKey: true, audioKey: true])
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

        let login = action("Launch at Login", #selector(toggleLogin))
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(login)
        menu.addItem(action("Quit openscreen", #selector(quit), key: "q"))
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
        item.button?.toolTip = "openscreen — \(statusLine())"
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
