import Foundation
import SQLite3

struct AudioChunk {
    var id: Int64
    var filePath: String
    var timestamp: String
    var device: String
    var isInput: Bool
    var status: String
}

struct Transcript {
    var id: Int64
    var chunkId: Int64
    var timestamp: String
    var startTime: Double
    var endTime: Double
    var text: String
    var device: String
    var isInput: Bool
    var filePath: String
}

/// Audio tables: `audio_chunks` / `audio_transcriptions` / `audio_transcriptions_fts`.
extension Database {
    func migrateAudio() throws {
        try exec("""
        CREATE TABLE IF NOT EXISTS audio_chunks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            file_path TEXT NOT NULL,
            timestamp TEXT NOT NULL,
            device TEXT NOT NULL DEFAULT '',
            is_input INTEGER NOT NULL DEFAULT 1,
            status TEXT NOT NULL DEFAULT 'pending'
                CHECK (status IN ('pending', 'transcribed', 'silent', 'failed')),
            attempts INTEGER NOT NULL DEFAULT 0,
            failure_reason TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_audio_chunks_status ON audio_chunks(status, timestamp);
        CREATE INDEX IF NOT EXISTS idx_audio_chunks_ts ON audio_chunks(timestamp);
        CREATE TABLE IF NOT EXISTS audio_transcriptions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            audio_chunk_id INTEGER NOT NULL REFERENCES audio_chunks(id),
            timestamp TEXT NOT NULL,
            start_time REAL NOT NULL DEFAULT 0,
            end_time REAL NOT NULL DEFAULT 0,
            transcription TEXT NOT NULL,
            device TEXT NOT NULL DEFAULT '',
            is_input INTEGER NOT NULL DEFAULT 1,
            engine TEXT NOT NULL DEFAULT 'whisper.cpp'
        );
        CREATE INDEX IF NOT EXISTS idx_audio_tx_ts ON audio_transcriptions(timestamp);
        CREATE VIRTUAL TABLE IF NOT EXISTS audio_fts USING fts5(
            transcription, device,
            content='audio_transcriptions', content_rowid='id', tokenize='unicode61'
        );
        CREATE TRIGGER IF NOT EXISTS audio_tx_ai AFTER INSERT ON audio_transcriptions BEGIN
            INSERT INTO audio_fts(rowid, transcription, device) VALUES (new.id, new.transcription, new.device);
        END;
        CREATE TRIGGER IF NOT EXISTS audio_tx_ad AFTER DELETE ON audio_transcriptions BEGIN
            INSERT INTO audio_fts(audio_fts, rowid, transcription, device)
            VALUES ('delete', old.id, old.transcription, old.device);
        END;
        """)
        // A crash mid-run can leave nothing in a bad state: pending rows are simply retried.
    }

    @discardableResult
    func insertChunk(filePath: String, timestamp: String, device: String, isInput: Bool, status: String = "pending") throws -> Int64 {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        let sql = "INSERT INTO audio_chunks (file_path, timestamp, device, is_input, status) VALUES (?,?,?,?,?)"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { throw DBError.prepare(errmsg) }
        defer { sqlite3_finalize(st) }
        bind(st, 1, filePath); bind(st, 2, timestamp); bind(st, 3, device)
        sqlite3_bind_int(st, 4, isInput ? 1 : 0); bind(st, 5, status)
        guard sqlite3_step(st) == SQLITE_DONE else { throw DBError.step(errmsg) }
        return sqlite3_last_insert_rowid(db)
    }

    /// Oldest pending chunk that has not failed too often. Clips from the last 15 minutes go first,
    /// so a long Re-transcribe never holds up what was just recorded.
    func nextPendingChunk(maxAttempts: Int = 3) -> AudioChunk? {
        let fresh = Time.iso.string(from: Date().addingTimeInterval(-15 * 60))
        return chunks("WHERE status = 'pending' AND attempts < ? ORDER BY timestamp >= ? DESC, timestamp ASC LIMIT 1",
                      [.int(Int64(maxAttempts)), .text(fresh)]).first
    }

    /// Audio clips recorded since `from` that Re-transcribe would run again, and their total length in seconds
    /// (clips are 30 s or shorter, so this counts 30 s each).
    func retranscribeCount(from: Date, clipSeconds: Double) -> (clips: Int, seconds: Double) {
        let n = scalarInt("SELECT COUNT(*) FROM audio_chunks WHERE timestamp >= ? AND status IN (\(Database.redoStatuses))",
                          [.text(Time.iso.string(from: from))])
        return (n, Double(n) * clipSeconds)
    }

    /// Clips Re-transcribe runs again: ones with words, and ones that failed. Clips found silent stay
    /// silent (a new model rarely finds speech where the old one found none, and a "pending" silent clip
    /// would look like speech to Meetings and Insights until it is done).
    static let redoStatuses = "'transcribed', 'failed'"

    /// Re-transcribe: queue the clips recorded since `from` again. Their old lines stay searchable
    /// until the new ones replace them for good (`saveTranscripts`). Meetings in that time keep the notes
    /// they have: the new lines do not make new notes (nor send the transcript to an AI provider again).
    /// Returns how many clips were queued.
    @discardableResult
    func requeueAudio(from: Date) -> Int {
        lock.lock(); defer { lock.unlock() }
        let iso = Time.iso.string(from: from)
        func run(_ sql: String) -> Bool {
            var st: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return false }
            defer { sqlite3_finalize(st) }
            bind(st, 1, iso)
            guard sqlite3_step(st) == SQLITE_DONE else { log("sql error: \(errmsg)"); return false }
            return true
        }
        guard sqlite3_exec(db, "BEGIN", nil, nil, nil) == SQLITE_OK else { return 0 }
        guard run("UPDATE meetings SET redone = 1 WHERE end_time >= ?"),
              run("UPDATE audio_chunks SET status = 'pending', attempts = 0, failure_reason = NULL WHERE timestamp >= ? AND status IN (\(Database.redoStatuses))") else {
            sqlite3_exec(db, "ROLLBACK", nil, nil, nil)
            return 0
        }
        let n = Int(sqlite3_changes(db))
        sqlite3_exec(db, "COMMIT", nil, nil, nil)
        return n
    }

    func chunk(id: Int64) -> AudioChunk? {
        chunks("WHERE id = ?", [.int(id)]).first
    }

    func markChunk(_ id: Int64, status: String, reason: String? = nil) {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        // A "pending" retry that has used up its attempts becomes "failed".
        let sql = """
        UPDATE audio_chunks SET attempts = attempts + 1, failure_reason = ?2,
            status = CASE WHEN ?1 = 'pending' AND attempts + 1 >= 3 THEN 'failed' ELSE ?1 END
        WHERE id = ?3
        """
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { return }
        defer { sqlite3_finalize(st) }
        bind(st, 1, status)
        if let reason { bind(st, 2, reason) } else { sqlite3_bind_null(st, 2) }
        sqlite3_bind_int64(st, 3, id)
        sqlite3_step(st)
    }

    /// Insert all segments of one chunk and mark it transcribed, in one transaction. Lines an earlier
    /// run left for the same chunk (Re-transcribe) are replaced. `engine` names the model that wrote them.
    /// Returns false (and saves nothing) when the clip was deleted while it was being transcribed.
    @discardableResult
    func saveTranscripts(chunk: AudioChunk, segments: [(start: Double, end: Double, text: String)], engine: String = "whisper.cpp") throws -> Bool {
        try exec("BEGIN")
        do {
            // Deleted (Delete Recordings, or kept-for-days cleanup) after it was picked up: do not bring its words back.
            if scalarInt("SELECT COUNT(*) FROM audio_chunks WHERE id = ?", [.int(chunk.id)]) == 0 {
                try exec("ROLLBACK")
                return false
            }
            lock.lock()
            var del: OpaquePointer?
            guard sqlite3_prepare_v2(db, "DELETE FROM audio_transcriptions WHERE audio_chunk_id = ?", -1, &del, nil) == SQLITE_OK else {
                lock.unlock(); throw DBError.prepare(errmsg)
            }
            sqlite3_bind_int64(del, 1, chunk.id)
            let deleted = sqlite3_step(del)
            sqlite3_finalize(del)
            if deleted != SQLITE_DONE { let m = errmsg; lock.unlock(); throw DBError.step(m) }
            var st: OpaquePointer?
            let sql = "INSERT INTO audio_transcriptions (audio_chunk_id, timestamp, start_time, end_time, transcription, device, is_input, engine) VALUES (?,?,?,?,?,?,?,?)"
            guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { lock.unlock(); throw DBError.prepare(errmsg) }
            let base = Time.iso.date(from: chunk.timestamp) ?? Date()
            for s in segments {
                sqlite3_reset(st)
                sqlite3_bind_int64(st, 1, chunk.id)
                bind(st, 2, Time.iso.string(from: base.addingTimeInterval(s.start)))
                sqlite3_bind_double(st, 3, s.start)
                sqlite3_bind_double(st, 4, s.end)
                bind(st, 5, s.text); bind(st, 6, chunk.device)
                sqlite3_bind_int(st, 7, chunk.isInput ? 1 : 0)
                bind(st, 8, engine)
                if sqlite3_step(st) != SQLITE_DONE { let m = errmsg; sqlite3_finalize(st); lock.unlock(); throw DBError.step(m) }
            }
            sqlite3_finalize(st)
            lock.unlock()
            markChunk(chunk.id, status: segments.isEmpty ? "silent" : "transcribed")
            try exec("COMMIT")
            return true
        } catch {
            try? exec("ROLLBACK")
            throw error
        }
    }

    func searchAudio(_ s: SearchQuery, fetch: Int) -> (rows: [Transcript], total: Int) {
        var whereParts: [String] = []
        var args: [Arg] = []
        var from = "audio_transcriptions t JOIN audio_chunks c ON c.id = t.audio_chunk_id"
        if let q = s.q, let match = Database.ftsQuery(q) {
            from = "audio_fts JOIN audio_transcriptions t ON t.id = audio_fts.rowid JOIN audio_chunks c ON c.id = t.audio_chunk_id"
            whereParts.append("audio_fts MATCH ?"); args.append(.text(match))
        }
        if let t = s.startTime { whereParts.append("t.timestamp >= ?"); args.append(.text(t)) }
        if let t = s.endTime { whereParts.append("t.timestamp <= ?"); args.append(.text(t)) }
        let whereSQL = whereParts.isEmpty ? "" : "WHERE " + whereParts.joined(separator: " AND ")
        let total = scalarInt("SELECT COUNT(*) FROM \(from) \(whereSQL)", args)

        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        let sql = """
        SELECT t.id, t.audio_chunk_id, t.timestamp, t.start_time, t.end_time, t.transcription, t.device, t.is_input, c.file_path
        FROM \(from) \(whereSQL) ORDER BY t.timestamp DESC LIMIT ?
        """
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return ([], total) }
        defer { sqlite3_finalize(st) }
        bindAll(st, args + [.int(Int64(fetch))])
        var out: [Transcript] = []
        while sqlite3_step(st) == SQLITE_ROW {
            out.append(Transcript(id: sqlite3_column_int64(st, 0), chunkId: sqlite3_column_int64(st, 1), timestamp: col(st, 2),
                                  startTime: sqlite3_column_double(st, 3), endTime: sqlite3_column_double(st, 4),
                                  text: col(st, 5), device: col(st, 6), isInput: sqlite3_column_int(st, 7) != 0,
                                  filePath: col(st, 8)))
        }
        return (out, total)
    }

    func audioStats() -> (chunks: Int, pending: Int, segments: Int) {
        (scalarInt("SELECT COUNT(*) FROM audio_chunks", []),
         scalarInt("SELECT COUNT(*) FROM audio_chunks WHERE status = 'pending'", []),
         scalarInt("SELECT COUNT(*) FROM audio_transcriptions", []))
    }

    private func chunks(_ tail: String, _ args: [Arg]) -> [AudioChunk] {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        let sql = "SELECT id, file_path, timestamp, device, is_input, status FROM audio_chunks \(tail)"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return [] }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        var out: [AudioChunk] = []
        while sqlite3_step(st) == SQLITE_ROW {
            out.append(AudioChunk(id: sqlite3_column_int64(st, 0), filePath: col(st, 1), timestamp: col(st, 2),
                                  device: col(st, 3), isInput: sqlite3_column_int(st, 4) != 0, status: col(st, 5)))
        }
        return out
    }
}
