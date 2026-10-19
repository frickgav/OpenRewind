import Foundation
import SQLite3

/// Where a frame's picture is: a still file, or one frame of a video (see Compress Older Recordings).
/// `videoTime` is the frame's time inside the video in seconds, `videoFrame` its number (both start at 0).
struct FrameSource: Hashable {
    var stillPath: String
    var videoPath = ""
    var videoFrame = 0
    var videoTime = 0.0
    var isVideo: Bool { !videoPath.isEmpty }
}

/// One video file holding older frames of one screen and a few minutes. `sourceBytes` is what the stills
/// it replaced took, so `sourceBytes - bytes` is the space it saves. `needsRewrite`: some of its frames
/// were deleted, so it must be written again without them.
struct VideoSegment {
    var id: Int64
    var path: String
    var display: String
    var startTime: String
    var endTime: String
    var width: Int
    var height: Int
    var frameCount: Int
    var codec: String
    var bytes: Int64
    var sourceBytes: Int64
    var needsRewrite = false
}

/// A still frame old enough to go into a video.
struct CompactCandidate {
    var id: Int64
    var time: Date
    var display: String
    var stillPath: String
}

/// The rows behind Compress Older Recordings. Windows has the same schema (VideoStore.cs).
extension Database {
    func migrateVideo() throws {
        for (name, type) in [("display", "TEXT NOT NULL DEFAULT ''"), ("video_path", "TEXT NOT NULL DEFAULT ''"),
                             ("video_frame", "INTEGER NOT NULL DEFAULT 0"), ("video_time", "REAL NOT NULL DEFAULT 0")]
        where scalarInt("SELECT COUNT(*) FROM pragma_table_info('frames') WHERE name = '\(name)'", []) == 0 {
            try exec("ALTER TABLE frames ADD COLUMN \(name) \(type)")
        }
        try exec("""
        CREATE INDEX IF NOT EXISTS idx_frames_video ON frames(video_path, video_frame) WHERE video_path != '';
        CREATE TABLE IF NOT EXISTS video_segments (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            path TEXT NOT NULL UNIQUE,
            display TEXT NOT NULL DEFAULT '',
            start_time TEXT NOT NULL,
            end_time TEXT NOT NULL,
            width INTEGER NOT NULL,
            height INTEGER NOT NULL,
            frame_count INTEGER NOT NULL,
            codec TEXT NOT NULL DEFAULT '',
            bytes INTEGER NOT NULL DEFAULT 0,
            source_bytes INTEGER NOT NULL DEFAULT 0,
            needs_rewrite INTEGER NOT NULL DEFAULT 0
        );
        """)
    }

    /// Still frames taken before `before` and after `after`, oldest first.
    func compactCandidates(before: Date, after: String, limit: Int) -> [CompactCandidate] {
        rows("SELECT id, timestamp, display, snapshot_path FROM frames WHERE video_path = '' AND snapshot_path != '' " +
             "AND timestamp < ? AND timestamp > ? ORDER BY timestamp ASC LIMIT ?",
             [.text(Time.iso.string(from: before)), .text(after), .int(Int64(limit))]) { st in
            Time.iso.date(from: self.col(st, 1)).map {
                CompactCandidate(id: sqlite3_column_int64(st, 0), time: $0, display: self.col(st, 2), stillPath: self.col(st, 3))
            }
        }
    }

    /// Point the frames at the new video, in one transaction. False (and nothing changed) when one of them
    /// was deleted or moved meanwhile: the video would then hold a picture the user deleted.
    func commitSegment(_ seg: VideoSegment, ids: [Int64]) -> Bool {
        videoGate.lock(); defer { videoGate.unlock() }
        lock.lock(); defer { lock.unlock() }
        for id in ids where scalarLocked("SELECT COUNT(*) FROM frames WHERE id = ? AND video_path = '' AND snapshot_path != ''", [.int(id)]) != 1 {
            return false
        }
        guard runLocked("BEGIN", []) else { return false }
        var ok = runLocked("INSERT INTO video_segments (path, display, start_time, end_time, width, height, frame_count, codec, bytes, source_bytes, needs_rewrite) " +
                           "VALUES (?,?,?,?,?,?,?,?,?,?,0)",
                           [.text(seg.path), .text(seg.display), .text(seg.startTime), .text(seg.endTime), .int(Int64(seg.width)),
                            .int(Int64(seg.height)), .int(Int64(seg.frameCount)), .text(seg.codec), .int(seg.bytes), .int(seg.sourceBytes)])
        for (i, id) in ids.enumerated() where ok {
            ok = runLocked("UPDATE frames SET video_path = ?, video_frame = ?, video_time = ? WHERE id = ?",
                           [.text(seg.path), .int(Int64(i)), .real(Double(i) / Compaction.fps), .int(id)])
        }
        _ = runLocked(ok ? "COMMIT" : "ROLLBACK", [])
        return ok
    }

    /// The stills of these frames are deleted: forget their paths.
    func clearStills(_ ids: [Int64]) {
        lock.lock(); defer { lock.unlock() }
        guard runLocked("BEGIN", []) else { return }
        for id in ids { _ = runLocked("UPDATE frames SET snapshot_path = '' WHERE id = ? AND video_path != ''", [.int(id)]) }
        _ = runLocked("COMMIT", [])
    }

    /// Frames already in a video whose still was not deleted yet (the app stopped in between).
    func leftoverStills(limit: Int = 5000) -> [(id: Int64, path: String)] {
        rows("SELECT id, snapshot_path FROM frames WHERE video_path != '' AND snapshot_path != '' LIMIT ?", [.int(Int64(limit))]) { st in
            (id: sqlite3_column_int64(st, 0), path: self.col(st, 1))
        }
    }

    func segments(onlyNeedingRewrite: Bool = false) -> [VideoSegment] {
        rows("SELECT id, path, display, start_time, end_time, width, height, frame_count, codec, bytes, source_bytes, needs_rewrite " +
             "FROM video_segments" + (onlyNeedingRewrite ? " WHERE needs_rewrite = 1" : "") + " ORDER BY start_time", []) { st in
            VideoSegment(id: sqlite3_column_int64(st, 0), path: self.col(st, 1), display: self.col(st, 2), startTime: self.col(st, 3),
                         endTime: self.col(st, 4), width: Int(sqlite3_column_int64(st, 5)), height: Int(sqlite3_column_int64(st, 6)),
                         frameCount: Int(sqlite3_column_int64(st, 7)), codec: self.col(st, 8), bytes: sqlite3_column_int64(st, 9),
                         sourceBytes: sqlite3_column_int64(st, 10), needsRewrite: sqlite3_column_int64(st, 11) != 0)
        }
    }

    /// Frames still in a video, in video order.
    func segmentFrames(path: String) -> [(id: Int64, source: FrameSource, timestamp: String)] {
        rows("SELECT id, video_frame, video_time, timestamp FROM frames WHERE video_path = ? AND video_path != '' ORDER BY video_frame", [.text(path)]) { st in
            (id: sqlite3_column_int64(st, 0),
             source: FrameSource(stillPath: "", videoPath: path, videoFrame: Int(sqlite3_column_int64(st, 1)), videoTime: sqlite3_column_double(st, 2)),
             timestamp: self.col(st, 3))
        }
    }

    /// Swap a video for its rewritten copy, in one transaction. False (nothing changed) when its frames
    /// changed meanwhile; it stays marked and is written again next time.
    func commitRewrite(old: VideoSegment, fresh: VideoSegment, ids: [Int64]) -> Bool {
        videoGate.lock(); defer { videoGate.unlock() }
        lock.lock(); defer { lock.unlock() }
        var now: [Int64] = []
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, "SELECT id FROM frames WHERE video_path = ? AND video_path != '' ORDER BY video_frame", -1, &st, nil) == SQLITE_OK else { return false }
        bindAll(st, [.text(old.path)])
        while sqlite3_step(st) == SQLITE_ROW { now.append(sqlite3_column_int64(st, 0)) }
        sqlite3_finalize(st)
        guard now == ids, scalarLocked("SELECT COUNT(*) FROM video_segments WHERE id = ? AND path = ?", [.int(old.id), .text(old.path)]) == 1,
              runLocked("BEGIN", []) else { return false }
        var ok = true
        for (i, id) in ids.enumerated() where ok {
            ok = runLocked("UPDATE frames SET video_path = ?, video_frame = ?, video_time = ? WHERE id = ?",
                           [.text(fresh.path), .int(Int64(i)), .real(Double(i) / Compaction.fps), .int(id)])
        }
        if ok {
            ok = runLocked("UPDATE video_segments SET path = ?, start_time = ?, end_time = ?, width = ?, height = ?, frame_count = ?, codec = ?, " +
                           "bytes = ?, source_bytes = ?, needs_rewrite = 0 WHERE id = ?",
                           [.text(fresh.path), .text(fresh.startTime), .text(fresh.endTime), .int(Int64(fresh.width)), .int(Int64(fresh.height)),
                            .int(Int64(fresh.frameCount)), .text(fresh.codec), .int(fresh.bytes), .int(fresh.sourceBytes), .int(old.id)])
        }
        _ = runLocked(ok ? "COMMIT" : "ROLLBACK", [])
        return ok
    }

    /// A video none of whose frames are left.
    func dropSegment(id: Int64) { run("DELETE FROM video_segments WHERE id = ?", [.int(id)]) }

    /// How many videos there are, the space they take, and the space they save compared with the stills.
    func videoStats() -> (segments: Int, bytes: Int64, saved: Int64) {
        let r: [(segments: Int, bytes: Int64, saved: Int64)] =
            rows("SELECT COUNT(*), COALESCE(SUM(bytes), 0), COALESCE(SUM(source_bytes), 0) FROM video_segments", []) { st in
                (segments: Int(sqlite3_column_int64(st, 0)), bytes: sqlite3_column_int64(st, 1),
                 saved: max(0, sqlite3_column_int64(st, 2) - sqlite3_column_int64(st, 1)))
            }
        return r.first ?? (0, 0, 0)
    }

    // MARK: helpers

    /// Rows mapped by `make` (nil skips a row).
    private func rows<T>(_ sql: String, _ args: [Arg], _ make: (OpaquePointer?) -> T?) -> [T] {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return [] }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        var out: [T] = []
        while sqlite3_step(st) == SQLITE_ROW { if let v = make(st) { out.append(v) } }
        return out
    }

    /// One statement, with `lock` already held (inside a transaction).
    private func runLocked(_ sql: String, _ args: [Arg]) -> Bool {
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return false }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        if sqlite3_step(st) != SQLITE_DONE { log("sql error: \(errmsg)"); return false }
        return true
    }

    private func scalarLocked(_ sql: String, _ args: [Arg]) -> Int {
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { return 0 }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        return sqlite3_step(st) == SQLITE_ROW ? Int(sqlite3_column_int64(st, 0)) : 0
    }
}
