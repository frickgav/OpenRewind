import AppKit

/// Every save, open and alert dialog goes through here. The timeline floats over everything (screen saver
/// level) and macOS opens a modal dialog at the modal panel level, which is under it: the dialog was up but
/// hidden, and the app sat waiting for an answer nobody could give. While a dialog is up, the timeline steps down
/// to floating (still over other apps), the app comes to the front so the dialog has the keyboard, and afterwards
/// (saved or cancelled) the timeline goes back up and gets the keyboard again.
enum Dialogs {
    @discardableResult
    static func run(_ panel: NSSavePanel) -> NSApplication.ModalResponse {
        inFront { panel.runModal() }
    }

    @discardableResult
    static func run(_ alert: NSAlert) -> NSApplication.ModalResponse {
        inFront { alert.runModal() }
    }

    private static func inFront(_ show: () -> NSApplication.ModalResponse) -> NSApplication.ModalResponse {
        let above = NSApp.windows.filter {
            $0 is OverlayPanel && $0.isVisible && $0.level.rawValue >= NSWindow.Level.modalPanel.rawValue
        }
        let levels = above.map { $0.level }
        for w in above { w.level = .floating }
        if !above.isEmpty {
            // macOS 14+ "cooperative" activate() can be refused for a background app, so also use the forcing call.
            NSApp.activate()
            NSApp.activate(ignoringOtherApps: true)
        }
        defer {
            for (w, level) in zip(above, levels) {
                w.level = level
                if w.isVisible { w.makeKey() }
            }
        }
        return show()
    }
}
