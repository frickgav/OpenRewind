import CoreMedia
import Foundation

/// Encodes and decodes the videos for Compress Older Recordings (AVFoundation, see FrameImages.swift).
protocol VideoCodec: AnyObject {
    /// Pixel size of a frame's picture, or nil when it cannot be read.
    func measure(_ frame: FrameSource) -> (width: Int, height: Int)?
    /// Write these frames, in order, as a new video at `path`: frame i at i / Compaction.fps seconds,
    /// width x height pixels (pictures a pixel bigger are cut to fit). Returns the codec ("hevc" or "h264").
    func write(_ frames: [FrameSource], width: Int, height: Int, to path: String) throws -> String
    /// True when the video at `path` opens and its first and last frames look like those pictures.
    func verify(_ path: String, _ frames: [FrameSource]) -> Bool
    /// Forget anything that has this video open, before it is deleted.
    func release(_ path: String)
}

/// A few minutes of one screen's stills that become one video.
struct SegmentPlan {
    var windowStart: Date
    var display: String
    var frames: [CompactCandidate]
}

/// Grouping rules for Compress Older Recordings, the same on Windows (Compaction.cs).
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

    /// data/video/2026-10-19/2026-10-19T14-05-00Z-<screen>.mp4 (with -2, -3 ... when taken).
    static func segmentPath(videoDir: String, windowStart: Date, display: String, taken: (String) -> Bool) -> String {
        let iso = Time.iso.string(from: windowStart)
        let dir = (videoDir as NSString).appendingPathComponent(String(iso.prefix(10)))
        let safe = String(display.map { $0.isLetter || $0.isNumber ? $0 : "-" }).trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        let name = String(iso.prefix(19)).replacingOccurrences(of: ":", with: "-") + "Z" + (safe.isEmpty ? "" : "-" + String(safe.prefix(40)))
        var n = 1
        while true {
            let p = (dir as NSString).appendingPathComponent(name + (n > 1 ? "-\(n)" : "") + ".mp4")
            if !taken(p) { return p }
            n += 1
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
/// It also rewrites videos that lost some of their frames to a delete, even while compression is off.
/// Windows has the same steps in Compaction.cs, where they are tested.
final class VideoCompactor {
    let db: Database
    let codec: VideoCodec
    let videoDir: String
    /// Videos no row points at are deleted once they are this old (a pass that stopped before committing).
    var orphanAge: TimeInterval = 600
    /// Frames up to this time were tried since the app started; ones that failed are tried again next launch.
    private var cursor = ""
    private let fm = FileManager.default

    struct Result { var frames = 0, videos = 0, rewritten = 0, failed = 0 }

    init(db: Database, codec: VideoCodec, dataDir: URL) {
        self.db = db
        self.codec = codec
        videoDir = dataDir.appendingPathComponent("video").path
    }

    /// One pass. `compress`: also turn older stills into video (the setting). `keepGoing` is asked between
    /// videos (quitting stops the pass soon); `keepCompressing` only between new videos (the setting was
    /// turned off: rewrites after a delete still finish).
    func runOnce(compress: Bool, now: Date, keepGoing: () -> Bool = { true }, keepCompressing: () -> Bool = { true }) -> Result {
        var r = Result()
        cleanUp()
        for seg in db.segments(onlyNeedingRewrite: true) {
            guard keepGoing() else { break }
            if rewrite(seg) { r.rewritten += 1 } else { r.failed += 1 }
        }
        guard compress else { return r }
        let cutoff = now.addingTimeInterval(-Compaction.minAge)
        while keepGoing() && keepCompressing() {
            let batch = db.compactCandidates(before: cutoff, after: cursor, limit: Compaction.batchSize)
            let plans = Compaction.plan(batch, cutoff: cutoff, batchFull: batch.count == Compaction.batchSize)
            if plans.isEmpty { break }
            for plan in plans {
                guard keepGoing() && keepCompressing() else { return r }
                let (n, v, f) = compact(plan)
                r.frames += n; r.videos += v; r.failed += f
                if let last = plan.frames.last { cursor = max(cursor, Time.iso.string(from: last.time)) }
            }
        }
        return r
    }

    /// Finish what a stopped pass left: temp files, videos no row points at, stills already in a video.
    private func cleanUp() {
        if fm.fileExists(atPath: videoDir) {
            let known = Set(db.segments().map(\.path))
            let files = (fm.enumerator(atPath: videoDir)?.allObjects as? [String] ?? []).filter { $0.hasSuffix(".mp4") }
            for rel in files {
                let p = (videoDir as NSString).appendingPathComponent(rel)
                let temp = p.hasSuffix(Compaction.tempSuffix)
                if !temp && known.contains(p) { continue }
                let modified = (try? fm.attributesOfItem(atPath: p)[.modificationDate] as? Date) ?? .distantPast
                if !temp && Date().timeIntervalSince(modified) < orphanAge { continue }
                delete(p)
            }
            for d in (try? fm.contentsOfDirectory(atPath: videoDir)) ?? [] {
                let p = (videoDir as NSString).appendingPathComponent(d)
                if (try? fm.contentsOfDirectory(atPath: p))?.isEmpty == true { try? fm.removeItem(atPath: p) }
            }
        }
        let left = db.leftoverStills()
        guard !left.isEmpty else { return }
        for l in left { delete(l.path) }
        db.clearStills(left.map(\.id))
    }

    private func compact(_ plan: SegmentPlan) -> (frames: Int, videos: Int, failed: Int) {
        let runs = Compaction.splitBySize(plan.frames.map { (item: $0, size: codec.measure(FrameSource(stillPath: $0.stillPath))) })
        var frames = 0, videos = 0, failed = 0
        for run in runs {
            let path = Compaction.segmentPath(videoDir: videoDir, windowStart: plan.windowStart, display: plan.display) {
                self.fm.fileExists(atPath: $0) || self.fm.fileExists(atPath: Compaction.tempPath($0))
            }
            let ids = run.frames.map(\.id)
            let sourceBytes = run.frames.reduce(Int64(0)) { $0 + size($1.stillPath) }
            let w = Compaction.even(run.width), h = Compaction.even(run.height)
            guard let first = run.frames.first, let last = run.frames.last,
                  let (name, bytes) = write(path, run.frames.map { FrameSource(stillPath: $0.stillPath) }, width: w, height: h) else {
                failed += 1; continue
            }
            let seg = VideoSegment(id: 0, path: path, display: plan.display, startTime: Time.iso.string(from: first.time),
                                   endTime: Time.iso.string(from: last.time), width: w, height: h, frameCount: ids.count,
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
    private func rewrite(_ old: VideoSegment) -> Bool {
        let rows = db.segmentFrames(path: old.path)
        guard let first = rows.first, let last = rows.last else {
            db.dropSegment(id: old.id)
            delete(old.path)
            return true
        }
        let path = Compaction.segmentPath(videoDir: videoDir, windowStart: Time.iso.date(from: old.startTime) ?? Date(), display: old.display) {
            self.fm.fileExists(atPath: $0) || self.fm.fileExists(atPath: Compaction.tempPath($0)) || $0 == old.path
        }
        // The stills it replaced took about this much (scaled to the frames left).
        let sourceBytes = old.frameCount > 0 ? old.sourceBytes * Int64(rows.count) / Int64(old.frameCount) : 0
        guard let (name, bytes) = write(path, rows.map(\.source), width: old.width, height: old.height) else { return false }
        let fresh = VideoSegment(id: old.id, path: path, display: old.display, startTime: first.timestamp, endTime: last.timestamp,
                                 width: old.width, height: old.height, frameCount: rows.count, codec: name, bytes: bytes, sourceBytes: sourceBytes)
        guard db.commitRewrite(old: old, fresh: fresh, ids: rows.map(\.id)) else {
            delete(path)
            return false
        }
        delete(old.path)
        return true
    }

    /// Encode to a temp file, check it reads back, move it into place. Nil when any of that failed
    /// (nothing is left behind and the stills are untouched).
    private func write(_ path: String, _ sources: [FrameSource], width: Int, height: Int) -> (codec: String, bytes: Int64)? {
        let tmp = Compaction.tempPath(path)
        do {
            try fm.createDirectory(atPath: (path as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
            let name = try codec.write(sources, width: width, height: height, to: tmp)
            guard codec.verify(tmp, sources) else {
                throw NSError(domain: "openrewind", code: 30, userInfo: [NSLocalizedDescriptionKey: "the new video did not read back like the pictures"])
            }
            codec.release(tmp)
            try fm.moveItem(atPath: tmp, toPath: path)
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
        do { try fm.removeItem(atPath: path) } catch { log("cannot delete \(path): \(error.localizedDescription)") }
    }

    private func size(_ path: String) -> Int64 {
        ((try? fm.attributesOfItem(atPath: path)[.size]) as? NSNumber)?.int64Value ?? 0
    }
}

/// Least recently used cache: decoded frames and open video readers. Not thread-safe; FrameImages locks.
final class Lru<Key: Hashable, Value> {
    private var map: [Key: Value] = [:]
    private var order: [Key] = []
    let capacity: Int

    init(capacity: Int) { self.capacity = capacity }

    func get(_ key: Key) -> Value? {
        guard let v = map[key] else { return nil }
        touch(key)
        return v
    }

    func set(_ key: Key, _ value: Value) {
        map[key] = value
        touch(key)
        while order.count > capacity { map.removeValue(forKey: order.removeFirst()) }
    }

    func removeAll(where match: (Key) -> Bool) {
        for k in order where match(k) { map.removeValue(forKey: k) }
        order.removeAll(where: match)
    }

    private func touch(_ key: Key) {
        order.removeAll { $0 == key }
        order.append(key)
    }
}
