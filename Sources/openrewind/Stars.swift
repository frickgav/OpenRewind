import AppKit
import Carbon.HIToolbox
import SQLCipher
import SwiftUI

/// A moment the user starred: with ⌥⌘S from anywhere, or S / the star button on the timeline.
/// Only the time (and an optional note) is stored. What was on screen comes from the screenshot
/// saved at or just before it, so a star never keeps a window title or address the recordings
/// don't, and the block list applies to it like to any other moment. Windows: Stars.cs.
struct Star: Identifiable, Equatable {
    var id: Int64
    var time: Date
    var note: String
}

/// A star with the screenshot it points at (nil when none was saved near it).
struct StarredMoment {
    var star: Star
    var frame: Frame?
}

extension Database {
    /// The Windows app creates the same table.
    func migrateStars() throws {
        try exec("""
        CREATE TABLE IF NOT EXISTS stars (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            note TEXT NOT NULL DEFAULT '',
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_stars_ts ON stars(timestamp);
        """)
    }

    /// Stars from `from` to `to` (either open), newest first. Empty when the table is missing (an AI app's
    /// read-only open of a database the recording app has not migrated yet).
    func stars(from: Date? = nil, to: Date? = nil, limit: Int = 500) -> [Star] {
        var parts: [String] = [], args: [Arg] = []
        if let from { parts.append("timestamp >= ?"); args.append(.text(Time.iso.string(from: from))) }
        if let to { parts.append("timestamp <= ?"); args.append(.text(Time.iso.string(from: to))) }
        let sql = "SELECT id, timestamp, note FROM stars \(parts.isEmpty ? "" : "WHERE " + parts.joined(separator: " AND ")) ORDER BY timestamp DESC LIMIT ?"
        return rowsChecked(sql, args + [.int(Int64(limit))], logErrors: false) { st -> Star? in
            guard let t = Time.iso.date(from: self.col(st, 1)) else { return nil }
            return Star(id: sqlite3_column_int64(st, 0), time: t, note: self.col(st, 2))
        } ?? []
    }

    @discardableResult
    func addStar(at time: Date, note: String = "") -> Int64? {
        let now = Time.iso.string(from: Date())
        lock.lock(); defer { lock.unlock() }
        guard runLocked("INSERT INTO stars (timestamp, note, created_at) VALUES (?, ?, ?)",
                        [.text(Time.iso.string(from: time)), .text(note), .text(now)]) != nil else { return nil }
        return sqlite3_last_insert_rowid(db)
    }

    func removeStar(id: Int64) {
        lock.lock(); defer { lock.unlock() }
        _ = runLocked("DELETE FROM stars WHERE id = ?", [.int(id)])
    }

    /// Stars go with the recordings of their time (Delete Recordings and Keep Screen Recordings For).
    func deleteStarsWith(from: Date?, to: Date?) {
        var parts: [String] = [], args: [Arg] = []
        if let from { parts.append("timestamp >= ?"); args.append(.text(Time.iso.string(from: from))) }
        if let to { parts.append("timestamp < ?"); args.append(.text(Time.iso.string(from: to))) }
        lock.lock(); defer { lock.unlock() }
        _ = runLocked("DELETE FROM stars \(parts.isEmpty ? "" : "WHERE " + parts.joined(separator: " AND "))", args)
    }

    /// The screenshot a star points at: the last one saved at or before it, or, when there is none in the
    /// minute before, the first one in the minute after (a star pressed just as recording started).
    func frame(forStar t: Date) -> Frame? {
        let iso = Time.iso.string(from: t)
        let lo = Time.iso.string(from: t.addingTimeInterval(-60)), hi = Time.iso.string(from: t.addingTimeInterval(60))
        func one(_ sql: String, _ a: String, _ b: String) -> Frame? {
            guard let id = rowsChecked(sql, [.text(a), .text(b)], { st -> Int64? in sqlite3_column_int64(st, 0) })?.first else { return nil }
            return frame(id: id)
        }
        return one("SELECT id FROM frames WHERE timestamp <= ? AND timestamp >= ? ORDER BY timestamp DESC LIMIT 1", iso, lo)
            ?? one("SELECT id FROM frames WHERE timestamp > ? AND timestamp <= ? ORDER BY timestamp ASC LIMIT 1", iso, hi)
    }

    func starredMoments(from: Date? = nil, to: Date? = nil, limit: Int = 500) -> [StarredMoment] {
        stars(from: from, to: to, limit: limit).map { StarredMoment(star: $0, frame: frame(forStar: $0.time)) }
    }
}

// MARK: - the timeline

extension TimelineModel {
    /// The star on the shown screenshot, if any.
    var currentStar: Star? {
        guard let f = current else { return nil }
        return stars.first { frameIndex(at: $0.time) == index && $0.time >= f.time.addingTimeInterval(-60) }
    }

    func reloadStars() { stars = db.stars() }

    /// S or the star button: star the shown moment, or take its star off.
    func toggleStar() {
        guard let f = current else { show(notice: "Nothing to star here"); return }
        if let s = currentStar {
            db.removeStar(id: s.id)
            reloadStars()
            if starredOnly { showStarred() }
            show(notice: "Star removed")
        } else {
            db.addStar(at: f.time)
            reloadStars()
            if starredOnly { showStarred() }
            show(notice: "Starred. Find it again with Starred next to search.")
        }
    }

    /// The Starred button next to search: list starred moments (only those matching the search words,
    /// when there are any) instead of search results.
    func setStarredOnly(_ on: Bool) {
        guard starredOnly != on else { return }
        starredOnly = on
        if on { showStarred(); return }
        let q = query.trimmingCharacters(in: .whitespaces)
        if q.isEmpty { hits = []; showResults = false } else { runSearch(q) }
    }

    /// Fills the result list with starred moments.
    func showStarred() {
        searchSeq += 1
        searching = false
        let words = query.lowercased().split(whereSeparator: { $0.isWhitespace }).map(String.init)
        let list = BlocklistStore.shared.current
        hits = db.starredMoments().compactMap { m -> SearchHit? in
            let f = m.frame
            if let app = appFilter, f?.appName != app { return nil }
            let hidden = f.map { list.hides($0) } ?? false
            let title = f.map { $0.appName.isEmpty ? "Screen" : $0.appName } ?? "Starred moment"
            // Blocked now: the app name only, as on the timeline.
            let detail = hidden ? "" : [m.star.note, f?.windowName ?? "", f?.browserUrl ?? ""].filter { !$0.isEmpty }.joined(separator: " · ")
            let text = hidden ? "" : (f?.text ?? "")
            if !words.isEmpty {
                let hay = (title + " " + detail + " " + text).lowercased()
                guard words.allSatisfy({ hay.contains($0) }) else { return nil }
            }
            let snippet = !words.isEmpty && !text.isEmpty ? Self.snippet(text, around: query) : detail
            return SearchHit(id: "s\(m.star.id)", kind: .star, time: m.star.time, title: title, snippet: snippet, chunkId: nil)
        }
        selectedHit = 0
        showResults = true
    }
}

/// The ★ Starred button next to search (Rewind has the same).
struct StarredFilterButton: View {
    @Environment(\.colorScheme) private var scheme
    @ObservedObject var model: TimelineModel

    var body: some View {
        let theme = Theme(scheme)
        Button { model.setStarredOnly(!model.starredOnly) } label: {
            Label("Starred", systemImage: model.starredOnly ? "star.fill" : "star")
                .font(.system(size: 12, weight: .semibold))
                .foregroundColor(model.starredOnly ? .yellow : theme.text)
                .padding(.horizontal, 12).frame(height: 30)
                .background(Capsule().fill(model.starredOnly ? theme.selected : theme.surfaceHi))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .help("Only starred moments. Star one with S here, or \(StarShortcut.label) from anywhere.")
    }
}

/// Star or unstar the shown moment.
struct StarButton: View {
    @ObservedObject var model: TimelineModel

    var body: some View {
        let on = model.currentStar != nil
        IconButton(symbol: on ? "star.fill" : "star", help: on ? "Remove the star from this moment (S)" : "Star this moment (S)") {
            model.toggleStar()
        }
        .foregroundColor(on ? .yellow : nil)
    }
}

extension TimelineBar {
    /// A small yellow star over each starred moment.
    static func drawStars(_ ctx: GraphicsContext, stars: [Star], from: Date, to: Date, x: (Date) -> CGFloat) {
        for s in stars where s.time >= from && s.time <= to {
            let cx = x(s.time), cy: CGFloat = 29, r: CGFloat = 5.5
            var p = Path()
            for i in 0..<10 {
                let a = Double(i) * .pi / 5 - .pi / 2
                let rr = i % 2 == 0 ? r : r * 0.45
                let pt = CGPoint(x: cx + CGFloat(cos(a)) * rr, y: cy + CGFloat(sin(a)) * rr)
                if i == 0 { p.move(to: pt) } else { p.addLine(to: pt) }
            }
            p.closeSubpath()
            ctx.fill(p, with: .color(.yellow))
            ctx.stroke(p, with: .color(.black.opacity(0.35)), lineWidth: 0.6)
        }
    }
}

// MARK: - ⌥⌘S from anywhere

/// The Star Moment shortcut. Rewind uses ⇧⌘S, but that is Save As in most apps, so the default is ⌥⌘S
/// (next to ⌥⌘T for the timeline). Saved under its own keys, like the timeline shortcut.
enum StarShortcut {
    static let standard = Shortcut(keyCode: kVK_ANSI_S, modifiers: optionKey | cmdKey, key: "S")

    static func load(_ d: UserDefaults = .standard) -> Shortcut {
        guard let key = d.string(forKey: "starHotkeyKey"), d.object(forKey: "starHotkeyCode") != nil else { return standard }
        return Shortcut(keyCode: d.integer(forKey: "starHotkeyCode"), modifiers: d.integer(forKey: "starHotkeyMods"), key: key)
    }

    static func save(_ sc: Shortcut, _ d: UserDefaults = .standard) {
        d.set(sc.keyCode, forKey: "starHotkeyCode")
        d.set(sc.modifiers, forKey: "starHotkeyMods")
        d.set(sc.key, forKey: "starHotkeyKey")
    }

    static var label: String { load().label }
}

/// "★ Moment starred" for a second near the top of the screen, without taking focus from the app in front.
final class StarToast {
    static let shared = StarToast()
    private var panel: NSPanel?
    private var work: DispatchWorkItem?

    func show(_ text: String) {
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main
        guard let screen else { return }
        let host = NSHostingView(rootView:
            Label(text, systemImage: "star.fill")
                .font(.system(size: 14, weight: .semibold))
                .symbolRenderingMode(.multicolor)
                .padding(.horizontal, 18).padding(.vertical, 10)
                .background(Capsule().fill(.regularMaterial))
                .overlay(Capsule().stroke(Color.primary.opacity(0.1)))
                .fixedSize())
        let size = host.fittingSize
        let p = panel ?? NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        p.level = .statusBar
        p.ignoresMouseEvents = true
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient]
        p.contentView = host
        let vf = screen.visibleFrame
        p.setFrame(NSRect(x: vf.midX - size.width / 2, y: vf.maxY - size.height - 24, width: size.width, height: size.height), display: true)
        p.alphaValue = 1
        p.orderFrontRegardless()
        panel = p
        work?.cancel()
        let w = DispatchWorkItem { [weak self] in
            NSAnimationContext.runAnimationGroup({ $0.duration = 0.3; self?.panel?.animator().alphaValue = 0 },
                                                 completionHandler: { self?.panel?.orderOut(nil) })
        }
        work = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.4, execute: w)
    }
}

// MARK: - Ask AI and AI apps

extension AITools {
    static let starredSpec = AIToolSpec(name: "list_starred",
        description: "Moments the user starred (marked as important with a shortcut) between two times, newest first, with what was on screen then. Use for questions like 'what did I star today' or 'find the thing I starred about pricing'.",
        parameters: ["type": "object", "required": [String](),
                     "properties": ["start": ["type": "string", "description": "Only after this time (optional). Local time like '2026-10-18 14:30', or 'today', 'yesterday', '2h ago'."],
                                    "end": ["type": "string", "description": "Only before this time (optional). Local time like '2026-10-18 14:30', or 'today', 'yesterday', '2h ago'."],
                                    "limit": ["type": "integer", "description": "Moments to return, up to 50. Default 20."]]])

    func listStarred(_ a: [String: Any]) -> String {
        let limit = max(1, min(50, AITools.int(a["limit"]) ?? 20))
        let moments = db.starredMoments(from: AITime.parse(a["start"]), to: AITime.parse(a["end"]), limit: 200)
        var lines: [String] = []
        for m in moments {
            // A star on a blocked app or site is left out, like the moment itself.
            if let f = m.frame, f.blocked || hides(app: f.appName, window: f.windowName, url: f.browserUrl) { continue }
            var line = "- \(AITime.label(m.star.time))"
            if let f = m.frame {
                line += " \(f.appName)"
                if !f.windowName.isEmpty { line += " — “\(f.windowName)”" }
                if !f.browserUrl.isEmpty { line += " <\(f.browserUrl)>" }
            } else {
                line += " (no screenshot saved near this moment)"
            }
            line += " (link: \(AITime.link(Time.iso.string(from: m.star.time))))"
            if !m.star.note.isEmpty { line += "\n  Note: \(m.star.note)" }
            if let text = m.frame.map({ AITools.squash($0.text) }), !text.isEmpty {
                line += "\n  On screen: " + (text.count > 400 ? String(text.prefix(400)) + "…" : text)
            }
            lines.append(line)
            if lines.count >= limit { break }
        }
        if lines.isEmpty { return "No starred moments in that range." }
        return "\(lines.count) starred moments, newest first:\n" + lines.joined(separator: "\n")
    }
}
