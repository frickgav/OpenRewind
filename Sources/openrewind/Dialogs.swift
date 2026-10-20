import AppKit

/// Every save, open and alert dialog goes through here. The timeline floats over everything (screen saver
/// level) and macOS opens a modal dialog at the modal panel level, which is under it: the dialog was up but
/// hidden, and the app sat waiting for an answer nobody could give. While a dialog is up, the timeline steps down
/// to floating (still over other apps), the app comes to the front so the dialog has the keyboard, and afterwards
/// (saved or cancelled) the timeline goes back up and gets the keyboard again. A timeline opened while a dialog
/// is up starts at floating too (see OverlayController.show).
///
/// Floating is under the menu bar and the Dock, so those show over the timeline while the dialog is up.
enum Dialogs {
    /// Dialogs up right now (one inside another is possible: an alert posted while a Save dialog is open).
    private static var depth = 0

    static var isShowing: Bool { depth > 0 || NSApp.modalWindow != nil }

    @discardableResult
    static func run(_ panel: NSSavePanel) -> NSApplication.ModalResponse {
        inFront { panel.runModal() }
    }

    @discardableResult
    static func run(_ alert: NSAlert) -> NSApplication.ModalResponse {
        inFront { alert.runModal() }
    }

    private static func inFront(_ show: () -> NSApplication.ModalResponse) -> NSApplication.ModalResponse {
        dispatchPrecondition(condition: .onQueue(.main))
        let timelines = { NSApp.windows.compactMap { $0 as? OverlayPanel } }
        let above = timelines().filter { $0.isVisible && $0.level.rawValue >= NSWindow.Level.modalPanel.rawValue }
        for w in above { w.level = .floating }
        if !above.isEmpty {
            // macOS 14+ "cooperative" activate() can be refused for a background app, so also use the forcing call.
            NSApp.activate()
            NSApp.activate(ignoringOtherApps: true)
        }
        depth += 1
        defer {
            depth -= 1
            // The last dialog closed: the timeline goes back over everything, also one opened while it was up.
            if depth == 0 {
                for w in timelines() where w.level != OverlayPanel.topLevel {
                    w.level = OverlayPanel.topLevel
                    if w.isVisible { w.makeKey() }
                }
            }
        }
        return show()
    }
}
