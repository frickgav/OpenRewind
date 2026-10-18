import AppKit
import CoreGraphics
import ScreenCaptureKit

struct ActiveWindow {
    var appName: String
    var windowName: String
    var pid: pid_t
}

enum Capture {
    static func activeWindow() -> ActiveWindow {
        guard let app = NSWorkspace.shared.frontmostApplication else {
            return ActiveWindow(appName: "", windowName: "", pid: 0)
        }
        let pid = app.processIdentifier
        var title = ""
        if let info = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] {
            for w in info where (w[kCGWindowOwnerPID as String] as? pid_t) == pid && (w[kCGWindowLayer as String] as? Int) == 0 {
                if let n = w[kCGWindowName as String] as? String, !n.isEmpty { title = n; break }
            }
        }
        return ActiveWindow(appName: app.localizedName ?? "", windowName: title, pid: pid)
    }

    static var hasScreenPermission: Bool { CGPreflightScreenCaptureAccess() }

    static func requestScreenPermission() { _ = CGRequestScreenCaptureAccess() }

    /// Grab the display that holds the mouse (falls back to the main display).
    static func screenshot() async throws -> CGImage {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        let mouse = NSEvent.mouseLocation
        let target = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) }
        let targetID = target?.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID
        guard let display = content.displays.first(where: { $0.displayID == targetID }) ?? content.displays.first else {
            throw NSError(domain: "openrewind", code: 10, userInfo: [NSLocalizedDescriptionKey: "no display found"])
        }
        // Do not record ourselves (the web UI may be open in a browser, that is fine; this excludes only openrewind).
        let mine = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
        let filter = SCContentFilter(display: display, excludingApplications: mine, exceptingWindows: [])
        let cfg = SCStreamConfiguration()
        let scale = target?.backingScaleFactor ?? 2
        cfg.width = Int(CGFloat(display.width) * scale)
        cfg.height = Int(CGFloat(display.height) * scale)
        cfg.showsCursor = false
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: cfg)
    }
}
