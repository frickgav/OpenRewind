import Foundation
import SQLite3

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
    @discardableResult
    func deleteFrames(from: Date?, to: Date?) -> Int {
        let (whereSQL, args) = Self.range("timestamp", from, to)
        let paths = strings("SELECT snapshot_path FROM frames \(whereSQL)", args)
        guard !paths.isEmpty else { return 0 }
        forgetScreenMoments(from: from, to: to)  // Search by Meaning vectors go with the screenshots
        forgetMeetingWindows(from: from, to: to)
        run("DELETE FROM frames \(whereSQL)", args)  // FTS rows go with it (trigger frames_ad)
        Self.removeFiles(paths)
        // A recap is made from its day's recordings, so it goes with them.
        deleteRecapsWith(from: from, to: to)
        return paths.count
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
            try? fm.removeItem(atPath: p)
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
