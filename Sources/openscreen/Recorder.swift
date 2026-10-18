import Foundation
import CoreGraphics

enum Time {
    static let iso: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    static func now() -> String { iso.string(from: Date()) }

    /// Accepts ISO 8601, "30m ago" / "2h ago" / "3d ago", "today", "yesterday", or "YYYY-MM-DD".
    static func parse(_ raw: String) -> String? {
        let s = raw.trimmingCharacters(in: .whitespaces).lowercased()
        let cal = Calendar.current
        if s == "now" { return now() }
        if s == "today" { return iso.string(from: cal.startOfDay(for: Date())) }
        if s == "yesterday" {
            return iso.string(from: cal.date(byAdding: .day, value: -1, to: cal.startOfDay(for: Date()))!)
        }
        if s.hasSuffix(" ago") || s.hasSuffix("ago") {
            let body = s.replacingOccurrences(of: "ago", with: "").trimmingCharacters(in: .whitespaces)
            let unit = body.last
            if let n = Double(body.dropLast().trimmingCharacters(in: .whitespaces)), let unit {
                let secs: Double = ["s": 1, "m": 60, "h": 3600, "d": 86400, "w": 604800][String(unit)] ?? 0
                if secs > 0 { return iso.string(from: Date().addingTimeInterval(-n * secs)) }
            }
            return nil
        }
        if let d = iso.date(from: raw) { return iso.string(from: d) }
        let plain = ISO8601DateFormatter()
        if let d = plain.date(from: raw) { return iso.string(from: d) }
        let day = DateFormatter()
        day.dateFormat = "yyyy-MM-dd"
        day.timeZone = .current
        if let d = day.date(from: raw) { return iso.string(from: d) }
        return nil
    }
}

final class Recorder {
    let config: Config
    let db: Database
    private var lastHash: UInt64?
    private var lastKey = ""
    private var lastWrite = Date.distantPast
    private let excluded: Set<String>

    init(config: Config, db: Database) {
        self.config = config
        self.db = db
        let env = ProcessInfo.processInfo.environment["OPENSCREEN_EXCLUDE"]
        let list = env?.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
            ?? ["1password", "keychain access", "bitwarden", "passwords"]
        excluded = Set(list)
    }

    func run() async {
        log("recording every \(config.interval)s → \(config.home.path)")
        while !Task.isCancelled {
            let started = Date()
            do { try await tick() } catch { log("capture error: \(error.localizedDescription)") }
            let wait = max(0.2, config.interval - Date().timeIntervalSince(started))
            try? await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000))
        }
    }

    func tick() async throws {
        let win = Capture.activeWindow()
        if excluded.contains(win.appName.lowercased()) { return }
        let image = try await Capture.screenshot()
        _ = try ingest(image: image, window: win, pid: win.pid)
    }

    /// Dedup, extract text, store snapshot, insert row. Returns the new frame id, or nil if skipped.
    func ingest(image: CGImage, window: ActiveWindow, pid: pid_t?) throws -> Int64? {
        let hash = ImageTools.dHash(image)
        let key = window.appName + "\u{1F}" + window.windowName
        // Same window and nearly same pixels: skip. Still store one frame per minute as a heartbeat.
        if key == lastKey, let lh = lastHash, ImageTools.distance(lh, hash) <= config.dedupThreshold,
           Date().timeIntervalSince(lastWrite) < 60 {
            return nil
        }

        var text = ""
        var source = "ocr"
        if config.useAccessibility, let pid, pid > 0, let ax = Accessibility.focusedWindowText(pid: pid) {
            text = ax; source = "accessibility"
        }
        if text.isEmpty { text = OCR.recognize(image) }

        let ts = Time.now()
        let dayDir = config.dataDir.appendingPathComponent(String(ts.prefix(10)))
        try FileManager.default.createDirectory(at: dayDir, withIntermediateDirectories: true)
        let file = dayDir.appendingPathComponent(ts.replacingOccurrences(of: ":", with: "-") + ".jpg")
        try ImageTools.writeJPEG(ImageTools.scaled(image, by: config.snapshotScale), to: file)

        let id = try db.insert(Frame(id: 0, timestamp: ts, appName: window.appName, windowName: window.windowName,
                                     focused: true, snapshotPath: file.path, text: text, textSource: source,
                                     hash: Int64(bitPattern: hash)))
        lastHash = hash; lastKey = key; lastWrite = Date()
        log("frame \(id) \(window.appName) — \(text.count) chars via \(source)")
        return id
    }
}
