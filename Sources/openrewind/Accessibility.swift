import AppKit
import ApplicationServices

/// Reads text from the focused window's accessibility tree.
/// This is the primary text source; OCR is the fallback.
enum Accessibility {
    static var isTrusted: Bool { AXIsProcessTrusted() }
    private static var askedForTree = Set<pid_t>()

    static func promptForTrust() {
        let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(opts)
    }

    /// Only the address of the tab a browser window shows, quickly: checked before every screenshot when
    /// sites are blocked, so a blocked tab is hidden in the same screenshot. `title` picks a window that is
    /// not in front; nil means the focused one. Pages that are not on the web ("chrome://newtab/") are
    /// returned as they are; nil means the address could not be read.
    static func browserURL(pid: pid_t, title: String? = nil) -> String? {
        guard let app = appElement(pid) else { return nil }
        let window: AXUIElement?
        if let title {
            let windows: [AXUIElement] = attr(app, kAXWindowsAttribute) ?? []
            window = windows.first { (attr($0, kAXTitleAttribute) as String?) == title }
        } else {
            window = attr(app, kAXFocusedWindowAttribute)
        }
        guard let window else { return nil }
        return walk(window, maxNodes: 1500, maxChars: 0, urlOnly: true).url
    }

    /// Text of the focused window, and an address to come back to it: the page address when the
    /// window is a browser, or the open file (file://) when the app shows a document (Pages, Preview, Xcode).
    /// `secureFields` is where its password fields are on screen (global, top-left origin), for blurring.
    static func focusedWindow(pid: pid_t, maxNodes: Int = 4000, maxChars: Int = 60_000) -> (text: String?, url: String?, secureFields: [CGRect]) {
        guard let app = appElement(pid), let window: AXUIElement = attr(app, kAXFocusedWindowAttribute) else { return (nil, nil, []) }
        return walk(window, maxNodes: maxNodes, maxChars: maxChars, urlOnly: false)
    }

    private static func appElement(_ pid: pid_t) -> AXUIElement? {
        guard isTrusted else { return nil }
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.5)
        // Chromium browsers (Chrome, Arc, Dia, Edge, Brave) build the page tree only when asked. Ask once per app.
        if !askedForTree.contains(pid) {
            askedForTree.insert(pid)
            AXUIElementSetAttributeValue(app, "AXManualAccessibility" as CFString, kCFBooleanTrue)
        }
        return app
    }

    /// Walks the window's tree for its text and address. `urlOnly` stops at the address and reads no text.
    private static func walk(_ window: AXUIElement, maxNodes: Int, maxChars: Int, urlOnly: Bool) -> (text: String?, url: String?, secureFields: [CGRect]) {

        var lines: [String] = []
        var seen = Set<String>()
        var chars = 0
        var visited = 0
        var url: String?
        var secure: [CGRect] = []
        var stack: [(AXUIElement, Int)] = [(window, 0)]
        while let (el, depth) = stack.popLast(), visited < maxNodes, urlOnly || chars < maxChars, !(urlOnly && url != nil) {
            visited += 1
            let role: String = attr(el, kAXRoleAttribute) ?? ""
            if role == "AXSecureTextField" {  // never read password fields
                if !urlOnly, let r = frame(el) { secure.append(r) }
                continue
            }
            // Safari, Chrome, Arc, Dia, Edge, Brave and Firefox put the tab's address on the page's web area.
            // The first one found is the top page (iframes sit deeper in the tree).
            if url == nil, role == "AXWebArea", let u = urlOnly ? pageURL(el) : webURL(el) { url = u }
            for key in urlOnly ? [] : [kAXValueAttribute, kAXTitleAttribute, kAXDescriptionAttribute] {
                if let s: String = attr(el, key) {
                    let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
                    if t.count > 1, !seen.contains(t) {
                        seen.insert(t); lines.append(t); chars += t.count
                    }
                }
            }
            if depth < 40, let kids: [AXUIElement] = attr(el, kAXChildrenAttribute) {
                for k in kids.reversed() { stack.append((k, depth + 1)) }
            }
        }
        if url == nil { url = documentURL(window) }
        let text = lines.joined(separator: "\n")
        return (text.count >= 20 ? text : nil, url, secure)
    }

    /// Where an element is on screen (global coordinates, top-left origin).
    private static func frame(_ el: AXUIElement) -> CGRect? {
        var p: CFTypeRef?
        var s: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, kAXPositionAttribute as CFString, &p) == .success,
              AXUIElementCopyAttributeValue(el, kAXSizeAttribute as CFString, &s) == .success,
              let pv = p, let sv = s,
              CFGetTypeID(pv) == AXValueGetTypeID(), CFGetTypeID(sv) == AXValueGetTypeID() else { return nil }
        var point = CGPoint.zero
        var size = CGSize.zero
        guard AXValueGetValue(pv as! AXValue, .cgPoint, &point), AXValueGetValue(sv as! AXValue, .cgSize, &size),
              size.width > 0, size.height > 0 else { return nil }
        return CGRect(origin: point, size: size)
    }

    /// Any page address, web or not ("chrome://newtab/"), for telling which site a tab shows.
    private static func pageURL(_ el: AXUIElement) -> String? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, "AXURL" as CFString, &v) == .success else { return nil }
        return (v as? URL)?.absoluteString ?? (v as? String)
    }

    /// Only web pages. Apps built on web views (Claude, Slack) report internal file:// pages, which cannot be reopened.
    private static func webURL(_ el: AXUIElement) -> String? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, "AXURL" as CFString, &v) == .success else { return nil }
        let u = (v as? URL) ?? (v as? String).flatMap(URL.init(string:))
        guard let u, let scheme = u.scheme?.lowercased(), ["http", "https"].contains(scheme) else { return nil }
        return u.absoluteString
    }

    /// The file a document window shows. Only real files on disk, so the app can open it again.
    private static func documentURL(_ window: AXUIElement) -> String? {
        guard let s: String = attr(window, kAXDocumentAttribute), let u = URL(string: s), u.isFileURL,
              FileManager.default.fileExists(atPath: u.path) else { return nil }
        return u.absoluteString
    }

    /// Bring the app's window with this title to the front. Returns false when no window has that title.
    @discardableResult
    static func raiseWindow(pid: pid_t, title: String) -> Bool {
        guard isTrusted, !title.isEmpty else { return false }
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.5)
        guard let windows: [AXUIElement] = attr(app, kAXWindowsAttribute),
              let w = windows.first(where: { (attr($0, kAXTitleAttribute) as String?) == title }) else { return false }
        AXUIElementSetAttributeValue(w, kAXMinimizedAttribute as CFString, kCFBooleanFalse)
        AXUIElementPerformAction(w, kAXRaiseAction as CFString)
        AXUIElementSetAttributeValue(w, kAXMainAttribute as CFString, kCFBooleanTrue)
        return true
    }

    private static func attr<T>(_ el: AXUIElement, _ name: String) -> T? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, name as CFString, &v) == .success else { return nil }
        return v as? T
    }
}
