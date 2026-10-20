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

/// A still frame old enough to go into a video. `timestamp` is the stored text, as it is (the compactor's
/// place in the list: formatting `time` again could come out a millisecond different).
struct CompactCandidate {
    var id: Int64
    var time: Date
    var display: String
    var stillPath: String
    var timestamp: String

    init(id: Int64, time: Date, display: String, stillPath: String, timestamp: String? = nil) {
        self.id = id; self.time = time; self.display = display; self.stillPath = stillPath
        self.timestamp = timestamp ?? Time.iso.string(from: time)
    }
}

/// The rows behind Compress Older Recordings. Windows has the same schema and steps (VideoStore.cs),
/// where they are tested.
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
        CREATE TABLE IF NOT EXISTS video_skip (
            frame_id INTEGER PRIMARY KEY,
            tries INTEGER NOT NULL DEFAULT 0
        );
        """)
    }

    /// Still frames taken before `before` and after `after` (a stored timestamp), oldest first. Stills that
    /// failed to go into a video Compaction.maxTries times are left out.
    func compactCandidates(before: Date, after: String, limit: Int) -> [CompactCandidate] {
        rowsChecked("SELECT id, timestamp, display, snapshot_path FROM frames WHERE video_path = '' AND snapshot_path != '' " +
                    "AND timestamp < ? AND timestamp > ? " +
                    "AND NOT EXISTS (SELECT 1 FROM video_skip s WHERE s.frame_id = frames.id AND s.tries >= ?) " +
                    "ORDER BY timestamp ASC LIMIT ?",
                    [.text(Time.iso.string(from: before)), .text(after), .int(Int64(Compaction.maxTries)), .int(Int64(limit))]) { st in
            Time.iso.date(from: self.col(st, 1)).map {
                CompactCandidate(id: sqlite3_column_int64(st, 0), time: $0, display: self.col(st, 2), stillPath: self.col(st, 3), timestamp: self.col(st, 1))
            }
        } ?? []
    }

    /// These stills could not be put into a video (again).
    func noteCompactFailure(_ ids: [Int64]) {
        lock.lock(); defer { lock.unlock() }
        _ = inTransaction {
            for id in ids {
                guard runLocked("INSERT INTO video_skip (frame_id, tries) VALUES (?, 1) ON CONFLICT(frame_id) DO UPDATE SET tries = tries + 1",
                                [.int(id)]) != nil else { return false }
            }
            // Forget frames that are gone.
            return runLocked("DELETE FROM video_skip WHERE frame_id NOT IN (SELECT id FROM frames)", []) != nil
        }
    }

    /// Point the frames at the new video, in one transaction. False (and nothing changed) when one of them
    /// was deleted or moved meanwhile: the video would then hold a picture the user deleted. BEGIN IMMEDIATE,
    /// so another process (the `delete` command) can't write between these checks and the commit.
    func commitSegment(_ seg: VideoSegment, ids: [Int64]) -> Bool {
        videoGate.lock(); defer { videoGate.unlock() }
        lock.lock(); defer { lock.unlock() }
        return inTransaction {
            for id in ids where scalarLocked("SELECT COUNT(*) FROM frames WHERE id = ? AND video_path = '' AND snapshot_path != ''", [.int(id)]) != 1 {
                return false
            }
            guard runLocked("INSERT INTO video_segments (path, display, start_time, end_time, width, height, frame_count, codec, bytes, source_bytes, needs_rewrite) " +
                            "VALUES (?,?,?,?,?,?,?,?,?,?,0)",
                            [.text(seg.path), .text(seg.display), .text(seg.startTime), .text(seg.endTime), .int(Int64(seg.width)),
                             .int(Int64(seg.height)), .int(Int64(seg.frameCount)), .text(seg.codec), .int(seg.bytes), .int(seg.sourceBytes)]) == 1
            else { return false }
            for (i, id) in ids.enumerated() {
                guard runLocked("UPDATE frames SET video_path = ?, video_frame = ?, video_time = ? WHERE id = ? AND video_path = ''",
                                [.text(seg.path), .int(Int64(i)), .real(Double(i) / Compaction.fps), .int(id)]) == 1 else { return false }
            }
            return true
        }
    }

    /// The stills of these frames are deleted: forget their paths.
    func clearStills(_ ids: [Int64]) {
        lock.lock(); defer { lock.unlock() }
        _ = inTransaction {
            for id in ids {
                guard runLocked("UPDATE frames SET snapshot_path = '' WHERE id = ? AND video_path != ''", [.int(id)]) != nil else { return false }
            }
            return true
        }
    }

    /// Frames already in a video whose still was not deleted yet (the app stopped in between).
    func leftoverStills(limit: Int = 5000) -> [(id: Int64, path: String)] {
        rowsChecked("SELECT id, snapshot_path FROM frames WHERE video_path != '' AND snapshot_path != '' LIMIT ?", [.int(Int64(limit))]) { st in
            (id: sqlite3_column_int64(st, 0), path: self.col(st, 1))
        } ?? []
    }

    /// The videos, or nil when they can't be read (never a partial list: cleanup deletes what is not in it).
    func segmentsChecked(onlyNeedingRewrite: Bool = false) -> [VideoSegment]? {
        rowsChecked("SELECT id, path, display, start_time, end_time, width, height, frame_count, codec, bytes, source_bytes, needs_rewrite " +
                    "FROM video_segments" + (onlyNeedingRewrite ? " WHERE needs_rewrite = 1" : "") + " ORDER BY start_time", []) { st in
            VideoSegment(id: sqlite3_column_int64(st, 0), path: self.col(st, 1), display: self.col(st, 2), startTime: self.col(st, 3),
                         endTime: self.col(st, 4), width: Int(sqlite3_column_int64(st, 5)), height: Int(sqlite3_column_int64(st, 6)),
                         frameCount: Int(sqlite3_column_int64(st, 7)), codec: self.col(st, 8), bytes: sqlite3_column_int64(st, 9),
                         sourceBytes: sqlite3_column_int64(st, 10), needsRewrite: sqlite3_column_int64(st, 11) != 0)
        }
    }

    func segments(onlyNeedingRewrite: Bool = false) -> [VideoSegment] { segmentsChecked(onlyNeedingRewrite: onlyNeedingRewrite) ?? [] }

    /// Paths of all videos, or nil when they can't be read.
    func segmentPaths() -> Set<String>? { segmentsChecked().map { Set($0.map(\.path)) } }

    /// A video row or a frame points at this file; nil when that can't be read (so a file is never deleted on a guess).
    func isVideoReferenced(_ path: String) -> Bool? {
        lock.lock(); defer { lock.unlock() }
        return scalarLocked("SELECT EXISTS(SELECT 1 FROM video_segments WHERE path = ?) OR EXISTS(SELECT 1 FROM frames WHERE video_path = ? AND video_path != '')",
                            [.text(path), .text(path)]).map { $0 != 0 }
    }

    /// Mark videos that hold more pictures than frames point at them: their frames were deleted without
    /// the mark (a delete cut short by a crash, or an older copy of the app). Returns how many.
    func markShrunkSegments() -> Int {
        lock.lock(); defer { lock.unlock() }
        return runLocked("UPDATE video_segments SET needs_rewrite = 1 WHERE needs_rewrite = 0 AND " +
                         "frame_count > (SELECT COUNT(*) FROM frames WHERE frames.video_path = video_segments.path)", []) ?? 0
    }

    /// Videos still waiting to lose deleted frames (shown in the menu and by doctor).
    func stuckRewrites() -> Int {
        lock.lock(); defer { lock.unlock() }
        return scalarLocked("SELECT COUNT(*) FROM video_segments WHERE needs_rewrite = 1", []) ?? 0
    }

    /// Frames still in a video, in video order; nil when they can't be read.
    func segmentFrames(path: String) -> [(id: Int64, source: FrameSource, timestamp: String)]? {
        rowsChecked("SELECT id, video_frame, video_time, timestamp FROM frames WHERE video_path = ? AND video_path != '' ORDER BY video_frame", [.text(path)]) { st in
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
        return inTransaction {
            guard sameFramesLocked(old, ids) else { return false }
            for (i, id) in ids.enumerated() {
                guard runLocked("UPDATE frames SET video_path = ?, video_frame = ?, video_time = ? WHERE id = ? AND video_path = ?",
                                [.text(fresh.path), .int(Int64(i)), .real(Double(i) / Compaction.fps), .int(id), .text(old.path)]) == 1 else { return false }
            }
            return runLocked("UPDATE video_segments SET path = ?, start_time = ?, end_time = ?, width = ?, height = ?, frame_count = ?, codec = ?, " +
                             "bytes = ?, source_bytes = ?, needs_rewrite = 0 WHERE id = ? AND path = ?",
                             [.text(fresh.path), .text(fresh.startTime), .text(fresh.endTime), .int(Int64(fresh.width)), .int(Int64(fresh.height)),
                              .int(Int64(fresh.frameCount)), .text(fresh.codec), .int(fresh.bytes), .int(fresh.sourceBytes), .int(old.id), .text(old.path)]) == 1
        }
    }

    /// Point a video's frames at stills saved from it and forget the video, in one transaction.
    /// False (nothing changed) when its frames changed meanwhile.
    func commitUnpack(old: VideoSegment, ids: [Int64], stills: [String]) -> Bool {
        videoGate.lock(); defer { videoGate.unlock() }
        lock.lock(); defer { lock.unlock() }
        return inTransaction {
            guard ids.count == stills.count, sameFramesLocked(old, ids) else { return false }
            for (id, still) in zip(ids, stills) {
                guard runLocked("UPDATE frames SET snapshot_path = ?, video_path = '', video_frame = 0, video_time = 0 WHERE id = ? AND video_path = ?",
                                [.text(still), .int(id), .text(old.path)]) == 1 else { return false }
            }
            guard runLocked("DELETE FROM video_segments WHERE id = ?", [.int(old.id)]) != nil else { return false }
            // They were in a video once; don't make a new one from them over and over if this keeps happening.
            for id in ids {
                guard runLocked("INSERT INTO video_skip (frame_id, tries) VALUES (?, 1) ON CONFLICT(frame_id) DO UPDATE SET tries = tries + 1",
                                [.int(id)]) != nil else { return false }
            }
            return true
        }
    }

    /// A video none of whose frames are left.
    func dropSegment(id: Int64) { run("DELETE FROM video_segments WHERE id = ?", [.int(id)]) }

    /// How many videos there are, the space they take, and the space they save compared with the stills.
    /// Zeros on a read-only open of a database without videos yet.
    func videoStats() -> (segments: Int, bytes: Int64, saved: Int64) {
        let r: [(segments: Int, bytes: Int64, saved: Int64)] =
            rowsChecked("SELECT COUNT(*), COALESCE(SUM(bytes), 0), COALESCE(SUM(source_bytes), 0) FROM video_segments", [], logErrors: false) { st in
                (segments: Int(sqlite3_column_int64(st, 0)), bytes: sqlite3_column_int64(st, 1),
                 saved: max(0, sqlite3_column_int64(st, 2) - sqlite3_column_int64(st, 1)))
            } ?? []
        return r.first ?? (0, 0, 0)
    }

    // MARK: helpers (also used by deleteFrames, see Retention.swift)

    /// The frames in the video are exactly `ids`, in that order, and its row is still there. `lock` held.
    private func sameFramesLocked(_ old: VideoSegment, _ ids: [Int64]) -> Bool {
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, "SELECT id FROM frames WHERE video_path = ? AND video_path != '' ORDER BY video_frame", -1, &st, nil) == SQLITE_OK else { return false }
        defer { sqlite3_finalize(st) }
        bindAll(st, [.text(old.path)])
        var now: [Int64] = []
        while true {
            let rc = sqlite3_step(st)
            if rc == SQLITE_DONE { break }
            guard rc == SQLITE_ROW else { return false }
            now.append(sqlite3_column_int64(st, 0))
        }
        return now == ids && scalarLocked("SELECT COUNT(*) FROM video_segments WHERE id = ? AND path = ?", [.int(old.id), .text(old.path)]) == 1
    }

    /// Rows mapped by `make` (nil skips a row). Nil when the query fails at any point, so a caller never
    /// takes a partial list for the whole one.
    func rowsChecked<T>(_ sql: String, _ args: [Arg], logErrors: Bool = true, _ make: (OpaquePointer?) -> T?) -> [T]? {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else {
            if logErrors { log("sql error: \(errmsg)") }
            return nil
        }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        var out: [T] = []
        while true {
            let rc = sqlite3_step(st)
            if rc == SQLITE_DONE { return out }
            guard rc == SQLITE_ROW else {
                if logErrors { log("sql error: \(errmsg)") }
                return nil
            }
            if let v = make(st) { out.append(v) }
        }
    }

    /// BEGIN IMMEDIATE, `body`, then COMMIT when it returns true, else ROLLBACK. `lock` must be held for all of it.
    func inTransaction(_ body: () -> Bool) -> Bool {
        guard runLocked("BEGIN IMMEDIATE", []) != nil else { return false }
        if body(), runLocked("COMMIT", []) != nil { return true }
        _ = runLocked("ROLLBACK", [])
        return false
    }

    /// One statement with `lock` held. Returns the rows it changed, or nil on an error.
    func runLocked(_ sql: String, _ args: [Arg]) -> Int? {
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return nil }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        if sqlite3_step(st) != SQLITE_DONE { log("sql error: \(errmsg)"); return nil }
        return Int(sqlite3_changes(db))
    }

    /// One number with `lock` held, or nil on an error.
    func scalarLocked(_ sql: String, _ args: [Arg]) -> Int? {
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return nil }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        return sqlite3_step(st) == SQLITE_ROW ? Int(sqlite3_column_int64(st, 0)) : nil
    }

    /// The first column of every row with `lock` held, or nil on an error.
    func stringsLocked(_ sql: String, _ args: [Arg]) -> [String]? {
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return nil }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        var out: [String] = []
        while true {
            let rc = sqlite3_step(st)
            if rc == SQLITE_DONE { return out }
            guard rc == SQLITE_ROW else { log("sql error: \(errmsg)"); return nil }
            out.append(col(st, 0))
        }
    }
}
