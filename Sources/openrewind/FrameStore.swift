import AVFoundation
import CoreGraphics
import CoreVideo
import Foundation
import ImageIO
import SQLite3

/// Screenshots are saved as single images first. Once they are a few minutes old, `FramePacker`
/// rewrites them into HEVC video chunks, which are many times smaller because most of the screen
/// stays the same from one frame to the next. A frame inside a chunk is stored in `snapshot_path`
/// as "<chunk>.mp4#<index>".
enum FrameStore {
    /// Each frame sits one second after the previous one inside a chunk. The real times are in the database.
    static let frameDuration = CMTime(value: 1, timescale: 1)
    /// A keyframe every this many frames: any frame decodes from at most this many, so scrubbing stays quick.
    static let keyframeEvery = 15
    /// Held while chunks are written or rewritten, so a delete never races a pack.
    static let packLock = NSLock()

    /// "<file>#<index>" → (file, index); a plain image path → (path, nil).
    static func split(_ path: String) -> (file: String, index: Int?) {
        guard let hash = path.lastIndex(of: "#"), let i = Int(path[path.index(after: hash)...]) else { return (path, nil) }
        return (String(path[..<hash]), i)
    }

    static func ref(_ file: String, _ index: Int) -> String { "\(file)#\(index)" }

    private static var generators: [String: AVAssetImageGenerator] = [:]
    private static var generatorOrder: [String] = []
    private static let readLock = NSLock()

    /// The picture for a stored frame: an image file, or one frame of a video chunk.
    static func image(_ path: String) -> CGImage? {
        let (file, index) = split(path)
        guard let index else { return ImageTools.load(URL(fileURLWithPath: file)) }
        readLock.lock(); defer { readLock.unlock() }
        let gen: AVAssetImageGenerator
        if let g = generators[file] {
            gen = g
        } else {
            guard FileManager.default.fileExists(atPath: file) else { return nil }
            gen = AVAssetImageGenerator(asset: AVURLAsset(url: URL(fileURLWithPath: file)))
            gen.requestedTimeToleranceBefore = .zero
            gen.requestedTimeToleranceAfter = .zero
            generators[file] = gen
            generatorOrder.append(file)
            if generatorOrder.count > 4 { generators.removeValue(forKey: generatorOrder.removeFirst()) }
        }
        return try? gen.copyCGImage(at: CMTimeMultiply(frameDuration, multiplier: Int32(index)), actualTime: nil)
    }

    /// Stop reading a chunk that is about to be deleted or replaced.
    static func forget(_ file: String) {
        readLock.lock(); defer { readLock.unlock() }
        generators.removeValue(forKey: file)
        generatorOrder.removeAll { $0 == file }
    }

    /// Pixel size of an image file without decoding it.
    static func imageSize(_ path: String) -> (Int, Int)? {
        guard let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
              let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any],
              let w = props[kCGImagePropertyPixelWidth] as? Int, let h = props[kCGImagePropertyPixelHeight] as? Int else { return nil }
        return (w, h)
    }

    /// Write `count` frames into one HEVC .mp4 at `url`. HEVC needs even sizes, so odd ones lose a pixel.
    static func encode(count: Int, width: Int, height: Int, to url: URL, frame: (Int) -> CGImage?) throws {
        let w = width & ~1, h = height & ~1
        try? FileManager.default.removeItem(at: url)
        let writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        func settings(_ rate: [String: Any]) -> [String: Any] {
            [AVVideoCodecKey: AVVideoCodecType.hevc, AVVideoWidthKey: w, AVVideoHeightKey: h,
             AVVideoCompressionPropertiesKey: rate.merging([
                AVVideoMaxKeyFrameIntervalKey: keyframeEvery,
                AVVideoAllowFrameReorderingKey: false,
                AVVideoExpectedSourceFrameRateKey: 1,
             ]) { a, _ in a }]
        }
        // Constant quality where the encoder supports it; else a bitrate that keeps text sharp.
        // (An unsupported setting would throw an Objective-C exception, so ask first.)
        var out = settings([AVVideoQualityKey: 0.6])
        if !writer.canApply(outputSettings: out, forMediaType: .video) {
            out = settings([AVVideoAverageBitRateKey: max(100_000, w * h / 16)])
        }
        guard writer.canApply(outputSettings: out, forMediaType: .video) else { throw packError("this Mac cannot encode HEVC") }
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: out)
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: w,
            kCVPixelBufferHeightKey as String: h,
        ])
        writer.add(input)
        guard writer.startWriting() else { throw writer.error ?? packError("cannot start \(url.lastPathComponent)") }
        writer.startSession(atSourceTime: .zero)
        for i in 0..<count {
            while !input.isReadyForMoreMediaData { Thread.sleep(forTimeInterval: 0.005) }
            guard let pool = adaptor.pixelBufferPool else { throw writer.error ?? packError("no pixel buffer pool") }
            var buf: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, pool, &buf)
            guard let buf else { throw packError("no pixel buffer") }
            CVPixelBufferLockBaseAddress(buf, [])
            if let ctx = CGContext(data: CVPixelBufferGetBaseAddress(buf), width: w, height: h, bitsPerComponent: 8,
                                   bytesPerRow: CVPixelBufferGetBytesPerRow(buf), space: CGColorSpaceCreateDeviceRGB(),
                                   bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) {
                ctx.setFillColor(CGColor(gray: 0, alpha: 1))
                ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
                if let img = frame(i) { ctx.draw(img, in: CGRect(x: 0, y: 0, width: w, height: h)) }
            }
            CVPixelBufferUnlockBaseAddress(buf, [])
            if !adaptor.append(buf, withPresentationTime: CMTimeMultiply(frameDuration, multiplier: Int32(i))) {
                throw writer.error ?? packError("cannot add frame \(i)")
            }
        }
        input.markAsFinished()
        let done = DispatchSemaphore(value: 0)
        writer.finishWriting { done.signal() }
        done.wait()
        guard writer.status == .completed else { throw writer.error ?? packError("cannot finish \(url.lastPathComponent)") }
    }

    static func packError(_ msg: String) -> NSError {
        NSError(domain: "openrewind", code: 30, userInfo: [NSLocalizedDescriptionKey: msg])
    }
}

/// Every few minutes, turns screenshots older than `minAge` into video chunks and deletes the images.
final class FramePacker {
    let db: Database
    private var timer: DispatchSourceTimer?
    private let queue = DispatchQueue(label: "openrewind.packer", qos: .utility)
    /// The newest frames stay single images: they show instantly and "Delete last 15 minutes" stays cheap.
    static let minAge: TimeInterval = 10 * 60
    static let maxFrames = 300

    init(db: Database) { self.db = db }

    func start() {
        guard timer == nil else { return }
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 30, repeating: 10 * 60)
        t.setEventHandler { [weak self] in self?.packOld() }
        t.resume()
        timer = t
    }

    func packOld(minAge: TimeInterval = FramePacker.minAge) {
        let cutoff = Time.iso.string(from: Date().addingTimeInterval(-minAge))
        let rows = db.idsAndPaths("SELECT id, snapshot_path FROM frames WHERE timestamp < ? AND snapshot_path != '' AND instr(snapshot_path, '#') = 0 ORDER BY timestamp ASC",
                                  [.text(cutoff)])
            .filter { FileManager.default.fileExists(atPath: $0.path) }
        guard !rows.isEmpty else { return }
        // One chunk per run of frames from the same day folder and screen size.
        var group: [(id: Int64, path: String)] = []
        var key = ""
        var size = (0, 0)
        var packed = 0
        func flush() {
            if !group.isEmpty, size.0 >= 2, size.1 >= 2 { packed += pack(group, width: size.0, height: size.1) }
            group = []
        }
        for r in rows {
            guard let s = FrameStore.imageSize(r.path) else { continue }
            let k = (r.path as NSString).deletingLastPathComponent + " \(s.0)x\(s.1)"
            if k != key || group.count >= Self.maxFrames { flush(); key = k; size = s }
            group.append(r)
        }
        flush()
        if packed > 0 { log("packed \(packed) screenshots into video") }
    }

    /// Encode `group` into one chunk, point the rows at it, delete the images. Returns frames packed.
    private func pack(_ group: [(id: Int64, path: String)], width: Int, height: Int) -> Int {
        FrameStore.packLock.lock(); defer { FrameStore.packLock.unlock() }
        let first = URL(fileURLWithPath: group[0].path)
        let chunk = first.deletingPathExtension().appendingPathExtension("mp4")
        let tmp = first.deletingPathExtension().appendingPathExtension("tmp.mp4")
        do {
            try FrameStore.encode(count: group.count, width: width, height: height, to: tmp) { i in
                autoreleasepool { ImageTools.load(URL(fileURLWithPath: group[i].path)) }
            }
            try? FileManager.default.removeItem(at: chunk)
            try FileManager.default.moveItem(at: tmp, to: chunk)
        } catch {
            try? FileManager.default.removeItem(at: tmp)
            log("could not pack \(first.lastPathComponent): \(error.localizedDescription)")
            return 0
        }
        // Rows deleted meanwhile cannot be: deletes wait for packLock.
        db.repoint(group.enumerated().map { (id: $0.element.id, old: $0.element.path, new: FrameStore.ref(chunk.path, $0.offset)) })
        for r in group { try? FileManager.default.removeItem(atPath: r.path) }
        return group.count
    }

    /// Some frames of `chunk` were deleted. Their pictures must go too, so write the rest into a
    /// new chunk (or remove the chunk when nothing is left). Caller holds `FrameStore.packLock`.
    static func dropDeleted(from chunk: String, db: Database) {
        let left = db.idsAndPaths("SELECT id, snapshot_path FROM frames WHERE substr(snapshot_path, 1, ?) = ?",
                                  [.int(Int64(chunk.unicodeScalars.count + 1)), .text(chunk + "#")])
            .sorted { (FrameStore.split($0.path).index ?? 0) < (FrameStore.split($1.path).index ?? 0) }
        FrameStore.forget(chunk)
        defer { FrameStore.forget(chunk); try? FileManager.default.removeItem(atPath: chunk) }
        guard let first = left.first, let img = FrameStore.image(first.path) else {
            if !left.isEmpty { log("could not read \(chunk); its remaining frames are lost") }
            return
        }
        let base = URL(fileURLWithPath: chunk).deletingPathExtension()
        let fresh = base.deletingLastPathComponent()
            .appendingPathComponent(base.lastPathComponent.components(separatedBy: "~")[0] + "~\(Int(Date().timeIntervalSince1970)).mp4")
        do {
            try FrameStore.encode(count: left.count, width: img.width, height: img.height, to: fresh) { i in
                autoreleasepool { FrameStore.image(left[i].path) }
            }
        } catch {
            try? FileManager.default.removeItem(at: fresh)
            log("could not rewrite \(chunk): \(error.localizedDescription)")
            return
        }
        db.repoint(left.enumerated().map { (id: $0.element.id, old: $0.element.path, new: FrameStore.ref(fresh.path, $0.offset)) })
    }
}

extension Database {
    func idsAndPaths(_ sql: String, _ args: [Arg]) -> [(id: Int64, path: String)] {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return [] }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        var out: [(id: Int64, path: String)] = []
        while sqlite3_step(st) == SQLITE_ROW { out.append((sqlite3_column_int64(st, 0), col(st, 1))) }
        return out
    }

    /// Move frames to new snapshot paths in one transaction; a row whose path changed meanwhile is left alone.
    func repoint(_ moves: [(id: Int64, old: String, new: String)]) {
        try? exec("BEGIN")
        for m in moves {
            run("UPDATE frames SET snapshot_path = ? WHERE id = ? AND snapshot_path = ?", [.text(m.new), .int(m.id), .text(m.old)])
        }
        try? exec("COMMIT")
    }
}
