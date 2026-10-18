import AppKit
import AVFoundation
import Combine
import ImageIO

/// State behind the timeline overlay: which moment is shown, its screenshot, and audio at that moment.
final class TimelineModel: ObservableObject {
    let db: Database
    @Published private(set) var frames: [TimelineFrame] = []
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
    /// True while the keyboard shortcut card is open.
    @Published var showHelp = false
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

    init(db: Database) { self.db = db }

    var current: TimelineFrame? { frames.indices.contains(index) ? frames[index] : nil }

    /// True when the newest screenshot is shown.
    var isLive: Bool { frames.isEmpty || index == frames.count - 1 }

    /// The frame shown at `date` and a small picture of it, for the hover preview on the bar.
    /// Nil when nothing was recorded within a minute before `date`.
    func preview(at date: Date) -> (frame: TimelineFrame, image: NSImage?)? {
        guard !frames.isEmpty, date >= frames[0].time else { return nil }
        let f = frames[frameIndex(at: date)]
        guard date.timeIntervalSince(f.time) <= 60 else { return nil }
        if let img = thumbCache[f.id] { return (f, img) }
        let opts = [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: 480,
                     kCGImageSourceCreateThumbnailWithTransform: true] as CFDictionary
        guard let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: f.path) as CFURL, nil),
              let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, opts) else { return (f, nil) }
        let img = NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
        thumbCache[f.id] = img
        thumbOrder.append(f.id)
        if thumbOrder.count > 64 { thumbCache.removeValue(forKey: thumbOrder.removeFirst()) }
        return (f, img)
    }

    /// Audio chunks that cover the playhead (mic and system audio can overlap).
    var audioHere: [TimelineAudio] {
        audio.filter { $0.start <= playhead && playhead < $0.end }
    }

    func reload(hours: Double = 24) {
        stopAudio()
        loadedSince = Date().addingTimeInterval(-hours * 3600)
        frames = db.timelineFrames(since: loadedSince)
        audio = db.timelineAudio(since: loadedSince)
        sessions = TimelineAxis.sessions(frames: frames, audio: audio)
        show(frames.count - 1)
    }

    /// Pull in older history when the user scrolls to the start.
    func loadOlder(hours: Double = 24) {
        let since = loadedSince.addingTimeInterval(-hours * 3600)
        let older = db.timelineFrames(since: since).filter { $0.time < loadedSince }
        guard !older.isEmpty else { return }
        let keepId = current?.id
        loadedSince = since
        frames = older + frames
        audio = db.timelineAudio(since: since)
        sessions = TimelineAxis.sessions(frames: frames, audio: audio)
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
        playhead = max(min(date, Date()), frames.first?.time ?? date)
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
        guard let img = NSImage(contentsOfFile: f.path) else { return nil }
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
            let p = try AVAudioPlayer(contentsOf: URL(fileURLWithPath: a.path))
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
