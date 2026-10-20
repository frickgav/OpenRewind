import Foundation
import SQLCipher

/// How long to keep recordings. `forever` means: only delete when the user asks.
enum KeepFor: String, CaseIterable {
    case day, week, month, year, forever

    var title: String {
        switch self {
        case .day: return "1 Day"
        case .week: return "1 Week"
        case .month: return "1 Month"
        case .year: return "1 Year"
        case .forever: return "Until I Delete Them"
        }
    }

    var seconds: TimeInterval? {
        switch self {
        case .day: return 86_400
        case .week: return 7 * 86_400
        case .month: return 30 * 86_400
        case .year: return 365 * 86_400
        case .forever: return nil
        }
    }
}

/// Deletes screen and audio recordings (files + rows) inside a time range.
extension Database {
    /// Delete frames with `from <= timestamp < to`. nil = open end. Returns frames deleted.
    /// A video left with no frames is deleted; one that still has frames outside the range is marked
    /// for the compactor to write again without the deleted ones (see VideoCompactor.runOnce).
    /// The rows and the marks change in one transaction, so a crash can't leave deleted pictures in a
    /// video nobody will rewrite; files are deleted only after it commits.
    @discardableResult
    func deleteFrames(from: Date?, to: Date?) -> Int {
        let (whereSQL, args) = Self.range("timestamp", from, to)
        let and = whereSQL.isEmpty ? "WHERE" : whereSQL + " AND"
        var n = 0, stills: [String] = [], emptied: [String] = [], partial = false
        // Search by Meaning vectors and meeting titles taken from these frames go first (their own statements:
        // they take the lock).
        if scalarInt("SELECT COUNT(*) FROM frames \(whereSQL)", args) > 0 {
            forgetScreenMoments(from: from, to: to)
            forgetMeetingWindows(from: from, to: to)
        }
        // Held throughout, so a compaction cannot commit a new video for these frames halfway through.
        videoGate.lock()
        lock.lock()
        let ok = inTransaction {
            guard let count = scalarLocked("SELECT COUNT(*) FROM frames \(whereSQL)", args) else { return false }
            n = count
            guard n > 0,
                  let s = stringsLocked("SELECT snapshot_path FROM frames \(whereSQL)", args),
                  let videos = stringsLocked("SELECT DISTINCT video_path FROM frames \(and) video_path != ''", args),
                  runLocked("DELETE FROM frames \(whereSQL)", args) != nil  // FTS rows go with it (trigger frames_ad)
            else { return false }
            stills = s
            for v in videos {
                guard let left = scalarLocked("SELECT COUNT(*) FROM frames WHERE video_path = ? AND video_path != ''", [.text(v)]) else { return false }
                if left == 0 {
                    guard runLocked("DELETE FROM video_segments WHERE path = ?", [.text(v)]) != nil else { return false }
                    emptied.append(v)
                } else {
                    guard runLocked("UPDATE video_segments SET needs_rewrite = 1 WHERE path = ?", [.text(v)]) != nil else { return false }
                    partial = true
                }
            }
            return true
        }
        lock.unlock()
        videoGate.unlock()
        guard ok else {
            if n > 0 { log("delete failed: nothing was deleted") }
            return 0
        }
        if !emptied.isEmpty { releasingVideos?(emptied) }
        Self.removeFiles(stills + emptied)
        if partial { videosNeedRewrite?() }
        // A recap is made from its day's recordings, so it goes with them.
        deleteRecapsWith(from: from, to: to)
        return n
    }

    /// Delete audio chunks with `from <= timestamp < to`, their transcripts and WAV files, and the
    /// meeting notes made from them.
    @discardableResult
    func deleteAudio(from: Date?, to: Date?) -> Int {
        deleteMeetings(from: from, to: to)
        let (whereSQL, args) = Self.range("timestamp", from, to)
        let paths = strings("SELECT file_path FROM audio_chunks \(whereSQL)", args)
        guard !paths.isEmpty else { return 0 }
        forgetAudioMoments(chunkWhere: whereSQL, args)
        run("DELETE FROM audio_transcriptions WHERE audio_chunk_id IN (SELECT id FROM audio_chunks \(whereSQL))", args)
        run("DELETE FROM audio_chunks \(whereSQL)", args)
        Self.removeFiles(paths)
        deleteRecapsWith(from: from, to: to)
        return paths.count
    }

    /// Give freed space back to the disk (after big deletes).
    func compact() { try? exec("PRAGMA wal_checkpoint(TRUNCATE); VACUUM;") }

    // MARK: helpers

    private static func range(_ col: String, _ from: Date?, _ to: Date?) -> (String, [Arg]) {
        var parts: [String] = [], args: [Arg] = []
        if let from { parts.append("\(col) >= ?"); args.append(.text(Time.iso.string(from: from))) }
        if let to { parts.append("\(col) < ?"); args.append(.text(Time.iso.string(from: to))) }
        return (parts.isEmpty ? "" : "WHERE " + parts.joined(separator: " AND "), args)
    }

    func strings(_ sql: String, _ args: [Arg]) -> [String] {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return [] }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        var out: [String] = []
        while sqlite3_step(st) == SQLITE_ROW { out.append(col(st, 0)) }
        return out
    }

    func run(_ sql: String, _ args: [Arg]) {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        if sqlite3_step(st) != SQLITE_DONE { log("sql error: \(errmsg)") }
    }

    /// Remove files, then any day folders left empty.
    private static func removeFiles(_ paths: [String]) {
        let fm = FileManager.default
        var dirs = Set<String>()
        for p in paths where !p.isEmpty {
            RecordingFile.delete(p)
            dirs.insert((p as NSString).deletingLastPathComponent)
        }
        for d in dirs where ((try? fm.contentsOfDirectory(atPath: d)) ?? ["x"]).isEmpty {
            try? fm.removeItem(atPath: d)
        }
    }
}

/// Runs at launch and every hour: removes recordings older than the chosen limits.
final class RetentionJob {
    let db: Database
    var screenKeep: KeepFor
    var audioKeep: KeepFor
    private var timer: Timer?

    init(db: Database, screen: KeepFor, audio: KeepFor) {
        self.db = db; self.screenKeep = screen; self.audioKeep = audio
    }

    func start() {
        runNow()
        timer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { [weak self] _ in self?.runNow() }
    }

    func runNow() {
        let db = self.db, screen = screenKeep, audio = audioKeep
        DispatchQueue.global(qos: .utility).async {
            var frames = 0, chunks = 0
            if let s = screen.seconds { frames = db.deleteFrames(from: nil, to: Date().addingTimeInterval(-s)) }
            if let s = audio.seconds { chunks = db.deleteAudio(from: nil, to: Date().addingTimeInterval(-s)) }
            if frames + chunks > 0 { log("auto-delete: removed \(frames) screenshots, \(chunks) audio clips") }
        }
    }
}
