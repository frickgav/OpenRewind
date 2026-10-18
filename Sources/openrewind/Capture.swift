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
    static func screenshot() async throws -> CGImage { try await capture().image }

    /// Like `screenshot`, leaving out the windows in `excluding`. Also returns where the display
    /// is (global points, top-left origin), so window frames can be placed on the image.
    static func capture(excluding: Set<CGWindowID> = []) async throws -> (image: CGImage, bounds: CGRect) {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        let mouse = NSEvent.mouseLocation
        let target = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) }
        let targetID = target?.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID
        guard let display = content.displays.first(where: { $0.displayID == targetID }) ?? content.displays.first else {
            throw NSError(domain: "openrewind", code: 10, userInfo: [NSLocalizedDescriptionKey: "no display found"])
        }
        // Do not record ourselves (the web UI may be open in a browser, that is fine; this excludes only openrewind),
        // nor blocked windows (Redaction blurs where they were).
        let me = ProcessInfo.processInfo.processIdentifier
        let skip = content.windows.filter { $0.owningApplication?.processID == me || excluding.contains($0.windowID) }
        let filter = SCContentFilter(display: display, excludingWindows: skip)
        let cfg = SCStreamConfiguration()
        let scale = target?.backingScaleFactor ?? 2
        cfg.width = Int(CGFloat(display.width) * scale)
        cfg.height = Int(CGFloat(display.height) * scale)
        cfg.showsCursor = false
        let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: cfg)
        return (image, CGDisplayBounds(display.displayID))
    }
}
