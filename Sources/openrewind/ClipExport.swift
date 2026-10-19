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
/// Clips are made only from screenshots already saved (blocked apps are blurred in them), never from a new capture.
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
            if secs <= 0 { continue }
            if hides?(f) == true { blocked += 1; continue }
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
    /// True for a frame the block list covers now (same rule as Ask AI): by app, window title, site or word.
    func hides(_ f: TimelineFrame) -> Bool {
        blocksFrame(appName: f.appName, windowName: f.windowName, url: f.url) || Blocklist.hasKeyword(f.url, keywords)
    }
}

/// Writes a clip of saved screenshots to an MP4 (H.264 with AVAssetWriter) or an animated GIF (ImageIO).
/// Only the files already on disk are used, so blocked apps stay blurred; nothing is captured again.
final class ClipExporter {
    private let lock = NSLock()
    private var stopped = false
    private var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return stopped }

    func cancel() { lock.lock(); stopped = true; lock.unlock() }

    struct Failure: LocalizedError {
        var message: String
        var errorDescription: String? { message }
    }

    /// Write `plan` to `url` in the background. `progress` (0–1) and `done` run on the main thread; `done` gets
    /// nil on success. A cancelled or failed export leaves no file behind.
    func export(_ plan: ClipPlan.Result, format: ClipFormat, to url: URL,
                progress: @escaping (Double) -> Void, done: @escaping (Error?) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async { [self] in
            let tmp = url.deletingLastPathComponent().appendingPathComponent("." + url.lastPathComponent + ".part")
            var failure: Error?
            do {
                try? FileManager.default.removeItem(at: tmp)
                let report = { (p: Double) in DispatchQueue.main.async { progress(p) } }
                if format == .gif { try writeGIF(plan.frames, to: tmp, progress: report) }
                else { try writeMP4(plan, to: tmp, progress: report) }
                if isCancelled { throw CancellationError() }
                // The save panel already asked before replacing a file.
                if FileManager.default.fileExists(atPath: url.path) { try FileManager.default.removeItem(at: url) }
                try FileManager.default.moveItem(at: tmp, to: url)
            } catch {
                failure = error
            }
            try? FileManager.default.removeItem(at: tmp)
            DispatchQueue.main.async { done(failure) }
        }
    }

    /// Size of the first screenshot that can still be read.
    private func outputSize(_ frames: [ClipFrame], _ format: ClipFormat) throws -> (width: Int, height: Int) {
        for f in frames {
            guard let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: f.frame.path) as CFURL, nil),
                  let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any],
                  let w = props[kCGImagePropertyPixelWidth] as? Int, let h = props[kCGImagePropertyPixelHeight] as? Int else { continue }
            return ClipPlan.outputSize(width: w, height: h, format: format)
        }
        throw Failure(message: "The screenshots in that range were deleted.")
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

    private func writeMP4(_ plan: ClipPlan.Result, to url: URL, progress: (Double) -> Void) throws {
        let (w, h) = try outputSize(plan.frames, .mp4)
        let writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        let settings: [String: Any] = [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: w,
            AVVideoHeightKey: h,
            AVVideoCompressionPropertiesKey: [
                // Screens are mostly still: a modest bitrate keeps text sharp without big files.
                AVVideoAverageBitRateKey: min(max(w * h * 2, 1_000_000), 6_000_000),
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

        let samples = ClipPlan.samples(plan.frames)
        var lastPath: String?
        var image: CGImage?
        for (i, s) in samples.enumerated() {
            while !input.isReadyForMoreMediaData {
                if isCancelled { break }
                Thread.sleep(forTimeInterval: 0.005)
            }
            if isCancelled { writer.cancelWriting(); throw CancellationError() }
            if s.frame.path != lastPath {
                // A missing file keeps the picture before it.
                image = ImageTools.load(URL(fileURLWithPath: s.frame.path)) ?? image
                lastPath = s.frame.path
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
            progress(Double(i + 1) / Double(samples.count))
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

    private func writeGIF(_ frames: [ClipFrame], to url: URL, progress: (Double) -> Void) throws {
        let (w, h) = try outputSize(frames, .gif)
        guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.gif.identifier as CFString, frames.count, nil),
              let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)
        else { throw Failure(message: "Could not start the GIF.") }
        CGImageDestinationSetProperties(dest, [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: 0]] as CFDictionary)
        var lastPath: String?
        var picture: CGImage?
        for (i, f) in frames.enumerated() {
            if isCancelled { throw CancellationError() }
            if f.frame.path != lastPath || picture == nil {
                // A missing file keeps the picture before it.
                if let image = ImageTools.load(URL(fileURLWithPath: f.frame.path)) {
                    Self.draw(image, in: ctx, width: w, height: h)
                    picture = Self.compact(ctx.makeImage())
                }
                lastPath = f.frame.path
            }
            guard let picture else { continue }
            let delay = [kCGImagePropertyGIFDelayTime: f.duration, kCGImagePropertyGIFUnclampedDelayTime: f.duration]
            CGImageDestinationAddImage(dest, picture, [kCGImagePropertyGIFDictionary: delay] as CFDictionary)
            progress(Double(i + 1) / Double(frames.count) * 0.9)
        }
        if isCancelled { throw CancellationError() }
        guard CGImageDestinationFinalize(dest) else { throw Failure(message: "Could not write the GIF.") }
        progress(1)
    }

    /// The picture kept as PNG bytes and read back only when the GIF is written, so a long GIF does not hold
    /// hundreds of full-size pictures in memory until then.
    private static func compact(_ image: CGImage?) -> CGImage? {
        guard let image else { return nil }
        let data = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(data as CFMutableData, UTType.png.identifier as CFString, 1, nil) else { return image }
        CGImageDestinationAddImage(dest, image, nil)
        guard CGImageDestinationFinalize(dest), let src = CGImageSourceCreateWithData(data as CFData, nil),
              let lazy = CGImageSourceCreateImageAtIndex(src, 0, [kCGImageSourceShouldCache: false] as CFDictionary) else { return image }
        return lazy
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

    /// Every word Live Text found on the shown screenshot.
    func copyAllText() {
        let transcript: String? = MainActor.assumeIsolated { liveText?.analysis?.transcript }
        guard !currentHidden, let text = transcript, !text.isEmpty else {
            show(notice: "No text found on this screenshot yet")
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
        // Above the timeline, which floats over everything.
        panel.level = NSWindow.Level(rawValue: NSWindow.Level.screenSaver.rawValue + 1)
        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard let cg = ImageTools.load(URL(fileURLWithPath: f.path)) else { show(notice: "The file for this moment was deleted"); return }
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
        show(notice: clipEnd == nil ? "Clip starts here. Press ] where it ends." : "Clip marked. ⌘E exports it.")
    }

    func markEnd() {
        clipEnd = playhead
        if let s = clipStart, s > playhead { clipStart = nil }
        show(notice: clipStart == nil ? "Clip ends here. Press [ where it starts." : "Clip marked. ⌘E exports it.")
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

    /// The screenshots in a range as the timeline shows them (with its app filter), and what the clip will be.
    func clipPlan(_ start: Date, _ end: Date, speed: Double, format: ClipFormat) -> (plan: ClipPlan.Result?, problem: String?) {
        guard end > start else { return (nil, "The end is before the start.") }
        guard end.timeIntervalSince(start) <= ClipPlan.maxLength else {
            return (nil, "A clip can cover at most \(Int(ClipPlan.maxLength / 3600)) hours.")
        }
        let shown = AppFilter.apply(db.timelineFrames(since: start), app: appFilter)
        let list = BlocklistStore.shared.current
        let plan = ClipPlan.make(shown, from: start, to: end, speed: speed, format: format) { list.hides($0) }
        guard !plan.frames.isEmpty else {
            return (nil, plan.blocked > 0 ? "Every screenshot in that range is from a blocked app or site." : "No screenshots in that range.")
        }
        return (plan, nil)
    }
}

/// The range the Export Clip card opened with.
struct ClipRange: Identifiable {
    let id = UUID()
    var start: Date
    var end: Date
}
