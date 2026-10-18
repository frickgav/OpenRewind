import AppKit
import ApplicationServices

/// Reads text from the focused window's accessibility tree.
/// This is the primary text source; OCR is the fallback.
enum Accessibility {
    static var isTrusted: Bool { AXIsProcessTrusted() }

    static func promptForTrust() {
        let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(opts)
    }

    static func focusedWindowText(pid: pid_t, maxNodes: Int = 4000, maxChars: Int = 60_000) -> String? {
        guard isTrusted else { return nil }
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.5)
        guard let window: AXUIElement = attr(app, kAXFocusedWindowAttribute) else { return nil }

        var lines: [String] = []
        var seen = Set<String>()
        var chars = 0
        var visited = 0
        var stack: [(AXUIElement, Int)] = [(window, 0)]
        while let (el, depth) = stack.popLast(), visited < maxNodes, chars < maxChars {
            visited += 1
            let role: String = attr(el, kAXRoleAttribute) ?? ""
            if role == "AXSecureTextField" { continue }  // never read password fields
            for key in [kAXValueAttribute, kAXTitleAttribute, kAXDescriptionAttribute] {
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
        let text = lines.joined(separator: "\n")
        return text.count >= 20 ? text : nil
    }

    private static func attr<T>(_ el: AXUIElement, _ name: String) -> T? {
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(el, name as CFString, &v) == .success else { return nil }
        return v as? T
    }
}
