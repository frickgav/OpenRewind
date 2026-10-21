import AppKit
import SwiftUI

/// Look shared by setup, Blocked Apps and the tour: dark, rounded, the website's blue.
enum SetupTheme {
    static let bg = Color(red: 0.07, green: 0.07, blue: 0.086)
    static let card = Color.white.opacity(0.06)
    static let cardHi = Color.white.opacity(0.1)
    static let stroke = Color.white.opacity(0.1)
    static let accent = Color(red: 0.047, green: 0.353, blue: 0.98)
    static let dim = Color.white.opacity(0.65)
    static let faint = Color.white.opacity(0.45)
    static let good = Color(red: 0.19, green: 0.82, blue: 0.35)
    static let warn = Color.orange
}

/// Blue rounded button for the main action, gray otherwise.
struct PillButtonStyle: ButtonStyle {
    var primary = false
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 14, weight: .semibold))
            .padding(.horizontal, 20).padding(.vertical, 10)
            .background(Capsule().fill(primary ? SetupTheme.accent : SetupTheme.card))
            .overlay(Capsule().stroke(primary ? Color.clear : SetupTheme.stroke))
            .foregroundColor(.white)
            .opacity(configuration.isPressed ? 0.75 : 1)
            .contentShape(Capsule())
    }
}

/// A row that toggles: icon, title, a gray line, and a check box on the right.
struct CheckRowView: View {
    var icon: Image?
    var emoji: String?
    var title: String
    var detail: String?
    @Binding var on: Bool
    @State private var hover = false

    var body: some View {
        Button { on.toggle() } label: {
            HStack(spacing: 12) {
                if let icon { icon.resizable().frame(width: 26, height: 26) }
                if let emoji { Text(emoji).font(.system(size: 20)).frame(width: 28) }
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.system(size: 14, weight: .semibold)).lineLimit(1)
                    if let detail { Text(detail).font(.system(size: 12)).foregroundColor(SetupTheme.dim).lineLimit(1) }
                }
                Spacer(minLength: 8)
                CheckBox(on: on)
            }
            .padding(.horizontal, 14).padding(.vertical, detail == nil ? 9 : 11)
            .background(RoundedRectangle(cornerRadius: 10).fill(hover ? SetupTheme.cardHi : SetupTheme.card))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }
}

struct CheckBox: View {
    var on: Bool
    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 6).fill(on ? SetupTheme.accent : Color.clear)
            RoundedRectangle(cornerRadius: 6).stroke(on ? SetupTheme.accent : SetupTheme.faint, lineWidth: 1.5)
            if on { Image(systemName: "checkmark").font(.system(size: 11, weight: .bold)).foregroundColor(.white) }
        }
        .frame(width: 20, height: 20)
    }
}

/// App icons, looked up once.
enum FoundAppIcons {
    private static var cache: [String: NSImage] = [:]
    static func icon(_ app: FoundApp) -> Image {
        if let i = cache[app.id] { return Image(nsImage: i) }
        let img = app.url.map { NSWorkspace.shared.icon(forFile: $0.path) } ?? NSImage(systemSymbolName: "app", accessibilityDescription: nil) ?? NSImage()
        cache[app.id] = img
        return Image(nsImage: img)
    }
}

/// Search box over the apps on this Mac, each with a check box. `selected` holds FoundApp ids.
struct AppPickerList: View {
    @Binding var selected: Set<String>
    /// Ids not to list (already covered elsewhere).
    var hide: Set<String> = []
    /// Called with the full list once loaded, so the caller can map ids back to apps.
    var loaded: ([FoundApp]) -> Void = { _ in }
    @State private var apps: [FoundApp]?
    @State private var query = ""

    var body: some View {
        VStack(spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundColor(SetupTheme.faint)
                TextField("Search apps on this Mac", text: $query).textFieldStyle(.plain).font(.system(size: 14))
            }
            .padding(.horizontal, 12).padding(.vertical, 9)
            .background(RoundedRectangle(cornerRadius: 10).fill(SetupTheme.card))
            if let apps {
                let shown = apps.filter { !hide.contains($0.id) && !hide.contains($0.name.lowercased()) &&
                    (query.isEmpty || $0.name.localizedCaseInsensitiveContains(query)) }
                if shown.isEmpty {
                    Spacer()
                    Text(query.isEmpty ? "No other apps found" : "No app matches “\(query)”").foregroundColor(SetupTheme.dim)
                    Spacer()
                } else {
                    ScrollView {
                        LazyVStack(spacing: 4) {
                            ForEach(shown) { app in
                                CheckRowView(icon: FoundAppIcons.icon(app), title: app.name, on: Binding(
                                    get: { selected.contains(app.id) },
                                    set: { if $0 { selected.insert(app.id) } else { selected.remove(app.id) } }))
                            }
                        }
                    }
                }
            } else {
                Spacer()
                ProgressView().controlSize(.small)
                Text("Looking for apps…").font(.system(size: 12)).foregroundColor(SetupTheme.dim)
                Spacer()
            }
        }
        .onAppear {
            guard apps == nil else { return }
            DispatchQueue.global(qos: .userInitiated).async {
                let all = FoundApp.all()
                DispatchQueue.main.async { apps = all; loaded(all) }
            }
        }
    }
}

// MARK: Settings → Blocked Apps & Sites

final class BlockedAppsModel: ObservableObject {
    @Published var apps: [Blocklist.App]
    @Published var keywords: [String]
    @Published var sites: [String]
    @Published var privateWindows: Bool { didSet { save() } }

    init() {
        let l = BlocklistStore.shared.current
        apps = l.apps
        keywords = l.keywords
        sites = l.sites
        privateWindows = l.privateWindows
    }

    func save() {
        let old = BlocklistStore.shared.current
        BlocklistStore.shared.save(Blocklist(apps: apps, keywords: keywords, privateWindows: privateWindows, sites: sites, extraNames: old.extraNames))
    }

    func addSites(_ rules: [String]) {
        for r in rules.compactMap(SiteRules.normalize) where !sites.contains(where: { $0.caseInsensitiveCompare(r) == .orderedSame }) {
            sites.append(r)
        }
        save()
    }

    func removeSite(_ r: String) { sites.removeAll { $0 == r }; save() }

    func remove(_ app: Blocklist.App) { apps.removeAll { $0 == app }; save() }

    func add(_ found: [FoundApp]) {
        for f in found where !apps.contains(where: { $0.id.lowercased() == f.bundleID.lowercased() && !f.bundleID.isEmpty }) {
            apps.append(Blocklist.App(id: f.bundleID, name: f.name))
        }
        save()
    }

    func addWords(_ words: [String]) {
        for w in words.map({ $0.trimmingCharacters(in: .whitespaces) }) where !w.isEmpty {
            if !keywords.contains(where: { $0.caseInsensitiveCompare(w) == .orderedSame }) { keywords.append(w) }
        }
        save()
    }

    func removeWord(_ w: String) { keywords.removeAll { $0 == w }; save() }
}

struct BlockedAppsView: View {
    /// For the recent sites list.
    var db: Database?
    /// Inside the Settings window's Privacy tab, which sets the size.
    var embedded = false
    @StateObject private var model = BlockedAppsModel()
    @State private var addingSites = false
    @State private var newWord = ""
    @State private var adding = false
    @State private var pick: Set<String> = []
    @State private var found: [FoundApp] = []
    /// Apps hidden from the timeline (see HiddenApps): still recorded, only left out when scrolling.
    @State private var hidden = HiddenApps.names

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Blocked apps, sites and windows are blurred in every screenshot, wherever they are on screen, and their text is never saved. The lock screen is never recorded.")
                .font(.system(size: 13)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)

            HStack {
                Text("Apps").font(.system(size: 17, weight: .bold))
                Spacer()
                Button("Add Apps…") { pick = []; adding = true }.buttonStyle(PillButtonStyle())
            }
            ScrollView {
                LazyVStack(spacing: 4) {
                    ForEach(model.apps.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }, id: \.self) { app in
                        removableRow(app.name, detail: app.id.isEmpty ? nil : app.id) { model.remove(app) }
                    }
                    if model.apps.isEmpty { Text("No blocked apps").foregroundColor(SetupTheme.faint).padding() }
                }
            }
            .frame(minHeight: 120)

            if !hidden.isEmpty {
                Text("Hidden from the timeline").font(.system(size: 17, weight: .bold))
                Text("Still recorded and searchable by AI apps, but left out when you scroll the timeline. Press H in the timeline to hide the app on screen.")
                    .font(.system(size: 12)).foregroundColor(SetupTheme.faint).fixedSize(horizontal: false, vertical: true)
                ForEach(hidden, id: \.self) { app in
                    removableRow(app, detail: nil, help: "Show in the timeline again") { HiddenApps.show(app) }
                }
            }

            HStack {
                Text("Websites and links").font(.system(size: 17, weight: .bold))
                Spacer()
                Button("Add Sites…") { addingSites = true }.buttonStyle(PillButtonStyle())
            }
            .sheet(isPresented: $addingSites) {
                SitePickerView(db: db, already: Set(model.sites)) { picked in
                    model.addSites(picked)
                    addingSites = false
                }
            }
            Text(Accessibility.isTrusted ? "A browser tab showing one of these is hidden, checked each time it is in front."
                 : "Needs Accessibility permission (Settings → General → Permissions) to see which site a browser tab shows.")
                .font(.system(size: 12)).foregroundColor(Accessibility.isTrusted ? SetupTheme.faint : SetupTheme.warn)
            ScrollView {
                LazyVStack(spacing: 4) {
                    ForEach(model.sites.sorted { (SiteRules.siteOf($0), $0) < (SiteRules.siteOf($1), $1) }, id: \.self) { r in
                        removableRow(r, detail: r.contains("/") || r.contains("?") ? "This link and the pages under it" : "The whole site") { model.removeSite(r) }
                    }
                    if model.sites.isEmpty { Text("No blocked sites. Click Add Sites… to pick from the sites you visit.").foregroundColor(SetupTheme.faint).padding() }
                }
            }
            .frame(minHeight: 100)

            CheckRowView(emoji: "🕶", title: "Private browsing windows",
                         detail: "Incognito, InPrivate and private windows, when the browser names them in the title",
                         on: $model.privateWindows)

            HStack {
                Text("Words in window titles").font(.system(size: 17, weight: .bold))
                Spacer()
                Button("Add Bank Sites") {
                    model.addSites(BlockCatalog.bankingDomains)
                    model.addWords(BlockCatalog.bankingSites)
                }.buttonStyle(PillButtonStyle())
            }
            Text("A browser's window title is the page title, so “PayPal” hides PayPal tabs. Whole words only.")
                .font(.system(size: 12)).foregroundColor(SetupTheme.faint)
            HStack(spacing: 8) {
                TextField("Add a word or site name", text: $newWord, onCommit: addWord)
                    .textFieldStyle(.plain).font(.system(size: 14))
                    .padding(.horizontal, 12).padding(.vertical, 9)
                    .background(RoundedRectangle(cornerRadius: 10).fill(SetupTheme.card))
                Button("Add", action: addWord).buttonStyle(PillButtonStyle(primary: true))
            }
            ScrollView {
                LazyVStack(spacing: 4) {
                    ForEach(model.keywords.sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }, id: \.self) { w in
                        removableRow(w, detail: nil) { model.removeWord(w) }
                    }
                }
            }
            .frame(minHeight: 100)
        }
        .padding(24)
        .frame(minWidth: embedded ? 0 : 560, minHeight: embedded ? 0 : 760)
        .background(SetupTheme.bg)
        .foregroundColor(.white)
        .preferredColorScheme(.dark)
        .onReceive(NotificationCenter.default.publisher(for: HiddenApps.changed)) { _ in hidden = HiddenApps.names }
        // An app blocked from the timeline (Hide and Stop Recording It) while this window is open.
        .onReceive(NotificationCenter.default.publisher(for: BlocklistStore.changed)) { _ in
            let now = BlocklistStore.shared.current.apps
            if now != model.apps { model.apps = now }
        }
        .sheet(isPresented: $adding) {
            VStack(alignment: .leading, spacing: 12) {
                Text("Add Apps to Block").font(.system(size: 17, weight: .bold))
                AppPickerList(selected: $pick, hide: Set(model.apps.flatMap { [$0.id, $0.name.lowercased()] }), loaded: { found = $0 })
                HStack {
                    Spacer()
                    Button("Cancel") { adding = false }.buttonStyle(PillButtonStyle())
                    Button("Block Selected") {
                        model.add(found.filter { pick.contains($0.id) })
                        adding = false
                    }.buttonStyle(PillButtonStyle(primary: true)).disabled(pick.isEmpty)
                }
            }
            .padding(20)
            .frame(width: 460, height: 540)
            .background(SetupTheme.bg)
            .foregroundColor(.white)
        }
    }

    private func addWord() {
        model.addWords([newWord])
        newWord = ""
    }

    private func removableRow(_ title: String, detail: String?, help: String = "Stop blocking", remove: @escaping () -> Void) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 14, weight: .semibold))
                if let detail { Text(detail).font(.system(size: 11)).foregroundColor(SetupTheme.faint) }
            }
            Spacer()
            Button(action: remove) {
                Image(systemName: "xmark").font(.system(size: 11, weight: .bold)).foregroundColor(SetupTheme.dim)
                    .frame(width: 26, height: 26).background(Circle().fill(SetupTheme.card))
            }
            .buttonStyle(.plain)
            .help(help)
        }
        .padding(.horizontal, 14).padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 10).fill(SetupTheme.card))
    }
}

/// Opens SwiftUI views in ordinary windows, one per kind; a second open brings it to the front.
final class SetupWindows: NSObject, NSWindowDelegate {
    static let shared = SetupWindows()
    private var windows: [String: NSWindow] = [:]
    private var onClose: [String: () -> Void] = [:]

    func show<V: View>(_ key: String, title: String, size: NSSize, resizable: Bool = false, onClose: (() -> Void)? = nil, @ViewBuilder _ view: () -> V) {
        NSApp.activate(ignoringOtherApps: true)
        if let w = windows[key] { w.makeKeyAndOrderFront(nil); return }
        var style: NSWindow.StyleMask = [.titled, .closable, .fullSizeContentView]
        if resizable { style.insert(.resizable) }
        // Never taller or wider than the screen; long lists scroll inside instead.
        var size = size
        if let visible = NSScreen.main?.visibleFrame {
            size.width = min(size.width, visible.width - 40)
            size.height = min(size.height, visible.height - 40)
        }
        let w = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: style, backing: .buffered, defer: false)
        w.title = title
        w.titlebarAppearsTransparent = true
        w.appearance = NSAppearance(named: .darkAqua)
        w.backgroundColor = NSColor(SetupTheme.bg)
        w.isReleasedWhenClosed = false
        let host = NSHostingView(rootView: view())
        // Otherwise the window grows to the content's ideal size, e.g. every app on the Mac.
        host.sizingOptions = []
        w.contentView = host
        w.center()
        w.delegate = self
        w.identifier = NSUserInterfaceItemIdentifier(key)
        windows[key] = w
        if let onClose { self.onClose[key] = onClose }
        w.makeKeyAndOrderFront(nil)
    }

    func close(_ key: String) { windows[key]?.close() }

    func isOpen(_ key: String) -> Bool { windows[key]?.isVisible == true }

    func windowWillClose(_ note: Notification) {
        guard let key = (note.object as? NSWindow)?.identifier?.rawValue else { return }
        windows[key] = nil
        onClose.removeValue(forKey: key)?()
    }
}
