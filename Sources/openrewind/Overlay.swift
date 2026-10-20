import AppKit
import Carbon.HIToolbox
import SwiftUI

/// Borderless panel that can still take keyboard focus.
final class OverlayPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

/// Full-screen timeline that floats over everything, on the screen with the mouse.
final class OverlayController {
    let model: TimelineModel
    private var panel: OverlayPanel?
    private var monitor: Any?
    private var scrollAccum: CGFloat = 0
    private var previousApp: NSRunningApplication?
    /// The Ask AI chat, shared with its own window. Set by the menu bar app.
    var chat: (() -> AIChatModel)?

    init(db: Database) {
        model = TimelineModel(db: db)
        model.leaveSearchBox = { [weak self] in self?.panel?.makeFirstResponder(nil) }
        model.closeForOpen = { [weak self] in self?.hide(restoreFocus: false) }
    }

    var isVisible: Bool { panel?.isVisible == true }

    func toggle() { isVisible ? hide() : show() }

    /// Open the timeline with the keyboard in the search box.
    func showSearch() {
        show()
        // After show() has given the keyboard to the timeline.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self] in self?.model.focusSearchToken += 1 }
    }

    func show() {
        previousApp = NSWorkspace.shared.frontmostApplication
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main!
        model.reload()
        Updater.shared.checkIfStale()

        let p = panel ?? makePanel()
        p.appearance = OverlayLook.current.appearance
        p.setFrame(screen.frame, display: true)
        panel = p
        p.alphaValue = 0
        // The hotkey is a user action, so macOS lets us take focus; the overlay needs the keyboard.
        // macOS 14+ "cooperative" activate() can be refused for a background app, so also use the forcing call.
        NSApp.activate()
        NSRunningApplication.current.activate(options: [.activateAllWindows])
        NSApp.activate(ignoringOtherApps: true)
        p.makeKeyAndOrderFront(nil)
        p.orderFrontRegardless()
        p.makeKey()
        NSAnimationContext.runAnimationGroup { $0.duration = 0.15; p.animator().alphaValue = 1 }
        // AppKit gives the first text field the keyboard on open. The timeline should have it,
        // so arrows, space and +/- work at once; ⌘F or / moves into the search box.
        p.makeFirstResponder(nil)
        DispatchQueue.main.async { p.makeFirstResponder(nil) }
        installMonitor()
    }

    /// Open the timeline at a moment (links in the AI chat).
    func show(at date: Date) {
        if !isVisible { show() }
        model.jump(to: date)
    }

    /// Open the timeline at a moment, only showing `app` when given (Insights).
    func show(at date: Date, app: String?) {
        if !isVisible { show() }
        model.open(at: date, app: app)
    }

    /// Open the timeline with the guided tour on it.
    func showTutorial() {
        show()
        model.tutorialStep = 0
    }

    /// `restoreFocus: false` when another app is about to come to the front (Open tab).
    func hide(restoreFocus: Bool = true) {
        model.stopAudio()
        model.exportClip = nil
        // Marks and a text selection belong to this visit: next time the bar opens on now, where they can't be
        // seen, and Esc would clear them instead of closing.
        model.clearMarks()
        _ = model.clearTextSelection()
        model.tutorialStep = nil
        if let m = monitor { NSEvent.removeMonitor(m); monitor = nil }
        // Close the videos it read, so no decrypted copy stays in the temp folder while it is hidden.
        DispatchQueue.global(qos: .utility).async { FrameImages.shared.releaseAll() }
        guard let p = panel else { return }
        NSAnimationContext.runAnimationGroup({ $0.duration = 0.12; p.animator().alphaValue = 0 }) {
            p.orderOut(nil)
        }
        if restoreFocus { previousApp?.activate() }
    }

    private func makePanel() -> OverlayPanel {
        let p = OverlayPanel(contentRect: .zero, styleMask: [.borderless],
                             backing: .buffered, defer: false)
        p.level = .screenSaver
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = false
        p.hidesOnDeactivate = false
        p.isReleasedWhenClosed = false
        p.contentView = NSHostingView(rootView: TimelineView(model: model, close: { [weak self] in self?.hide() }, chat: chat?()))
        return p
    }

    private func installMonitor() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .scrollWheel]) { [weak self] e in
            guard let self, self.isVisible else { return e }
            // A Save dialog or alert over the timeline (or any other window) gets its own keys and scrolling:
            // Return, Esc, arrows and typing a file name must not move the timeline under it.
            if NSApp.modalWindow != nil { return e }
            if let w = e.window, w !== self.panel { return e }
            return self.handle(e) ? nil : e
        }
    }

    /// Returns true when the event was used.
    private func handle(_ e: NSEvent) -> Bool {
        if e.type == .scrollWheel {
            // Over the Ask AI chat, scrolling moves the chat, not the timeline.
            if model.showAI, let w = e.window, e.locationInWindow.x > w.frame.width - 452 { return false }
            // Scroll up / left = back in time. Trackpads send many small deltas, wheels send whole lines.
            let d = e.scrollingDeltaY + e.scrollingDeltaX
            if e.hasPreciseScrollingDeltas {
                scrollAccum += d
                let stepSize: CGFloat = 14
                let steps = Int(scrollAccum / stepSize)
                if steps != 0 { scrollAccum -= CGFloat(steps) * stepSize; model.step(-steps) }
            } else if d != 0 {
                model.step(d > 0 ? -1 : 1)
            }
            return true
        }
        let shift = e.modifierFlags.contains(.shift)
        let cmd = e.modifierFlags.contains(.command)
        let typing = panel?.firstResponder is NSTextView  // the search box is focused

        // The Export Clip card has the keyboard for its fields; esc closes it (and stops an export).
        if model.exportClip != nil {
            if Int(e.keyCode) == kVK_Escape { model.exportClip = nil; return true }
            return false
        }
        if cmd && e.keyCode == UInt16(kVK_ANSI_F) { model.focusSearchToken += 1; model.showResults = !model.query.isEmpty; return true }
        if cmd && e.keyCode == UInt16(kVK_ANSI_G) { model.nextHit(older: !shift); return true }
        if typing {
            switch Int(e.keyCode) {
            case kVK_Escape:
                // First Esc closes the list / leaves the box, second Esc closes the overlay.
                if model.showResults { model.showResults = false } else { panel?.makeFirstResponder(nil) }
                return true
            case kVK_DownArrow where model.showResults: model.moveSelection(1); return true
            case kVK_UpArrow where model.showResults: model.moveSelection(-1); return true
            default: return false  // let the text field have it
            }
        }
        if cmd && e.keyCode == UInt16(kVK_ANSI_C) { model.copy(); return true }
        if cmd && e.keyCode == UInt16(kVK_ANSI_S) { model.saveFrame(); return true }
        if cmd && e.keyCode == UInt16(kVK_ANSI_E) { model.openExport(); return true }
        if !cmd && e.characters == "?" { model.showHelp.toggle(); return true }
        if !cmd && e.charactersIgnoringModifiers == "/" { model.focusSearchToken += 1; return true }
        switch Int(e.keyCode) {
        case kVK_Escape:
            if model.showHelp { model.showHelp = false }
            else if model.showResults { model.showResults = false }
            else if model.showAI { model.showAI = false }
            else if model.clearTextSelection() {}
            else if model.hasMarks { model.clearMarks() }
            else { hide() }
        case kVK_LeftArrow: shift ? model.jump(to: model.playhead.addingTimeInterval(-60)) : model.step(-1)
        case kVK_RightArrow: shift ? model.jump(to: model.playhead.addingTimeInterval(60)) : model.step(1)
        case kVK_UpArrow: model.jump(to: model.playhead.addingTimeInterval(-600))
        case kVK_DownArrow: model.jump(to: model.playhead.addingTimeInterval(600))
        case kVK_Space: model.toggleAudio()
        case kVK_ANSI_N, kVK_End: model.jumpToNow()
        case kVK_ANSI_O where !cmd: model.openCurrent()
        case kVK_ANSI_A where !cmd: model.toggleAppFilter()
        case kVK_ANSI_LeftBracket where !cmd: model.markStart()
        case kVK_ANSI_RightBracket where !cmd: model.markEnd()
        case kVK_ANSI_Equal, kVK_ANSI_KeypadPlus: model.span = max(60, model.span / 2)
        case kVK_ANSI_Minus, kVK_ANSI_KeypadMinus: model.span = min(12 * 3600, model.span * 2)
        default: return false
        }
        return true
    }
}

/// Light or dark look for the timeline overlay, saved in UserDefaults.
enum OverlayLook: String, CaseIterable {
    case system, light, dark
    static let key = "overlayLook"

    static var current: OverlayLook {
        OverlayLook(rawValue: UserDefaults.standard.string(forKey: key) ?? "") ?? .system
    }

    var title: String {
        switch self {
        case .system: return "Match System"
        case .light: return "Light"
        case .dark: return "Dark"
        }
    }

    /// Nil follows the macOS setting.
    var appearance: NSAppearance? {
        switch self {
        case .system: return nil
        case .light: return NSAppearance(named: .aqua)
        case .dark: return NSAppearance(named: .darkAqua)
        }
    }
}

/// System-wide hotkey through Carbon. Needs no extra permission.
final class HotKey {
    private var ref: EventHotKeyRef?
    private var handler: EventHandlerRef?
    private var hk: UInt32 = 0
    private let action: () -> Void
    /// False when macOS refused the key, usually because another app already uses it.
    private(set) var registered = false
    private static var actions: [UInt32: () -> Void] = [:]
    private static var nextId: UInt32 = 1

    init(keyCode: Int, modifiers: Int, action: @escaping () -> Void) {
        self.action = action
        let id = HotKey.nextId
        hk = id
        HotKey.nextId += 1
        HotKey.actions[id] = action
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var hk = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
            DispatchQueue.main.async { HotKey.actions[hk.id]?() }
            return noErr
        }, 1, &spec, nil, &handler)
        let hkID = EventHotKeyID(signature: OSType(0x4F53_434E), id: id)  // "OSCN"
        let status = RegisterEventHotKey(UInt32(keyCode), UInt32(modifiers), hkID, GetApplicationEventTarget(), 0, &ref)
        registered = status == noErr
        if status != noErr { log("hotkey registration failed (\(status)); another app may use it") }
    }

    deinit {
        HotKey.actions[hk] = nil
        if let ref { UnregisterEventHotKey(ref) }
        if let handler { RemoveEventHandler(handler) }
    }
}

/// A key combination for the timeline hotkey, saved in UserDefaults.
struct Shortcut: Equatable {
    var keyCode: Int
    /// Carbon modifier flags (cmdKey, optionKey, controlKey, shiftKey).
    var modifiers: Int
    /// What the key shows, like "T" or "F5".
    var key: String

    static let standard = Shortcut(keyCode: kVK_ANSI_T, modifiers: optionKey | cmdKey, key: "T")

    /// Text like "⌥⌘T", in the order macOS menus use.
    var label: String {
        var s = ""
        if modifiers & controlKey != 0 { s += "⌃" }
        if modifiers & optionKey != 0 { s += "⌥" }
        if modifiers & shiftKey != 0 { s += "⇧" }
        if modifiers & cmdKey != 0 { s += "⌘" }
        return s + key
    }

    var menuModifiers: NSEvent.ModifierFlags {
        var m: NSEvent.ModifierFlags = []
        if modifiers & controlKey != 0 { m.insert(.control) }
        if modifiers & optionKey != 0 { m.insert(.option) }
        if modifiers & shiftKey != 0 { m.insert(.shift) }
        if modifiers & cmdKey != 0 { m.insert(.command) }
        return m
    }

    /// Nil when the key press is not a usable hotkey (it needs ⌘, ⌥ or ⌃, except for F-keys).
    init?(event e: NSEvent) {
        let f = e.modifierFlags
        var m = 0
        if f.contains(.command) { m |= cmdKey }
        if f.contains(.option) { m |= optionKey }
        if f.contains(.control) { m |= controlKey }
        if f.contains(.shift) { m |= shiftKey }
        let fkey = Shortcut.fKeys[Int(e.keyCode)]
        guard m & (cmdKey | optionKey | controlKey) != 0 || fkey != nil else { return nil }
        let name = fkey ?? Shortcut.special[Int(e.keyCode)]
            ?? (e.charactersIgnoringModifiers ?? "").uppercased()
        guard !name.isEmpty, !name.contains(where: { $0.isNewline }) else { return nil }
        self.init(keyCode: Int(e.keyCode), modifiers: m, key: name)
    }

    init(keyCode: Int, modifiers: Int, key: String) {
        self.keyCode = keyCode; self.modifiers = modifiers; self.key = key
    }

    private static let fKeys: [Int: String] = [
        kVK_F1: "F1", kVK_F2: "F2", kVK_F3: "F3", kVK_F4: "F4", kVK_F5: "F5", kVK_F6: "F6",
        kVK_F7: "F7", kVK_F8: "F8", kVK_F9: "F9", kVK_F10: "F10", kVK_F11: "F11", kVK_F12: "F12",
        kVK_F13: "F13", kVK_F14: "F14", kVK_F15: "F15", kVK_F16: "F16", kVK_F17: "F17", kVK_F18: "F18", kVK_F19: "F19",
    ]
    private static let special: [Int: String] = [
        kVK_Space: "Space", kVK_Return: "↩", kVK_Tab: "⇥", kVK_Delete: "⌫", kVK_ForwardDelete: "⌦",
        kVK_LeftArrow: "←", kVK_RightArrow: "→", kVK_UpArrow: "↑", kVK_DownArrow: "↓",
        kVK_Home: "↖", kVK_End: "↘", kVK_PageUp: "⇞", kVK_PageDown: "⇟",
    ]

    static func load(_ d: UserDefaults) -> Shortcut {
        guard let key = d.string(forKey: "hotkeyKey"), d.object(forKey: "hotkeyCode") != nil else { return .standard }
        return Shortcut(keyCode: d.integer(forKey: "hotkeyCode"), modifiers: d.integer(forKey: "hotkeyMods"), key: key)
    }

    func save(_ d: UserDefaults) {
        d.set(keyCode, forKey: "hotkeyCode")
        d.set(modifiers, forKey: "hotkeyMods")
        d.set(key, forKey: "hotkeyKey")
    }
}
