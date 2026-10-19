import AppKit
import AVFoundation
import Combine
import ImageIO

/// State behind the timeline overlay: which moment is shown, its screenshot, and audio at that moment.
final class TimelineModel: ObservableObject {
    let db: Database
    /// The screenshots shown: all of them, or only those of the filtered app.
    @Published private(set) var frames: [TimelineFrame] = []
    /// Every screenshot loaded, whatever the app filter.
    private var allFrames: [TimelineFrame] = []
    /// Only show moments when this app was in front (nil = all apps).
    @Published private(set) var appFilter: String?
    @Published private(set) var audio: [TimelineAudio] = []
    /// Recorded stretches, with long breaks between them (see TimelineAxis).
    private(set) var sessions: [TimelineAxis.Session] = []
    @Published private(set) var index = 0
    @Published private(set) var image: NSImage?
    @Published private(set) var caption: String?
    @Published private(set) var playingChunk: Int64?
    /// Seconds of history shown across the timeline bar.
    @Published var span: TimeInterval = 15 * 60
    /// The exact time the playhead is on. Follows audio while it plays.
    @Published private(set) var playhead = Date()

    // Search (see TimelineSearch.swift).
    @Published var query = ""
    @Published var hits: [SearchHit] = []
    @Published var selectedHit = 0
    /// True while the result list is open under the search box.
    @Published var showResults = false
    /// Bumped to ask the view to focus the search box.
    @Published var focusSearchToken = 0
    var searchWork: DispatchWorkItem?
    /// Set by the overlay: gives the keyboard back to the timeline (so space plays audio again).
    var leaveSearchBox: (() -> Void)?
    /// Set by the overlay: closes it without giving focus back to the app that was in front.
    var closeForOpen: (() -> Void)?
    /// Short message shown for a moment at the top (for example, why O did nothing).
    @Published var notice: String?
    private var noticeWork: DispatchWorkItem?
    /// True while the keyboard shortcut card is open.
    @Published var showHelp = false
    /// The Ask AI chat beside the timeline.
    @Published var showAI = false
    /// The guided tour's step while it runs (see Tutorial.swift).
    @Published var tutorialStep: Int?
    /// Where the searched words are on the shown screenshot, as fractions of its size (top-left origin).
    @Published var highlights: [CGRect] = []
    var highlightWork: DispatchWorkItem?
    var highlightCache: [String: [CGRect]] = [:]
    var highlightOrder: [String] = []
    private var thumbCache: [Int64: NSImage] = [:]
    private var thumbOrder: [Int64] = []

    private var player: AVAudioPlayer?
    private var playTimer: Timer?
    private var imageCache: [Int64: NSImage] = [:]
    private var cacheOrder: [Int64] = []
    var loadedSince = Date()
    /// "Now" as of the last reload. The bar uses this instead of the clock, so it holds still while open.
    private(set) var now = Date()

    init(db: Database) { self.db = db }

    var current: TimelineFrame? { frames.indices.contains(index) ? frames[index] : nil }

    /// True when the newest screenshot is shown.
    var isLive: Bool { frames.isEmpty || index == frames.count - 1 }

    /// The frame shown at `date` and a small picture of it, for the hover preview on the bar.
    /// Nil when nothing was recorded within a minute before `date`.
    func preview(at date: Date) -> (frame: TimelineFrame, image: NSImage?)? {
        guard !frames.isEmpty, date >= frames[0].time else { return nil }
        let f = frames[frameIndex(at: date)]
        guard date <= (f.end == nil ? f.time.addingTimeInterval(60) : AppFilter.until(f)) else { return nil }
        if let img = thumbCache[f.id] { return (f, img) }
        let opts = [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: 480,
                     kCGImageSourceCreateThumbnailWithTransform: true] as CFDictionary
        guard let data = RecordingFile.data(f.path), let src = CGImageSourceCreateWithData(data as CFData, nil),
              let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, opts) else { return (f, nil) }
        let img = NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
        thumbCache[f.id] = img
        thumbOrder.append(f.id)
        if thumbOrder.count > 64 { thumbCache.removeValue(forKey: thumbOrder.removeFirst()) }
        return (f, img)
    }

    /// What "Open" brings back for the shown screenshot.
    enum OpenTarget {
        case web(URL)       // a browser tab
        case file(URL)      // a document the app had open
        case window         // the app, and its window with the same title if it is still open
    }

    var currentTarget: OpenTarget? {
        guard let f = current, !f.appName.isEmpty, f.appName.lowercased() != "openrewind" else { return nil }
        if let u = URL(string: f.url), let scheme = u.scheme?.lowercased() {
            if ["http", "https"].contains(scheme) { return .web(u) }
            if u.isFileURL, FileManager.default.fileExists(atPath: u.path) { return .file(u) }
        }
        return appURL(f.appName) == nil ? nil : .window
    }

    private func appURL(_ name: String) -> URL? {
        NSWorkspace.shared.runningApplications.first { $0.localizedName == name }?.bundleURL
            ?? ["/Applications", "/System/Applications", "/System/Applications/Utilities",
                "/Applications/Utilities", NSHomeDirectory() + "/Applications"]
                .map { URL(fileURLWithPath: "\($0)/\(name).app") }
                .first { FileManager.default.fileExists(atPath: $0.path) }
    }

    /// Go back to this moment: the browser tab, the document, or the app window.
    func openCurrent() {
        guard let target = currentTarget, let f = current else {
            show(notice: "Nothing to open for this moment")
            return
        }
        closeForOpen?()
        let app = appURL(f.appName)
        let cfg = NSWorkspace.OpenConfiguration()
        switch target {
        case .web(let url), .file(let url):
            if let app {
                NSWorkspace.shared.open([url], withApplicationAt: app, configuration: cfg)
            } else {
                NSWorkspace.shared.open(url)
            }
        case .window:
            guard let app else { return }
            let title = f.windowName
            // A browser moment saved without its address (Accessibility was off): find the tab by its title.
            // Wait until the timeline is gone, since macOS may ask first whether OpenRewind may control the browser.
            if let bundleId = Bundle(url: app)?.bundleIdentifier, BrowserTabs.canSelect(bundleId) {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in
                    if !BrowserTabs.select(title: title, bundleId: bundleId) { self?.openWindow(app: app, title: title) }
                }
                return
            }
            openWindow(app: app, title: title)
        }
    }

    /// Open the app and bring its window with this title to the front (needs Accessibility).
    private func openWindow(app: URL, title: String) {
        if !Accessibility.isTrusted { log("Accessibility is off: opening \(app.lastPathComponent) without finding the window") }
        NSWorkspace.shared.openApplication(at: app, configuration: NSWorkspace.OpenConfiguration()) { running, _ in
            guard let pid = running?.processIdentifier else { return }
            // A freshly launched app needs a moment before its windows exist.
            for delay in [0.0, 0.6, 1.5] {
                DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                    Accessibility.raiseWindow(pid: pid, title: title)
                }
            }
        }
    }

    func show(notice text: String) {
        notice = text
        noticeWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.notice = nil }
        noticeWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.5, execute: work)
    }

    /// Apps in the loaded history, the one in front longest first (for the app filter menu).
    private(set) var apps: [String] = []

    /// The newest screenshot of any app.
    var newest: TimelineFrame? { allFrames.last }

    /// Show only the moments when `app` was in front, or every app (nil). Stays near the same time.
    func setAppFilter(_ app: String?) {
        guard app != appFilter else { return }
        let at = playhead
        stopAudio()
        appFilter = app
        applyFilter()
        show(frameIndex(at: at))
        if !query.trimmingCharacters(in: .whitespaces).isEmpty { runSearch(query.trimmingCharacters(in: .whitespaces)) }
    }

    /// A: show only the app in this screenshot, or every app again.
    func toggleAppFilter() {
        if appFilter != nil { setAppFilter(nil); return }
        guard let app = current?.appName, !app.isEmpty else { return }
        setAppFilter(app)
        show(notice: "Only showing \(app). Press A for all apps.")
    }

    /// Frames and the bar's stretches for the current app filter. While filtering, time in
    /// other apps becomes breaks, and audio does not keep those stretches open.
    private func applyFilter() {
        frames = AppFilter.apply(allFrames, app: appFilter)
        sessions = appFilter == nil
            ? TimelineAxis.sessions(frames: frames, audio: audio, now: now)
            : TimelineAxis.sessions(frames: frames, audio: [], now: now, gap: AppFilter.gapThreshold)
    }

    /// Audio chunks that cover the playhead (mic and system audio can overlap).
    var audioHere: [TimelineAudio] {
        audio.filter { $0.start <= playhead && playhead < $0.end }
    }

    /// `keepApp`: keep the app filter (opening an older search result); a fresh open shows every app.
    func reload(hours: Double = 24, keepApp: Bool = false) {
        stopAudio()
        now = Date()
        loadedSince = now.addingTimeInterval(-hours * 3600)
        allFrames = db.timelineFrames(since: loadedSince)
        apps = AppFilter.apps(allFrames)
        audio = db.timelineAudio(since: loadedSince)
        if !keepApp || !allFrames.contains(where: { $0.appName == appFilter }) { appFilter = nil }
        applyFilter()
        show(frames.count - 1)
    }

    /// Pull in older history when the user scrolls to the start.
    func loadOlder(hours: Double = 24) {
        let since = loadedSince.addingTimeInterval(-hours * 3600)
        let older = db.timelineFrames(since: since).filter { $0.time < loadedSince }
        guard !older.isEmpty else { return }
        let keepId = current?.id
        loadedSince = since
        allFrames = older + allFrames
        apps = AppFilter.apps(allFrames)
        audio = db.timelineAudio(since: since)
        applyFilter()
        if let keepId, let i = frames.firstIndex(where: { $0.id == keepId }) { index = i }
    }

    func step(_ n: Int) {
        guard !frames.isEmpty else { return }
        if index + n < 0 { loadOlder() }
        stopAudio()
        show(index + n)
    }

    func jump(to date: Date) {
        guard !frames.isEmpty else { return }
        if date < frames[0].time { loadOlder() }
        stopAudio()
        show(frameIndex(at: date))
        playhead = max(min(date, now), frames.first?.time ?? date)
        updateCaption()
    }

    func jumpToNow() { stopAudio(); show(frames.count - 1) }

    /// Last frame at or before `date` (binary search; frames are sorted by time).
    func frameIndex(at date: Date) -> Int {
        var lo = 0, hi = frames.count - 1, best = 0
        while lo <= hi {
            let mid = (lo + hi) / 2
            if frames[mid].time <= date { best = mid; lo = mid + 1 } else { hi = mid - 1 }
        }
        return best
    }

    private func show(_ i: Int) {
        guard !frames.isEmpty else { image = nil; return }
        index = max(0, min(frames.count - 1, i))
        let f = frames[index]
        playhead = f.time
        image = loadImage(f)
        refreshHighlights()
        updateCaption()
        // Warm the cache for the next scroll step either way.
        for j in [index - 1, index + 1] where frames.indices.contains(j) { _ = loadImage(frames[j]) }
    }

    private func loadImage(_ f: TimelineFrame) -> NSImage? {
        if let img = imageCache[f.id] { return img }
        guard let data = RecordingFile.data(f.path), let img = NSImage(data: data) else { return nil }
        imageCache[f.id] = img
        cacheOrder.append(f.id)
        if cacheOrder.count > 24 { imageCache.removeValue(forKey: cacheOrder.removeFirst()) }
        return img
    }

    private func updateCaption() {
        guard let a = audioHere.first(where: { $0.id == playingChunk }) ?? audioHere.first else { caption = nil; return }
        let offset = playhead.timeIntervalSince(a.start)
        let caps = db.captions(chunkId: a.id)
        caption = caps.first(where: { $0.start <= offset + 0.5 && offset <= $0.end + 0.5 })?.text
            ?? caps.last(where: { $0.start <= offset })?.text
    }

    /// Axis for the current zoom: each break gets 6% of the visible width.
    var axis: TimelineAxis { TimelineAxis(sessions: sessions, gapV: span * 0.06) }

    // MARK: - audio

    func toggleAudio() {
        if player?.isPlaying == true { stopAudio() } else if let a = audioHere.first { play(a) }
    }

    func play(_ a: TimelineAudio) {
        stopAudio()
        do {
            let p = try AVAudioPlayer(data: RecordingFile.read(a.path))
            p.currentTime = max(0, playhead.timeIntervalSince(a.start))
            p.play()
            player = p
            playingChunk = a.id
            // While audio plays, move the playhead and the screenshot along with it, like a video.
            playTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
                guard let self, let p = self.player else { return }
                if !p.isPlaying { self.stopAudio(); return }
                let t = a.start.addingTimeInterval(p.currentTime)
                self.playhead = t
                let i = self.frameIndex(at: t)
                if i != self.index {
                    self.index = i
                    self.image = self.loadImage(self.frames[i])
                    self.refreshHighlights()
                }
                self.updateCaption()
            }
        } catch {
            log("cannot play audio: \(error.localizedDescription)")
        }
    }

    func stopAudio() {
        player?.stop()
        player = nil
        playTimer?.invalidate()
        playTimer = nil
        playingChunk = nil
    }

    // MARK: - colors

    /// Stable color per app so the timeline bar shows app switches at a glance.
    static func color(for app: String) -> NSColor {
        var h: UInt32 = 2166136261
        for b in app.utf8 { h = (h ^ UInt32(b)) &* 16777619 }
        return NSColor(hue: CGFloat(h % 360) / 360, saturation: 0.55, brightness: 0.95, alpha: 1)
    }
}
