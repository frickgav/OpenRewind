import AppKit
import AVFoundation
import CoreMedia
import CoreVideo
import Foundation
import ImageIO
import VideoToolbox

/// The one way to get a frame's picture, whether it is a still file or a frame inside a video
/// (Compress Older Recordings). The timeline, search highlights, the local API, Ask AI and the MCP server
/// all read through here. Video frames are decoded by a few cached image generators that seek exactly
/// (zero tolerance) and kept in a small LRU (at most 48 frames and 256 MB), so scrubbing stays quick.
/// A decode holds only its own video's lock, so cached frames and other videos are served meanwhile.
/// With Encrypt Recordings on, stills and videos are encrypted files: a still is decrypted in memory, and
/// a video (which AVFoundation reads from a file) is decrypted to a private temp file (0600) for as long
/// as it is open here, then deleted. A video is closed after 30 s without use, and every video is closed
/// when the timeline hides, the app locks or quits, and after each MCP message (releaseAll), so decrypted
/// copies don't sit in the temp folder. Windows has the same in FrameImages.cs.
final class FrameImages {
    static let shared = FrameImages()
    private static let cacheBytes = 256 << 20

    /// One open video: its generator, and a lock so one decode runs on it at a time. An encrypted video
    /// is read from a decrypted temp copy, deleted when the video is closed.
    private final class Video {
        let generator: AVAssetImageGenerator
        let lock = NSLock()
        private let plainCopy: String?
        private var touched = Date()
        init(_ path: String) {
            plainCopy = FrameImages.plainCopy(path)
            generator = FrameImages.generator(plainCopy ?? path)
        }
        deinit { if let plainCopy { try? FileManager.default.removeItem(atPath: plainCopy) } }

        /// Keeps the copy's modification time fresh while it is in use (call under `lock`), so the temp
        /// cleanup in another OpenRewind process, which spares copies changed in the last 10 minutes, leaves it.
        func touch() {
            guard let plainCopy, Date().timeIntervalSince(touched) > 60 else { return }
            touched = Date()
            try? FileManager.default.setAttributes([.modificationDate: touched], ofItemAtPath: plainCopy)
        }
    }

    /// How long a video stays open without use.
    static let idleClose: TimeInterval = 30

    /// For an encrypted video, a decrypted copy in the temp folder (only this user can read it); nil for a
    /// plain one, or when it can't be decrypted (the decode then fails and says so).
    static func plainCopy(_ path: String) -> String? {
        guard RecordingFile.isEncrypted(path: path) else { return nil }
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent("\(RecordingFile.tempCopyPrefix)\(UUID().uuidString).\((path as NSString).pathExtension)").path
        do {
            let plain = try RecordingFile.read(path)
            guard FileManager.default.createFile(atPath: tmp, contents: plain, attributes: [.posixPermissions: 0o600]) else { return nil }
            return tmp
        } catch {
            log("cannot open \((path as NSString).lastPathComponent): \(error.localizedDescription)")
            return nil
        }
    }

    /// An image source for a still, decrypted in memory when it is encrypted.
    static func stillSource(_ path: String) -> CGImageSource? {
        guard !path.isEmpty else { return nil }
        if RecordingFile.isEncrypted(path: path) {
            return RecordingFile.data(path).flatMap { CGImageSourceCreateWithData($0 as CFData, nil) }
        }
        return CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil)
    }

    private let lock = NSLock()
    /// Decoded video frames by "path#frame".
    private let frames = Lru<String, CGImage>(capacity: 48, weight: { $0.bytesPerRow * $0.height }, maxWeight: FrameImages.cacheBytes)
    /// Open videos, so the next frame of the same one does not open the file again.
    private let videos = Lru<String, Video>(capacity: 4)
    /// Goes up on every release: a decode that ran across one doesn't cache what it got.
    private var releases = 0
    /// When each open video was last read (for the idle close).
    private var lastUse: [String: Date] = [:]
    private let idleTimer = DispatchSource.makeTimerSource(queue: .global(qos: .utility))

    init() {
        idleTimer.schedule(deadline: .now() + 10, repeating: 10)
        idleTimer.setEventHandler { [weak self] in self?.closeIdle() }
        idleTimer.resume()
    }

    /// Close videos not read for `idleClose` (their decrypted copies are deleted with them).
    func closeIdle(now: Date = Date()) {
        lock.lock(); defer { lock.unlock() }
        let idle = Set(lastUse.filter { now.timeIntervalSince($0.value) >= Self.idleClose }.keys)
        guard !idle.isEmpty else { return }
        videos.removeAll { idle.contains($0) }
        for p in idle { lastUse[p] = nil }
    }

    /// Close every video and drop every decoded frame: the timeline hid, the app locked or is quitting,
    /// or an MCP message was answered. No decrypted copy of a video is left behind.
    func releaseAll() {
        lock.lock(); defer { lock.unlock() }
        releases += 1
        videos.removeAll { _ in true }
        frames.removeAll { _ in true }
        lastUse = [:]
    }

    /// The picture of frame `id`. A list of frames can be older than the database: the compactor may have moved
    /// the frame into a video (and deleted the still) or written its video again under a new name since. When the
    /// picture isn't where `f` says, look the frame up again.
    func current(_ db: Database, id: Int64, _ f: FrameSource, cache: Bool = true) -> CGImage? {
        if let img = image(f, cache: cache) { return img }
        guard let now = db.frame(id: id)?.source, now != f else { return nil }
        return image(now, cache: cache)
    }

    /// The picture, or nil when it is gone or can't be read. `cache: false` for one-off reads (the compactor
    /// reading a whole video) that would only push the timeline's frames out of the cache.
    func image(_ f: FrameSource, cache: Bool = true) -> CGImage? {
        guard f.isVideo else { return f.stillPath.isEmpty ? nil : RecordingFile.image(f.stillPath) }
        let key = Self.key(f.videoPath, f.videoFrame)
        lock.lock()
        if let hit = frames.get(key) { lock.unlock(); return hit }
        let video: Video
        if let v = videos.get(f.videoPath) {
            video = v
        } else {
            guard FileManager.default.fileExists(atPath: f.videoPath) else { lock.unlock(); return nil }
            video = Video(f.videoPath)
            videos.set(f.videoPath, video)
        }
        lastUse[f.videoPath] = Date()
        let seen = releases
        lock.unlock()

        video.lock.lock()
        video.touch()
        let img = Self.frame(video.generator, at: CMTime(seconds: f.videoTime, preferredTimescale: 600))
        video.lock.unlock()

        lock.lock(); defer { lock.unlock() }
        guard let img else {
            log("cannot read frame \(f.videoFrame) of \((f.videoPath as NSString).lastPathComponent)")
            videos.removeAll { $0 == f.videoPath }
            return nil
        }
        if cache && releases == seen { frames.set(key, img) }
        return img
    }

    /// The picture for AppKit, one point per pixel.
    func nsImage(_ f: FrameSource) -> NSImage? {
        image(f).map { NSImage(cgImage: $0, size: NSSize(width: $0.width, height: $0.height)) }
    }

    /// A small copy, at most `maxPixels` on its long side (the timeline's hover preview).
    func thumbnail(_ f: FrameSource, maxPixels: Int) -> CGImage? {
        if !f.isVideo {
            let opts = [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: maxPixels,
                        kCGImageSourceCreateThumbnailWithTransform: true] as CFDictionary
            guard let src = Self.stillSource(f.stillPath) else { return nil }
            return CGImageSourceCreateThumbnailAtIndex(src, 0, opts)
        }
        guard let img = image(f) else { return nil }
        let scale = min(1, CGFloat(maxPixels) / CGFloat(max(img.width, img.height)))
        return scale < 1 ? ImageTools.scaled(img, by: scale) : img
    }

    /// The picture as JPEG, at most `maxSide` pixels on its long side (0 = full size).
    func jpeg(_ f: FrameSource, maxSide: Int = 0, quality: Double = 0.8) -> Data? {
        guard let img = image(f) else { return nil }
        let scale = maxSide > 0 ? min(1, CGFloat(maxSide) / CGFloat(max(img.width, img.height))) : 1
        return ImageTools.jpegData(scale < 1 ? ImageTools.scaled(img, by: scale) : img, quality: quality)
    }

    /// Pixel size of a still without decoding it.
    static func stillSize(_ path: String) -> (width: Int, height: Int)? {
        guard let src = stillSource(path),
              let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any],
              let w = props[kCGImagePropertyPixelWidth] as? Int, let h = props[kCGImagePropertyPixelHeight] as? Int else { return nil }
        return (w, h)
    }

    /// Close a video and forget its frames (before it is deleted or replaced).
    func release(_ videoPath: String) {
        lock.lock(); defer { lock.unlock() }
        releases += 1
        videos.removeAll { $0 == videoPath }
        lastUse[videoPath] = nil
        let prefix = videoPath + "#"
        frames.removeAll { $0.hasPrefix(prefix) }
    }

    private static func key(_ path: String, _ frame: Int) -> String { path + "#\(frame)" }

    /// A small gray copy for checking a video frame against its screenshot. The screenshot is first cut to
    /// the even size a video has, and both are shrunk smoothly, so thin lines of text land on the same
    /// thumbnail pixels (a video scaled down from a very large screenshot compares the same way).
    static func comparable(_ image: CGImage) -> [UInt8] {
        let w = Compaction.even(image.width), h = Compaction.even(image.height)
        let cut = (w == image.width && h == image.height) ? image : image.cropping(to: CGRect(x: 0, y: 0, width: w, height: h)) ?? image
        return ImageTools.thumbnail(cut, quality: .high)
    }

    /// A generator that returns exactly the frame asked for, at its stored size.
    static func generator(_ path: String) -> AVAssetImageGenerator {
        let asset = AVURLAsset(url: URL(fileURLWithPath: path), options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
        let gen = AVAssetImageGenerator(asset: asset)
        gen.requestedTimeToleranceBefore = .zero
        gen.requestedTimeToleranceAfter = .zero
        gen.appliesPreferredTrackTransform = false
        return gen
    }

    /// One frame, waited for (callers are already off the main thread, or need the picture right away).
    static func frame(_ gen: AVAssetImageGenerator, at time: CMTime) -> CGImage? {
        let box = Box<CGImage>()
        let done = DispatchSemaphore(value: 0)
        gen.generateCGImageAsynchronously(for: time) { image, _, error in
            box.value = image
            if let error { box.error = error.localizedDescription }
            done.signal()
        }
        if done.wait(timeout: .now() + 15) == .timedOut {
            gen.cancelAllCGImageGeneration()
            return nil
        }
        return box.value
    }

    /// Runs async AVFoundation work from synchronous code (the compactor runs on its own queue).
    static func wait<T>(_ work: @escaping @Sendable () async throws -> T) -> T? {
        let box = Box<T>()
        let done = DispatchSemaphore(value: 0)
        Task.detached {
            box.value = try? await work()
            done.signal()
        }
        done.wait()
        return box.value
    }
}

/// A result handed back from a completion handler.
final class Box<T>: @unchecked Sendable {
    var value: T?
    var error: String?
}

/// VideoCodec for the compactor: AVAssetWriter, HEVC when this Mac has a hardware HEVC encoder, else H.264.
/// Every Mac this app runs on (macOS 14) decodes both. Read back with AVAssetImageGenerator.
final class AVCodec: VideoCodec {
    private let lock = NSLock()
    private var probed: (codec: String?, error: String?)?

    /// "hevc", "h264", or nil when this Mac can't make videos (checked once, see probe).
    var usable: String? { probe().codec }
    var probeError: String? { probe().error }
    /// The check has run (it takes a moment, so the menu does not wait for it).
    var checked: Bool {
        lock.lock(); defer { lock.unlock() }
        return probed != nil
    }

    func measure(_ frame: FrameSource) -> (width: Int, height: Int)? {
        if frame.isVideo { return FrameImages.shared.image(frame, cache: false).map { ($0.width, $0.height) } }
        return FrameImages.stillSize(frame.stillPath)
    }

    func write(_ frames: [FrameSource], width: Int, height: Int, to path: String) throws -> String {
        guard let codec = usable else { throw Self.error("no video encoder on this Mac (\(probeError ?? "unknown"))") }
        try Self.encode(count: frames.count, width: width, height: height, hevc: codec == "hevc", to: path) {
            FrameImages.shared.image(frames[$0], cache: false)
        }
        return codec
    }

    func verify(_ path: String, _ frames: [FrameSource], width: Int, height: Int) -> Bool {
        guard !frames.isEmpty else { return false }
        let name = (path as NSString).lastPathComponent
        let asset = AVURLAsset(url: URL(fileURLWithPath: path), options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
        guard let duration = FrameImages.wait({ try await asset.load(.duration) }),
              duration.seconds >= (Double(frames.count) - 0.5) / Compaction.fps else {
            log("\(name) is shorter than its \(frames.count) frames")
            return false
        }
        let gen = FrameImages.generator(path)
        for i in Compaction.checkedFrames(frames.count) {
            guard let decoded = FrameImages.frame(gen, at: Compaction.time(ofFrame: i)),
                  let source = FrameImages.shared.image(frames[i], cache: false) else { return false }
            guard decoded.width == width, decoded.height == height else {
                log("\(name): frame \(i) is \(decoded.width)x\(decoded.height), expected \(width)x\(height)")
                return false
            }
            guard Compaction.similar(FrameImages.comparable(decoded), FrameImages.comparable(source)) else {
                log("\(name): frame \(i) does not look like its picture")
                return false
            }
        }
        return true
    }

    func writeStill(_ frame: FrameSource, to path: String) throws {
        guard let decoded = FrameImages.shared.image(frame, cache: false) else { throw Self.error("the frame could not be read") }
        // Drawn into plain 8-bit sRGB first, like the screenshots the recorder saves: a decoded video frame
        // carries the video's color space (BT.709), converted here so the picture looks the same.
        guard let ctx = CGContext(data: nil, width: decoded.width, height: decoded.height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.noneSkipFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else {
            throw Self.error("cannot draw the frame")
        }
        ctx.draw(decoded, in: CGRect(x: 0, y: 0, width: decoded.width, height: decoded.height))
        guard let img = ctx.makeImage(), let data = ImageTools.jpegData(img, quality: 0.95) else { throw Self.error("the frame could not be saved as JPEG") }
        try RecordingFile.write(data, to: URL(fileURLWithPath: path))  // encrypted when Encrypt Recordings is on
    }

    func release(_ path: String) { FrameImages.shared.release(path) }

    /// Whether the videos already made still play (a folder restored from another Mac, a damaged file):
    /// opens up to `max` of them and reads their first frame. For doctor.
    static func checkVideos(_ db: Database, max: Int = 100) -> (checked: Int, bad: Int) {
        let all = db.segments()
        let pick = all.count <= max ? all : (0..<max).map { all[$0 * all.count / max] }
        var bad = 0
        for seg in pick where FrameImages.shared.image(FrameSource(stillPath: "", videoPath: seg.path), cache: false) == nil { bad += 1 }
        return (pick.count, bad)
    }

    private func probe() -> (codec: String?, error: String?) {
        lock.lock(); defer { lock.unlock() }
        if let p = probed { return p }
        let p = Self.runProbe()
        probed = p
        return p
    }

    /// Encode and decode a short test clip with odd proportions (not a multiple of 16, like many screens):
    /// hardware HEVC first, then H.264. Returns the codec that worked.
    private static func runProbe() -> (codec: String?, error: String?) {
        var error: String?
        let w = 362, h = 206
        let pictures = (0..<3).compactMap { testPicture($0, width: w, height: h) }
        guard pictures.count == 3 else { return (nil, "cannot draw test pictures") }
        for hevc in [true, false] {
            let path = FileManager.default.temporaryDirectory
                .appendingPathComponent("openrewind-probe-\(getpid())-\(hevc ? "hevc" : "h264").mp4").path
            defer { try? FileManager.default.removeItem(atPath: path) }
            do {
                try encode(count: 3, width: w, height: h, hevc: hevc, to: path) { pictures[$0] }
                let gen = FrameImages.generator(path)
                for i in [0, 2, 1] {
                    guard let back = FrameImages.frame(gen, at: Compaction.time(ofFrame: i)), back.width == w, back.height == h,
                          Compaction.similar(FrameImages.comparable(back), FrameImages.comparable(pictures[i])) else {
                        throw Self.error("frame \(i) did not read back the same")
                    }
                }
                return (hevc ? "hevc" : "h264", nil)
            } catch let e {
                error = "\(hevc ? "HEVC" : "H.264"): \(e.localizedDescription)"
                log("video check: \(error!)")
            }
        }
        return (nil, error)
    }

    /// Something like a screen: a title bar, lines like text, and a box that moves.
    static func testPicture(_ i: Int, width: Int = 320, height: Int = 180) -> CGImage? {
        guard let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else { return nil }
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        ctx.setFillColor(CGColor(red: 0.27, green: 0.51, blue: 0.71, alpha: 1))
        ctx.fill(CGRect(x: 0, y: height - 24, width: width, height: 24))
        ctx.setFillColor(CGColor(red: 1, green: 0.65, blue: 0, alpha: 1))
        ctx.fill(CGRect(x: 20 + i * 90, y: 40, width: 80, height: 80))
        ctx.setFillColor(CGColor(red: 0, green: 0, blue: 0, alpha: 1))
        for line in 0..<4 { ctx.fill(CGRect(x: 10, y: 10 + line * 7, width: 120 + (line * 37 + i * 50) % 160, height: 3)) }
        return ctx.makeImage()
    }

    /// Writes `count` pictures as an MP4: picture i at i / fps seconds, width x height pixels. A picture
    /// within a pixel of that size loses its odd last row or column; any other is scaled to fit (very wide
    /// captures, see Compaction.fit). Keyframe every Compaction.keyframeInterval frames and no B-frames, so
    /// one frame never needs more than that many decoded to show it.
    static func encode(count: Int, width: Int, height: Int, hevc: Bool, to path: String, picture: (Int) -> CGImage?) throws {
        let url = URL(fileURLWithPath: path)
        try? FileManager.default.removeItem(at: url)
        let writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        var compression: [String: Any] = [
            AVVideoMaxKeyFrameIntervalKey: Compaction.keyframeInterval,
            AVVideoAllowFrameReorderingKey: false,
            AVVideoQualityKey: 0.6,
        ]
        var settings: [String: Any] = [
            AVVideoCodecKey: hevc ? AVVideoCodecType.hevc : AVVideoCodecType.h264,
            AVVideoWidthKey: width,
            AVVideoHeightKey: height,
            AVVideoColorPropertiesKey: [
                AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
                AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
                AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2,
            ],
        ]
        // HEVC in software is slow and hot: only on a Mac that does it in hardware (the probe falls back to H.264).
        if hevc { settings[AVVideoEncoderSpecificationKey] = [kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder as String: true] }
        settings[AVVideoCompressionPropertiesKey] = compression
        if !writer.canApply(outputSettings: settings, forMediaType: .video) {
            // Some encoders take no quality setting: cap the bit rate instead (screens compress very well).
            compression.removeValue(forKey: AVVideoQualityKey)
            compression[AVVideoAverageBitRateKey] = max(200_000, Int(Double(width * height) * 0.3))
            settings[AVVideoCompressionPropertiesKey] = compression
            guard writer.canApply(outputSettings: settings, forMediaType: .video) else {
                throw error("this Mac's \(hevc ? "HEVC" : "H.264") encoder does not take these settings")
            }
        }
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: width,
            kCVPixelBufferHeightKey as String: height,
        ])
        guard writer.canAdd(input) else { throw error("cannot add a video track") }
        writer.add(input)
        guard writer.startWriting() else { throw writer.error ?? error("cannot start writing") }
        writer.startSession(atSourceTime: .zero)
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        do {
            for i in 0..<count {
                guard let img = picture(i) else { throw error("a picture could not be read") }
                while !input.isReadyForMoreMediaData && writer.status == .writing { Thread.sleep(forTimeInterval: 0.005) }
                guard writer.status == .writing, let pool = adaptor.pixelBufferPool else { throw writer.error ?? error("the encoder stopped") }
                var buffer: CVPixelBuffer?
                guard CVPixelBufferPoolCreatePixelBuffer(nil, pool, &buffer) == kCVReturnSuccess, let pb = buffer else {
                    throw error("out of video memory")
                }
                CVPixelBufferLockBaseAddress(pb, [])
                let ctx = CGContext(data: CVPixelBufferGetBaseAddress(pb), width: width, height: height, bitsPerComponent: 8,
                                    bytesPerRow: CVPixelBufferGetBytesPerRow(pb), space: space,
                                    bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
                if let ctx {
                    ctx.setFillColor(CGColor(red: 0, green: 0, blue: 0, alpha: 1))
                    ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
                    if abs(img.width - width) <= 1 && abs(img.height - height) <= 1 {
                        // Top-left aligned: an odd last row or column falls off the bottom or right.
                        ctx.draw(img, in: CGRect(x: 0, y: height - img.height, width: img.width, height: img.height))
                    } else {
                        ctx.interpolationQuality = .high
                        ctx.draw(img, in: CGRect(x: 0, y: 0, width: width, height: height))
                    }
                }
                CVPixelBufferUnlockBaseAddress(pb, [])
                guard ctx != nil else { throw error("cannot draw into a video frame") }
                guard adaptor.append(pb, withPresentationTime: Compaction.time(ofFrame: i)) else {
                    throw writer.error ?? error("the encoder refused frame \(i)")
                }
            }
        } catch let e {
            writer.cancelWriting()
            try? FileManager.default.removeItem(at: url)
            throw e
        }
        input.markAsFinished()
        // The last frame lasts one frame, so the video is `count` frames long.
        writer.endSession(atSourceTime: Compaction.time(ofFrame: count))
        let done = DispatchSemaphore(value: 0)
        writer.finishWriting { done.signal() }
        done.wait()
        guard writer.status == .completed else {
            try? FileManager.default.removeItem(at: url)
            throw writer.error ?? error("the video could not be finished")
        }
    }

    static func error(_ message: String) -> NSError {
        NSError(domain: "openrewind", code: 31, userInfo: [NSLocalizedDescriptionKey: message])
    }
}

/// Runs the compactor in the background: two minutes after launch, every 15 minutes, and soon after a
/// delete that left a video with frames to drop (also when that delete comes during a pass: one more pass
/// follows). One pass at a time, at utility priority.
final class VideoJob {
    let codec: AVCodec
    private let compactor: VideoCompactor
    private let queue = DispatchQueue(label: "openrewind.video", qos: .utility)
    private var timer: DispatchSourceTimer?
    private let state = NSLock()
    private var running = false
    /// A kick came while a pass was running: run again when it ends.
    private var pending = false
    private var stopping = false
    private var on: Bool

    init(db: Database, dataDir: URL, enabled: Bool) {
        let c = AVCodec()
        codec = c
        compactor = VideoCompactor(db: db, codec: c, dataDir: dataDir)
        on = enabled
        db.videosNeedRewrite = { [weak self] in self?.kick() }
    }

    /// Compress Older Recordings. Off: no new videos, but rewrites after deletes still happen and videos still play.
    var enabled: Bool {
        get { state.lock(); defer { state.unlock() }; return on }
        set { state.lock(); on = newValue; state.unlock() }
    }

    private var isStopping: Bool { state.lock(); defer { state.unlock() }; return stopping }

    /// Why this Mac can't make videos (shown in the menu), or nil when it can or that is not known yet.
    var unavailable: String? { codec.checked && codec.usable == nil ? codec.probeError ?? "no video encoder" : nil }

    func start() {
        let t = DispatchSource.makeTimerSource(queue: .global(qos: .utility))
        t.schedule(deadline: .now() + 120, repeating: 900)
        t.setEventHandler { [weak self] in self?.kick() }
        t.resume()
        timer = t
    }

    /// Start a pass now; when one is running, run another right after it.
    func kick() {
        state.lock()
        if stopping { state.unlock(); return }
        if running { pending = true; state.unlock(); return }
        running = true
        state.unlock()
        queue.async { [self] in
            // Decrypted copies (audio for Whisper, videos) a crash left behind: not only at launch, as the
            // app can run for weeks.
            RecordingFile.cleanTempCopies()
            while true {
                let compress = enabled && codec.usable != nil
                let r = compactor.runOnce(compress: compress, now: Date(), keepGoing: { !self.isStopping }, keepCompressing: { self.enabled })
                if r.frames + r.rewritten + r.unpacked + r.failed > 0 {
                    log("compress older recordings: \(r.frames) frames into \(r.videos) videos (\(codec.usable ?? "-")), \(r.rewritten) rewritten, " +
                        "\(r.unpacked) turned back into pictures, \(r.failed) failed")
                }
                state.lock()
                if pending && !stopping {
                    pending = false
                    state.unlock()
                    continue
                }
                running = false
                pending = false
                state.unlock()
                return
            }
        }
    }

    func stop() {
        state.lock(); stopping = true; state.unlock()
        timer?.cancel()
    }
}
