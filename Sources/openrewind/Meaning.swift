import CoreGraphics
import Foundation
import IOKit.ps
import NaturalLanguage
import SQLCipher

// Search by Meaning: a background indexer gives each "moment" (a stretch of one window, or one
// audio clip) a sentence vector made on this Mac, and a search ranks moments by how close they
// are to the question. Nothing leaves the computer. Moments of blocked apps, windows and sites are
// never indexed, and are left out of results too (for ones indexed before they were blocked).
// The Windows app does the same in windows/OpenRewind.Core/Meaning/.

// MARK: - moments

/// A stored screenshot as the indexer sees it.
struct MomentFrame {
    var id: Int64
    var timestamp: String
    var appName: String
    var windowName: String
    var url: String
    var text: String
    /// Saved while a blocked window or site was in front (frames.blocked); never indexed.
    var blocked = false
}

/// One transcript line as the indexer sees it.
struct MomentSegment {
    var chunkId: Int64
    var timestamp: String
    var text: String
    var isInput: Bool
}

/// Screen moments run from firstId to lastId (frame ids); audio moments are one clip (firstId = lastId = chunk id).
struct Moment {
    var isAudio: Bool
    var start: String
    var end: String
    var firstId: Int64
    var lastId: Int64
    var appName: String
    var windowName: String
    var url: String
    var text: String
    /// Screen moments: how many frames firstId…lastId held when the moment was made. A save is
    /// refused if that changed (some were deleted meanwhile), so deleted text is never kept.
    var frames = 0
}

/// Cuts recordings into moments worth one vector each. The screen holds the same text for many
/// frames, so a moment is a stretch of one app window (until the window, page or app changes,
/// a 2-minute break, 10 minutes, or about 1,500 characters of new text), and only lines it has
/// not shown before in that stretch are added. Audio: one moment per transcribed clip.
enum MomentChunker {
    static let textBudget = 1500
    static let maxGap: TimeInterval = 120
    static let maxLength: TimeInterval = 600

    private struct Stretch {
        var first: MomentFrame
        var last: MomentFrame
        var firstIndex: Int
        var lastIndex: Int
        var seen = Set<String>()
        var text = ""
    }

    /// Frames in id order. `complete`: these are all the frames there are for now, so the last
    /// stretch can be closed; otherwise it is held back for the next batch (unless it starts the
    /// batch, which would get nowhere). Returns the moments and the id of the last frame they
    /// account for (the next batch starts after it).
    static func screen(_ frames: [MomentFrame], complete: Bool, blocked: (MomentFrame) -> Bool) -> (moments: [Moment], done: Int64) {
        var moments: [Moment] = []
        guard let firstFrame = frames.first, let lastFrame = frames.last else { return ([], 0) }
        var cur: Stretch?
        func close() {
            if let c = cur, !c.text.isEmpty { moments.append(toMoment(c)) }
            cur = nil
        }
        for (i, f) in frames.enumerated() {
            if f.blocked || blocked(f) { close(); continue }
            let ls = lines(f.text)
            if ls.isEmpty { continue }
            if let c = cur, breaks(c, f) { close() }
            var fresh = cur.map { c in ls.filter { !c.seen.contains($0) } } ?? ls
            if let c = cur, !c.text.isEmpty, c.text.count + fresh.reduce(0, { $0 + $1.count + 1 }) > textBudget {
                close()
                fresh = ls
            }
            var c = cur ?? Stretch(first: f, last: f, firstIndex: i, lastIndex: i)
            c.last = f
            c.lastIndex = i
            for l in fresh {
                guard c.seen.insert(l).inserted, c.text.count < textBudget else { continue }
                let room = textBudget - c.text.count
                c.text += (l.count < room ? l : String(l.prefix(room))) + "\n"
            }
            cur = c
        }
        var done = lastFrame.id
        if let c = cur, !complete, c.first.id > firstFrame.id {
            done = c.first.id - 1  // still going: read it again with the next batch
            cur = nil
        }
        close()
        return (moments, done)
    }

    private static func breaks(_ s: Stretch, _ f: MomentFrame) -> Bool {
        if f.appName != s.last.appName || f.windowName != s.last.windowName || f.url != s.last.url { return true }
        guard let t = Time.iso.date(from: f.timestamp), let last = Time.iso.date(from: s.last.timestamp),
              let first = Time.iso.date(from: s.first.timestamp) else { return true }
        return t.timeIntervalSince(last) > maxGap || t.timeIntervalSince(first) > maxLength
    }

    private static func toMoment(_ s: Stretch) -> Moment {
        var text = s.text
        while text.hasSuffix("\n") { text.removeLast() }
        return Moment(isAudio: false, start: s.first.timestamp, end: s.last.timestamp, firstId: s.first.id, lastId: s.last.id,
                      appName: s.first.appName, windowName: s.first.windowName, url: s.first.url, text: text,
                      frames: s.lastIndex - s.firstIndex + 1)
    }

    /// Trimmed lines with single spaces, without empty ones and stray single characters.
    static func lines(_ text: String) -> [String] {
        text.split(separator: "\n", omittingEmptySubsequences: false).compactMap { raw -> String? in
            let l = raw.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
            return l.count >= 2 ? l : nil
        }
    }

    /// Transcript lines in clip order (as stored), one moment per clip.
    static func audio(_ segments: [MomentSegment]) -> [Moment] {
        var moments: [Moment] = []
        var i = 0
        while i < segments.count {
            var j = i
            while j < segments.count && segments[j].chunkId == segments[i].chunkId { j += 1 }
            let clip = segments[i..<j]
            var text = clip.map { $0.text.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ") }
                .filter { !$0.isEmpty }.joined(separator: " ")
            if text.count > textBudget { text = String(text.prefix(textBudget)) }
            if text.count >= 2, let first = clip.first, let last = clip.last {
                moments.append(Moment(isAudio: true, start: first.timestamp, end: last.timestamp, firstId: first.chunkId, lastId: first.chunkId,
                                      appName: first.isInput ? "Microphone" : "System audio", windowName: "", url: "", text: text))
            }
            i = j
        }
        return moments
    }

    /// What the model reads: the window title and app give the screen text its context.
    static func embedText(_ m: Moment) -> String {
        if m.isAudio || (m.windowName.isEmpty && m.appName.isEmpty) { return m.text }
        return (m.windowName.isEmpty ? "" : m.windowName + " — ") + m.appName + "\n" + m.text
    }
}

// MARK: - vectors

/// Stored as one signed byte per number, scaled so the largest is ±127: a quarter of the size of
/// floats, and the ranking barely changes. Same as Vectors.cs.
enum Vectors {
    static func normalize(_ v: [Float]) -> [Float] {
        let n = sqrt(v.reduce(Float(0)) { $0 + $1 * $1 })
        return n == 0 ? v : v.map { $0 / n }
    }

    static func quantize(_ v: [Float]) -> Data {
        let mx = v.reduce(Float(0)) { max($0, abs($1)) }
        guard mx > 0 else { return Data(count: v.count) }
        return Data(v.map { UInt8(bitPattern: Int8(max(-127, min(127, ($0 / mx * 127).rounded())))) })
    }

    /// Cosine similarity of a stored vector and a query of length 1. The stored scale cancels out.
    static func score(_ stored: UnsafeBufferPointer<Int8>, _ query: [Float]) -> Float {
        guard stored.count == query.count else { return -.infinity }
        var dot: Float = 0, norm: Float = 0
        for i in 0..<stored.count {
            let x = Float(stored[i])
            dot += x * query[i]
            norm += x * x
        }
        return norm == 0 ? 0 : dot / norm.squareRoot()
    }
}

/// Keeps the k best-scoring items seen so far, best first, so a search can stream every row and hold only k.
struct TopK<T> {
    let k: Int
    private(set) var best: [(item: T, score: Float)] = []

    init(_ k: Int) { self.k = max(1, k) }

    var floor: Float { best.count < k ? -.infinity : best[best.count - 1].score }

    mutating func add(_ item: T, _ score: Float) {
        guard !score.isNaN, score > floor || best.count < k else { return }
        var lo = 0, hi = best.count
        while lo < hi { let mid = (lo + hi) / 2; if best[mid].score >= score { lo = mid + 1 } else { hi = mid } }
        best.insert((item, score), at: lo)
        if best.count > k { best.removeLast() }
    }
}

// MARK: - storage

/// A moment found by Search by Meaning. Times are ISO (UTC).
struct MeaningHit {
    var id: Int64
    var isAudio: Bool
    var timestamp: String
    var end: String
    var firstId: Int64
    var appName: String
    var windowName: String
    var url: String
    var text: String
    var score: Float = 0
}

/// How far the index has got.
struct MeaningProgress {
    var moments: Int
    var done: Int
    var total: Int
    var waiting: Int
    var upToDate: Bool { waiting == 0 }
    var percent: Int { total == 0 ? 100 : min(99, done * 100 / total) }
}

/// Search by Meaning storage. Same schema as the Windows app (MeaningStore.cs):
/// - `meaning_moments`: one row per moment with its text, read only for the few results shown.
/// - `meaning_vectors`: the same ids with the vector and the columns a search filters on, kept narrow
///   so a search reads little, page by page.
/// - `meaning_state`: how far the indexer got, and with which model.
extension Database {
    static let frameCursor = "frame_cursor", chunkCursor = "chunk_cursor"
    /// Set once the cursors left by the builds that saved no vectors were started over (see migrateMeaning).
    static let resetMarker = "reset_null_vectors"
    /// Rows a search reads per page. The database lock is let go between pages, so recording never waits long.
    static let scanPage = 4000

    func migrateMeaning() throws {
        // A test build of this feature kept the vector in meaning_moments. Start that index over.
        if scalarInt("SELECT COUNT(*) FROM pragma_table_info('meaning_moments') WHERE name = 'vector'", []) > 0 {
            try exec("DROP TABLE meaning_moments; DROP TABLE IF EXISTS meaning_state;")
        }
        try exec("""
        CREATE TABLE IF NOT EXISTS meaning_moments (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL CHECK (kind IN ('screen', 'audio')),
            timestamp TEXT NOT NULL,
            end_time TEXT NOT NULL,
            first_id INTEGER NOT NULL,
            last_id INTEGER NOT NULL,
            app_name TEXT NOT NULL DEFAULT '',
            window_name TEXT NOT NULL DEFAULT '',
            browser_url TEXT NOT NULL DEFAULT '',
            text TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_meaning_ts ON meaning_moments(timestamp);
        CREATE INDEX IF NOT EXISTS idx_meaning_first ON meaning_moments(kind, first_id);
        CREATE TABLE IF NOT EXISTS meaning_vectors (
            id INTEGER PRIMARY KEY,
            kind TEXT NOT NULL,
            timestamp TEXT NOT NULL,
            end_time TEXT NOT NULL,
            app_name TEXT NOT NULL DEFAULT '',
            window_name TEXT NOT NULL DEFAULT '',
            browser_url TEXT NOT NULL DEFAULT '',
            model TEXT NOT NULL,
            vector BLOB NOT NULL
        );
        CREATE TRIGGER IF NOT EXISTS meaning_moments_ad AFTER DELETE ON meaning_moments BEGIN
            DELETE FROM meaning_vectors WHERE id = old.id;
        END;
        CREATE TABLE IF NOT EXISTS meaning_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """)
        // Earlier Mac builds saved no vector at all (a missing value) while the cursors still moved on.
        // With no vectors there is nothing to lose: start the cursors over so the past gets indexed.
        // Only once (the marker stays, also through clearMeaning): an index that has simply stored
        // nothing yet, or whose saves keep failing, is not walked again from the start at every launch.
        if scalarInt("SELECT COUNT(*) FROM meaning_state WHERE key = ?", [.text(Self.resetMarker)]) == 0 {
            if scalarInt("SELECT COUNT(*) FROM meaning_vectors", []) == 0 {
                try exec("DELETE FROM meaning_moments; DELETE FROM meaning_state WHERE key IN ('\(Self.frameCursor)', '\(Self.chunkCursor)');")
            }
            try exec("INSERT OR REPLACE INTO meaning_state (key, value) VALUES ('\(Self.resetMarker)', '1');")
        }
    }

    /// True when this database has the Search by Meaning tables (an older app may not have made them yet).
    var hasMeaningIndex: Bool {
        scalarInt("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'meaning_vectors'", []) > 0
    }

    func meaningState(_ key: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, "SELECT value FROM meaning_state WHERE key = ?", -1, &st, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(st) }
        bind(st, 1, key)
        return sqlite3_step(st) == SQLITE_ROW ? col(st, 0) : nil
    }

    private static let stateUpsert = "INSERT INTO meaning_state (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value"

    /// Runs one statement; the caller holds `lock`. `blob` is the last "?", after `args`. False on an
    /// error (logged), when the statement has a different number of "?" than values given, so a
    /// value is never left out (it would be saved as NULL), and for an empty `blob`.
    @discardableResult
    func runHeld(_ sql: String, _ args: [Arg], blob: Data? = nil) -> Bool {
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return false }
        defer { sqlite3_finalize(st) }
        let given = args.count + (blob == nil ? 0 : 1)
        guard sqlite3_bind_parameter_count(st) == Int32(given) else {
            log("sql error: \(sqlite3_bind_parameter_count(st)) placeholders but \(given) values in: \(sql.prefix(80))")
            return false
        }
        bindAll(st, args)
        if let blob {
            // An empty Data has no address, which SQLite would store as NULL; and an empty vector would
            // spoil searches (the first one read sets the size). Never store one.
            guard !blob.isEmpty else { log("sql error: an empty value for: \(sql.prefix(80))"); return false }
            let rc = blob.withUnsafeBytes { sqlite3_bind_blob(st, Int32(given), $0.baseAddress, Int32($0.count), SQLITE_TRANSIENT) }
            guard rc == SQLITE_OK else { log("sql error: \(errmsg)"); return false }
        }
        guard sqlite3_step(st) == SQLITE_DONE else { log("sql error: \(errmsg)"); return false }
        return true
    }

    /// `keep`: checked under the database lock; false (the indexer was stopped meanwhile) writes nothing.
    /// False when the write failed (logged).
    @discardableResult
    func setMeaningState(_ key: String, _ value: String, keep: (() -> Bool)? = nil) -> Bool {
        lock.lock(); defer { lock.unlock() }
        if let keep, !keep() { return true }
        return runHeld(Self.stateUpsert, [.text(key), .text(value)])
    }

    private func meaningCursor(_ key: String) -> Int64 { meaningState(key).flatMap { Int64($0) } ?? 0 }

    /// Vectors from another model can't be compared with this one's: start over when it changed.
    func meaningUseModel(_ model: String, keep: (() -> Bool)? = nil) {
        guard meaningState("model") != model else { return }
        lock.lock()
        if let keep, !keep() { lock.unlock(); return }
        clearMeaningHeld()
        runHeld(Self.stateUpsert, [.text("model"), .text(model)])
        lock.unlock()
        log("search by meaning: indexing with \(model)")
    }

    /// Forget every vector and moment (Search by Meaning turned off, or a new model).
    func clearMeaning() {
        lock.lock(); defer { lock.unlock() }
        clearMeaningHeld()
    }

    private func clearMeaningHeld() {
        runHeld("DELETE FROM meaning_vectors", [])
        runHeld("DELETE FROM meaning_moments", [])
        runHeld("DELETE FROM meaning_state WHERE key != ?", [.text(Self.resetMarker)])
    }

    /// Screenshots after `after` (id) taken before `before` (ISO), oldest first, without the picture.
    func meaningFrames(after: Int64, before: String, limit: Int) -> [MomentFrame] {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        let sql = "SELECT id, timestamp, app_name, window_name, browser_url, text, blocked FROM frames WHERE id > ? AND timestamp < ? ORDER BY id LIMIT ?"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return [] }
        defer { sqlite3_finalize(st) }
        bindAll(st, [.int(after), .text(before), .int(Int64(limit))])
        var out: [MomentFrame] = []
        while sqlite3_step(st) == SQLITE_ROW {
            out.append(MomentFrame(id: sqlite3_column_int64(st, 0), timestamp: col(st, 1), appName: col(st, 2),
                                   windowName: col(st, 3), url: col(st, 4), text: col(st, 5), blocked: sqlite3_column_int(st, 6) != 0))
        }
        return out
    }

    /// Transcripts of the clips after `after` (id), up to the first clip still waiting for Whisper.
    /// `done` is the last clip id they account for.
    func meaningAudio(after: Int64, limit: Int) -> (segments: [MomentSegment], done: Int64) {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        var ready: [Int64] = []
        var done = after
        guard sqlite3_prepare_v2(db, "SELECT id, status FROM audio_chunks WHERE id > ? ORDER BY id LIMIT ?", -1, &st, nil) == SQLITE_OK else {
            log("sql error: \(errmsg)"); return ([], after)
        }
        bindAll(st, [.int(after), .int(Int64(limit))])
        while sqlite3_step(st) == SQLITE_ROW {
            let status = col(st, 1)
            if status == "pending" { break }
            done = sqlite3_column_int64(st, 0)
            if status == "transcribed" { ready.append(done) }
        }
        sqlite3_finalize(st)
        guard !ready.isEmpty else { return ([], done) }
        let sql = """
        SELECT t.audio_chunk_id, t.timestamp, t.transcription, c.is_input FROM audio_transcriptions t
        JOIN audio_chunks c ON c.id = t.audio_chunk_id
        WHERE t.audio_chunk_id IN (\(ready.map(String.init).joined(separator: ","))) ORDER BY t.audio_chunk_id, t.start_time, t.id
        """
        var segs: [MomentSegment] = []
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return ([], after) }
        defer { sqlite3_finalize(st) }
        while sqlite3_step(st) == SQLITE_ROW {
            segs.append(MomentSegment(chunkId: sqlite3_column_int64(st, 0), timestamp: col(st, 1), text: col(st, 2),
                                      isInput: sqlite3_column_int(st, 3) != 0))
        }
        return (segs, done)
    }

    /// Store a moment and move the cursor past it, together. The moment is left out (the cursor still
    /// moves) when its screenshots or clip were deleted while the vector was being made: its first or
    /// last frame is gone, or fewer frames are left between them than it was made from. With `keep`
    /// false (the indexer was stopped meanwhile) nothing is written. False when the write failed
    /// (logged, rolled back, the cursor stays put).
    @discardableResult
    func saveMoment(_ m: Moment, model: String, vector: Data, cursorKey: String, cursor: Int64, keep: (() -> Bool)? = nil) -> Bool {
        lock.lock(); defer { lock.unlock() }
        if let keep, !keep() { return true }
        guard sqlite3_exec(db, "BEGIN", nil, nil, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return false }
        let exists: String, ids: [Arg]
        if m.isAudio {
            exists = "EXISTS (SELECT 1 FROM audio_chunks WHERE id = ?)"; ids = [.int(m.firstId)]
        } else if m.frames > 0 {
            exists = "EXISTS (SELECT 1 FROM frames WHERE id = ?) AND EXISTS (SELECT 1 FROM frames WHERE id = ?) AND (SELECT COUNT(*) FROM frames WHERE id BETWEEN ? AND ?) = ?"
            ids = [.int(m.firstId), .int(m.lastId), .int(m.firstId), .int(m.lastId), .int(Int64(m.frames))]
        } else {
            exists = "EXISTS (SELECT 1 FROM frames WHERE id = ?) AND EXISTS (SELECT 1 FROM frames WHERE id = ?)"
            ids = [.int(m.firstId), .int(m.lastId)]
        }
        let kind = m.isAudio ? "audio" : "screen"
        var ok = runHeld("""
            INSERT INTO meaning_moments (kind, timestamp, end_time, first_id, last_id, app_name, window_name, browser_url, text)
            SELECT ?, ?, ?, ?, ?, ?, ?, ?, ? WHERE \(exists)
            """, [.text(kind), .text(m.start), .text(m.end), .int(m.firstId), .int(m.lastId),
                  .text(m.appName), .text(m.windowName), .text(m.url), .text(m.text)] + ids)
        if ok, sqlite3_changes(db) > 0 {
            ok = runHeld("""
                INSERT INTO meaning_vectors (id, kind, timestamp, end_time, app_name, window_name, browser_url, model, vector)
                VALUES (last_insert_rowid(), ?, ?, ?, ?, ?, ?, ?, ?)
                """, [.text(kind), .text(m.start), .text(m.end), .text(m.appName), .text(m.windowName), .text(m.url), .text(model)],
                blob: vector)
        }
        if ok { ok = runHeld(Self.stateUpsert, [.text(cursorKey), .text(String(cursor))]) }
        if ok, sqlite3_exec(db, "COMMIT", nil, nil, nil) == SQLITE_OK { return true }
        if ok { log("sql error: \(errmsg)") }
        sqlite3_exec(db, "ROLLBACK", nil, nil, nil)
        return false
    }

    /// Every stored vector of `model` in the time range, without the text (that is read for the best
    /// few only). Read a page at a time; the lock is let go between pages and `each` runs outside it.
    func scanMoments(model: String, start: String?, end: String?,
                     _ each: (_ id: Int64, _ audio: Bool, _ app: String, _ window: String, _ url: String, _ vector: UnsafeBufferPointer<Int8>) -> Void) {
        var parts = ["id > ?", "model = ?"]
        if start != nil { parts.append("end_time >= ?") }
        if end != nil { parts.append("timestamp <= ?") }
        let sql = "SELECT id, kind, app_name, window_name, browser_url, vector FROM meaning_vectors WHERE \(parts.joined(separator: " AND ")) ORDER BY id LIMIT \(Self.scanPage)"
        struct Row { var id: Int64; var audio: Bool; var app: String; var window: String; var url: String; var offset: Int; var count: Int }
        var rows: [Row] = []
        rows.reserveCapacity(Self.scanPage)
        var vecs: [Int8] = []
        var dim = 0
        var after: Int64 = 0
        while true {
            rows.removeAll(keepingCapacity: true)
            lock.lock()
            var st: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); lock.unlock(); return }
            var args: [Arg] = [.int(after), .text(model)]
            if let start { args.append(.text(start)) }
            if let end { args.append(.text(end)) }
            bindAll(st, args)
            while sqlite3_step(st) == SQLITE_ROW {
                let n = Int(sqlite3_column_bytes(st, 5))
                if dim == 0 { dim = max(1, n); vecs = [Int8](repeating: 0, count: Self.scanPage * dim) }
                let i = rows.count
                var count = -1  // not this model's size: never matches
                if n == dim, let p = sqlite3_column_blob(st, 5) {
                    vecs.withUnsafeMutableBytes { dst in dst.baseAddress!.advanced(by: i * dim).copyMemory(from: p, byteCount: dim) }
                    count = dim
                }
                rows.append(Row(id: sqlite3_column_int64(st, 0), audio: col(st, 1) == "audio", app: col(st, 2), window: col(st, 3),
                                url: col(st, 4), offset: i * dim, count: count))
            }
            sqlite3_finalize(st)
            lock.unlock()
            vecs.withUnsafeBufferPointer { all in
                for r in rows {
                    let v = r.count < 0 ? UnsafeBufferPointer<Int8>(start: nil, count: 0)
                        : UnsafeBufferPointer(rebasing: all[r.offset..<(r.offset + r.count)])
                    each(r.id, r.audio, r.app, r.window, r.url, v)
                }
            }
            guard rows.count == Self.scanPage, let last = rows.last else { return }
            after = last.id
        }
    }

    func moments(ids: [Int64]) -> [Int64: MeaningHit] {
        guard !ids.isEmpty else { return [:] }
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        let sql = "SELECT id, kind, timestamp, end_time, first_id, app_name, window_name, browser_url, text FROM meaning_moments WHERE id IN (\(ids.map(String.init).joined(separator: ",")))"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return [:] }
        defer { sqlite3_finalize(st) }
        var out: [Int64: MeaningHit] = [:]
        while sqlite3_step(st) == SQLITE_ROW {
            let id = sqlite3_column_int64(st, 0)
            out[id] = MeaningHit(id: id, isAudio: col(st, 1) == "audio", timestamp: col(st, 2), end: col(st, 3), firstId: sqlite3_column_int64(st, 4),
                                 appName: col(st, 5), windowName: col(st, 6), url: col(st, 7), text: col(st, 8))
        }
        return out
    }

    func meaningProgress(now: Date = Date()) -> MeaningProgress {
        let fc = meaningCursor(Self.frameCursor), cc = meaningCursor(Self.chunkCursor)
        let settled = Time.iso.string(from: now.addingTimeInterval(-MeaningService.settle - 60))
        let total = scalarInt("SELECT COUNT(*) FROM frames", []) + scalarInt("SELECT COUNT(*) FROM audio_chunks", [])
        let done = scalarInt("SELECT COUNT(*) FROM frames WHERE id <= ?", [.int(fc)]) + scalarInt("SELECT COUNT(*) FROM audio_chunks WHERE id <= ?", [.int(cc)])
        // Clips after one Whisper has not done yet wait for it, so they don't count as behind.
        let waiting = scalarInt("SELECT COUNT(*) FROM frames WHERE id > ? AND timestamp < ?", [.int(fc), .text(settled)])
            + scalarInt("""
                SELECT COUNT(*) FROM audio_chunks WHERE id > ? AND status = 'transcribed'
                AND id < COALESCE((SELECT MIN(id) FROM audio_chunks WHERE id > ? AND status = 'pending'), 9223372036854775807)
                """, [.int(cc), .int(cc)])
        return MeaningProgress(moments: scalarInt("SELECT COUNT(*) FROM meaning_vectors", []), done: done, total: total, waiting: waiting)
    }

    /// Retention and Delete Recordings: vectors go with the screenshots they were made from (the
    /// trigger meaning_moments_ad removes the vector rows). Run in the frame delete's transaction, after
    /// the frames are gone, with `lock` held throughout, so the indexer can't store a moment of these
    /// frames in between. Moments that overlap the range go, and so does any screen moment whose first
    /// or last frame no longer exists (one that slipped in some other way). False on an error.
    func forgetScreenMomentsLocked(from: Date?, to: Date?) -> Bool {
        var parts = ["kind = 'screen'"], args: [Arg] = []
        if let from { parts.append("end_time >= ?"); args.append(.text(Time.iso.string(from: from))) }
        if let to { parts.append("timestamp < ?"); args.append(.text(Time.iso.string(from: to))) }
        return runLocked("DELETE FROM meaning_moments WHERE \(parts.joined(separator: " AND "))", args) != nil
            && runLocked("""
                DELETE FROM meaning_moments WHERE kind = 'screen'
                AND (NOT EXISTS (SELECT 1 FROM frames WHERE frames.id = meaning_moments.first_id)
                     OR NOT EXISTS (SELECT 1 FROM frames WHERE frames.id = meaning_moments.last_id))
                """, []) != nil
    }

    /// ...and with the audio clips (`chunkWhere` selects the clips being deleted).
    func forgetAudioMoments(chunkWhere: String, _ args: [Arg]) {
        run("DELETE FROM meaning_moments WHERE kind = 'audio' AND first_id IN (SELECT id FROM audio_chunks \(chunkWhere))", args)
    }
}

// MARK: - embedding

/// Turns text into a sentence vector of length 1, on this computer.
protocol Embedder: AnyObject {
    /// Stored with every vector; a different id means the index is made again.
    var modelId: String { get }
    func embed(_ text: String) -> [Float]?
}

/// Apple's built-in sentence embedding (NaturalLanguage). It comes with macOS, so nothing is downloaded.
final class AppleEmbedder: Embedder {
    let modelId: String
    private let embedding: NLEmbedding
    private let lock = NSLock()

    private init(_ embedding: NLEmbedding, language: NLLanguage) {
        self.embedding = embedding
        modelId = "apple-sentence-\(language.rawValue)-r\(embedding.revision)"
    }

    /// The model for the Mac's language, else English. Nil when this Mac has neither.
    static func make() -> AppleEmbedder? {
        var langs: [NLLanguage] = []
        if let code = Locale.preferredLanguages.first.map({ Locale(identifier: $0).language.languageCode?.identifier ?? "" }), !code.isEmpty {
            langs.append(NLLanguage(rawValue: code))
        }
        langs.append(.english)
        for l in langs { if let e = NLEmbedding.sentenceEmbedding(for: l) { return AppleEmbedder(e, language: l) } }
        return nil
    }

    func embed(_ text: String) -> [Float]? {
        let t = text.count > 2000 ? String(text.prefix(2000)) : text
        lock.lock(); defer { lock.unlock() }
        guard let v = embedding.vector(for: t), !v.isEmpty else { return nil }
        return Vectors.normalize(v.map { Float($0) })
    }
}

// MARK: - indexer and search

/// How hard the indexer may work: idle (nobody at the Mac), active, or on battery.
enum MeaningPace {
    case idle, active, battery

    /// Index briskly while nobody is at the Mac, gently while they work, and slowest on battery or in Low Power Mode.
    static func now() -> MeaningPace {
        if ProcessInfo.processInfo.isLowPowerModeEnabled || onBattery { return .battery }
        let idle = [CGEventType.mouseMoved, .keyDown, .leftMouseDown, .scrollWheel]
            .map { CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: $0) }.min() ?? 0
        return idle > 300 ? .idle : .active
    }

    static var onBattery: Bool {
        guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let type = IOPSGetProvidingPowerSourceType(info)?.takeUnretainedValue() else { return false }
        return (type as String) == "Battery Power"
    }
}

/// Filters for a meaning search. Times are ISO (UTC).
struct MeaningQuery {
    var start: String?
    var end: String?
    /// Only screen moments of this app (exact name).
    var app: String?
    /// Only screen moments whose app and window title pass this.
    var match: ((String, String) -> Bool)?
    var screen = true
    var audio = true
    var limit = 40
}

final class MeaningService {
    let db: Database
    private let lock = NSLock()
    private var _embedder: Embedder?
    private var _paused = false
    /// Nil until turned on (or when this Mac has no sentence model).
    var embedder: Embedder? {
        get { lock.lock(); defer { lock.unlock() }; return _embedder }
        set { lock.lock(); _embedder = newValue; lock.unlock() }
    }
    /// True while saves keep failing (the indexer waits and tries again); the menu says so.
    private(set) var paused: Bool {
        get { lock.lock(); defer { lock.unlock() }; return _paused }
        set { lock.lock(); _paused = newValue; lock.unlock() }
    }
    var pace: () -> MeaningPace = { .active }
    var blocked: () -> Blocklist = { BlocklistStore.shared.current }
    private var thread: Thread?
    private let wake = NSCondition()
    private var cancelled = false
    /// Bumped by stop(). An indexer from before writes nothing more, even one stop() gave up waiting for.
    private var generation = 0

    /// Screenshots newer than this are left for the next round: the window may still be changing.
    static let settle: TimeInterval = 120
    /// The Settings switch (UserDefaults), read by the menu bar app and by `OpenRewind mcp`.
    static let settingKey = "searchByMeaning"
    static let frameBatch = 400, chunkBatch = 100

    init(db: Database) { self.db = db }

    var running: Bool { thread != nil }

    func start() {
        guard thread == nil else { return }
        paused = false
        wake.lock(); cancelled = false; let gen = generation; wake.unlock()
        let t = Thread { [weak self] in self?.loop(gen) }
        t.qualityOfService = .background
        t.name = "Search by Meaning"
        thread = t
        t.start()
        log("search by meaning: indexer on")
    }

    /// Stops the indexer and waits for it, so nothing is written after this returns.
    func stop() {
        wake.lock(); generation += 1; wake.unlock()
        guard let t = thread else { return }
        wake.lock(); cancelled = true; wake.broadcast(); wake.unlock()
        let until = Date().addingTimeInterval(10)
        while t.isExecuting && !t.isFinished && Date() < until { usleep(10_000) }
        if !t.isFinished { log("search by meaning: indexer still busy; it will write nothing more") }
        thread = nil
        log("search by meaning: indexer off")
    }

    private var isCancelled: Bool { wake.lock(); defer { wake.unlock() }; return cancelled }

    /// Waits; true when the indexer was stopped meanwhile.
    private func rest(_ seconds: TimeInterval) -> Bool {
        wake.lock(); defer { wake.unlock() }
        let until = Date().addingTimeInterval(seconds)
        while !cancelled && Date() < until { _ = wake.wait(until: until) }
        return cancelled
    }

    private struct Stopped: Error {}
    /// A moment could not be stored, or a cursor not moved (the database said why in the log). The
    /// cursor stays put. `cursorKey` and `cursor`: where it would have moved to.
    struct SaveFailed: Error, CustomStringConvertible {
        var cursorKey: String
        var cursor: Int64
        var description: String { "a moment could not be saved" }
    }

    static func backoff(failures: Int) -> TimeInterval { min(3600, 60 * pow(2, Double(max(0, min(failures, 7) - 1)))) }

    /// Tries of one moment before it is skipped (with a log line), so a moment that can never be saved
    /// does not hold up everything after it. With the waits above that is about 15 minutes.
    static let triesPerMoment = 5

    /// The indexer's errors in a row. Each one doubles the wait (1 minute up to 1 hour), so a write that
    /// keeps failing is tried, and logged, a few times an hour instead of over and over.
    struct Retry {
        private(set) var failures = 0
        private var stuck: (key: String, cursor: Int64)?
        private var tries = 0

        mutating func succeeded() { failures = 0; stuck = nil; tries = 0 }

        /// How long to wait, and the moment to skip when the same one failed `triesPerMoment` times in a row.
        mutating func failed(_ error: Error) -> (pause: TimeInterval, skip: SaveFailed?) {
            failures += 1
            let pause = MeaningService.backoff(failures: failures)
            guard let f = error as? SaveFailed else { stuck = nil; tries = 0; return (pause, nil) }
            if let s = stuck, s.key == f.cursorKey, s.cursor == f.cursor { tries += 1 } else { stuck = (f.cursorKey, f.cursor); tries = 1 }
            guard tries >= MeaningService.triesPerMoment else { return (pause, nil) }
            stuck = nil; tries = 0
            return (pause, f)
        }
    }

    private func loop(_ gen: Int) {
        var retry = Retry()
        while !isCancelled {
            var pause: TimeInterval
            if embedder == nil { pause = 5 } else {
                do {
                    let worked = try autoreleasepool { try step(gen: gen) }
                    retry.succeeded()
                    paused = false
                    pause = worked ? 0 : 30
                } catch is Stopped { return }
                catch {
                    paused = true
                    let r = retry.failed(error)
                    pause = r.pause
                    log("search by meaning: \(error); trying again in \(Int(pause / 60)) min")
                    if let skip = r.skip, db.setMeaningState(skip.cursorKey, String(skip.cursor), keep: keeper(gen)) {
                        log("search by meaning: skipped a moment that could not be saved \(Self.triesPerMoment) times")
                    }
                }
            }
            if pause > 0, rest(pause) { return }
        }
    }

    /// Checked under the database lock before every write: false once the indexer of `g` was stopped.
    private func keeper(_ g: Int) -> () -> Bool {
        return { [weak self] in
            guard let self else { return false }
            self.wake.lock(); defer { self.wake.unlock() }
            return self.generation == g && !self.cancelled
        }
    }

    /// Index one batch of screenshots and one of audio. False when there was nothing to do.
    @discardableResult
    func step(now: Date = Date(), gen: Int? = nil) throws -> Bool {
        guard let e = embedder else { return false }
        wake.lock(); let g = gen ?? generation; wake.unlock()
        let keep = keeper(g)
        db.meaningUseModel(e.modelId, keep: keep)
        let list = blocked()
        var worked = false

        let fc = db.meaningState(Database.frameCursor).flatMap { Int64($0) } ?? 0
        let frames = db.meaningFrames(after: fc, before: Time.iso.string(from: now.addingTimeInterval(-Self.settle)), limit: Self.frameBatch)
        if !frames.isEmpty {
            let (moments, done) = MomentChunker.screen(frames, complete: frames.count < Self.frameBatch) {
                Self.hides(list, app: $0.appName, window: $0.windowName, url: $0.url)
            }
            for m in moments { try save(e, m, cursorKey: Database.frameCursor, cursor: m.lastId, keep: keep); worked = true }
            if done > fc {
                guard db.setMeaningState(Database.frameCursor, String(done), keep: keep) else { throw SaveFailed(cursorKey: Database.frameCursor, cursor: done) }
                worked = true
            }
        }

        let cc = db.meaningState(Database.chunkCursor).flatMap { Int64($0) } ?? 0
        let (segments, doneChunks) = db.meaningAudio(after: cc, limit: Self.chunkBatch)
        for m in MomentChunker.audio(segments) { try save(e, m, cursorKey: Database.chunkCursor, cursor: m.lastId, keep: keep); worked = true }
        if doneChunks > cc {
            guard db.setMeaningState(Database.chunkCursor, String(doneChunks), keep: keep) else { throw SaveFailed(cursorKey: Database.chunkCursor, cursor: doneChunks) }
            worked = true
        }
        return worked
    }

    /// Make and store one vector, then rest a multiple of the time it took (more while the user is busy or on battery).
    private func save(_ e: Embedder, _ m: Moment, cursorKey: String, cursor: Int64, keep: @escaping () -> Bool) throws {
        if !keep() { throw Stopped() }
        let t0 = Date()
        // Stop this round on a failed write: the rest of the batch would fail the same way, and
        // the cursor must not move past moments that were never stored.
        if let v = e.embed(MomentChunker.embedText(m)).map(Vectors.quantize), !v.isEmpty {
            guard db.saveMoment(m, model: e.modelId, vector: v, cursorKey: cursorKey, cursor: cursor, keep: keep) else {
                throw SaveFailed(cursorKey: cursorKey, cursor: cursor)
            }
        } else {
            // Nothing the model can read (or an empty vector, never stored): move on.
            guard db.setMeaningState(cursorKey, String(cursor), keep: keep) else { throw SaveFailed(cursorKey: cursorKey, cursor: cursor) }
        }
        if !keep() { throw Stopped() }
        let factor: Double
        switch pace() { case .idle: factor = 1; case .active: factor = 4; case .battery: factor = 9 }
        if rest(Date().timeIntervalSince(t0) * factor + 0.02) { throw Stopped() }
    }

    /// Same rule as the Ask AI lookups: app, private window, blocked word in the title or address, or blocked site.
    static func hides(_ list: Blocklist, app: String, window: String, url: String) -> Bool {
        list.blocksFrame(appName: app, windowName: window, url: url) || Blocklist.hasKeyword(url, list.keywords)
    }

    func progress() -> MeaningProgress { db.meaningProgress() }

    /// The moments closest in meaning to `text`, best first. Blocked ones are left out.
    func search(_ text: String, _ q: MeaningQuery, blocked list: Blocklist? = nil) -> [MeaningHit] {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let e = embedder, !t.isEmpty, let v = e.embed(t) else { return [] }
        return Self.rank(db, model: e.modelId, query: v, q, blocked: list ?? blocked())
    }

    /// Streams every stored vector and keeps the best `q.limit`.
    static func rank(_ db: Database, model: String, query: [Float], _ q: MeaningQuery, blocked: Blocklist) -> [MeaningHit] {
        var top = TopK<Int64>(q.limit)
        db.scanMoments(model: model, start: q.start, end: q.end) { id, audio, app, window, url, vec in
            if audio ? !q.audio : !q.screen { return }
            if !audio, let a = q.app, app != a { return }
            if !audio, let match = q.match, !match(app, window) { return }
            if !audio, hides(blocked, app: app, window: window, url: url) { return }
            top.add(id, Vectors.score(vec, query))
        }
        let rows = db.moments(ids: top.best.map { $0.item })
        return top.best.compactMap { b -> MeaningHit? in
            guard var h = rows[b.item] else { return nil }
            h.score = b.score
            return h
        }
    }
}
