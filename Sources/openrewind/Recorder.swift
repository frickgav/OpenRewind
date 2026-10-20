import AppKit
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

/// When a new screenshot is close enough to the last stored one to skip it.
enum FrameDedup {
    /// Choices for how often to store a frame while only a few words change (typing, a ticking clock).
    /// 2 s is every capture, the default.
    static let gapChoices = [2, 5, 10, 30]

    /// Words added plus words removed between two texts, ignoring order.
    static func wordsChanged(_ a: String, _ b: String) -> Int {
        var counts: [Substring: Int] = [:]
        for w in words(a) { counts[w, default: 0] += 1 }
        for w in words(b) { counts[w, default: 0] -= 1 }
        return counts.values.reduce(0) { $0 + abs($1) }
    }

    /// True when the texts differ by at most 4 words, or 2% of the longer one.
    static func isMinorChange(_ a: String, _ b: String) -> Bool {
        let longer = max(words(a).count, words(b).count)
        return wordsChanged(a, b) <= max(4, longer / 50)
    }

    private static func words(_ s: String) -> [Substring] { s.split(whereSeparator: { $0.isWhitespace }) }
}

final class Recorder {
    /// UserDefaults key for the screenshot size picked in the menu bar (1.0 = full resolution).
    static let scaleKey = "snapshotScale"

    /// The menu bar choice, or the config value when none was picked. Read for every screenshot, so a change applies at once.
    static func snapshotScale(default d: CGFloat) -> CGFloat {
        let v = UserDefaults.standard.double(forKey: scaleKey)
        return v > 0 ? CGFloat(min(v, 1)) : d
    }

    let config: Config
    let db: Database
    private var lastThumb: [UInt8]?
    private var lastText = ""
    private var lastKey = ""
    private var lastWrite = Date.distantPast
    /// Seconds between stored frames while only a few words change. At or below the capture interval: every capture.
    var minorChangeGap: TimeInterval = 2
    private let excluded: Set<String>
    /// Browser windows showing a blocked site (see Blocklist.sites).
    private let siteWindows = SiteWindows()

    init(config: Config, db: Database) {
        self.config = config
        self.db = db
        // Never recorded at all: the lock screen and screen saver. Apps the user blocks are
        // blurred instead (see Blocklist and Redaction).
        // Nor ourselves: while the timeline is open, frames would only show the desktop behind it.
        excluded = ["loginwindow", "screensaverengine", "openrewind"]
    }

    func run() async {
        log("recording every \(config.interval)s → \(config.home.path)")
        var warned = false
        while !Task.isCancelled {
            // No permission yet: wait quietly instead of failing every 2 seconds.
            if !Capture.hasScreenPermission {
                if !warned { log("waiting for Screen Recording permission (System Settings → Privacy & Security)"); warned = true }
                try? await Task.sleep(nanoseconds: 5_000_000_000)
                continue
            }
            if warned { log("screen permission granted"); warned = false }
            let started = Date()
            do { try await tick() } catch { log("capture error: \(error.localizedDescription)") }
            let wait = max(0.2, config.interval - Date().timeIntervalSince(started))
            try? await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000))
        }
    }

    func tick() async throws {
        let win = Capture.activeWindow()
        if excluded.contains(win.appName.lowercased()) { return }
        let list = BlocklistStore.shared.current
        let app = NSRunningApplication(processIdentifier: win.pid)
        // Blocked sites: which tab each browser window shows, read just before the screenshot.
        // Without Accessibility no address can be read (Settings says so), so nothing is hidden by site.
        if !list.sites.isEmpty, Accessibility.isTrusted {
            readSites(front: win.windowID)
            if BrowserTabs.isBrowser(app?.bundleIdentifier) {
                siteWindows.saw(win.windowID, title: win.windowName, url: Accessibility.browserURL(pid: win.pid))
            }
        }
        let windows = Redaction.windows(list, sites: siteWindows)
        let shot = try await Capture.capture(excluding: Set(windows.filter(\.blocked).map(\.id)))
        guard let image = Redaction.apply(shot.image, bounds: shot.bounds, windows: windows) else {
            log("could not blur blocked apps; skipping this frame"); return
        }
        // A blocked window in front: keep the app name for the timeline, but not its title or its text.
        if list.blocksWindow(bundleID: app?.bundleIdentifier, appName: win.appName, path: app?.bundleURL?.path, title: win.windowName)
            || siteWindows.blocks(win.windowID, list) {
            _ = try ingest(image: image, window: ActiveWindow(appName: win.appName, windowName: "", pid: win.pid, windowID: win.windowID), pid: nil,
                           blocked: true, display: shot.display)
        } else {
            _ = try ingest(image: image, window: win, pid: win.pid, display: shot.display)
        }
    }

    /// Reads the address of each browser window that is new or whose title changed (a tab switch, a new
    /// page), so a blocked page is hidden even when its window is not in front. The window in front is read
    /// by tick() every time. At most a few changed windows are read per screenshot; new ones always are, so
    /// none is shown before its address is known.
    private func readSites(front: CGWindowID) {
        guard let info = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return }
        var browser: [pid_t: Bool] = [:]
        var onScreen = Set<CGWindowID>()
        var changed = 0
        for w in info where (w[kCGWindowLayer as String] as? Int) == 0 {
            guard let pid = w[kCGWindowOwnerPID as String] as? pid_t, let id = w[kCGWindowNumber as String] as? CGWindowID else { continue }
            if browser[pid] == nil { browser[pid] = BrowserTabs.isBrowser(NSRunningApplication(processIdentifier: pid)?.bundleIdentifier) }
            guard browser[pid] == true else { continue }
            onScreen.insert(id)
            // Untitled windows are a browser's popups and bubbles, and can't be told apart to read them.
            let title = w[kCGWindowName as String] as? String ?? ""
            guard id != front, !title.isEmpty, siteWindows.needsRead(id, title: title) else { continue }
            if siteWindows.knows(id) {
                guard changed < 3 else { continue }
                changed += 1
            }
            siteWindows.saw(id, title: title, url: Accessibility.browserURL(pid: pid, title: title))
        }
        siteWindows.prune(keeping: onScreen)
    }

    /// Dedup, extract text, store snapshot, insert row. Returns the new frame id, or nil if skipped.
    /// `blocked`: a blocked window or site is in front (pass no pid, so its text is not read); the frame
    /// is marked so Insights leaves it out. `display` names the screen (older frames of one screen go
    /// into one video, see VideoCompactor).
    func ingest(image: CGImage, window: ActiveWindow, pid: pid_t?, blocked: Bool = false, display: String = "") throws -> Int64? {
        let hash = ImageTools.dHash(image)
        let thumb = ImageTools.thumbnail(image)
        let key = window.appName + "\u{1F}" + window.windowName
        let heartbeatDue = Date().timeIntervalSince(lastWrite) >= 60
        // Same window and (almost) no pixels changed: skip. Still store one frame per minute as a heartbeat.
        if key == lastKey, let lt = lastThumb, ImageTools.changedPixels(lt, thumb) < config.minChangedPixels, !heartbeatDue {
            return nil
        }

        var text = ""
        var source = "ocr"
        var url = ""
        if config.useAccessibility, let pid, pid > 0 {
            let w = Accessibility.focusedWindow(pid: pid)
            if let ax = w.text { text = ax; source = "accessibility" }
            url = w.url ?? ""
            // The tab changed to a blocked site after the check in tick(), so the screenshot is not blurred: drop it.
            if BlocklistStore.shared.current.blocksURL(url) {
                siteWindows.saw(window.windowID, title: window.windowName, url: url)
                return nil
            }
        }
        if text.isEmpty { text = OCR.recognize(image) }
        // Pixels moved (cursor blink, animation) but the text is the same: nothing new to search.
        if key == lastKey, text == lastText, !heartbeatDue {
            lastThumb = thumb
            return nil
        }
        // Only a few words changed (typing, a clock) and the user chose a longer gap: wait for it.
        // lastThumb stays on the stored frame, so the screen it settles on is still saved once the gap is up.
        if key == lastKey, !heartbeatDue, minorChangeGap > config.interval,
           Date().timeIntervalSince(lastWrite) < minorChangeGap, FrameDedup.isMinorChange(lastText, text) {
            return nil
        }

        let ts = Time.now()
        let dayDir = config.dataDir.appendingPathComponent(String(ts.prefix(10)))
        try FileManager.default.createDirectory(at: dayDir, withIntermediateDirectories: true)
        let file = try ImageTools.writeSnapshot(ImageTools.scaled(image, by: Recorder.snapshotScale(default: config.snapshotScale)),
                                                to: dayDir.appendingPathComponent(ts.replacingOccurrences(of: ":", with: "-")))

        let id = try db.insert(Frame(id: 0, timestamp: ts, appName: window.appName, windowName: window.windowName,
                                     focused: true, snapshotPath: file.path, text: text, textSource: source,
                                     hash: Int64(bitPattern: hash), browserUrl: url, blocked: blocked, display: display))
        lastThumb = thumb; lastText = text; lastKey = key; lastWrite = Date()
        log("frame \(id) \(window.appName) — \(text.count) chars via \(source)")
        return id
    }
}
