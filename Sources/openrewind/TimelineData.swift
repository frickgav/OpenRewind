import Foundation
import SQLCipher

struct TimelineFrame {
    var id: Int64
    var time: Date
    var appName: String
    var windowName: String
    var path: String
    /// Browser tab address or document file URL of this frame, or "".
    var url: String = ""
    /// Set only while the timeline shows one app: when the next screenshot (of any app) was taken.
    var end: Date? = nil
    /// Saved while a blocked window or site was in front; never copied or exported.
    var blocked = false

    /// Set when the picture is a frame of a video (Compress Older Recordings); `path` is then "".
    var videoPath = ""
    var videoFrame = 0
    var videoTime = 0.0

    /// Where the picture is. Read it with FrameImages.
    var source: FrameSource { FrameSource(stillPath: path, videoPath: videoPath, videoFrame: videoFrame, videoTime: videoTime) }
}

struct TimelineAudio {
    var id: Int64
    var start: Date
    var duration: Double
    var device: String
    var isInput: Bool
    var path: String
    var end: Date { start.addingTimeInterval(duration) }
}

struct TimelineCaption {
    var start: Double
    var end: Double
    var text: String
}

/// Light reads for the timeline overlay. Rows come back oldest first.
extension Database {
    func timelineFrames(since: Date) -> [TimelineFrame] {
        lock.lock(); defer { lock.unlock() }
        return readFrames("WHERE timestamp >= ? ORDER BY timestamp ASC", [Time.iso.string(from: since)])
    }

    /// Screenshots taken from `from` to `to` (inclusive), plus the first one after `to`, which says how long the
    /// last one lasted. Export Clip reads only this, however much history there is.
    func timelineFrames(from: Date, to: Date) -> [TimelineFrame] {
        lock.lock(); defer { lock.unlock() }
        let a = Time.iso.string(from: from), b = Time.iso.string(from: to)
        return readFrames("WHERE timestamp >= ? AND timestamp <= ? ORDER BY timestamp ASC", [a, b])
            + readFrames("WHERE timestamp > ? ORDER BY timestamp ASC LIMIT 1", [b])
    }

    /// Frames matching `filter` (with `?` for each of `args`). Call with the lock held.
    private func readFrames(_ filter: String, _ args: [String]) -> [TimelineFrame] {
        var st: OpaquePointer?
        let sql = "SELECT id, timestamp, app_name, window_name, snapshot_path, browser_url, blocked, video_path, video_frame, video_time FROM frames " + filter
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(st) }
        for (i, a) in args.enumerated() { bind(st, Int32(i + 1), a) }
        var out: [TimelineFrame] = []
        while sqlite3_step(st) == SQLITE_ROW {
            guard let t = Time.iso.date(from: col(st, 1)) else { continue }
            out.append(TimelineFrame(id: sqlite3_column_int64(st, 0), time: t, appName: col(st, 2),
                                     windowName: col(st, 3), path: col(st, 4), url: col(st, 5),
                                     blocked: sqlite3_column_int(st, 6) != 0, videoPath: col(st, 7),
                                     videoFrame: Int(sqlite3_column_int64(st, 8)), videoTime: sqlite3_column_double(st, 9)))
        }
        return out
    }

    func timelineAudio(since: Date) -> [TimelineAudio] {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        // Start a little earlier so a chunk that began before `since` but is still playing is included.
        let sql = "SELECT id, timestamp, device, is_input, file_path FROM audio_chunks WHERE timestamp >= ? ORDER BY timestamp ASC"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(st) }
        bind(st, 1, Time.iso.string(from: since.addingTimeInterval(-120)))
        var out: [TimelineAudio] = []
        while sqlite3_step(st) == SQLITE_ROW {
            guard let t = Time.iso.date(from: col(st, 1)) else { continue }
            let path = col(st, 4)
            // 16 kHz mono 16-bit WAV: 32,000 bytes per second after the 44-byte header. The size on
            // disk is used as is (one stat, no read): an encrypted file is 38 bytes longer, about 1 ms.
            let size = ((try? FileManager.default.attributesOfItem(atPath: path))?[.size] as? NSNumber)?.intValue ?? 0
            guard size > 44 else { continue }
            out.append(TimelineAudio(id: sqlite3_column_int64(st, 0), start: t, duration: Double(size - 44) / 32_000,
                                     device: col(st, 2), isInput: sqlite3_column_int(st, 3) != 0, path: path))
        }
        return out
    }

    func captions(chunkId: Int64) -> [TimelineCaption] {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        let sql = "SELECT start_time, end_time, transcription FROM audio_transcriptions WHERE audio_chunk_id = ? ORDER BY start_time"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(st) }
        sqlite3_bind_int64(st, 1, chunkId)
        var out: [TimelineCaption] = []
        while sqlite3_step(st) == SQLITE_ROW {
            out.append(TimelineCaption(start: sqlite3_column_double(st, 0), end: sqlite3_column_double(st, 1), text: col(st, 2)))
        }
        return out
    }
}

extension Database {
    /// Time of the oldest screenshot or audio clip still stored.
    func oldestRecording() -> Date? {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        let sql = "SELECT MIN(t) FROM (SELECT MIN(timestamp) AS t FROM frames UNION ALL SELECT MIN(timestamp) FROM audio_chunks)"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(st) }
        guard sqlite3_step(st) == SQLITE_ROW else { return nil }
        return Time.iso.date(from: col(st, 0))
    }
}

/// Total size of everything in a folder (screenshots, audio, database).
func folderSize(_ url: URL) -> Int64 {
    guard let e = FileManager.default.enumerator(at: url, includingPropertiesForKeys: [.totalFileAllocatedSizeKey, .isRegularFileKey]) else { return 0 }
    var total: Int64 = 0
    for case let f as URL in e {
        let v = try? f.resourceValues(forKeys: [.totalFileAllocatedSizeKey, .isRegularFileKey])
        if v?.isRegularFile == true { total += Int64(v?.totalFileAllocatedSize ?? 0) }
    }
    return total
}
