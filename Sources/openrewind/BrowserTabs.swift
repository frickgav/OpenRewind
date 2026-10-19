import AppKit

/// Goes back to a browser tab by its title with AppleScript. For moments saved without the tab's
/// address (Accessibility was off, or the page tree was not ready yet), so O can still bring the tab
/// back while it is open. The first time, macOS asks whether OpenRewind may control the browser.
enum BrowserTabs {
    /// Chrome and the browsers built on it that use Chrome's tab script.
    private static let chromium: Set<String> = [
        "com.google.Chrome", "com.google.Chrome.beta", "com.google.Chrome.dev", "com.google.Chrome.canary",
        "com.brave.Browser", "com.microsoft.edgemac", "com.vivaldi.Vivaldi", "com.operasoftware.Opera",
    ]
    private static let safari: Set<String> = ["com.apple.Safari", "com.apple.SafariTechnologyPreview"]

    /// Browsers whose tab address is read from the page (see Accessibility.focusedURL), for blocked sites.
    private static let others: Set<String> = [
        "company.thebrowser.Browser", "company.thebrowser.dia", "org.mozilla.firefox", "org.mozilla.firefoxdeveloperedition",
        "org.mozilla.nightly", "app.zen-browser.zen", "com.kagi.kagimacOS", "ai.perplexity.comet", "com.operasoftware.OperaGX",
        "org.chromium.Chromium", "net.imput.helium", "com.sigmaos.sigmaos.macos",
    ]

    static func isBrowser(_ bundleId: String?) -> Bool {
        guard let bundleId else { return false }
        return chromium.contains(bundleId) || safari.contains(bundleId) || others.contains(bundleId)
    }

    static func canSelect(_ bundleId: String?) -> Bool {
        guard let bundleId else { return false }
        return chromium.contains(bundleId) || safari.contains(bundleId)
    }

    /// Bring the tab with this window title to the front. Returns false when no open tab has it.
    /// Runs on the main thread (NSAppleScript is not thread-safe).
    static func select(title: String, bundleId: String) -> Bool {
        guard canSelect(bundleId), !title.isEmpty else { return false }
        let t = quoted(title)
        // The window title is the active tab's title; Chrome may add the profile name after it.
        let source: String
        if safari.contains(bundleId) {
            source = """
            tell application id "\(bundleId)"
                repeat with w in windows
                    repeat with tb in tabs of w
                        set n to name of tb
                        if n is \(t) or (length of n > 3 and \(t) starts with n) then
                            set current tab of w to tb
                            set index of w to 1
                            activate
                            return true
                        end if
                    end repeat
                end repeat
                return false
            end tell
            """
        } else {
            source = """
            tell application id "\(bundleId)"
                repeat with w in windows
                    set i to 0
                    repeat with tb in tabs of w
                        set i to i + 1
                        set n to title of tb
                        if n is \(t) or (length of n > 3 and \(t) starts with n) then
                            set active tab index of w to i
                            set index of w to 1
                            activate
                            return true
                        end if
                    end repeat
                end repeat
                return false
            end tell
            """
        }
        var error: NSDictionary?
        let result = NSAppleScript(source: source)?.executeAndReturnError(&error)
        if let error { log("cannot switch to the tab in \(bundleId): \(error[NSAppleScript.errorMessage] ?? error)") }
        return result?.booleanValue == true
    }

    /// An AppleScript string literal.
    private static func quoted(_ s: String) -> String {
        "\"" + s.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }
}
