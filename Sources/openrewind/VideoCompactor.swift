import CoreMedia
import Foundation

/// Encodes and decodes the videos for Compress Older Recordings (AVFoundation, see FrameImages.swift).
protocol VideoCodec: AnyObject {
    /// Pixel size of a frame's picture, or nil when it cannot be read.
    func measure(_ frame: FrameSource) -> (width: Int, height: Int)?
    /// Write these frames, in order, as a new video at `path`: frame i at i / Compaction.fps seconds,
    /// width x height pixels (pictures a pixel bigger are cut to fit; bigger ones are scaled down, see
    /// Compaction.fit). Returns the codec ("hevc" or "h264").
    func write(_ frames: [FrameSource], width: Int, height: Int, to path: String) throws -> String
    /// True when the video at `path` opens, is width x height and long enough, and a few of its frames
    /// (Compaction.checkedFrames: first, last and two in between) look like those pictures.
    func verify(_ path: String, _ frames: [FrameSource], width: Int, height: Int) -> Bool
    /// Save one frame's picture as a JPEG still (a video that can't be written again is turned back into
    /// stills, see VideoCompactor.unpack).
    func writeStill(_ frame: FrameSource, to path: String) throws
    /// Forget anything that has this video open, before it is deleted.
    func release(_ path: String)
}

/// A few minutes of one screen's stills that become one video.
struct SegmentPlan {
    var windowStart: Date
    var display: String
    var frames: [CompactCandidate]
}

/// Grouping rules for Compress Older Recordings, the same on Windows (Compaction.cs, where they are tested).
enum Compaction {
    /// Frame i of a video is shown at i / fps seconds.
    static let fps = 1.0
    /// Frames younger than this stay stills (the recent timeline stays as fast as before).
    static let minAge: TimeInterval = 3600
    /// One video per screen per this much time. Windows start on the clock (:00, :05, ...), so one never spans midnight.
    static let window: TimeInterval = 300
    /// Frames looked at per database read.
    static let batchSize = 2000
    /// Most frames in one video (a short capture interval could make more in one window).
    static let maxFrames = 600
    /// Keyframe every this many frames: scrubbing decodes at most this many to show one.
    static let keyframeInterval = 20
    /// Largest video every H.264 encoder takes (level 5.1). Bigger screenshots are scaled down to fit.
    static let maxWidth = 4096, maxHeight = 2304
    /// A still that failed to go into a video this many times stays a still (a picture or size the
    /// encoder can't take would otherwise be tried again on every launch).
    static let maxTries = 3

    static func windowStart(_ t: Date, window: TimeInterval = window) -> Date {
        Date(timeIntervalSince1970: (t.timeIntervalSince1970 / window).rounded(.down) * window)
    }

    static func time(ofFrame i: Int) -> CMTime { CMTime(seconds: Double(i) / fps, preferredTimescale: 600) }

    /// Group stills (oldest first) by time window and screen. Windows that end after `cutoff` are left for
    /// later, because they may still get frames; so is the last window of a full batch, which may go on past it.
    static func plan(_ batch: [CompactCandidate], cutoff: Date, window: TimeInterval = window, batchFull: Bool) -> [SegmentPlan] {
        var plans: [SegmentPlan] = []
        var index: [String: Int] = [:]
        for c in batch {
            let start = windowStart(c.time, window: window)
            if start.addingTimeInterval(window) > cutoff { break }
            let key = "\(start.timeIntervalSince1970)\u{1F}\(c.display)"
            if let i = index[key] { plans[i].frames.append(c) } else {
                index[key] = plans.count
                plans.append(SegmentPlan(windowStart: start, display: c.display, frames: [c]))
            }
        }
        if batchFull, let last = plans.last?.windowStart {
            let complete = plans.filter { $0.windowStart != last }
            // One window holding a whole batch: take it as it is, the rest becomes another video.
            if !complete.isEmpty { return complete }
        }
        return plans
    }

    /// Runs of frames with the same pixel size (a video has one size), at most `max` frames each.
    /// Frames whose size is unknown are left out.
    static func splitBySize<T>(_ items: [(item: T, size: (width: Int, height: Int)?)], max: Int = maxFrames) -> [(width: Int, height: Int, frames: [T])] {
        var runs: [(width: Int, height: Int, frames: [T])] = []
        for (item, size) in items {
            guard let size, size.width >= 2, size.height >= 2 else { continue }
            if let last = runs.last, last.width == size.width, last.height == size.height, last.frames.count < max {
                runs[runs.count - 1].frames.append(item)
            } else {
                runs.append((size.width, size.height, [item]))
            }
        }
        return runs
    }

    /// Encoders want even sizes: an odd last row or column is cut off.
    static func even(_ n: Int) -> Int { Swift.max(2, n & ~1) }

    /// The video size for pictures of w x h: the same cut to even, or scaled down to fit maxWidth x maxHeight.
    static func fit(_ w: Int, _ h: Int) -> (width: Int, height: Int) {
        let scale = Swift.min(1, Swift.min(Double(maxWidth) / Double(w), Double(maxHeight) / Double(h)))
        return scale >= 1 ? (even(w), even(h)) : (even(Int(Double(w) * scale)), even(Int(Double(h) * scale)))
    }

    /// Which frames of a new video are checked against their pictures: the first, the last, and two in between.
    static func checkedFrames(_ count: Int) -> [Int] {
        var seen = Set<Int>()
        return [0, count / 3, 2 * count / 3, count - 1].filter { $0 >= 0 && $0 < count && seen.insert($0).inserted }
    }

    /// data/video/2026-10-19/2026-10-19T14-05-00Z-<screen>-<random>.mp4. The random part means a name is
    /// never used twice, so a timeline still holding an old frame's place can't show another video's picture.
    static func segmentPath(videoDir: String, windowStart: Date, display: String, taken: (String) -> Bool) -> String {
        let iso = Time.iso.string(from: windowStart)
        let dir = (videoDir as NSString).appendingPathComponent(String(iso.prefix(10)))
        let safe = String(display.map { $0.isLetter || $0.isNumber ? $0 : "-" }).trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        let name = String(iso.prefix(19)).replacingOccurrences(of: ":", with: "-") + "Z" + (safe.isEmpty ? "" : "-" + String(safe.prefix(40)))
        while true {
            let suffix = String(UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased().prefix(8))
            let p = (dir as NSString).appendingPathComponent(name + "-" + suffix + ".mp4")
            if !taken(p) { return p }
        }
    }

    /// Where a video is written before it is checked. Never referenced by the database.
    static func tempPath(_ path: String) -> String { String(path.dropLast(4)) + tempSuffix }
    static let tempSuffix = ".tmp.mp4"

    /// Two small gray thumbnails show the same picture: the average pixel is off by at most `tolerance`
    /// levels (video is lossy, so they are never exactly equal).
    static func similar(_ a: [UInt8], _ b: [UInt8], tolerance: Double = 12) -> Bool {
        guard a.count == b.count, !a.isEmpty else { return false }
        var sum = 0
        for i in 0..<a.count { sum += abs(Int(a[i]) - Int(b[i])) }
        return Double(sum) / Double(a.count) <= tolerance
    }
}

/// Compress Older Recordings: turns stills older than an hour into short videos, crash-safely.
/// Each video is written to a temp file, read back and checked, renamed, then the frames are pointed
/// at it in one transaction, and only then are the stills deleted. Stopping at any point loses nothing:
/// the next pass deletes temp and unreferenced videos and finishes deleting stills already in a video.
/// It also rewrites videos that lost some of their frames to a delete, even while compression is off;
/// a video that can't be written again is turned back into stills, so deleted pictures never stay in one.
/// Windows has the same steps in Compaction.cs, where they are tested.
final class VideoCompactor {
    let db: Database
    let codec: VideoCodec
    let dataDir: String
    let videoDir: String
    /// Videos no row points at are deleted once they are this old (a pass that stopped before committing).
    var orphanAge: TimeInterval = 600
    /// Frames up to this time (the stored timestamp, as it is) were tried since the app started.
    private var cursor = ""
    private let fm = FileManager.default

    struct Result { var frames = 0, videos = 0, rewritten = 0, unpacked = 0, failed = 0 }

    init(db: Database, codec: VideoCodec, dataDir: URL) {
        self.db = db
        self.codec = codec
        self.dataDir = dataDir.path
        videoDir = dataDir.appendingPathComponent("video").path
    }

    /// One pass. `compress`: also turn older stills into video (the setting). `keepGoing` is asked between
    /// videos (quitting stops the pass soon); `keepCompressing` only between new videos (the setting was
    /// turned off: rewrites after a delete still finish).
    func runOnce(compress: Bool, now: Date, keepGoing: () -> Bool = { true }, keepCompressing: () -> Bool = { true }) -> Result {
        var r = Result()
        // The database could not be read: do nothing this pass rather than act on a guess.
        guard cleanUp() else { return r }
        for seg in db.segments(onlyNeedingRewrite: true) {
            guard keepGoing() else { break }
            switch rewrite(seg) {
            case true?: r.rewritten += 1
            // Its frames changed while it was written (another delete): it stays marked, the next pass does it.
            case nil: break
            // It could not be written again: keep its frames as stills rather than deleted ones in a video.
            case false?: if unpack(seg) { r.unpacked += 1 } else { r.failed += 1 }
            }
        }
        guard compress else { return r }
        let cutoff = now.addingTimeInterval(-Compaction.minAge)
        while keepGoing() && keepCompressing() {
            let batch = db.compactCandidates(before: cutoff, after: cursor, limit: Compaction.batchSize)
            let plans = Compaction.plan(batch, cutoff: cutoff, batchFull: batch.count == Compaction.batchSize)
            if plans.isEmpty { break }
            let before = cursor
            for plan in plans {
                guard keepGoing() && keepCompressing() else { return r }
                let (n, v, f) = compact(plan)
                r.frames += n; r.videos += v; r.failed += f
                if let last = plan.frames.last, last.timestamp > cursor { cursor = last.timestamp }
            }
            if cursor == before { break }  // never read the same batch twice
        }
        return r
    }

    /// Finish what a stopped pass left: temp files, videos no row points at, stills already in a video,
    /// and videos that lost frames without being marked (a delete cut short, or one by another process).
    /// False when the database could not be read (nothing is deleted then).
    private func cleanUp() -> Bool {
        if fm.fileExists(atPath: videoDir) {
            guard let known = db.segmentPaths() else {
                log("compress older recordings: cannot read the list of videos; skipping this pass")
                return false
            }
            let files = (fm.enumerator(atPath: videoDir)?.allObjects as? [String] ?? []).filter { $0.hasSuffix(".mp4") }
            for rel in files {
                let p = (videoDir as NSString).appendingPathComponent(rel)
                let temp = p.hasSuffix(Compaction.tempSuffix)
                if !temp && known.contains(p) { continue }
                let modified = (try? fm.attributesOfItem(atPath: p)[.modificationDate] as? Date) ?? .distantPast
                if !temp && Date().timeIntervalSince(modified) < orphanAge { continue }
                // Asked again for this one file: delete only what no video row and no frame points at.
                if !temp && db.isVideoReferenced(p) != false { continue }
                delete(p)
            }
            for d in (try? fm.contentsOfDirectory(atPath: videoDir)) ?? [] {
                let p = (videoDir as NSString).appendingPathComponent(d)
                if (try? fm.contentsOfDirectory(atPath: p))?.isEmpty == true { try? fm.removeItem(atPath: p) }
            }
        }
        let left = db.leftoverStills()
        if !left.isEmpty {
            for l in left { delete(l.path) }
            db.clearStills(left.map(\.id))
        }
        let marked = db.markShrunkSegments()
        if marked > 0 { log("compress older recordings: \(marked) videos lost frames without being marked; writing them again") }
        return true
    }

    private func compact(_ plan: SegmentPlan) -> (frames: Int, videos: Int, failed: Int) {
        let runs = Compaction.splitBySize(plan.frames.map { (item: $0, size: codec.measure(FrameSource(stillPath: $0.stillPath))) })
        var frames = 0, videos = 0, failed = 0
        for run in runs {
            let path = newPath(plan.windowStart, plan.display)
            let ids = run.frames.map(\.id)
            let sourceBytes = run.frames.reduce(Int64(0)) { $0 + size($1.stillPath) }
            let (w, h) = Compaction.fit(run.width, run.height)
            guard let first = run.frames.first, let last = run.frames.last,
                  let (name, bytes) = write(path, run.frames.map { FrameSource(stillPath: $0.stillPath) }, width: w, height: h) else {
                db.noteCompactFailure(ids)
                failed += 1; continue
            }
            let seg = VideoSegment(id: 0, path: path, display: plan.display, startTime: first.timestamp,
                                   endTime: last.timestamp, width: w, height: h, frameCount: ids.count,
                                   codec: name, bytes: bytes, sourceBytes: sourceBytes)
            guard db.commitSegment(seg, ids: ids) else {
                // A frame was deleted while this was written: drop the video, the rest goes next launch.
                delete(path)
                continue
            }
            for c in run.frames { delete(c.stillPath) }
            db.clearStills(ids)
            frames += ids.count
            videos += 1
        }
        return (frames, videos, failed)
    }

    /// Write the frames of a video that lost some to a delete into a new video, then swap it in.
    /// Decoding and encoding again loses a little quality; that happens about once per video.
    /// True when done, false when the video could not be written, nil when its frames changed meanwhile.
    private func rewrite(_ old: VideoSegment) -> Bool? {
        guard let rows = db.segmentFrames(path: old.path) else { return nil }
        guard let first = rows.first, let last = rows.last else {
            db.dropSegment(id: old.id)
            delete(old.path)
            return true
        }
        let path = newPath(Time.iso.date(from: old.startTime) ?? Date(), old.display)
        // The stills it replaced took about this much (scaled to the frames left).
        let sourceBytes = old.frameCount > 0 ? old.sourceBytes * Int64(rows.count) / Int64(old.frameCount) : 0
        guard let (name, bytes) = write(path, rows.map(\.source), width: old.width, height: old.height) else { return false }
        let fresh = VideoSegment(id: old.id, path: path, display: old.display, startTime: first.timestamp, endTime: last.timestamp,
                                 width: old.width, height: old.height, frameCount: rows.count, codec: name, bytes: bytes, sourceBytes: sourceBytes)
        guard db.commitRewrite(old: old, fresh: fresh, ids: rows.map(\.id)) else {
            delete(path)
            return nil
        }
        delete(old.path)
        return true
    }

    /// A video that must lose frames but can't be written again (no encoder any more, a size it won't
    /// take): save the frames it keeps as stills, point their rows at them, and delete the video.
    private func unpack(_ old: VideoSegment) -> Bool {
        guard let rows = db.segmentFrames(path: old.path) else { return false }
        var written: [String] = []
        do {
            for r in rows {
                let ts = r.timestamp
                let dir = (dataDir as NSString).appendingPathComponent(ts.count >= 10 ? String(ts.prefix(10)) : "unpacked")
                try fm.createDirectory(atPath: dir, withIntermediateDirectories: true)
                let file = (dir as NSString).appendingPathComponent(ts.replacingOccurrences(of: ":", with: "-") + "-\(r.id).jpg")
                try codec.writeStill(r.source, to: file)
                written.append(file)
            }
            guard db.commitUnpack(old: old, ids: rows.map(\.id), stills: written) else {
                throw AVCodec.error("its frames changed meanwhile")
            }
        } catch {
            log("compress older recordings: cannot turn \((old.path as NSString).lastPathComponent) back into pictures: \(error.localizedDescription)")
            for f in written { delete(f) }
            return false
        }
        delete(old.path)
        log("compress older recordings: \((old.path as NSString).lastPathComponent) could not be written again, so its \(rows.count) frames are pictures again")
        return true
    }

    /// A name no file, temp file or database row uses.
    private func newPath(_ windowStart: Date, _ display: String) -> String {
        Compaction.segmentPath(videoDir: videoDir, windowStart: windowStart, display: display) {
            self.fm.fileExists(atPath: $0) || self.fm.fileExists(atPath: Compaction.tempPath($0)) || self.db.isVideoReferenced($0) != false
        }
    }

    /// Encode to a temp file, check it reads back, move it into place. Nil when any of that failed
    /// (nothing is left behind and the stills are untouched). With Encrypt Recordings on, the video is
    /// made in the private temp folder (never as a plain file under data/) and saved encrypted.
    private func write(_ path: String, _ sources: [FrameSource], width: Int, height: Int) -> (codec: String, bytes: Int64)? {
        let encrypt = RecordingFile.encryptNew
        let tmp = encrypt
            ? fm.temporaryDirectory.appendingPathComponent("\(RecordingFile.tempCopyPrefix)\(UUID().uuidString).mp4").path
            : Compaction.tempPath(path)
        do {
            try fm.createDirectory(atPath: (path as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
            let name = try codec.write(sources, width: width, height: height, to: tmp)
            guard codec.verify(tmp, sources, width: width, height: height) else {
                throw AVCodec.error("the new video did not read back like the pictures")
            }
            codec.release(tmp)
            if encrypt {
                guard let plain = fm.contents(atPath: tmp) else { throw AVCodec.error("the new video could not be read") }
                try RecordingFile.write(plain, to: URL(fileURLWithPath: path))
                delete(tmp)
            } else {
                try fm.moveItem(atPath: tmp, toPath: path)
            }
            return (name, size(path))
        } catch {
            log("compress older recordings: could not write \((path as NSString).lastPathComponent): \(error.localizedDescription)")
            delete(tmp)
            delete(path)
            return nil
        }
    }

    private func delete(_ path: String) {
        guard !path.isEmpty, fm.fileExists(atPath: path) else { return }
        if path.hasSuffix(".mp4") { codec.release(path) }
        // Under RecordingFile's lock, so an encryption switch converting this file never writes it back.
        RecordingFile.delete(path)
        if fm.fileExists(atPath: path) { log("cannot delete \(path)") }
    }

    private func size(_ path: String) -> Int64 {
        ((try? fm.attributesOfItem(atPath: path)[.size]) as? NSNumber)?.int64Value ?? 0
    }
}

/// Least recently used cache: decoded frames and open video readers. At most `capacity` entries and, when
/// `weight` is given, at most `maxWeight` in total (decoded screenshots are big: cap them by bytes).
/// Not thread-safe; FrameImages locks.
final class Lru<Key: Hashable, Value> {
    private var map: [Key: Value] = [:]
    private var order: [Key] = []
    let capacity: Int
    private let weight: ((Value) -> Int)?
    private let maxWeight: Int
    /// Total weight of what is cached.
    private(set) var total = 0

    init(capacity: Int, weight: ((Value) -> Int)? = nil, maxWeight: Int = .max) {
        self.capacity = capacity
        self.weight = weight
        self.maxWeight = maxWeight
    }

    var count: Int { map.count }

    func get(_ key: Key) -> Value? {
        guard let v = map[key] else { return nil }
        touch(key)
        return v
    }

    func set(_ key: Key, _ value: Value) {
        if let old = map.removeValue(forKey: key) {
            total -= of(old)
            order.removeAll { $0 == key }
        }
        let w = of(value)
        guard w <= maxWeight else { return }  // bigger than the whole cache: don't keep it
        map[key] = value
        order.append(key)
        total += w
        while map.count > capacity || total > maxWeight, !order.isEmpty {
            if let gone = map.removeValue(forKey: order.removeFirst()) { total -= of(gone) }
        }
    }

    func removeAll(where match: (Key) -> Bool) {
        for k in order where match(k) {
            if let gone = map.removeValue(forKey: k) { total -= of(gone) }
        }
        order.removeAll(where: match)
    }

    private func of(_ v: Value) -> Int { weight?(v) ?? 0 }

    private func touch(_ key: Key) {
        order.removeAll { $0 == key }
        order.append(key)
    }
}
