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

    init(db: Database) {
        model = TimelineModel(db: db)
        model.leaveSearchBox = { [weak self] in self?.panel?.makeFirstResponder(nil) }
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

        let p = panel ?? makePanel()
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

    func hide() {
        model.stopAudio()
        if let m = monitor { NSEvent.removeMonitor(m); monitor = nil }
        guard let p = panel else { return }
        NSAnimationContext.runAnimationGroup({ $0.duration = 0.12; p.animator().alphaValue = 0 }) {
            p.orderOut(nil)
        }
        previousApp?.activate()
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
        p.contentView = NSHostingView(rootView: TimelineView(model: model, close: { [weak self] in self?.hide() }))
        return p
    }

    private func installMonitor() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .scrollWheel]) { [weak self] e in
            guard let self, self.isVisible else { return e }
            return self.handle(e) ? nil : e
        }
    }

    /// Returns true when the event was used.
    private func handle(_ e: NSEvent) -> Bool {
        if e.type == .scrollWheel {
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
        if !cmd && e.characters == "?" { model.showHelp.toggle(); return true }
        if !cmd && e.charactersIgnoringModifiers == "/" { model.focusSearchToken += 1; return true }
        switch Int(e.keyCode) {
        case kVK_Escape:
            if model.showHelp { model.showHelp = false }
            else if model.showResults { model.showResults = false }
            else { hide() }
        case kVK_LeftArrow: shift ? model.jump(to: model.playhead.addingTimeInterval(-60)) : model.step(-1)
        case kVK_RightArrow: shift ? model.jump(to: model.playhead.addingTimeInterval(60)) : model.step(1)
        case kVK_UpArrow: model.jump(to: model.playhead.addingTimeInterval(-600))
        case kVK_DownArrow: model.jump(to: model.playhead.addingTimeInterval(600))
        case kVK_Space: model.toggleAudio()
        case kVK_ANSI_N, kVK_End: model.jumpToNow()
        case kVK_ANSI_O where !cmd: model.openInBrowser?()
        case kVK_ANSI_Equal, kVK_ANSI_KeypadPlus: model.span = max(60, model.span / 2)
        case kVK_ANSI_Minus, kVK_ANSI_KeypadMinus: model.span = min(12 * 3600, model.span * 2)
        default: return false
        }
        return true
    }
}

/// System-wide hotkey through Carbon. Needs no extra permission.
final class HotKey {
    private var ref: EventHotKeyRef?
    private var handler: EventHandlerRef?
    private let action: () -> Void
    private static var actions: [UInt32: () -> Void] = [:]
    private static var nextId: UInt32 = 1

    init(keyCode: Int, modifiers: Int, action: @escaping () -> Void) {
        self.action = action
        let id = HotKey.nextId
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
        if status != noErr { log("hotkey registration failed (\(status)); another app may use it") }
    }

    deinit {
        if let ref { UnregisterEventHotKey(ref) }
        if let handler { RemoveEventHandler(handler) }
    }
}
