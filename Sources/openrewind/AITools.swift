import Foundation
import CoreGraphics
import SQLite3

/// What the AI can look up in the local database. A whole day of screen text is far too much to
/// send, so the model asks for summaries and searches instead, and every answer is capped in size.
/// Frames of apps, sites and windows on the block list are left out of every answer, including
/// ones saved before the user blocked them. The Windows app has the same tools in AiTools.cs.
struct AITools {
    let db: Database
    /// What to leave out. The chat sets it from the current block list before each question.
    var blocklist = Blocklist(apps: [], keywords: [], privateWindows: false)
    /// Reads a frame's screenshot as a small JPEG. Set only while the user has turned on
    /// "Let AI look at screenshots"; nil means view_screenshot is not offered at all.
    var screenshots: ((Frame) -> Data?)?

    /// One summary covers at most this many days, so a question about a whole year does not load
    /// every frame of it (set for AI apps outside OpenRewind, see MCPServer).
    var maxSummaryDays: Int?

    /// What the activity summary calls a blocked app: its time still counts, but not its name.
    static let blockedAppLabel = "(blocked app)"

    /// The tools to send with a request.
    var offered: [AIToolSpec] { screenshots == nil ? Self.specs : Self.specs + [Self.screenshotSpec] }

    static let screenshotSpec = AIToolSpec(name: "view_screenshot",
        description: "The screenshot saved closest to a moment, as an image. Use only when the screen text is not enough (charts, pictures, layout, what something looked like). Each image costs far more than text, so look at few.",
        parameters: schema(["time": timeArg("The moment to look at.")], required: ["time"]))

    /// A saved screenshot as a JPEG for the AI: at most 1568 pixels on the long side, which is what
    /// the providers shrink images to anyway.
    static func jpegForAI(_ path: String) -> Data? {
        guard let img = ImageTools.load(URL(fileURLWithPath: path)) else { return nil }
        let scale = min(1, 1568 / CGFloat(max(img.width, img.height)))
        return ImageTools.jpegData(scale < 1 ? ImageTools.scaled(img, by: scale) : img, quality: 0.75)
    }

    /// The arguments as the model sent them, for the chat window.
    static func arguments(_ call: AIToolCall) -> String {
        let data = try? JSONSerialization.data(withJSONObject: call.args, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        return call.name + " " + (data.flatMap { String(data: $0, encoding: .utf8) } ?? "{}")
    }

    /// Runs a lookup. Only view_screenshot returns an image.
    func execute(_ call: AIToolCall) -> (text: String, image: Data?) {
        guard call.name == "view_screenshot" else { return (run(call), nil) }
        guard let screenshots else { return ("Error: the user has not allowed screenshots. Use the text tools.", nil) }
        guard let t = AITime.parse(call.args["time"]) else { return ("Error: time is required, e.g. '2026-10-18 14:30'.", nil) }
        guard let f = db.frame(near: t) else { return ("No screenshot was saved near that time.", nil) }
        if hides(app: f.appName, window: f.windowName, url: f.browserUrl) {
            return ("That moment is from a blocked app or site, so its screenshot can't be shown.", nil)
        }
        var what = "Screenshot from \(AITime.label(f.timestamp)) \(f.appName)"
        if !f.windowName.isEmpty { what += " — “\(f.windowName)”" }
        what += " (link: \(AITime.link(f.timestamp)))"
        guard let image = screenshots(f) else { return ("\(what): the image file is missing (it may have been deleted).", nil) }
        return (what + ". The image is attached.", image)
    }

    /// Most characters one tool answer may send to the provider.
    static let resultLimit = 14_000

    static let specs: [AIToolSpec] = [
        AIToolSpec(name: "activity_summary",
                   description: "Time spent per app, window and website between two times, plus a timeline of what was in front. Use first for questions like 'what did I work on today'.",
                   parameters: schema(["start": timeArg("Start time. Default: start of today."),
                                       "end": timeArg("End time. Default: now.")])),
        AIToolSpec(name: "search_screen",
                   description: "Full-text search over text that was on screen (read from screenshots), app names and window titles. Returns matching moments with a snippet.",
                   parameters: schema(["query": ["type": "string", "description": "Words to look for. Every word must match (prefix match)."],
                                       "start": timeArg("Only after this time (optional)."),
                                       "end": timeArg("Only before this time (optional)."),
                                       "app": ["type": "string", "description": "Only this app, e.g. 'Safari' (optional)."],
                                       "limit": ["type": "integer", "description": "Results to return, up to 30. Default 15."]],
                                      required: ["query"])),
        AIToolSpec(name: "search_audio",
                   description: "Full-text search over transcripts of the microphone (the user) and system audio (calls, videos). Leave query empty to list everything said in a time range.",
                   parameters: schema(["query": ["type": "string", "description": "Words to look for (optional)."],
                                       "start": timeArg("Only after this time (optional)."),
                                       "end": timeArg("Only before this time (optional)."),
                                       "limit": ["type": "integer", "description": "Results to return, up to 60. Default 25."]])),
        AIToolSpec(name: "get_screen_text",
                   description: "The text that was on screen during a short time range (best under 30 minutes), sampled evenly and trimmed. Use to read details after a summary or search.",
                   parameters: schema(["start": timeArg("Start time."), "end": timeArg("End time."),
                                       "app": ["type": "string", "description": "Only this app (optional)."]],
                                      required: ["start", "end"])),
        AIToolSpec(name: "list_meetings",
                   description: "Meetings and calls found on this computer (Zoom, Teams, Google Meet, Webex, FaceTime, Slack huddles) between two times, with their summary, decisions and action items when they have notes. Use first for questions about meetings or calls, like 'what did we decide in my 2pm call'. For exactly what was said, use search_audio over the meeting's time range.",
                   parameters: schema(["start": timeArg("Start time. Default: 7 days ago."),
                                       "end": timeArg("End time. Default: now.")])),
    ]

    private static func schema(_ props: [String: Any], required: [String] = []) -> [String: Any] {
        ["type": "object", "properties": props, "required": required]
    }

    private static func timeArg(_ d: String) -> [String: Any] {
        ["type": "string", "description": d + " Local time like '2026-10-18 14:30', or 'today', 'yesterday', '2h ago'."]
    }

    /// Whether web addresses in this lookup's answer may open from Ask AI's reply. Not for meeting
    /// notes: a model wrote them from what other people said on a call.
    static func suppliesLinks(_ tool: String) -> Bool { tool != "list_meetings" }

    /// A short line for the chat window: what was looked up.
    static func describe(_ call: AIToolCall) -> String {
        let a = call.args
        let range = [a["start"] as? String, a["end"] as? String].compactMap { $0 }.joined(separator: " to ")
        let when = range.isEmpty ? "" : " (\(range))"
        switch call.name {
        case "activity_summary": return "Summarized activity" + (range.isEmpty ? " today" : when)
        case "search_screen": return "Searched screen for “\((a["query"] as? String) ?? "")”" + when
        case "search_audio":
            let q = (a["query"] as? String) ?? ""
            return (q.isEmpty ? "Read transcripts" : "Searched audio for “\(q)”") + when
        case "get_screen_text": return "Read screen text" + when
        case "list_meetings": return "Listed meetings" + (range.isEmpty ? " from the last 7 days" : when)
        case "view_screenshot": return "Looked at the screenshot from \((a["time"] as? String) ?? "")"
        default: return call.name
        }
    }

    func run(_ call: AIToolCall) -> String {
        let a = call.args
        let out: String
        switch call.name {
        case "activity_summary":
            var start = AITime.parse(a["start"]) ?? Calendar.current.startOfDay(for: Date())
            let end = AITime.parse(a["end"]) ?? Date()
            var note = ""
            if let days = maxSummaryDays, end.timeIntervalSince(start) > Double(days) * 86400 {
                start = end.addingTimeInterval(-Double(days) * 86400)
                note = "Only the last \(days) days of that range are summarized. Ask again with an earlier end for older days.\n"
            }
            // Time in a blocked app still counts, as on the timeline, but not its name, window titles or addresses.
            let rows = db.activityRows(from: start, to: end).map { r in
                let app = blocklist.blocksApp(bundleID: nil, name: r.app) ? AITools.blockedAppLabel : r.app
                return hides(app: r.app, window: r.window, url: r.url) ? ActivityRow(time: r.time, app: app, window: "", url: "")
                    : ActivityRow(time: r.time, app: app, window: r.window, url: r.url)
            }
            out = note + AIActivity.summary(rows: rows, start: start, end: end)
        case "search_screen":
            out = searchScreen(a)
        case "search_audio":
            out = searchAudio(a)
        case "list_meetings":
            out = listMeetings(a)
        case "get_screen_text":
            guard let start = AITime.parse(a["start"]), let end = AITime.parse(a["end"]) else {
                return "Error: start and end are required, e.g. '2026-10-18 14:00'."
            }
            out = screenText(from: start, to: end, app: a["app"] as? String)
        default:
            out = "Error: unknown tool \(call.name)."
        }
        return AITools.cap(out)
    }

    /// True for a frame the block list covers now, by app name, private-window title, blocked site,
    /// or a blocked word in the title or web address.
    func hides(app: String, window: String, url: String) -> Bool {
        blocklist.blocksFrame(appName: app, windowName: window, url: url) || Blocklist.hasKeyword(url, blocklist.keywords)
    }

    /// True for an app on the block list. The journal leaves such apps out of its lists entirely.
    func hidesApp(_ app: String) -> Bool { blocklist.blocksApp(bundleID: nil, name: app) }

    static func cap(_ s: String, _ limit: Int = resultLimit) -> String {
        s.count <= limit ? s : String(s.prefix(limit)) + "\n[cut to fit; narrow the time range or search for more]"
    }

    private func searchScreen(_ a: [String: Any]) -> String {
        let q = ((a["query"] as? String) ?? "").trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return "Error: query is required." }
        let limit = max(1, min(30, AITools.int(a["limit"]) ?? 15))
        var s = SearchQuery(q: q, appName: a["app"] as? String)
        s.startTime = AITime.parse(a["start"]).map { Time.iso.string(from: $0) }
        s.endTime = AITime.parse(a["end"]).map { Time.iso.string(from: $0) }
        s.limit = limit * 4
        let (rows, total) = db.search(s)
        var seen = Set<String>(), lines: [String] = []
        for f in rows where !hides(app: f.appName, window: f.windowName, url: f.browserUrl) {
            let snip = AITools.snippet(f.text, query: q)
            // While typing, many frames in a row show the same thing.
            guard seen.insert("\(f.appName)|\(f.windowName)|\(snip.prefix(80))").inserted else { continue }
            var line = "- \(AITime.label(f.timestamp)) \(f.appName)"
            if !f.windowName.isEmpty { line += " — “\(f.windowName)”" }
            if !f.browserUrl.isEmpty { line += " <\(f.browserUrl)>" }
            line += " (link: \(AITime.link(f.timestamp)))\n  \(snip)"
            lines.append(line)
            if lines.count >= limit { break }
        }
        if lines.isEmpty { return "No screen text matches “\(q)” in that range." }
        return "\(total) matching frames; newest first, near-duplicates skipped:\n" + lines.joined(separator: "\n")
    }

    private func searchAudio(_ a: [String: Any]) -> String {
        let q = ((a["query"] as? String) ?? "").trimmingCharacters(in: .whitespaces)
        let limit = max(1, min(60, AITools.int(a["limit"]) ?? 25))
        var s = SearchQuery(q: q.isEmpty ? nil : q)
        s.startTime = AITime.parse(a["start"]).map { Time.iso.string(from: $0) }
        s.endTime = AITime.parse(a["end"]).map { Time.iso.string(from: $0) }
        let (rows, total) = db.searchAudio(s, fetch: limit)
        if rows.isEmpty { return q.isEmpty ? "Nothing was transcribed in that range." : "No transcript matches “\(q)”." }
        let lines = rows.reversed().map { t in
            "- \(AITime.label(t.timestamp)) \(t.isInput ? "Microphone (the user)" : "System audio") (link: \(AITime.link(t.timestamp))): \(t.text.trimmingCharacters(in: .whitespacesAndNewlines))"
        }
        return "\(total) transcript segments, showing \(rows.count) (oldest first):\n" + lines.joined(separator: "\n")
    }

    private func listMeetings(_ a: [String: Any]) -> String {
        let start = AITime.parse(a["start"]) ?? Date().addingTimeInterval(-7 * 86_400)
        let end = AITime.parse(a["end"]) ?? Date()
        // Calls the user said Not This Time to are not offered.
        let rows = db.meetings(overlapping: start, to: end).filter { !$0.skipped && !MeetingDetector.hides(blocklist, $0) }
        if rows.isEmpty { return "No meetings were found between \(AITime.label(start)) and \(AITime.label(end))." }
        let shown = rows.suffix(30)
        var out = "\(rows.count) meetings, showing \(shown.count) (oldest first, local time):\n"
        func section(_ title: String, _ text: String) -> String {
            "  \(title): " + (text.isEmpty ? "none" : "\n" + text.components(separatedBy: "\n").map { "    " + $0.trimmingCharacters(in: .whitespaces) }.joined(separator: "\n")) + "\n"
        }
        for m in shown {
            out += "\n- \(AITime.label(m.start))–\(MeetingTranscript.clock(m.end)) \(m.app) — “\(m.name)” (link: openrewind://t/\(Int(m.start.timeIntervalSince1970)))"
            if m.isLive { out += " — still going on" }
            out += "\n"
            if m.hasSummary {
                out += section("Summary", m.summary) + section("Decisions", m.decisions) + section("Action items", m.actionItems)
            } else {
                out += "  No notes yet. To read what was said, call search_audio with start '\(AITime.label(m.start))' and end '\(AITime.label(m.end.addingTimeInterval(60)))'.\n"
            }
        }
        return out
    }

    private func screenText(from start: Date, to end: Date, app: String?) -> String {
        var s = SearchQuery(appName: app)
        s.startTime = Time.iso.string(from: start)
        s.endTime = Time.iso.string(from: end)
        s.limit = 400
        let rows = db.search(s).rows.reversed().filter { !$0.text.isEmpty && !hides(app: $0.appName, window: $0.windowName, url: $0.browserUrl) }
        // Skip frames whose text did not change.
        var unique: [Frame] = []
        for f in rows where unique.last?.text != f.text { unique.append(f) }
        if unique.isEmpty { return "No screen text was recorded in that range." }
        let picks = AITools.evenly(unique, max: 12)
        let each = max(400, (AITools.resultLimit - 400) / picks.count)
        var out = "\(unique.count) different screens in this range; showing \(picks.count):\n"
        for f in picks {
            var head = "\n## \(AITime.label(f.timestamp)) \(f.appName)"
            if !f.windowName.isEmpty { head += " — “\(f.windowName)”" }
            if !f.browserUrl.isEmpty { head += " <\(f.browserUrl)>" }
            let text = AITools.squash(f.text)
            out += head + " (link: \(AITime.link(f.timestamp)))\n" + (text.count > each ? String(text.prefix(each)) + "…" : text) + "\n"
        }
        return out
    }

    // MARK: helpers

    static func int(_ v: Any?) -> Int? {
        if let i = v as? Int { return i }
        if let d = v as? Double { return Int(d) }
        if let s = v as? String { return Int(s) }
        return nil
    }

    static func evenly<T>(_ items: [T], max n: Int) -> [T] {
        guard items.count > n, n > 1 else { return items }
        return (0..<n).map { items[$0 * (items.count - 1) / (n - 1)] }
    }

    /// OCR text has lots of line breaks and spacing; one space is enough for the model.
    static func squash(_ s: String) -> String {
        s.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
    }

    /// About 300 characters around the first query word found.
    static func snippet(_ text: String, query: String) -> String {
        let flat = squash(text)
        let lower = flat.lowercased()
        let words = query.lowercased().split(separator: " ").map(String.init)
        guard let hit = words.lazy.compactMap({ lower.range(of: $0) }).first else { return String(flat.prefix(300)) }
        let at = lower.distance(from: lower.startIndex, to: hit.lowerBound)
        let chars = Array(flat)
        let from = min(max(0, at - 140), chars.count)
        let to = min(chars.count, from + 300)
        return (from > 0 ? "…" : "") + String(chars[from..<to]) + (to < chars.count ? "…" : "")
    }
}

/// Times in tool answers are local and short; links open that moment in the timeline.
enum AITime {
    private static let labelFormat: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm"
        return f
    }()

    private static let localFormats: [DateFormatter] = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm"].map {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = $0
        return f
    }

    static func label(_ iso: String) -> String { Time.iso.date(from: iso).map { label($0) } ?? iso }
    static func label(_ d: Date) -> String { labelFormat.string(from: d) }

    static func link(_ iso: String) -> String {
        "openrewind://t/\(Int((Time.iso.date(from: iso) ?? Date()).timeIntervalSince1970))"
    }

    /// The moment in an openrewind://t/<seconds> link.
    static func date(fromLink url: URL) -> Date? {
        guard url.scheme == "openrewind", let secs = Double(url.lastPathComponent) else { return nil }
        return Date(timeIntervalSince1970: secs)
    }

    /// Local times the model writes ("2026-10-18 14:30"), plus everything `Time.parse` takes.
    static func parse(_ v: Any?) -> Date? {
        guard let raw = (v as? String)?.trimmingCharacters(in: .whitespaces), !raw.isEmpty else { return nil }
        for f in localFormats { if let d = f.date(from: raw) { return d } }
        return Time.parse(raw).flatMap { Time.iso.date(from: $0) }
    }
}

/// One row per saved frame, for the activity summary.
struct ActivityRow {
    var time: Date
    var app: String
    var window: String
    var url: String
    /// Saved while a blocked window or site was in front (Insights).
    var blocked = false
}

/// Turns frames into "time per app" and a timeline of what was in front.
enum AIActivity {
    /// A frame counts until the next one, but never longer than this: the recorder saves at least
    /// once a minute while the screen is on, so a longer gap means the Mac was idle or off.
    static let maxGap: TimeInterval = 120

    static func summary(rows: [ActivityRow], start: Date, end: Date) -> String {
        guard !rows.isEmpty else { return "Nothing was recorded between \(AITime.label(start)) and \(AITime.label(end))." }
        var apps: [String: TimeInterval] = [:], windows: [String: [String: TimeInterval]] = [:], sites: [String: TimeInterval] = [:]
        struct Block { var start: Date; var end: Date; var app: String; var window: String; var secs: TimeInterval }
        var blocks: [Block] = []
        var total: TimeInterval = 0
        for (i, r) in rows.enumerated() {
            let next = i + 1 < rows.count ? rows[i + 1].time : min(end, r.time.addingTimeInterval(30))
            let secs = max(0, min(maxGap, next.timeIntervalSince(r.time)))
            total += secs
            apps[r.app, default: 0] += secs
            if !r.window.isEmpty { windows[r.app, default: [:]][r.window, default: 0] += secs }
            if let host = URL(string: r.url)?.host, !host.isEmpty {
                sites[host.hasPrefix("www.") ? String(host.dropFirst(4)) : host, default: 0] += secs
            }
            if var b = blocks.last, b.app == r.app, b.window == r.window, r.time.timeIntervalSince(b.end) <= maxGap {
                b.end = r.time.addingTimeInterval(secs); b.secs += secs
                blocks[blocks.count - 1] = b
            } else {
                blocks.append(Block(start: r.time, end: r.time.addingTimeInterval(secs), app: r.app, window: r.window, secs: secs))
            }
        }

        var out = "Activity from \(AITime.label(start)) to \(AITime.label(end)) (local time). About \(duration(total)) recorded.\n\nTime by app:\n"
        for (app, secs) in apps.sorted(by: { $0.value > $1.value }).prefix(15) where secs >= 60 {
            out += "- \(app.isEmpty ? "(unknown)" : app): \(duration(secs))\n"
            for (w, ws) in (windows[app] ?? [:]).sorted(by: { $0.value > $1.value }).prefix(5) where ws >= 60 {
                out += "  - “\(w.prefix(120))”: \(duration(ws))\n"
            }
        }
        let topSites = sites.sorted(by: { $0.value > $1.value }).prefix(15).filter { $0.value >= 60 }
        if !topSites.isEmpty {
            out += "\nWebsites:\n" + topSites.map { "- \($0.key): \(duration($0.value))" }.joined(separator: "\n") + "\n"
        }
        // Short switches are noise; raise the bar until the timeline fits.
        var minSecs: TimeInterval = 120
        var shown = blocks.filter { $0.secs >= minSecs }
        while shown.count > 70 { minSecs *= 2; shown = blocks.filter { $0.secs >= minSecs } }
        out += "\nTimeline (stretches of \(Int(minSecs / 60))+ minutes):\n"
        let clock = DateFormatter()
        clock.dateFormat = "HH:mm"
        for b in shown {
            out += "- \(clock.string(from: b.start))–\(clock.string(from: b.end)) \(b.app)"
            if !b.window.isEmpty { out += " — “\(b.window.prefix(100))”" }
            out += " (link: openrewind://t/\(Int(b.start.timeIntervalSince1970)))\n"
        }
        return out
    }

    static func duration(_ secs: TimeInterval) -> String {
        let m = Int(secs / 60)
        return m >= 60 ? "\(m / 60)h \(m % 60)m" : "\(max(1, m))m"
    }
}

extension Database {
    /// Light rows (no text) for the activity summary, oldest first.
    func activityRows(from start: Date, to end: Date) -> [ActivityRow] {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        let sql = "SELECT timestamp, app_name, window_name, browser_url FROM frames WHERE timestamp >= ? AND timestamp <= ? ORDER BY timestamp ASC"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(st) }
        bind(st, 1, Time.iso.string(from: start))
        bind(st, 2, Time.iso.string(from: end))
        var out: [ActivityRow] = []
        while sqlite3_step(st) == SQLITE_ROW {
            guard let t = Time.iso.date(from: col(st, 0)) else { continue }
            out.append(ActivityRow(time: t, app: col(st, 1), window: col(st, 2), url: col(st, 3)))
        }
        return out
    }
}
