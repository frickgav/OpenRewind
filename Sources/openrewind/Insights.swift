import AppKit
import Foundation
import SQLite3
import SwiftUI

enum InsightsRange: String { case day, week }

/// A stretch of time with one app, window and address in front and no break in it.
/// `blocked`: saved while a blocked window or site was in front (left out of Insights).
struct ActivitySpan {
    var start: Date
    var end: Date
    var app: String
    var window: String
    var url: String
    var blocked = false
    var seconds: TimeInterval { end.timeIntervalSince(start) }
}

/// One bar: an app or a website. `first` is when it was first in front in the period.
/// `others` counts the apps or sites folded into the "Other" bar (0 for a real one).
struct InsightsItem: Identifiable {
    var name: String
    var seconds: TimeInterval
    var previousSeconds: TimeInterval
    var first: Date
    var others = 0
    var isOther: Bool { others > 0 }
    var id: String { isOther ? "\u{1}other" : name }
}

/// A long stretch mostly in one app. `seconds` is the time in that app.
struct FocusSession: Identifiable {
    var app: String
    var start: Date
    var end: Date
    var seconds: TimeInterval
    var id: String { "\(app)|\(start.timeIntervalSince1970)" }
}

/// Where the time went in one day or week. Everything blocked is left out.
struct InsightsReport {
    var range: InsightsRange
    /// The period. `start` is local midnight (the first day of the week for a week).
    var start: Date
    var end: Date
    /// The previous day or week, cut to the same length when this one is still going.
    var previousStart: Date
    var previousEnd: Date
    var totalSeconds: TimeInterval = 0
    var previousTotalSeconds: TimeInterval = 0
    var switches = 0
    var previousSwitches = 0
    /// Top apps (most time first) and an "Other" bar for the rest.
    var apps: [InsightsItem] = []
    var sites: [InsightsItem] = []
    /// Active seconds per local hour, summed over the period.
    var hours = [Double](repeating: 0, count: 24)
    /// Active seconds per day (one row for a day, seven for a week) and local hour.
    var dayHours: [[Double]] = []
    /// Longest first.
    var focus: [FocusSession] = []
    /// True when the period is still going, so the comparison stops at the same time.
    var soFar = false

    var isEmpty: Bool { totalSeconds < 1 }
}

/// Insights: time per app and site, when you were active, focus stretches and app switches,
/// worked out on this Mac from the saved frames (no AI). The Windows app has the same rules
/// in Insights.cs.
enum Insights {
    /// A frame counts until the next one, unless the break is longer than this: then the computer
    /// was locked, asleep or paused (a screen that is on gets a frame at least once a minute), and
    /// the frame counts like the newest one.
    static let idleCutoff: TimeInterval = 120
    /// A screen that does not change gets one frame a minute. A gap this long means nothing changed.
    static let stillGap: TimeInterval = 50
    /// After this long without any change on screen, the rest counts as away, unless audio was
    /// recorded then (a call, a video): that counts as present.
    static let stillLimit: TimeInterval = 300
    /// Length of a recorded audio clip (the default; `--chunk` changes it).
    static let audioChunk: TimeInterval = 30
    /// The newest frame of all counts this long.
    static let lastFrame: TimeInterval = 2
    /// A focus session survives a look at another app (or a pause) this long.
    static let focusDetour: TimeInterval = 60
    /// Shortest focus session listed.
    static let minFocus: TimeInterval = 600
    /// Share of a focus session's time spent in its app.
    static let focusShare = 0.8
    static let topCount = 10
    static let focusCount = 5

    /// The day or week around `day`, in local time. Weeks start on the calendar's first weekday.
    static func period(_ range: InsightsRange, _ day: Date, cal: Calendar = .current) -> (start: Date, end: Date) {
        let d = cal.startOfDay(for: day)
        if range == .day { return (d, cal.date(byAdding: .day, value: 1, to: d)!) }
        let back = (cal.component(.weekday, from: d) - cal.firstWeekday + 7) % 7
        let start = cal.date(byAdding: .day, value: -back, to: d)!
        return (start, cal.date(byAdding: .day, value: 7, to: start)!)
    }

    /// The same period one step earlier or later.
    static func step(_ range: InsightsRange, _ day: Date, _ n: Int, cal: Calendar = .current) -> Date {
        cal.date(byAdding: .day, value: n * (range == .day ? 1 : 7), to: cal.startOfDay(for: day))!
    }

    /// Reads the frames and works out the report. `hides` leaves out blocked apps, windows and sites.
    static func load(db: Database, range: InsightsRange, day: Date, now: Date = Date(),
                     hides: (String, String, String) -> Bool, cal: Calendar = .current) -> InsightsReport {
        let (_, end) = period(range, day, cal: cal)
        let (prevStart, _) = period(range, step(range, day, -1, cal: cal), cal: cal)
        let spans = db.activitySpans(from: prevStart, to: end)
        return build(spans: spans, range: range, day: day, now: now, hides: hides, cal: cal)
    }

    /// The report for the period around `day`, from spans covering it and the period before.
    static func build(spans: [ActivitySpan], range: InsightsRange, day: Date, now: Date,
                      hides: (String, String, String) -> Bool, cal: Calendar = .current) -> InsightsReport {
        let (start, end) = period(range, day, cal: cal)
        let (prevStart, prevEnd) = period(range, step(range, day, -1, cal: cal), cal: cal)
        var r = InsightsReport(range: range, start: start, end: end, previousStart: prevStart, previousEnd: prevEnd)
        // A period that is still going is compared with the same stretch of the one before.
        if now > start && now < end {
            r.soFar = true
            // By the clock: the same time of day, that many days into the previous period.
            let days = cal.dateComponents([.day], from: start, to: cal.startOfDay(for: now)).day ?? 0
            let clock = cal.dateComponents([.hour, .minute, .second], from: now)
            let base = cal.date(byAdding: .day, value: days, to: prevStart) ?? prevStart
            r.previousEnd = cal.date(bySettingHour: clock.hour ?? 0, minute: clock.minute ?? 0, second: clock.second ?? 0, of: base)
                ?? prevStart.addingTimeInterval(now.timeIntervalSince(start))
        }

        // Blocked frames are left out entirely: marked when saved, or blocked now (ask once per app,
        // window and address).
        var verdicts: [String: Bool] = [:]
        let kept = spans.filter { s in
            if s.blocked { return false }
            let key = "\(s.app)\u{1}\(s.window)\u{1}\(s.url)"
            if let v = verdicts[key] { return !v }
            let v = hides(s.app, s.window, s.url)
            verdicts[key] = v
            return !v
        }.sorted { $0.start < $1.start }

        let current = clip(kept, from: start, to: min(end, now))
        let before = clip(kept, from: r.previousStart, to: r.previousEnd)
        r.totalSeconds = current.reduce(0) { $0 + $1.seconds }
        r.previousTotalSeconds = before.reduce(0) { $0 + $1.seconds }
        r.switches = switches(current)
        r.previousSwitches = switches(before)
        r.apps = top(current, before, key: { $0.app })
        r.sites = top(current.filter { !site($0.url).isEmpty }, before, key: { site($0.url) })
        r.focus = Array(focusSessions(current).prefix(focusCount))

        let days = range == .day ? 1 : 7
        r.dayHours = Array(repeating: [Double](repeating: 0, count: 24), count: days)
        for s in current {
            for (local, secs) in byLocalHour(s.start, s.end, cal: cal) {
                let h = cal.component(.hour, from: local)
                r.hours[h] += secs
                let d = cal.dateComponents([.day], from: start, to: cal.startOfDay(for: local)).day ?? -1
                if d >= 0 && d < days { r.dayHours[d][h] += secs }
            }
        }
        return r
    }

    /// The parts of the spans inside [from, to).
    static func clip(_ spans: [ActivitySpan], from: Date, to: Date) -> [ActivitySpan] {
        spans.compactMap { s in
            let a = max(s.start, from), b = min(s.end, to)
            guard b > a else { return nil }
            var c = s
            c.start = a
            c.end = b
            return c
        }
    }

    /// Frames (oldest first) to spans. Each frame counts until the next one, except: after a break
    /// longer than idleCutoff it counts lastFrame, and once nothing has changed on screen for
    /// stillLimit (only the once-a-minute frames), it stops counting until something changes or
    /// audio is recorded (`audio`: start times of recorded clips, oldest first).
    /// Frames in a row with the same app, window, address and blocked mark and no break between them are joined.
    static func spans<S: Sequence>(_ rows: S, audio: [Date] = []) -> [ActivitySpan] where S.Element == ActivityRow {
        var out: [ActivitySpan] = []
        var still: TimeInterval = 0
        var a = 0
        // True when a clip overlaps [from, to). Frames come in time order, so `a` only moves forward.
        func heard(_ from: Date, _ to: Date) -> Bool {
            while a < audio.count && audio[a].addingTimeInterval(audioChunk) <= from { a += 1 }
            return a < audio.count && audio[a] < to
        }
        func add(_ f: ActivityRow, _ gap: TimeInterval) {
            let active: TimeInterval
            if gap > idleCutoff {
                active = lastFrame; still = 0
            } else if gap >= stillGap && !heard(f.time, f.time.addingTimeInterval(gap)) {
                active = max(0, min(gap, stillLimit - still)); still += gap
            } else {
                active = gap; still = 0
            }
            guard active > 0 else { return }
            let end = f.time.addingTimeInterval(active)
            if let last = out.last, last.app == f.app, last.window == f.window, last.url == f.url, last.blocked == f.blocked,
               last.end >= f.time.addingTimeInterval(-0.001) {
                out[out.count - 1].end = end
            } else {
                out.append(ActivitySpan(start: f.time, end: end, app: f.app, window: f.window, url: f.url, blocked: f.blocked))
            }
        }
        var prev: ActivityRow?
        for row in rows {
            if let p = prev { add(p, row.time.timeIntervalSince(p.time)) }
            prev = row
        }
        if let p = prev { add(p, lastFrame) }
        return out
    }

    /// "github.com" for a web address (without "www."), or "" when it is not one.
    static func site(_ url: String) -> String {
        guard !url.isEmpty, let u = URL(string: url), let scheme = u.scheme?.lowercased(),
              scheme == "http" || scheme == "https", let host = u.host?.lowercased(), !host.isEmpty else { return "" }
        return host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
    }

    /// Time per app (or site), most first: the top ones, then one "Other" bar for the rest.
    static func top(_ current: [ActivitySpan], _ before: [ActivitySpan], key: (ActivitySpan) -> String) -> [InsightsItem] {
        var time: [String: Double] = [:], first: [String: Date] = [:], prev: [String: Double] = [:]
        for s in current {
            let k = key(s)
            time[k, default: 0] += s.seconds
            if first[k] == nil { first[k] = s.start }
        }
        for s in before { prev[key(s), default: 0] += s.seconds }
        let sorted = time.sorted {
            $0.value != $1.value ? $0.value > $1.value : $0.key.localizedCaseInsensitiveCompare($1.key) == .orderedAscending
        }
        var items = sorted.prefix(topCount).map {
            InsightsItem(name: $0.key, seconds: $0.value, previousSeconds: prev[$0.key] ?? 0, first: first[$0.key]!)
        }
        let rest = sorted.dropFirst(topCount)
        if !rest.isEmpty {
            items.append(InsightsItem(name: "Other", seconds: rest.reduce(0) { $0 + $1.value },
                                      previousSeconds: rest.reduce(0) { $0 + (prev[$1.key] ?? 0) },
                                      first: rest.compactMap { first[$0.key] }.min()!, others: rest.count))
        }
        return items
    }

    /// Times the app in front changed while in use. A change after a break (idle, locked, or a
    /// blocked app) does not count.
    static func switches(_ spans: [ActivitySpan]) -> Int {
        guard spans.count > 1 else { return 0 }
        var n = 0
        for i in 1..<spans.count where spans[i].app != spans[i - 1].app && spans[i].start.timeIntervalSince(spans[i - 1].end) <= lastFrame {
            n += 1
        }
        return n
    }

    /// Stretches mostly in one app: its spans, joined while the time away between them (other
    /// apps, breaks) stays under focusDetour, at least minFocus in that app and focusShare of the
    /// stretch. Longest first.
    static func focusSessions(_ spans: [ActivitySpan]) -> [FocusSession] {
        var out: [FocusSession] = []
        func keep(_ f: FocusSession) {
            if f.seconds >= minFocus && f.seconds >= focusShare * f.end.timeIntervalSince(f.start) { out.append(f) }
        }
        for (app, group) in Dictionary(grouping: spans, by: { $0.app }) {
            var cur: FocusSession?
            for s in group.sorted(by: { $0.start < $1.start }) {
                if var c = cur, s.start.timeIntervalSince(c.end) <= focusDetour {
                    c.end = max(c.end, s.end)
                    c.seconds += s.seconds
                    cur = c
                } else {
                    if let c = cur { keep(c) }
                    cur = FocusSession(app: app, start: s.start, end: s.end, seconds: s.seconds)
                }
            }
            if let c = cur { keep(c) }
        }
        return out.sorted { $0.seconds != $1.seconds ? $0.seconds > $1.seconds : $0.start < $1.start }
    }

    /// Splits [start, end) at local hour boundaries: each piece's start and length in seconds.
    static func byLocalHour(_ start: Date, _ end: Date, cal: Calendar = .current) -> [(Date, TimeInterval)] {
        var out: [(Date, TimeInterval)] = []
        var t = start
        while t < end {
            var next = cal.dateInterval(of: .hour, for: t)?.end ?? t.addingTimeInterval(3600)
            if next <= t { next = t.addingTimeInterval(3600) }
            if next > end { next = end }
            out.append((t, next.timeIntervalSince(t)))
            t = next
        }
        return out
    }

    // MARK: text

    /// "2h 05m", "45m", "under 1m".
    static func duration(_ secs: TimeInterval) -> String {
        let m = Int((secs / 60).rounded())
        if m < 1 { return secs >= 1 ? "under 1m" : "0m" }
        return m >= 60 ? "\(m / 60)h \(String(format: "%02d", m % 60))m" : "\(m)m"
    }

    /// "+12m", "−1h 05m", or "same" for a change under a minute.
    static func change(_ now: TimeInterval, _ before: TimeInterval) -> String {
        let d = now - before
        if abs(d) < 60 { return "same" }
        return (d > 0 ? "+" : "−") + duration(abs(d))
    }

    /// "+4", "−12", or "same".
    static func change(_ now: Int, _ before: Int) -> String {
        now == before ? "same" : now > before ? "+\(now - before)" : "−\(before - now)"
    }
}

extension Database {
    /// Unbroken stretches with one app, window and address in front, oldest first, from frames in
    /// [from, to). Reads only time, app, window, address and the blocked mark, through
    /// idx_frames_insights, so the screen text is never touched; the spans are worked out while
    /// reading (Insights.spans), row by row. Working out the gaps with SQLite window functions
    /// instead was several times slower.
    func activitySpans(from: Date, to: Date) -> [ActivitySpan] {
        // Frames a little before and after, so the edges count the same as the middle.
        let start = from.addingTimeInterval(-(Insights.stillLimit + Insights.idleCutoff))
        let stop = Time.iso.string(from: to.addingTimeInterval(Insights.idleCutoff))
        func date(_ st: OpaquePointer?) -> Date {
            Date(timeIntervalSince1970: ((sqlite3_column_double(st, 0) - 2440587.5) * 86_400_000).rounded() / 1000)
        }
        lock.lock(); defer { lock.unlock() }
        var audio: [Date] = []
        var a: OpaquePointer?
        if sqlite3_prepare_v2(db, "SELECT julianday(timestamp) FROM audio_chunks WHERE timestamp >= ? AND timestamp < ? AND status != 'silent' ORDER BY timestamp",
                              -1, &a, nil) == SQLITE_OK {
            bind(a, 1, Time.iso.string(from: start.addingTimeInterval(-Insights.audioChunk)))
            bind(a, 2, stop)
            while sqlite3_step(a) == SQLITE_ROW {
                if sqlite3_column_type(a, 0) != SQLITE_NULL { audio.append(date(a)) }
            }
        }
        sqlite3_finalize(a)
        var st: OpaquePointer?
        let sql = "SELECT julianday(timestamp), app_name, window_name, browser_url, blocked FROM frames WHERE timestamp >= ? AND timestamp < ? ORDER BY timestamp"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return [] }
        defer { sqlite3_finalize(st) }
        bind(st, 1, Time.iso.string(from: start))
        bind(st, 2, stop)
        // Rows go straight into the span builder, one at a time.
        let rows = AnyIterator<ActivityRow> {
            while sqlite3_step(st) == SQLITE_ROW {
                guard sqlite3_column_type(st, 0) != SQLITE_NULL else { continue }
                return ActivityRow(time: date(st), app: self.col(st, 1), window: self.col(st, 2), url: self.col(st, 3),
                                   blocked: sqlite3_column_int(st, 4) != 0)
            }
            return nil
        }
        return Insights.spans(rows, audio: audio)
    }
}

// MARK: - window

/// The Insights window. One at a time; it follows Settings → Timeline Look.
final class InsightsWindow: NSObject, NSWindowDelegate {
    static let shared = InsightsWindow()
    private var window: NSWindow?
    private var model: InsightsModel?

    /// `openAt` shows the timeline at a moment, only showing an app when given.
    func show(db: Database, openAt: @escaping (Date, String?) -> Void) {
        NSApp.activate(ignoringOtherApps: true)
        if let w = window, let m = model {
            m.reload()
            w.makeKeyAndOrderFront(nil)
            return
        }
        let m = InsightsModel(db: db)
        m.openAt = openAt
        var size = NSSize(width: 660, height: 820)
        if let visible = NSScreen.main?.visibleFrame {
            size.width = min(size.width, visible.width - 40)
            size.height = min(size.height, visible.height - 40)
        }
        let w = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                         styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        w.title = "Insights"
        w.appearance = OverlayLook.current.appearance
        w.isReleasedWhenClosed = false
        w.minSize = NSSize(width: 520, height: 420)
        let host = NSHostingView(rootView: InsightsView(model: m))
        host.sizingOptions = []
        w.contentView = host
        w.center()
        w.delegate = self
        window = w
        model = m
        m.reload()
        w.makeKeyAndOrderFront(nil)
    }

    var isOpen: Bool { window?.isVisible == true }

    func close() { window?.close() }

    func windowWillClose(_ note: Notification) {
        window = nil
        model = nil
    }
}

/// What the Insights window shows; reports are worked out off the main thread.
final class InsightsModel: ObservableObject {
    @Published var range: InsightsRange = .day
    @Published var day = Calendar.current.startOfDay(for: Date())
    @Published var report: InsightsReport?
    @Published var loading = false
    var openAt: (Date, String?) -> Void = { _, _ in }
    private let db: Database
    private var loadID = 0

    init(db: Database) { self.db = db }

    func setRange(_ r: InsightsRange) {
        guard r != range else { return }
        range = r
        reload()
    }

    /// Previous (-1) or next (+1) day or week; never past the current one.
    func step(_ n: Int) {
        if n > 0 && isCurrent { return }
        day = Insights.step(range, day, n)
        reload()
    }

    func goToNow() {
        day = Calendar.current.startOfDay(for: Date())
        reload()
    }

    var isCurrent: Bool {
        let (s, e) = Insights.period(range, day)
        let now = Date()
        return now >= s && now < e
    }

    func reload() {
        loadID += 1
        let id = loadID, r = range, d = day, store = self.db
        loading = true
        // Same rule as Ask AI: app name, private-window title, blocked site, or a blocked word in the title or address.
        let list = BlocklistStore.shared.current
        DispatchQueue.global(qos: .userInitiated).async {
            let rep = Insights.load(db: store, range: r, day: d, hides: { a, w, u in
                list.blocksFrame(appName: a, windowName: w, url: u) || Blocklist.hasKeyword(u, list.keywords)
            })
            DispatchQueue.main.async { [weak self] in
                guard let self, id == self.loadID else { return }
                self.report = rep
                self.loading = false
            }
        }
    }

    var title: String {
        let cal = Calendar.current
        let today = cal.startOfDay(for: Date())
        if range == .day {
            if day == today { return "Today" }
            if day == cal.date(byAdding: .day, value: -1, to: today) { return "Yesterday" }
            return day.formatted(.dateTime.weekday(.wide).month(.wide).day())
        }
        if isCurrent { return "This week" }
        let (s, _) = Insights.period(range, day)
        let (cs, _) = Insights.period(range, today)
        if cal.date(byAdding: .day, value: -7, to: cs) == s { return "Last week" }
        return "Week of " + s.formatted(.dateTime.month(.wide).day())
    }

    var subtitle: String {
        let (s, e) = Insights.period(range, day)
        if range == .day { return s.formatted(date: .complete, time: .omitted) }
        let last = Calendar.current.date(byAdding: .day, value: -1, to: e) ?? e
        return "\(s.formatted(.dateTime.month(.abbreviated).day())) – \(last.formatted(.dateTime.month(.abbreviated).day().year()))"
    }

    /// "vs yesterday", "vs last week by this time"…
    var versus: String {
        let what = range == .day ? (isCurrent ? "yesterday" : "the day before") : (isCurrent ? "last week" : "the week before")
        return report?.soFar == true ? "vs \(what) by this time" : "vs \(what)"
    }
}

struct InsightsView: View {
    @ObservedObject var model: InsightsModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                header
                if let r = model.report {
                    if r.isEmpty {
                        Text("Nothing was recorded \(model.range == .day ? "on this day" : "in this week"), or only in blocked apps and sites.")
                            .foregroundStyle(.secondary)
                    } else {
                        stats(r)
                        section("Apps") { bars(r.apps, color: .accentColor, app: true) }
                        if !r.sites.isEmpty { section("Websites") { bars(r.sites, color: .teal, app: false) } }
                        section(model.range == .day ? "When you were active" : "When you were active, by day") { heat(r) }
                        section("Focus sessions") { focus(r) }
                        Text("Worked out on this Mac from your recordings, without AI. Blocked apps and sites are left out. Time counts from one screenshot to the next. Breaks over 2 minutes (locked, asleep, paused) don't count, and after 5 minutes with nothing changing on screen and no audio recorded, the rest counts as away. Click an app, site or focus session to see it in the timeline.")
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                } else {
                    Text("Working it out…").foregroundStyle(.secondary)
                }
            }
            .padding(20)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .background(keys)
    }

    /// ← → previous / next, D / W day or week.
    private var keys: some View {
        Group {
            Button("") { model.step(-1) }.keyboardShortcut(.leftArrow, modifiers: [])
            Button("") { model.step(1) }.keyboardShortcut(.rightArrow, modifiers: [])
            Button("") { model.setRange(.day) }.keyboardShortcut("d", modifiers: [])
            Button("") { model.setRange(.week) }.keyboardShortcut("w", modifiers: [])
        }
        .opacity(0)
        .accessibilityHidden(true)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Picker("", selection: Binding(get: { model.range }, set: { model.setRange($0) })) {
                    Text("Day").tag(InsightsRange.day)
                    Text("Week").tag(InsightsRange.week)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 140)
                Spacer()
                if !model.isCurrent {
                    Button(model.range == .day ? "Today" : "This week") { model.goToNow() }
                }
                Button { model.step(-1) } label: { Image(systemName: "chevron.left") }
                    .help("Previous \(model.range == .day ? "day" : "week")")
                Button { model.step(1) } label: { Image(systemName: "chevron.right") }
                    .disabled(model.isCurrent)
                    .help("Next \(model.range == .day ? "day" : "week")")
            }
            Text(model.title).font(.system(size: 24, weight: .bold))
            HStack(spacing: 6) {
                Text(model.subtitle)
                if model.loading && model.report != nil { Text("· updating…") }
            }
            .font(.callout)
            .foregroundStyle(.secondary)
        }
    }

    private func stats(_ r: InsightsReport) -> some View {
        let longest = r.focus.first
        return HStack(spacing: 10) {
            card("Active time", Insights.duration(r.totalSeconds), "\(Insights.change(r.totalSeconds, r.previousTotalSeconds)) \(model.versus)")
            card("App switches", r.switches.formatted(), "\(Insights.change(r.switches, r.previousSwitches)) \(model.versus)")
            card("Longest focus", longest.map { Insights.duration($0.seconds) } ?? "—", longest?.app ?? "No 10-minute stretch yet")
        }
    }

    private func card(_ label: String, _ value: String, _ sub: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            Text(value).font(.system(size: 22, weight: .bold)).lineLimit(1)
            Text(sub).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.tail)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .controlBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color(nsColor: .separatorColor)))
    }

    private func section<Content: View>(_ title: String, @ViewBuilder _ content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.headline)
            content()
        }
    }

    private func bars(_ items: [InsightsItem], color: Color, app: Bool) -> some View {
        let maxSecs = max(1, items.map(\.seconds).max() ?? 1)
        return VStack(spacing: 2) {
            ForEach(items) { it in
                BarRow(item: it, fraction: it.seconds / maxSecs, color: it.isOther ? Color.secondary : color, app: app,
                       versus: model.versus) {
                    guard !it.isOther else { return }
                    model.openAt(it.first, app ? it.name : nil)
                }
            }
        }
    }

    private func heat(_ r: InsightsReport) -> some View {
        let rows = model.range == .day ? [r.hours] : r.dayHours
        let start = r.start
        let cal = Calendar.current
        return VStack(alignment: .leading, spacing: 2) {
            ForEach(0..<rows.count, id: \.self) { d in
                let row = rows[d]
                HStack(spacing: 2) {
                    if model.range == .week {
                        let date = cal.date(byAdding: .day, value: d, to: start) ?? start
                        Text(date.formatted(.dateTime.weekday(.abbreviated).day()))
                            .font(.caption).foregroundStyle(.secondary)
                            .frame(width: 56, alignment: .leading)
                    }
                    ForEach(0..<24, id: \.self) { h in
                        let secs = row[h]
                        RoundedRectangle(cornerRadius: 3)
                            .fill(secs < 1 ? Color.secondary.opacity(0.15) : Color.accentColor.opacity(0.25 + 0.75 * min(1, secs / 2700)))
                            .frame(height: model.range == .day ? 34 : 20)
                            .help("\(HourLabel.text(h)): \(secs < 1 ? "nothing" : Insights.duration(secs))")
                    }
                }
            }
            HStack(spacing: 0) {
                if model.range == .week { Spacer().frame(width: 58) }
                ForEach([0, 6, 12, 18], id: \.self) { h in
                    Text(HourLabel.text(h)).font(.caption2).foregroundStyle(.tertiary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            .padding(.top, 2)
        }
    }

    private func focus(_ r: InsightsReport) -> some View {
        VStack(spacing: 2) {
            if r.focus.isEmpty {
                Text("No stretch of 10 minutes or more mostly in one app.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            ForEach(r.focus) { f in
                FocusRow(session: f, week: model.range == .week) { model.openAt(f.start, f.app) }
            }
        }
    }
}

/// Hour names under the grid: "6 am" where the clock uses AM/PM, "06" otherwise.
enum HourLabel {
    static func text(_ h: Int) -> String {
        let usesAMPM = DateFormatter.dateFormat(fromTemplate: "j", options: 0, locale: .current)?.contains("a") ?? false
        if !usesAMPM { return String(format: "%02d", h) }
        let d = Calendar.current.date(bySettingHour: h, minute: 0, second: 0, of: Date()) ?? Date()
        return d.formatted(.dateTime.hour()).lowercased()
    }
}

/// An app or site with its bar; highlighted under the mouse, opens the timeline when clicked.
private struct BarRow: View {
    let item: InsightsItem
    let fraction: Double
    let color: Color
    let app: Bool
    let versus: String
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        HStack(spacing: 10) {
            HStack(spacing: 6) {
                if app, !item.isOther, let icon = AppIcons.icon(for: item.name) {
                    Image(nsImage: icon).resizable().frame(width: 16, height: 16)
                }
                Text(item.isOther ? "Other (\(item.others))" : item.name.isEmpty ? "Unknown app" : item.name)
                    .foregroundStyle(item.isOther ? Color.secondary : Color.primary)
                    .lineLimit(1).truncationMode(.tail)
            }
            .frame(width: 180, alignment: .leading)
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    RoundedRectangle(cornerRadius: 4).fill(Color.secondary.opacity(0.15))
                    RoundedRectangle(cornerRadius: 4).fill(color)
                        .frame(width: max(3, geo.size.width * fraction))
                }
            }
            .frame(height: 12)
            Text(Insights.duration(item.seconds)).monospacedDigit().frame(width: 64, alignment: .trailing)
            Text(item.isOther ? "" : Insights.change(item.seconds, item.previousSeconds))
                .font(.caption).foregroundStyle(.secondary).monospacedDigit()
                .frame(width: 70, alignment: .trailing)
        }
        .padding(.horizontal, 8)
        .frame(height: 30)
        .background(RoundedRectangle(cornerRadius: 6).fill(hover && !item.isOther ? Color.secondary.opacity(0.12) : Color.clear))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture(perform: action)
        .help("\(item.isOther ? "\(item.others) more" : item.name): \(Insights.duration(item.seconds)) (\(Insights.change(item.seconds, item.previousSeconds)) \(versus))")
    }
}

private struct FocusRow: View {
    let session: FocusSession
    let week: Bool
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        HStack(spacing: 6) {
            if let icon = AppIcons.icon(for: session.app) {
                Image(nsImage: icon).resizable().frame(width: 16, height: 16)
            }
            Text(session.app.isEmpty ? "Unknown app" : session.app).lineLimit(1)
            Spacer()
            Text((week ? session.start.formatted(.dateTime.weekday(.abbreviated)) + " " : "")
                 + "\(session.start.formatted(date: .omitted, time: .shortened)) – \(session.end.formatted(date: .omitted, time: .shortened))")
                .foregroundStyle(.secondary)
            Text(Insights.duration(session.seconds)).bold().monospacedDigit().frame(width: 70, alignment: .trailing)
        }
        .padding(.horizontal, 8)
        .frame(height: 30)
        .background(RoundedRectangle(cornerRadius: 6).fill(hover ? Color.secondary.opacity(0.12) : Color.clear))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture(perform: action)
    }
}
