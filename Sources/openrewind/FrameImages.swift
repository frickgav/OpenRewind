import AppKit
import AVFoundation
import CoreMedia
import CoreVideo
import Foundation
import ImageIO
import VideoToolbox

/// The one way to get a frame's picture, whether it is a still file or a frame inside a video
/// (Compress Older Recordings). The timeline, search highlights, the local API and Ask AI all read through here.
/// Video frames are decoded by a few cached image generators that seek exactly (zero tolerance) and kept
/// in a small LRU, so scrubbing stays quick. Windows has the same in FrameImages.cs.
final class FrameImages {
    static let shared = FrameImages()

    private let lock = NSLock()
    /// Decoded video frames by "path#frame".
    private let frames = Lru<String, CGImage>(capacity: 48)
    /// Open videos, so the next frame of the same one does not open the file again.
    private let generators = Lru<String, AVAssetImageGenerator>(capacity: 4)

    /// The picture, or nil when it is gone or can't be read.
    func image(_ f: FrameSource) -> CGImage? {
        guard f.isVideo else { return f.stillPath.isEmpty ? nil : ImageTools.load(URL(fileURLWithPath: f.stillPath)) }
        let key = Self.key(f.videoPath, f.videoFrame)
        lock.lock(); defer { lock.unlock() }
        if let hit = frames.get(key) { return hit }
        let gen: AVAssetImageGenerator
        if let g = generators.get(f.videoPath) {
            gen = g
        } else {
            guard FileManager.default.fileExists(atPath: f.videoPath) else { return nil }
            gen = Self.generator(f.videoPath)
            generators.set(f.videoPath, gen)
        }
        guard let img = Self.frame(gen, at: CMTime(seconds: f.videoTime, preferredTimescale: 600)) else {
            log("cannot read frame \(f.videoFrame) of \((f.videoPath as NSString).lastPathComponent)")
            generators.removeAll { $0 == f.videoPath }
            return nil
        }
        frames.set(key, img)
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
            guard !f.stillPath.isEmpty, let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: f.stillPath) as CFURL, nil) else { return nil }
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
        guard !path.isEmpty, let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
              let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any],
              let w = props[kCGImagePropertyPixelWidth] as? Int, let h = props[kCGImagePropertyPixelHeight] as? Int else { return nil }
        return (w, h)
    }

    /// Close a video and forget its frames (before it is deleted or replaced).
    func release(_ videoPath: String) {
        lock.lock(); defer { lock.unlock() }
        generators.removeAll { $0 == videoPath }
        let prefix = videoPath + "#"
        frames.removeAll { $0.hasPrefix(prefix) }
    }

    private static func key(_ path: String, _ frame: Int) -> String { path + "#\(frame)" }

    /// A small gray copy for checking a video frame against its screenshot. The screenshot is first cut to
    /// the even size the video has, so both are shrunk the same way (thin lines of text would otherwise
    /// land on different thumbnail pixels).
    static func comparable(_ image: CGImage) -> [UInt8] {
        let w = Compaction.even(image.width), h = Compaction.even(image.height)
        let cut = (w == image.width && h == image.height) ? image : image.cropping(to: CGRect(x: 0, y: 0, width: w, height: h)) ?? image
        return ImageTools.thumbnail(cut)
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
/// Read back with AVAssetImageGenerator.
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
        if frame.isVideo { return FrameImages.shared.image(frame).map { ($0.width, $0.height) } }
        return FrameImages.stillSize(frame.stillPath)
    }

    func write(_ frames: [FrameSource], width: Int, height: Int, to path: String) throws -> String {
        guard let codec = usable else { throw Self.error("no video encoder on this Mac (\(probeError ?? "unknown"))") }
        try Self.encode(count: frames.count, width: width, height: height, hevc: codec == "hevc", to: path) {
            FrameImages.shared.image(frames[$0])
        }
        return codec
    }

    func verify(_ path: String, _ frames: [FrameSource]) -> Bool {
        guard !frames.isEmpty else { return false }
        let asset = AVURLAsset(url: URL(fileURLWithPath: path), options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
        guard let duration = FrameImages.wait({ try await asset.load(.duration) }),
              duration.seconds >= (Double(frames.count) - 0.5) / Compaction.fps else {
            log("\((path as NSString).lastPathComponent) is shorter than its \(frames.count) frames")
            return false
        }
        let gen = FrameImages.generator(path)
        for i in Set([0, frames.count - 1]).sorted() {
            guard let decoded = FrameImages.frame(gen, at: Compaction.time(ofFrame: i)),
                  let source = FrameImages.shared.image(frames[i]) else { return false }
            guard decoded.width == Compaction.even(source.width), decoded.height == Compaction.even(source.height) else {
                log("\((path as NSString).lastPathComponent): frame \(i) is \(decoded.width)x\(decoded.height), expected \(source.width)x\(source.height)")
                return false
            }
            guard Compaction.similar(FrameImages.comparable(decoded), FrameImages.comparable(source)) else {
                log("\((path as NSString).lastPathComponent): frame \(i) does not look like its picture")
                return false
            }
        }
        return true
    }

    func release(_ path: String) { FrameImages.shared.release(path) }

    private func probe() -> (codec: String?, error: String?) {
        lock.lock(); defer { lock.unlock() }
        if let p = probed { return p }
        let p = Self.runProbe()
        probed = p
        return p
    }

    /// Encode and decode a short test clip: hardware HEVC first, then H.264. Returns the codec that worked.
    private static func runProbe() -> (codec: String?, error: String?) {
        var error: String?
        let pictures = (0..<3).compactMap(testPicture)
        guard pictures.count == 3 else { return (nil, "cannot draw test pictures") }
        for hevc in [true, false] {
            let path = FileManager.default.temporaryDirectory
                .appendingPathComponent("openrewind-probe-\(getpid())-\(hevc ? "hevc" : "h264").mp4").path
            defer { try? FileManager.default.removeItem(atPath: path) }
            do {
                try encode(count: 3, width: 320, height: 180, hevc: hevc, to: path) { pictures[$0] }
                let gen = FrameImages.generator(path)
                for i in [0, 2, 1] {
                    guard let back = FrameImages.frame(gen, at: Compaction.time(ofFrame: i)), back.width == 320, back.height == 180,
                          Compaction.similar(ImageTools.thumbnail(back), ImageTools.thumbnail(pictures[i])) else {
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

    /// Something like a screen: a title bar and a box that moves.
    static func testPicture(_ i: Int) -> CGImage? {
        guard let ctx = CGContext(data: nil, width: 320, height: 180, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else { return nil }
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: 320, height: 180))
        ctx.setFillColor(CGColor(red: 0.27, green: 0.51, blue: 0.71, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 156, width: 320, height: 24))
        ctx.setFillColor(CGColor(red: 1, green: 0.65, blue: 0, alpha: 1))
        ctx.fill(CGRect(x: 20 + i * 90, y: 40, width: 80, height: 80))
        ctx.setFillColor(CGColor(red: 0, green: 0, blue: 0, alpha: 1))
        for line in 0..<4 { ctx.fill(CGRect(x: 10, y: 10 + line * 7, width: 120 + (line * 37 + i * 50) % 160, height: 3)) }
        return ctx.makeImage()
    }

    /// Writes `count` pictures as an MP4: picture i at i / fps seconds, width x height pixels (a picture a
    /// pixel bigger loses its last row or column). Keyframe every Compaction.keyframeInterval frames and no
    /// B-frames, so one frame never needs more than that many decoded to show it.
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
                    // Top-left aligned: an odd last row or column falls off the bottom or right.
                    ctx.draw(img, in: CGRect(x: 0, y: height - img.height, width: img.width, height: img.height))
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
/// delete that left a video with frames to drop. One pass at a time, at utility priority.
final class VideoJob {
    let codec: AVCodec
    private let compactor: VideoCompactor
    private let queue = DispatchQueue(label: "openrewind.video", qos: .utility)
    private var timer: DispatchSourceTimer?
    private let state = NSLock()
    private var running = false
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

    /// Start a pass now unless one is running.
    func kick() {
        state.lock()
        if running || stopping { state.unlock(); return }
        running = true
        state.unlock()
        queue.async { [self] in
            defer { state.lock(); running = false; state.unlock() }
            let compress = enabled && codec.usable != nil
            let r = compactor.runOnce(compress: compress, now: Date(), keepGoing: { !self.isStopping }, keepCompressing: { self.enabled })
            if r.frames + r.rewritten + r.failed > 0 {
                log("compress older recordings: \(r.frames) frames into \(r.videos) videos (\(codec.usable ?? "-")), \(r.rewritten) rewritten, \(r.failed) failed")
            }
        }
    }

    func stop() {
        state.lock(); stopping = true; state.unlock()
        timer?.cancel()
    }
}
