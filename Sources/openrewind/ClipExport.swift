import AppKit
import AVFoundation
import ImageIO
import UniformTypeIdentifiers
import VisionKit

enum ClipFormat: String, CaseIterable, Identifiable {
    case mp4, gif
    var id: String { rawValue }
    var title: String { self == .mp4 ? "MP4 video" : "Animated GIF" }
    var type: UTType { self == .mp4 ? .mpeg4Movie : .gif }
}

/// One picture in an exported clip: the screenshot, when it starts in the clip and how long it stays (clip seconds).
struct ClipFrame {
    var frame: TimelineFrame
    var at: Double
    var duration: Double
}

/// Which screenshots go into an exported clip, and for how long each one shows.
/// Clips are made only from screenshots already saved, never from a new capture.
/// The Windows app has the same rules in ClipPlan.cs (with tests).
enum ClipPlan {
    /// Longest stretch of time one clip can cover.
    static let maxLength: TimeInterval = 2 * 3600
    /// Export Clip… without marks: the 5 minutes up to the shown screenshot.
    static let defaultLength: TimeInterval = 5 * 60
    /// 1 = real time.
    static let speeds: [Double] = [1, 2, 5, 10, 30]
    /// A screenshot stays at most this long (seconds), like a block on the bar; longer breaks are skipped.
    static let maxHold: Double = 60
    /// The last screenshot of a clip stays at least this long (seconds of real time).
    static let lastHold: Double = 2
    /// GIFs get big fast: at most this many pictures, and browsers slow down anything shown under 2/100 s.
    static let maxGifFrames = 600
    static let minGifDelay = 0.05
    static let maxVideoFrames = 20_000
    static let minVideoStep = 1.0 / 30
    /// Long side of the output, in pixels. Screenshots are scaled down to fit, never up.
    static let maxGifSide = 1280, maxVideoSide = 1920
    /// A video repeats a held picture at least this often (seconds), so players can seek and show it.
    static let maxVideoGap = 1.0

    struct Result {
        var frames: [ClipFrame]
        var blocked: Int
        var realSeconds: Double
        /// Length of the clip in seconds.
        var duration: Double { frames.last.map { $0.at + $0.duration } ?? 0 }
    }

    /// The range Export Clip… starts with: the marked range, or the 5 minutes up to the shown moment,
    /// never longer than maxLength.
    static func defaultRange(shown: Date, markStart: Date?, markEnd: Date?) -> (start: Date, end: Date) {
        var start: Date, end: Date
        if let a = markStart, let b = markEnd {
            (start, end) = a <= b ? (a, b) : (b, a)
        } else if let s = markStart {
            (start, end) = shown > s ? (s, shown) : (s, s.addingTimeInterval(defaultLength))
        } else if let e = markEnd {
            (start, end) = (e.addingTimeInterval(-defaultLength), e)
        } else {
            (start, end) = (shown.addingTimeInterval(-defaultLength), shown)
        }
        if end.timeIntervalSince(start) > maxLength { start = end.addingTimeInterval(-maxLength) }
        return (start, end)
    }

    /// The screenshots taken from `start` to `end` (inclusive) out of `frames` (time-sorted, as the timeline shows
    /// them, so an app filter applies), at `speed` times real time. Each one lasts until the next screenshot, at
    /// most maxHold; frames that `hides` (the block list now) are left out and their time skipped.
    static func make(_ frames: [TimelineFrame], from start: Date, to end: Date, speed: Double, format: ClipFormat,
                     hides: ((TimelineFrame) -> Bool)? = nil) -> Result {
        let speed = max(0.01, speed)
        var raw: [(frame: TimelineFrame, secs: Double)] = []
        var blocked = 0
        var i = frames.firstIndex(where: { $0.time >= start }) ?? frames.count
        while i < frames.count && frames[i].time <= end {
            defer { i += 1 }
            let f = frames[i]
            // While filtering by app, a frame lasts until the next screenshot of any app (its end).
            let next = f.end != nil ? AppFilter.until(f)
                : i + 1 < frames.count ? frames[i + 1].time : f.time.addingTimeInterval(lastHold)
            var secs = min(next.timeIntervalSince(f.time), maxHold)
            let last = i + 1 >= frames.count || frames[i + 1].time > end
            if last { secs = min(secs, max(lastHold, end.timeIntervalSince(f.time))) }
            if hides?(f) == true { blocked += 1; continue }
            if secs <= 0 { continue }
            raw.append((f, secs))
        }
        let real = raw.reduce(0) { $0 + $1.secs }
        let total = real / speed
        let step = format == .gif ? max(minGifDelay, total / Double(maxGifFrames)) : max(minVideoStep, total / Double(maxVideoFrames))
        return Result(frames: merge(raw.map { ($0.frame, $0.secs / speed) }, step: step), blocked: blocked, realSeconds: real)
    }

    /// Pictures shorter than `step` give way to the next one, so the clip has no more pictures than it can show.
    private static func merge(_ items: [(TimelineFrame, Double)], step: Double) -> [ClipFrame] {
        guard let first = items.first else { return [] }
        var out: [ClipFrame] = []
        var cur = ClipFrame(frame: first.0, at: 0, duration: first.1)
        var at = first.1
        for (f, secs) in items.dropFirst() {
            if cur.duration < step {
                cur.frame = f
                cur.duration += secs
            } else {
                out.append(cur)
                cur = ClipFrame(frame: f, at: at, duration: secs)
            }
            at += secs
        }
        cur.duration = max(cur.duration, step)
        out.append(cur)
        return out
    }

    /// Video samples: each picture, repeated every `maxGap` seconds while it is held.
    static func samples(_ frames: [ClipFrame], maxGap: Double = maxVideoGap) -> [ClipFrame] {
        var out: [ClipFrame] = []
        for f in frames {
            let n = max(1, Int((f.duration / maxGap - 1e-9).rounded(.up)))
            let d = f.duration / Double(n)
            for k in 0..<n { out.append(ClipFrame(frame: f.frame, at: f.at + Double(k) * d, duration: d)) }
        }
        return out
    }

    /// How many video samples `samples` makes, without making them.
    static func sampleCount(_ frames: [ClipFrame], maxGap: Double = maxVideoGap) -> Int {
        frames.reduce(0) { $0 + max(1, Int(($1.duration / maxGap - 1e-9).rounded(.up))) }
    }

    /// H.264 bitrate (bits per second) for a `w`×`h` video: screens are mostly still, so a modest one keeps text
    /// sharp without big files.
    static func videoBitrate(width w: Int, height h: Int) -> Int { min(max(w * h * 2, 1_000_000), 6_000_000) }

    /// A rough guess, shown before exporting, of the file's size (bytes) and how long making it takes (seconds)
    /// for a `w`×`h` output. Screens compress very differently, so this only gives the order of magnitude.
    static func estimate(_ plan: Result, format: ClipFormat, width w: Int, height h: Int) -> (bytes: Int, seconds: Double) {
        let pictures = Double(plan.frames.count)
        let px = Double(w) * Double(h)
        if format == .gif {
            // The first picture in full, then only what changed, which is usually a small part of the screen.
            let bytes = px * 0.5 + max(0, pictures - 1) * px * 0.03 + 1024
            return (Int(bytes), pictures * (0.05 + px * 5e-8))
        }
        // A mostly still screen uses about a third of the bitrate allowed.
        let size = Double(videoBitrate(width: w, height: h)) / 8 * plan.duration / 3 + 4096
        return (Int(size), pictures * 0.05 + Double(sampleCount(plan.frames)) * 0.01)
    }

    /// "820 KB" or "12.4 MB".
    static func fileSize(_ bytes: Int) -> String {
        bytes < 1024 * 1024 ? "\(max(1, bytes / 1024)) KB" : String(format: "%.1f MB", Double(bytes) / 1_048_576)
    }

    /// What is going into the clip, shown before exporting: "0:42 clip · 312 frames · 1280×800 · about 4.1 MB · takes about 0:20".
    static func summary(_ plan: Result, format: ClipFormat, width w: Int, height h: Int) -> String {
        let e = estimate(plan, format: format, width: w, height: h)
        let n = plan.frames.count
        return "\(length(plan.duration)) clip · \(n) frame\(n == 1 ? "" : "s") · \(w)×\(h) · about \(fileSize(e.bytes)) · takes about \(length(max(1, e.seconds)))"
    }

    /// While exporting: "Frame 120 of 600 · 20% · 0:12 elapsed · about 0:48 left".
    static func progressText(done: Int, total: Int, elapsed: Double) -> String {
        let total = max(1, total)
        let done = min(max(done, 0), total)
        let s = "Frame \(done) of \(total) · \(done * 100 / total)% · \(length(elapsed)) elapsed"
        if done >= total { return s + " · finishing the file…" }
        if done == 0 || elapsed < 1 { return s + " · working out the time left…" }
        return s + " · about \(length(elapsed * Double(total - done) / Double(done))) left"
    }

    /// Output size for a `w`×`h` screenshot: scaled down to fit, even sizes for video (H.264 needs them).
    static func outputSize(width w: Int, height h: Int, format: ClipFormat) -> (width: Int, height: Int) {
        let limit = format == .gif ? maxGifSide : maxVideoSide
        let s = min(1, Double(limit) / Double(max(1, w, h)))
        var ow = max(1, Int(Double(w) * s)), oh = max(1, Int(Double(h) * s))
        if format == .mp4 { ow = max(2, ow & ~1); oh = max(2, oh & ~1) }
        return (ow, oh)
    }

    /// "OpenRewind 2026-10-19 14.30.05.gif", in local time.
    static func fileName(_ date: Date, ext: String) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HH.mm.ss"
        return "OpenRewind \(f.string(from: date)).\(ext)"
    }

    /// "0:42", "12:05" or "1:02:03".
    static func length(_ seconds: Double) -> String {
        let t = Int(max(0, seconds).rounded())
        return t >= 3600 ? String(format: "%d:%02d:%02d", t / 3600, t / 60 % 60, t % 60) : String(format: "%d:%02d", t / 60, t % 60)
    }

    static func speedLabel(_ speed: Double) -> String {
        speed == 1 ? "1× (real time)" : String(format: "%g×", speed)
    }
}

extension Blocklist {
    /// True for a frame saved while a blocked window or site was in front, or one the block list covers now
    /// (same rule as Ask AI): by app, window title, site or word.
    func hides(_ f: TimelineFrame) -> Bool {
        f.blocked || blocksFrame(appName: f.appName, windowName: f.windowName, url: f.url) || Blocklist.hasKeyword(f.url, keywords)
    }
}

/// Writes a clip of saved screenshots to an MP4 (H.264 with AVAssetWriter) or an animated GIF (GifWriter, written
/// as it goes). Only the files already on disk are used; nothing is captured again.
final class ClipExporter {
    private let lock = NSLock()
    private var stopped = false
    var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return stopped }

    func cancel() { lock.lock(); stopped = true; lock.unlock() }

    struct Failure: LocalizedError {
        var message: String
        var errorDescription: String? { message }
    }

    /// Write `plan` as a `size` clip to `url` in the background. `progress` gets how many of the plan's frames are
    /// done, about ten times a second; it and `done` run on the main thread. `done` gets nil on success.
    /// A cancelled or failed export leaves no file behind.
    func export(_ plan: ClipPlan.Result, db: Database, format: ClipFormat, size: (width: Int, height: Int), to url: URL,
                progress: @escaping (Int) -> Void, done: @escaping (Error?) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async { [self] in
            let tmp = url.deletingLastPathComponent().appendingPathComponent("." + url.lastPathComponent + ".part")
            var failure: Error?
            do {
                try? FileManager.default.removeItem(at: tmp)
                let total = plan.frames.count
                var last = Date.distantPast, lastDone = -1
                let report = { (n: Int) in
                    // At most ten times a second, and always the last one.
                    guard n != lastDone, n >= total || Date().timeIntervalSince(last) >= 0.1 else { return }
                    last = Date(); lastDone = n
                    DispatchQueue.main.async { progress(n) }
                }
                if format == .gif { try writeGIF(plan.frames, db: db, size: size, to: tmp, progress: report) }
                else { try writeMP4(plan, db: db, size: size, to: tmp, progress: report) }
                if isCancelled { throw CancellationError() }
                // The save panel already asked before replacing a file; replacing keeps the old one if it fails.
                if FileManager.default.fileExists(atPath: url.path) {
                    _ = try FileManager.default.replaceItemAt(url, withItemAt: tmp)
                } else {
                    try FileManager.default.moveItem(at: tmp, to: url)
                }
            } catch {
                failure = error
            }
            try? FileManager.default.removeItem(at: tmp)
            DispatchQueue.main.async { done(failure) }
        }
    }

    /// Output size for the first screenshot that can still be read (only a still's header is read; a frame kept
    /// in a video is decoded), or nil.
    static func outputSize(_ frames: [ClipFrame], db: Database, _ format: ClipFormat) -> (width: Int, height: Int)? {
        for f in frames {
            if !f.frame.source.isVideo, let s = FrameImages.stillSize(f.frame.path) {
                return ClipPlan.outputSize(width: s.width, height: s.height, format: format)
            }
            if let img = FrameImages.shared.current(db, id: f.frame.id, f.frame.source, cache: false) {
                return ClipPlan.outputSize(width: img.width, height: img.height, format: format)
            }
        }
        return nil
    }

    /// Draw `image` scaled to fit `size`, centered on black.
    private static func draw(_ image: CGImage?, in ctx: CGContext, width: Int, height: Int) {
        ctx.setFillColor(CGColor(gray: 0, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        guard let image else { return }
        let s = min(CGFloat(width) / CGFloat(image.width), CGFloat(height) / CGFloat(image.height))
        let w = CGFloat(image.width) * s, h = CGFloat(image.height) * s
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: (CGFloat(width) - w) / 2, y: (CGFloat(height) - h) / 2, width: w, height: h))
    }

    private func writeMP4(_ plan: ClipPlan.Result, db: Database, size: (width: Int, height: Int), to url: URL, progress: (Int) -> Void) throws {
        let (w, h) = size
        let writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        let settings: [String: Any] = [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: w,
            AVVideoHeightKey: h,
            AVVideoCompressionPropertiesKey: [
                // Screens are mostly still: a modest bitrate keeps text sharp without big files.
                AVVideoAverageBitRateKey: ClipPlan.videoBitrate(width: w, height: h),
                AVVideoMaxKeyFrameIntervalDurationKey: 2.0,
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
            ] as [String: Any],
        ]
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: w,
            kCVPixelBufferHeightKey as String: h,
        ])
        guard writer.canAdd(input) else { throw Failure(message: "This Mac can't make an H.264 video of that size.") }
        writer.add(input)
        guard writer.startWriting() else { throw writer.error ?? Failure(message: "Could not start the video.") }
        writer.startSession(atSourceTime: .zero)

        // Each video sample, with the frame of the plan it shows.
        var samples: [(sample: ClipFrame, frame: Int)] = []
        for (p, f) in plan.frames.enumerated() {
            for s in ClipPlan.samples([f]) { samples.append((s, p)) }
        }
        var lastSource: FrameSource?
        var image: CGImage?
        for (i, (s, frame)) in samples.enumerated() {
            while !input.isReadyForMoreMediaData {
                if isCancelled { break }
                Thread.sleep(forTimeInterval: 0.005)
            }
            if isCancelled { writer.cancelWriting(); throw CancellationError() }
            if s.frame.source != lastSource {
                // A missing picture keeps the one before it. Read past the picture cache, so an export does not
                // push out the timeline's.
                image = FrameImages.shared.current(db, id: s.frame.id, s.frame.source, cache: false) ?? image
                lastSource = s.frame.source
            }
            guard let buffer = pixelBuffer(adaptor.pixelBufferPool, width: w, height: h, image: image) else {
                writer.cancelWriting()
                throw Failure(message: "Out of memory while making the video.")
            }
            if !adaptor.append(buffer, withPresentationTime: CMTime(seconds: s.at, preferredTimescale: 6000)) {
                let error = writer.error
                writer.cancelWriting()
                throw error ?? Failure(message: "Could not write the video.")
            }
            progress(i + 1 == samples.count ? plan.frames.count : frame)
        }
        input.markAsFinished()
        writer.endSession(atSourceTime: CMTime(seconds: plan.duration, preferredTimescale: 6000))
        let finished = DispatchSemaphore(value: 0)
        writer.finishWriting { finished.signal() }
        finished.wait()
        if writer.status != .completed { throw writer.error ?? Failure(message: "Could not finish the video.") }
    }

    private func pixelBuffer(_ pool: CVPixelBufferPool?, width: Int, height: Int, image: CGImage?) -> CVPixelBuffer? {
        var pb: CVPixelBuffer?
        if let pool { CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pb) }
        if pb == nil { CVPixelBufferCreate(nil, width, height, kCVPixelFormatType_32BGRA, nil, &pb) }
        guard let pb else { return nil }
        CVPixelBufferLockBaseAddress(pb, [])
        defer { CVPixelBufferUnlockBaseAddress(pb, []) }
        guard let ctx = CGContext(data: CVPixelBufferGetBaseAddress(pb), width: width, height: height, bitsPerComponent: 8,
                                  bytesPerRow: CVPixelBufferGetBytesPerRow(pb), space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        else { return nil }
        Self.draw(image, in: ctx, width: width, height: height)
        return pb
    }

    private func writeGIF(_ frames: [ClipFrame], db: Database, size: (width: Int, height: Int), to url: URL, progress: (Int) -> Void) throws {
        let (w, h) = size
        // BGRA in memory, as GifWriter reads it.
        guard FileManager.default.createFile(atPath: url.path, contents: nil),
              let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue),
              let pixels = ctx.data
        else { throw Failure(message: "Could not start the GIF.") }
        let file = try FileHandle(forWritingTo: url)
        defer { try? file.close() }
        let gif = try GifWriter(output: file, width: w, height: h)
        var lastSource: FrameSource?
        var drawn = false
        for (i, f) in frames.enumerated() {
            if isCancelled { throw CancellationError() }
            if f.frame.source != lastSource {
                // A missing picture keeps the one before it.
                if let image = FrameImages.shared.current(db, id: f.frame.id, f.frame.source, cache: false) {
                    Self.draw(image, in: ctx, width: w, height: h)
                    drawn = true
                }
                lastSource = f.frame.source
            }
            if drawn {
                try gif.addFrame(pixels.assumingMemoryBound(to: UInt8.self), bytesPerRow: ctx.bytesPerRow, seconds: f.duration)
            }
            progress(i + 1)
        }
        if isCancelled { throw CancellationError() }
        try gif.finish()
    }
}

// MARK: - the timeline's copy, save, marks and export

extension TimelineModel {
    /// True when the shown screenshot is from an app, window or site the block list covers now.
    var currentHidden: Bool {
        guard let f = current else { return false }
        return BlocklistStore.shared.current.hides(f)
    }

    /// The shown screenshot, or nil (with a notice) when there is none or it is blocked now.
    private func frameForExport() -> TimelineFrame? {
        guard let f = current, image != nil else { show(notice: "No screenshot at this moment"); return nil }
        if currentHidden { show(notice: "This moment is from a blocked app or site"); return nil }
        return f
    }

    /// The text selected with Live Text, or nil. The timeline only runs on the main thread.
    var selectedText: String? {
        MainActor.assumeIsolated {
            guard let v = liveText, v.hasActiveTextSelection else { return nil }
            let text = v.selectedText
            return text.isEmpty ? nil : text
        }
    }

    /// Unselect Live Text. False when nothing was selected.
    func clearTextSelection() -> Bool {
        MainActor.assumeIsolated {
            guard let v = liveText, v.hasActiveTextSelection else { return false }
            v.resetSelection()
            return true
        }
    }

    /// Copy the text selected with Live Text, or the shown screenshot when nothing is selected.
    func copy() {
        if let text = selectedText {
            // The block list may have changed since the words were read.
            if currentHidden { _ = clearTextSelection(); show(notice: "This moment is from a blocked app or site"); return }
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
            show(notice: "Copied the selected text")
            return
        }
        copyFrame()
    }

    func copyFrame() {
        guard frameForExport() != nil, let img = image else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.writeObjects([img])
        show(notice: "Copied the screenshot")
    }

    /// Every word on the shown screenshot: what Live Text found, or the text saved with it (from
    /// Accessibility or OCR when it was recorded) while Live Text is still reading or not available.
    func copyAllText() {
        guard let f = current else { show(notice: "No screenshot at this moment"); return }
        if currentHidden || f.blocked { show(notice: "This moment is from a blocked app or site"); return }
        let transcript: String? = MainActor.assumeIsolated { liveText?.analysis?.transcript }
        var text = transcript ?? ""
        if text.isEmpty { text = db.frame(id: f.id)?.text ?? "" }
        guard !text.isEmpty else {
            show(notice: "No text found on this screenshot")
            return
        }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        show(notice: "Copied the text on this screenshot")
    }

    /// Save the shown screenshot as a PNG.
    func saveFrame() {
        guard let f = frameForExport() else { return }
        stopAudio()
        let panel = NSSavePanel()
        panel.title = "Save Screenshot"
        panel.allowedContentTypes = [.png]
        panel.nameFieldStringValue = ClipPlan.fileName(f.time, ext: "png")
        panel.directoryURL = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first
        guard Dialogs.run(panel) == .OK, let url = panel.url else { return }
        // The timeline may have been closed (or locked) while the dialog was up.
        guard isShown else { return }
        guard let cg = FrameImages.shared.current(db, id: f.id, f.source) else { show(notice: "The file for this moment was deleted"); return }
        do {
            try ImageTools.write(cg, to: url, type: .png, quality: 1)
            show(notice: "Saved \(url.lastPathComponent)")
        } catch {
            log("cannot save screenshot: \(error.localizedDescription)")
            show(notice: "Could not save the screenshot there")
        }
    }

    func markStart() {
        clipStart = playhead
        if let e = clipEnd, e < playhead { clipEnd = nil }
        show(notice: clipEnd == nil ? "Starts here. Press ] where it ends." : "Marked. ⌫ deletes it, ⌘E exports it.")
    }

    func markEnd() {
        clipEnd = playhead
        if let s = clipStart, s > playhead { clipStart = nil }
        show(notice: clipStart == nil ? "Ends here. Press [ where it starts." : "Marked. ⌫ deletes it, ⌘E exports it.")
    }

    var hasMarks: Bool { clipStart != nil || clipEnd != nil }

    func clearMarks() { clipStart = nil; clipEnd = nil }

    /// Export Clip…: the marked range, or the 5 minutes up to the shown screenshot.
    func openExport() {
        guard !frames.isEmpty else { show(notice: "Nothing recorded yet"); return }
        stopAudio()
        let r = ClipPlan.defaultRange(shown: playhead, markStart: clipStart, markEnd: clipEnd)
        exportClip = ClipRange(start: r.start, end: r.end)
    }

    /// The screenshots in a range as the timeline shows them (with its app filter, without hidden apps), what the clip will be and its
    /// size, or why there is none. Reads only that range of the database; safe off the main thread.
    static func clipPlan(db: Database, appFilter: String?, _ start: Date, _ end: Date, speed: Double,
                         format: ClipFormat) -> ClipInfo {
        func problem(_ s: String) -> ClipInfo { ClipInfo(plan: nil, size: nil, problem: s) }
        guard end > start else { return problem("The end is before the start.") }
        guard end.timeIntervalSince(start) <= ClipPlan.maxLength else {
            return problem("A clip can cover at most \(Int(ClipPlan.maxLength / 3600)) hours.")
        }
        let shown = AppFilter.apply(db.timelineFrames(from: start, to: end), app: appFilter, hidden: HiddenApps.keys)
        let list = BlocklistStore.shared.current
        let plan = ClipPlan.make(shown, from: start, to: end, speed: speed, format: format) { list.hides($0) }
        guard !plan.frames.isEmpty else {
            return problem(plan.blocked > 0 ? "Every screenshot in that range is from a blocked app or site." : "No screenshots in that range.")
        }
        guard let size = ClipExporter.outputSize(plan.frames, db: db, format) else { return problem("The screenshots in that range were deleted.") }
        return ClipInfo(plan: plan, size: size, problem: nil)
    }
}

/// A clip ready to export (its plan and output size), or why there is none.
struct ClipInfo {
    var plan: ClipPlan.Result?
    var size: (width: Int, height: Int)?
    var problem: String?
}

/// The range the Export Clip card opened with.
struct ClipRange: Identifiable {
    let id = UUID()
    var start: Date
    var end: Date
}
