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
    /// Websites and links (see SiteRules): a browser tab showing one is hidden.
    let sites: [String]
    /// Extra app names from OPENREWIND_EXCLUDE.
    let extraNames: [String]
    private let keys: Set<String>
    /// Where the blocked apps are installed. Some apps show their windows from a helper app inside
    /// their bundle (Docker.app runs "Docker Desktop.app"), so a process anywhere in here is blocked too.
    private let paths: [String]

    init(apps: [App], keywords: [String], privateWindows: Bool, sites: [String] = [], extraNames: [String] = []) {
        self.apps = apps
        self.keywords = keywords
        self.privateWindows = privateWindows
        var seen = Set<String>()
        self.sites = sites.compactMap(SiteRules.normalize).filter { seen.insert($0).inserted }
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

    /// True when the address is one of the blocked sites or links.
    func blocksURL(_ url: String?) -> Bool {
        !sites.isEmpty && sites.contains { SiteRules.matches($0, url) }
    }

    /// For something already recorded, where only the app name, window title and address are known
    /// (search results, the timeline, anything that reads stored frames).
    func blocksFrame(appName: String?, windowName: String?, url: String?) -> Bool {
        blocksWindow(bundleID: nil, appName: appName, title: windowName) || blocksURL(url)
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

/// Blocked websites and links. A rule is a site ("chase.com", which covers its subdomains too) or a
/// link ("docs.google.com/document/d/abc", which covers that page and everything under it).
/// Rules are stored without "https://" or "www.". The Windows app has the same rules in SiteRules.cs.
enum SiteRules {
    /// The rule for what the user typed or picked ("https://www.Chase.com/" → "chase.com"), or nil
    /// when it is not a site. The fragment (#…) and a trailing "/" are dropped; a query is kept, since
    /// it can be what tells one page from another ("youtube.com/watch?v=…").
    static func normalize(_ raw: String?) -> String? {
        var s = (raw ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !s.isEmpty, !s.contains(where: { $0.isWhitespace }) else { return nil }
        if !s.contains("://") { s = "https://" + s }
        guard let c = URLComponents(string: s), isWeb(c) else { return nil }
        let h = host(c.host ?? "")
        guard !h.isEmpty, h.contains(".") || h == "localhost" else { return nil }
        var path = c.percentEncodedPath
        while path.hasSuffix("/") { path.removeLast() }
        let query = pathQuery(c).query
        if !query.isEmpty && path.isEmpty { path = "/" }
        return h + path + query
    }

    /// True when the address is on the rule's site, or is the rule's page or below it.
    static func matches(_ rule: String, _ url: String?) -> Bool {
        guard let url, !url.isEmpty, !rule.isEmpty, let c = URLComponents(string: url), isWeb(c) else { return false }
        let cut = rule.firstIndex { $0 == "/" || $0 == "?" }
        let ruleHost = cut.map { String(rule[..<$0]) } ?? rule
        let h = host(c.host ?? "")
        guard h == ruleHost || h.hasSuffix("." + ruleHost) else { return false }
        guard let cut else { return true }
        // Percent-encoded, so plain ASCII: lower-casing keeps the lengths.
        let rest = Array(rule[cut...].lowercased().utf8)
        let pq = pathQuery(c)
        let have = Array((pq.path + pq.query).lowercased().utf8)
        guard have.starts(with: rest) else { return false }
        // "/repo" covers "/repo/issues" and "/repo?tab=x", not "/repository".
        if have.count == rest.count { return true }
        return "/?&#".utf8.contains(have[rest.count])
    }

    /// The part of a rule people recognize: "chase.com" for both "chase.com" and "chase.com/login".
    static func siteOf(_ rule: String) -> String {
        rule.firstIndex { $0 == "/" || $0 == "?" }.map { String(rule[..<$0]) } ?? rule
    }

    /// Lower case, without "www.".
    static func host(_ host: String) -> String {
        var h = host.trimmingCharacters(in: .whitespaces).lowercased()
        while h.hasSuffix(".") { h.removeLast() }
        return h.hasPrefix("www.") ? String(h.dropFirst(4)) : h
    }

    private static func isWeb(_ c: URLComponents) -> Bool {
        let scheme = c.scheme?.lowercased()
        return scheme == "http" || scheme == "https"
    }

    /// The path ("/" when empty, like Windows' Uri) and "?query" or "".
    private static func pathQuery(_ c: URLComponents) -> (path: String, query: String) {
        let q = c.percentEncodedQuery ?? ""
        return (c.percentEncodedPath.isEmpty ? "/" : c.percentEncodedPath, q.isEmpty ? "" : "?" + q)
    }
}

/// Which browser windows show a blocked site. Only the window in front can be asked for its address,
/// so each window keeps the last address seen while it was in front; that keeps a blocked tab hidden
/// after you click away to another window. Checked against the current rules, so removing a site
/// shows its windows again at once.
final class SiteWindows {
    private var lastURL: [CGWindowID: String] = [:]
    private let lock = NSLock()

    /// The window in front shows `url`. Nil means the address could not be read: keep what we knew.
    func saw(_ window: CGWindowID, url: String?) {
        guard window != 0, let url else { return }
        lock.lock(); defer { lock.unlock() }
        if lastURL.count > 500 { lastURL.removeAll() }  // closed windows; the ones in use are seen again soon
        lastURL[window] = url
    }

    func blocks(_ window: CGWindowID, _ list: Blocklist) -> Bool {
        guard !list.sites.isEmpty else { return false }
        lock.lock(); let url = lastURL[window]; lock.unlock()
        return list.blocksURL(url)
    }
}

/// One page of a recent site: its address, the window title it had, and when it was last on screen.
struct RecentPage: Hashable {
    var url: String
    var title: String
    var lastSeen: String
}

/// A site in the user's history, for the "block from recent sites" picker.
struct RecentSite: Identifiable {
    var id: String { site }
    var site: String
    var lastSeen: String
    var pages: [RecentPage]
}

enum RecentSites {
    /// Groups pages (newest first) by site, newest site first. Pages that are not web addresses are skipped.
    static func group(_ pages: [RecentPage], maxPagesPerSite: Int = 50) -> [RecentSite] {
        var bySite: [String: [RecentPage]] = [:]
        var order: [String] = []
        for p in pages {
            guard let c = URLComponents(string: p.url), ["http", "https"].contains(c.scheme?.lowercased() ?? "") else { continue }
            let site = SiteRules.host(c.host ?? "")
            guard !site.isEmpty else { continue }
            if bySite[site] == nil { bySite[site] = []; order.append(site) }
            if bySite[site]!.count < maxPagesPerSite { bySite[site]!.append(p) }
        }
        return order.map { RecentSite(site: $0, lastSeen: bySite[$0]![0].lastSeen, pages: bySite[$0]!) }
    }

    /// "Today", "Yesterday", "3 days ago" or "Oct 2" for a stored timestamp.
    static func ago(_ iso: String, now: Date = Date()) -> String {
        guard let t = Time.iso.date(from: iso) ?? ISO8601DateFormatter().date(from: iso) else { return "" }
        let cal = Calendar.current
        let days = cal.dateComponents([.day], from: cal.startOfDay(for: t), to: cal.startOfDay(for: now)).day ?? 0
        if days <= 0 { return "Today" }
        if days == 1 { return "Yesterday" }
        if days < 7 { return "\(days) days ago" }
        let f = DateFormatter()
        f.setLocalizedDateFormatFromTemplate("MMMd")
        return f.string(from: t)
    }
}

/// Where the block list lives (UserDefaults, next to the menu settings) and the copy recording reads.
final class BlocklistStore {
    static let shared = BlocklistStore()
    static let changed = Notification.Name("OpenRewindBlocklistChanged")

    private let defaults = UserDefaults.standard
    private let lock = NSLock()
    private var cached: Blocklist?
    static let appsKey = "blockedApps", keywordsKey = "blockedKeywords", privateKey = "blockPrivateWindows", sitesKey = "blockedSites"
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
        defaults.set(list.sites, forKey: Self.sitesKey)
        lock.lock(); cached = nil; lock.unlock()
        log("block list: \(list.apps.count) apps, \(list.keywords.count) title words, \(list.sites.count) sites, private windows \(list.privateWindows ? "hidden" : "recorded")")
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
        let sites = defaults.stringArray(forKey: Self.sitesKey) ?? []
        let extra = Config.env("EXCLUDE")?.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) } ?? []
        return Blocklist(apps: apps, keywords: keywords, privateWindows: priv, sites: sites, extraNames: extra)
    }

    /// Block these sites too (rules from the site picker), keeping everything else as it is.
    func addSites(_ rules: [String]) {
        let old = current
        let add = rules.filter { r in !old.sites.contains { $0.caseInsensitiveCompare(r) == .orderedSame } }
        guard !add.isEmpty else { return }
        save(Blocklist(apps: old.apps, keywords: old.keywords, privateWindows: old.privateWindows, sites: old.sites + add, extraNames: old.extraNames))
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

    /// The same banks and money sites by address, so their tabs are hidden whatever the page title says.
    static let bankingDomains = [
        "bankofamerica.com", "chase.com", "wellsfargo.com", "citi.com", "capitalone.com", "paypal.com", "venmo.com",
        "coinbase.com", "robinhood.com", "fidelity.com", "schwab.com", "vanguard.com", "americanexpress.com", "ally.com",
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
