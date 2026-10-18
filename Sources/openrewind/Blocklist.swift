import AppKit
import Foundation

/// Apps and windows to hide. Blocked windows are blurred in the screenshot (wherever they are on
/// screen, not only when in front) and their text is never stored. The Windows app has the same
/// rules in Blocklist.cs.
struct Blocklist {
    /// `id` is the bundle id ("com.1password.1password"), `name` what the user sees. Either one matching blocks the app.
    struct App: Codable, Hashable {
        var id: String
        var name: String
    }

    let apps: [App]
    /// Words or site names: a window whose title has one of them is hidden ("Chase", "PayPal").
    let keywords: [String]
    /// Hide private / incognito browser windows.
    let privateWindows: Bool
    /// Extra app names from OPENREWIND_EXCLUDE.
    let extraNames: [String]
    private let keys: Set<String>
    /// Where the blocked apps are installed. Some apps show their windows from a helper app inside
    /// their bundle (Docker.app runs "Docker Desktop.app"), so a process anywhere in here is blocked too.
    private let paths: [String]

    init(apps: [App], keywords: [String], privateWindows: Bool, extraNames: [String] = []) {
        self.apps = apps
        self.keywords = keywords
        self.privateWindows = privateWindows
        self.extraNames = extraNames
        keys = Set((apps.flatMap { [$0.id, $0.name] } + extraNames).map { $0.lowercased() }.filter { !$0.isEmpty })
        paths = apps.compactMap { $0.id.isEmpty ? nil : NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0.id)?.path }
            .map { $0.hasSuffix("/") ? $0 : $0 + "/" }
    }

    /// `path` is the running app's bundle path (NSRunningApplication.bundleURL).
    func blocksApp(bundleID: String?, name: String?, path: String? = nil) -> Bool {
        if let b = bundleID?.lowercased(), !b.isEmpty, keys.contains(b) { return true }
        if let n = name?.lowercased(), !n.isEmpty, keys.contains(n) { return true }
        if let p = path, !p.isEmpty, paths.contains(where: { (p + "/").hasPrefix($0) }) { return true }
        return false
    }

    func blocksWindow(bundleID: String?, appName: String?, path: String? = nil, title: String?) -> Bool {
        blocksApp(bundleID: bundleID, name: appName, path: path)
            || (privateWindows && Blocklist.isPrivateTitle(title))
            || Blocklist.hasKeyword(title, keywords)
    }

    /// Browsers that mark private windows in the window title.
    static let privateMarkers = ["incognito", "inprivate", "private browsing", "private window", "(private)"]

    static func isPrivateTitle(_ title: String?) -> Bool {
        guard let t = title?.lowercased(), !t.isEmpty else { return false }
        return privateMarkers.contains { t.contains($0) }
    }

    /// Whole words only, any case: "Chase" hides "Chase Online" but not "Purchased".
    static func hasKeyword(_ title: String?, _ words: [String]) -> Bool {
        guard let title, !title.isEmpty else { return false }
        for w in words {
            let word = w.trimmingCharacters(in: .whitespaces)
            guard !word.isEmpty else { continue }
            var from = title.startIndex
            while let r = title.range(of: word, options: [.caseInsensitive, .diacriticInsensitive], range: from..<title.endIndex) {
                let before = r.lowerBound == title.startIndex ? nil : title[title.index(before: r.lowerBound)]
                let after = r.upperBound == title.endIndex ? nil : title[r.upperBound]
                if !isWordChar(before) && !isWordChar(after) { return true }
                from = title.index(after: r.lowerBound)
            }
        }
        return false
    }

    private static func isWordChar(_ c: Character?) -> Bool { c.map { $0.isLetter || $0.isNumber } ?? false }
}

/// Where the block list lives (UserDefaults, next to the menu settings) and the copy recording reads.
final class BlocklistStore {
    static let shared = BlocklistStore()
    static let changed = Notification.Name("OpenRewindBlocklistChanged")

    private let defaults = UserDefaults.standard
    private let lock = NSLock()
    private var cached: Blocklist?
    static let appsKey = "blockedApps", keywordsKey = "blockedKeywords", privateKey = "blockPrivateWindows"
    static let onboardingKey = "onboardingDone"

    /// What recording uses right now. Safe to call from any thread.
    var current: Blocklist {
        lock.lock(); defer { lock.unlock() }
        if let c = cached { return c }
        let c = load()
        cached = c
        return c
    }

    var onboardingDone: Bool {
        get { defaults.bool(forKey: Self.onboardingKey) }
        set { defaults.set(newValue, forKey: Self.onboardingKey) }
    }

    func save(_ list: Blocklist) {
        let apps = list.apps.map { ["id": $0.id, "name": $0.name] }
        defaults.set(apps, forKey: Self.appsKey)
        defaults.set(list.keywords, forKey: Self.keywordsKey)
        defaults.set(list.privateWindows, forKey: Self.privateKey)
        lock.lock(); cached = nil; lock.unlock()
        log("block list: \(list.apps.count) apps, \(list.keywords.count) title words, private windows \(list.privateWindows ? "hidden" : "recorded")")
        NotificationCenter.default.post(name: Self.changed, object: nil)
    }

    /// Before setup has run: password managers and private windows.
    private func load() -> Blocklist {
        let apps: [Blocklist.App]
        if let raw = defaults.array(forKey: Self.appsKey) as? [[String: String]] {
            apps = raw.compactMap { d in d["name"].map { Blocklist.App(id: d["id"] ?? "", name: $0) } }
        } else {
            apps = BlockCatalog.mac.filter { $0.category == .passwordManager }.map { Blocklist.App(id: $0.id, name: $0.name) }
        }
        let keywords = defaults.stringArray(forKey: Self.keywordsKey) ?? []
        let priv = defaults.object(forKey: Self.privateKey) as? Bool ?? true
        let extra = Config.env("EXCLUDE")?.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) } ?? []
        return Blocklist(apps: apps, keywords: keywords, privateWindows: priv, extraNames: extra)
    }
}

/// Suggestions for setup and Settings.
enum BlockCatalog {
    enum Category { case passwordManager, finance }
    struct Entry {
        var id: String
        var name: String
        var category: Category
    }

    /// Bundle ids where known; the name catches the rest.
    static let mac: [Entry] = [
        Entry(id: "com.1password.1password", name: "1Password", category: .passwordManager),
        Entry(id: "com.agilebits.onepassword7", name: "1Password 7", category: .passwordManager),
        Entry(id: "com.bitwarden.desktop", name: "Bitwarden", category: .passwordManager),
        Entry(id: "com.apple.keychainaccess", name: "Keychain Access", category: .passwordManager),
        Entry(id: "com.apple.Passwords", name: "Passwords", category: .passwordManager),
        Entry(id: "org.keepassxc.keepassxc", name: "KeePassXC", category: .passwordManager),
        Entry(id: "com.lastpass.LastPass", name: "LastPass", category: .passwordManager),
        Entry(id: "in.sinew.Enpass-Desktop", name: "Enpass", category: .passwordManager),
        Entry(id: "", name: "Dashlane", category: .passwordManager),
        Entry(id: "", name: "NordPass", category: .passwordManager),
        Entry(id: "", name: "Proton Pass", category: .passwordManager),
        Entry(id: "", name: "Strongbox", category: .passwordManager),
        Entry(id: "", name: "Quicken", category: .finance),
        Entry(id: "", name: "Banktivity", category: .finance),
        Entry(id: "", name: "MoneyMoney", category: .finance),
        Entry(id: "", name: "Ledger Live", category: .finance),
        Entry(id: "", name: "Exodus", category: .finance),
        Entry(id: "", name: "Trezor Suite", category: .finance),
    ]

    /// Bank and money sites, matched against browser window titles (the page title).
    static let bankingSites = [
        "Online Banking", "Bank of America", "Chase", "Wells Fargo", "Citibank", "Capital One", "PayPal", "Venmo",
        "Coinbase", "Robinhood", "Fidelity", "Charles Schwab", "Vanguard", "American Express", "Ally Bank",
    ]

    static func apps(_ c: Category) -> [Blocklist.App] {
        mac.filter { $0.category == c }.map { Blocklist.App(id: $0.id, name: $0.name) }
    }
}

/// An app on this Mac, for the block list pickers.
struct FoundApp: Identifiable, Hashable {
    var id: String { bundleID.isEmpty ? name.lowercased() : bundleID }
    var bundleID: String
    var name: String
    var url: URL?

    /// Apps in the usual folders plus anything open now, sorted by name.
    static func all() -> [FoundApp] {
        var found: [String: FoundApp] = [:]
        let fm = FileManager.default
        let home = fm.homeDirectoryForCurrentUser
        let dirs = ["/Applications", "/Applications/Utilities", "/System/Applications", "/System/Applications/Utilities"]
            .map { URL(fileURLWithPath: $0) } + [home.appendingPathComponent("Applications")]
        func add(_ url: URL) {
            guard let b = Bundle(url: url), let id = b.bundleIdentifier, found[id] == nil,
                  id != Bundle.main.bundleIdentifier else { return }
            let name = (b.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String)
                ?? (b.object(forInfoDictionaryKey: "CFBundleName") as? String)
                ?? url.deletingPathExtension().lastPathComponent
            found[id] = FoundApp(bundleID: id, name: name, url: url)
        }
        for dir in dirs {
            guard let items = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]) else { continue }
            for u in items {
                if u.pathExtension == "app" { add(u) }
                else if (try? u.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true,
                        let inner = try? fm.contentsOfDirectory(at: u, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]) {
                    inner.filter { $0.pathExtension == "app" }.forEach(add)  // one folder deep: Setapp, Microsoft Office…
                }
            }
        }
        for app in NSWorkspace.shared.runningApplications where app.activationPolicy == .regular {
            if let url = app.bundleURL { add(url) }
        }
        return found.values.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }
}
