import AppKit
import ApplicationServices
import SQLCipher

/// One thing that was open: an app window ("app"), a browser tab ("tab") or a document ("document").
/// `appID` is how to start the app again: the bundle id on the Mac, the exe path on Windows.
struct WorkspaceItem: Codable, Equatable {
    var kind: String
    var app: String
    var appID: String
    var title = ""
    var url = ""
    var path = ""

    enum CodingKeys: String, CodingKey { case kind, app, appID = "app_id", title, url, path }

    init(kind: String, app: String, appID: String, title: String = "", url: String = "", path: String = "") {
        self.kind = kind; self.app = app; self.appID = appID; self.title = title; self.url = url; self.path = path
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        kind = try c.decode(String.self, forKey: .kind)
        app = try c.decodeIfPresent(String.self, forKey: .app) ?? ""
        appID = try c.decodeIfPresent(String.self, forKey: .appID) ?? ""
        title = try c.decodeIfPresent(String.self, forKey: .title) ?? ""
        url = try c.decodeIfPresent(String.self, forKey: .url) ?? ""
        path = try c.decodeIfPresent(String.self, forKey: .path) ?? ""
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(kind, forKey: .kind)
        try c.encode(app, forKey: .app)
        try c.encode(appID, forKey: .appID)
        if !title.isEmpty { try c.encode(title, forKey: .title) }
        if !url.isEmpty { try c.encode(url, forKey: .url) }
        if !path.isEmpty { try c.encode(path, forKey: .path) }
    }
}

/// What was open at one moment, saved about once a minute while it changes.
struct WorkspaceSnapshot {
    var id: Int64
    var time: Date
    var items: [WorkspaceItem]
}

/// Restore Workspace: while recording, the apps, windows, documents and the tab each browser window shows
/// are saved now and then (only when the set changed); on the timeline, ⇧O opens again what was open at the
/// shown moment, after asking which. Never closes anything. Windows: Workspaces.cs, same table and JSON.
enum Workspace {
    static let app = "app", tab = "tab", document = "document"

    static func json(_ items: [WorkspaceItem]) -> String {
        (try? JSONEncoder().encode(items)).flatMap { String(data: $0, encoding: .utf8) } ?? "[]"
    }

    /// Items from the saved JSON; anything unreadable is skipped.
    static func parse(_ json: String) -> [WorkspaceItem] {
        guard let data = json.data(using: .utf8),
              let raw = try? JSONSerialization.jsonObject(with: data) as? [Any] else { return [] }
        return raw.compactMap { one in
            guard let d = try? JSONSerialization.data(withJSONObject: one),
                  let item = try? JSONDecoder().decode(WorkspaceItem.self, from: d),
                  [app, tab, document].contains(item.kind) else { return nil }
            return item
        }
    }

    /// Two snapshots with the same fingerprint have the same things open. Window titles are left out (they
    /// change with every keystroke in some apps); apps, tab addresses and document paths count.
    static func fingerprint(_ items: [WorkspaceItem]) -> String {
        Set(items.map { "\($0.kind)\u{1F}\($0.appID.lowercased())\u{1F}\($0.kind == app ? "" : $0.url + "\u{1F}" + $0.path)" })
            .sorted().joined(separator: "\n")
    }

    static func isWebURL(_ s: String) -> Bool {
        guard let u = URL(string: s), let scheme = u.scheme?.lowercased() else { return false }
        return scheme == "http" || scheme == "https"
    }

    /// File types that run something when opened (programs, scripts, shortcuts, installers, app bundles,
    /// disk images): Restore Workspace never opens them, whatever a saved snapshot says. Same list on Windows.
    static let runs: Set<String> = ["exe", "com", "bat", "cmd", "ps1", "psm1", "psd1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta", "msi", "msp", "msix", "appx", "scr", "pif", "lnk", "url", "reg", "cpl", "jar", "appref-ms", "application", "gadget", "inf", "scf", "sh", "bash", "zsh", "csh", "command", "tool", "app", "pkg", "mpkg", "workflow", "terminal", "scpt", "scptd", "applescript", "py", "pyw", "rb", "pl", "php", "action", "prefpane", "kext", "plugin", "webloc", "inetloc", "fileloc", "desktop", "run", "bin", "dll", "sys", "drv", "ocx", "xll", "ade", "adp", "mde", "chm", "settingcontent-ms", "library-ms", "diagcab", "iso", "img", "vhd", "vhdx", "dmg"]

    /// A document that is safe to open again: a file (not a folder or bundle) with an extension that does
    /// not run something, and not marked executable.
    static func isSafeDocument(_ path: String) -> Bool {
        let url = URL(fileURLWithPath: path)
        let ext = url.pathExtension.lowercased()
        guard !ext.isEmpty, !runs.contains(ext) else { return false }
        var dir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: path, isDirectory: &dir), !dir.boolValue else { return false }
        return !FileManager.default.isExecutableFile(atPath: path)
    }

    /// What may be opened again: not blocked apps, private windows, blocked words or sites; tabs only with
    /// a web address; documents only when the file is still there. One entry per tab, document and app.
    static func restorable(_ items: [WorkspaceItem], _ list: Blocklist) -> [WorkspaceItem] {
        var seen = Set<String>()
        return items.filter { i in
            if list.blocksWindow(bundleID: i.appID, appName: i.app, title: i.title) { return false }
            switch i.kind {
            case tab:
                guard isWebURL(i.url), !list.blocksURL(i.url), !Blocklist.hasKeyword(i.url, list.keywords) else { return false }
                return seen.insert("tab:" + i.url).inserted
            case document:
                guard !i.path.isEmpty, isSafeDocument(i.path) else { return false }
                return seen.insert("doc:" + i.path).inserted
            default:
                guard !i.appID.isEmpty else { return false }
                return seen.insert("app:" + i.appID.lowercased()).inserted
            }
        }
    }

    /// One line for the confirmation list: "Safari — Pricing (example.com)".
    static func describe(_ i: WorkspaceItem) -> String {
        let what: String
        switch i.kind {
        case tab: what = i.title.isEmpty ? i.url : "\(i.title) (\(URL(string: i.url)?.host ?? i.url))"
        case document: what = (i.path as NSString).lastPathComponent
        default: what = i.title
        }
        return what.isEmpty || what == i.app ? i.app : "\(i.app) — \(what)"
    }
}

extension Database {
    /// The Windows app creates the same table.
    func migrateWorkspaces() throws {
        try exec("""
        CREATE TABLE IF NOT EXISTS workspaces (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            items TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_workspaces_ts ON workspaces(timestamp);
        """)
    }

    /// Save what is open at `time`, unless it is the same as the last snapshot. Returns the new id, or nil.
    @discardableResult
    func saveWorkspace(at time: Date, _ items: [WorkspaceItem]) -> Int64? {
        lock.lock(); defer { lock.unlock() }
        if let last = stringsLocked("SELECT items FROM workspaces ORDER BY timestamp DESC, id DESC LIMIT 1", [])?.first,
           Workspace.fingerprint(Workspace.parse(last)) == Workspace.fingerprint(items) { return nil }
        guard runLocked("INSERT INTO workspaces (timestamp, items) VALUES (?, ?)",
                        [.text(Time.iso.string(from: time)), .text(Workspace.json(items))]) != nil else { return nil }
        return sqlite3_last_insert_rowid(db)
    }

    /// What was open at `time`: the last snapshot at or before it, within a day. Nil when none (or the
    /// table is missing, in an AI app's read-only open of an older database).
    func workspace(at time: Date) -> WorkspaceSnapshot? {
        rowsChecked("SELECT id, timestamp, items FROM workspaces WHERE timestamp <= ? AND timestamp >= ? ORDER BY timestamp DESC, id DESC LIMIT 1",
                    [.text(Time.iso.string(from: time)), .text(Time.iso.string(from: time.addingTimeInterval(-86400)))],
                    logErrors: false) { st -> WorkspaceSnapshot? in
            guard let t = Time.iso.date(from: self.col(st, 1)) else { return nil }
            return WorkspaceSnapshot(id: sqlite3_column_int64(st, 0), time: t, items: Workspace.parse(self.col(st, 2)))
        }?.first
    }

    /// Snapshots go with the recordings of their time (Delete Recordings and Keep Screen Recordings For).
    func deleteWorkspacesWith(from: Date?, to: Date?) {
        let (whereSQL, args) = Self.range("timestamp", from, to)
        run("DELETE FROM workspaces \(whereSQL)", args)
    }
}

/// Saves what is open about once a minute while the screen is recorded: each regular app's windows with
/// their documents (Accessibility), and for browsers the address of the tab each window shows, read again
/// only when its title changed. Blocked apps, private windows, blocked words and sites are never saved;
/// secrets in titles and addresses are taken out (Secrets). Stored only when the set changed.
final class WorkspaceWatcher {
    private let db: Database
    private let recording: () -> Bool
    private var timer: Timer?
    private let queue = DispatchQueue(label: "openrewind.workspaces", qos: .utility)
    /// Tab address per browser window, by pid and window title.
    private var urls: [String: String?] = [:]
    private var busy = false
    /// At most this many browser windows have their address read per minute.
    private let maxReads = 6

    init(db: Database, recording: @escaping () -> Bool) {
        self.db = db
        self.recording = recording
    }

    func start() {
        guard timer == nil else { return }
        let t = Timer(timeInterval: 60, repeats: true) { [weak self] _ in self?.tick() }
        t.tolerance = 10
        RunLoop.main.add(t, forMode: .common)
        timer = t
        DispatchQueue.main.asyncAfter(deadline: .now() + 20) { [weak self] in self?.tick() }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    private func tick() {
        guard !busy, recording() else { return }
        let me = ProcessInfo.processInfo.processIdentifier
        let apps = NSWorkspace.shared.runningApplications
            .filter { $0.activationPolicy == .regular && $0.processIdentifier != me && !$0.isTerminated }
            .map { (pid: $0.processIdentifier, name: $0.localizedName ?? "", bundleID: $0.bundleIdentifier ?? "", path: $0.bundleURL?.path) }
        busy = true
        queue.async { [weak self] in
            guard let self else { return }
            let items = self.snapshot(apps)
            if !items.isEmpty, let id = self.db.saveWorkspace(at: Date(), items) { log("workspace \(id): \(items.count) items") }
            DispatchQueue.main.async { self.busy = false }
        }
    }

    private func snapshot(_ apps: [(pid: pid_t, name: String, bundleID: String, path: String?)]) -> [WorkspaceItem] {
        let list = BlocklistStore.shared.current
        let redact = Secrets.enabled
        var items: [WorkspaceItem] = []
        var reads = 0
        var seen = Set<String>()
        for a in apps where !a.bundleID.isEmpty && !a.name.isEmpty {
            if list.blocksApp(bundleID: a.bundleID, name: a.name, path: a.path) { continue }
            let windows = Accessibility.isTrusted ? Self.windows(a.pid) : []
            if windows.isEmpty {
                items.append(WorkspaceItem(kind: Workspace.app, app: a.name, appID: a.bundleID))
                continue
            }
            let browser = BrowserTabs.isBrowser(a.bundleID)
            for w in windows {
                if list.blocksWindow(bundleID: a.bundleID, appName: a.name, path: a.path, title: w.title) { continue }
                let title = redact ? Secrets.redact(w.title) : w.title
                items.append(WorkspaceItem(kind: Workspace.app, app: a.name, appID: a.bundleID, title: title))
                if let doc = w.document {
                    items.append(WorkspaceItem(kind: Workspace.document, app: a.name, appID: a.bundleID, title: title, path: doc))
                }
                guard browser, !w.title.isEmpty else { continue }
                let key = "\(a.pid)\u{1F}\(w.title)"
                seen.insert(key)
                if urls[key] == nil {
                    guard reads < maxReads else { continue }
                    reads += 1
                    urls[key] = .some(Accessibility.browserURL(pid: a.pid, title: w.title))
                }
                guard let url = urls[key] ?? nil, Workspace.isWebURL(url), !list.blocksURL(url),
                      !Blocklist.hasKeyword(url, list.keywords) else { continue }
                items.append(WorkspaceItem(kind: Workspace.tab, app: a.name, appID: a.bundleID, title: title,
                                           url: redact ? Secrets.redactURL(url) : url))
            }
        }
        urls = urls.filter { seen.contains($0.key) }
        return items
    }

    /// The app's standard windows: title and the file shown (when it is a file that exists).
    private static func windows(_ pid: pid_t) -> [(title: String, document: String?)] {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.5)
        var v: CFTypeRef?
        guard AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &v) == .success, let wins = v as? [AXUIElement] else { return [] }
        var out: [(title: String, document: String?)] = []
        for w in wins.prefix(30) {
            func str(_ name: String) -> String? {
                var x: CFTypeRef?
                guard AXUIElementCopyAttributeValue(w, name as CFString, &x) == .success else { return nil }
                return x as? String
            }
            if let sub = str(kAXSubroleAttribute), sub != (kAXStandardWindowSubrole as String) { continue }
            let title = str(kAXTitleAttribute) ?? ""
            var doc: String?
            if let d = str(kAXDocumentAttribute), let u = URL(string: d), u.isFileURL, FileManager.default.fileExists(atPath: u.path) { doc = u.path }
            out.append((title, doc))
        }
        return out
    }
}

/// Opens again what was open at a moment, after the user picks what. Apps that are running already are
/// left as they are; nothing is ever closed.
enum WorkspaceRestore {
    /// The confirmation: one check box per item, all on. Returns the picked items, or nil for Cancel.
    static func ask(_ snap: WorkspaceSnapshot, _ items: [WorkspaceItem]) -> [WorkspaceItem]? {
        let alert = NSAlert()
        alert.messageText = "Open what was open at \(snap.time.formatted(date: .omitted, time: .shortened))?"
        alert.informativeText = "Apps that are running stay as they are, and nothing is closed. Only web pages and files that still exist are opened."
        alert.addButton(withTitle: "Open")
        alert.addButton(withTitle: "Cancel")
        let stack = NSStackView()
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 6
        let boxes = items.map { i -> NSButton in
            let b = NSButton(checkboxWithTitle: Workspace.describe(i), target: nil, action: nil)
            b.state = .on
            b.lineBreakMode = .byTruncatingTail
            b.widthAnchor.constraint(lessThanOrEqualToConstant: 400).isActive = true
            return b
        }
        boxes.forEach { stack.addArrangedSubview($0) }
        stack.edgeInsets = NSEdgeInsets(top: 4, left: 4, bottom: 4, right: 4)
        let height = min(320, CGFloat(items.count) * 24 + 8)
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 420, height: height))
        scroll.hasVerticalScroller = items.count > 13
        scroll.drawsBackground = false
        let doc = FlippedView(frame: NSRect(x: 0, y: 0, width: 404, height: CGFloat(items.count) * 24 + 8))
        stack.frame = doc.bounds
        stack.autoresizingMask = [.width, .height]
        doc.addSubview(stack)
        scroll.documentView = doc
        alert.accessoryView = scroll
        guard Dialogs.run(alert) == .alertFirstButtonReturn else { return nil }
        return zip(items, boxes).filter { $0.1.state == .on }.map(\.0)
    }

    /// Returns how many things were opened.
    @discardableResult
    static func open(_ items: [WorkspaceItem]) -> Int {
        let running = Set(NSWorkspace.shared.runningApplications.compactMap(\.bundleIdentifier))
        var opened = 0
        var started = Set<String>()
        for i in items {
            let appURL = NSWorkspace.shared.urlForApplication(withBundleIdentifier: i.appID)
            let cfg = NSWorkspace.OpenConfiguration()
            switch i.kind {
            case Workspace.tab:
                guard Workspace.isWebURL(i.url), let url = URL(string: i.url) else { continue }
                if let appURL { NSWorkspace.shared.open([url], withApplicationAt: appURL, configuration: cfg) } else { NSWorkspace.shared.open(url) }
                opened += 1
            case Workspace.document:
                guard Workspace.isSafeDocument(i.path) else { continue }
                let url = URL(fileURLWithPath: i.path)
                if let appURL { NSWorkspace.shared.open([url], withApplicationAt: appURL, configuration: cfg) } else { NSWorkspace.shared.open(url) }
                opened += 1
            default:
                // Apps with tabs or documents to open come back with them.
                if running.contains(i.appID) || started.contains(i.appID) { continue }
                if items.contains(where: { $0.kind != Workspace.app && $0.appID == i.appID }) { continue }
                guard let appURL else { continue }
                cfg.activates = false
                NSWorkspace.shared.openApplication(at: appURL, configuration: cfg)
                started.insert(i.appID)
                opened += 1
            }
        }
        return opened
    }
}

/// Top-left origin, so a list in a scroll view starts at the top.
private final class FlippedView: NSView {
    override var isFlipped: Bool { true }
}
