import CoreGraphics
import Foundation
import IOKit.ps
import NaturalLanguage
import SQLite3

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
        for f in frames {
            if blocked(f) { close(); continue }
            let ls = lines(f.text)
            if ls.isEmpty { continue }
            if let c = cur, breaks(c, f) { close() }
            var fresh = cur.map { c in ls.filter { !c.seen.contains($0) } } ?? ls
            if let c = cur, !c.text.isEmpty, c.text.count + fresh.reduce(0, { $0 + $1.count + 1 }) > textBudget {
                close()
                fresh = ls
            }
            var c = cur ?? Stretch(first: f, last: f)
            c.last = f
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
                      appName: s.first.appName, windowName: s.first.windowName, url: s.first.url, text: text)
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

/// The `meaning_moments` table: one row per moment with its vector, and `meaning_state` for how
/// far the indexer got. Same schema as the Windows app (MeaningStore.cs).
extension Database {
    static let frameCursor = "frame_cursor", chunkCursor = "chunk_cursor"

    func migrateMeaning() throws {
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
            text TEXT NOT NULL DEFAULT '',
            model TEXT NOT NULL,
            vector BLOB NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_meaning_ts ON meaning_moments(timestamp);
        CREATE INDEX IF NOT EXISTS idx_meaning_first ON meaning_moments(kind, first_id);
        CREATE TABLE IF NOT EXISTS meaning_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """)
    }

    func meaningState(_ key: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, "SELECT value FROM meaning_state WHERE key = ?", -1, &st, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(st) }
        bind(st, 1, key)
        return sqlite3_step(st) == SQLITE_ROW ? col(st, 0) : nil
    }

    func setMeaningState(_ key: String, _ value: String) {
        run("INSERT INTO meaning_state (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value", [.text(key), .text(value)])
    }

    private func meaningCursor(_ key: String) -> Int64 { meaningState(key).flatMap { Int64($0) } ?? 0 }

    /// Vectors from another model can't be compared with this one's: start over when it changed.
    func meaningUseModel(_ model: String) {
        guard meaningState("model") != model else { return }
        clearMeaning()
        setMeaningState("model", model)
        log("search by meaning: indexing with \(model)")
    }

    /// Forget every vector (Search by Meaning turned off, or a new model).
    func clearMeaning() {
        run("DELETE FROM meaning_moments", [])
        run("DELETE FROM meaning_state", [])
    }

    /// Screenshots after `after` (id) taken before `before` (ISO), oldest first, without the picture.
    func meaningFrames(after: Int64, before: String, limit: Int) -> [MomentFrame] {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        let sql = "SELECT id, timestamp, app_name, window_name, browser_url, text FROM frames WHERE id > ? AND timestamp < ? ORDER BY id LIMIT ?"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return [] }
        defer { sqlite3_finalize(st) }
        bindAll(st, [.int(after), .text(before), .int(Int64(limit))])
        var out: [MomentFrame] = []
        while sqlite3_step(st) == SQLITE_ROW {
            out.append(MomentFrame(id: sqlite3_column_int64(st, 0), timestamp: col(st, 1), appName: col(st, 2),
                                   windowName: col(st, 3), url: col(st, 4), text: col(st, 5)))
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

    /// Store a moment and move the cursor past it, together. Nothing is stored when its
    /// screenshots or clip were deleted while the vector was being made.
    func saveMoment(_ m: Moment, model: String, vector: Data, cursorKey: String, cursor: Int64) {
        lock.lock(); defer { lock.unlock() }
        guard sqlite3_exec(db, "BEGIN", nil, nil, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return }
        let exists = m.isAudio
            ? "EXISTS (SELECT 1 FROM audio_chunks WHERE id = ?)"
            : "EXISTS (SELECT 1 FROM frames WHERE id = ?) AND EXISTS (SELECT 1 FROM frames WHERE id = ?)"
        let sql = """
        INSERT INTO meaning_moments (kind, timestamp, end_time, first_id, last_id, app_name, window_name, browser_url, text, model, vector)
        SELECT ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ? WHERE \(exists)
        """
        var ok = false
        var st: OpaquePointer?
        if sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK {
            bindAll(st, [.text(m.isAudio ? "audio" : "screen"), .text(m.start), .text(m.end), .int(m.firstId), .int(m.lastId),
                         .text(m.appName), .text(m.windowName), .text(m.url), .text(m.text), .text(model)])
            _ = vector.withUnsafeBytes { sqlite3_bind_blob(st, 11, $0.baseAddress, Int32($0.count), SQLITE_TRANSIENT) }
            sqlite3_bind_int64(st, 12, m.firstId)
            if !m.isAudio { sqlite3_bind_int64(st, 13, m.lastId) }
            ok = sqlite3_step(st) == SQLITE_DONE
        }
        sqlite3_finalize(st)
        if ok, sqlite3_prepare_v2(db, "INSERT INTO meaning_state (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                                  -1, &st, nil) == SQLITE_OK {
            bindAll(st, [.text(cursorKey), .text(String(cursor))])
            ok = sqlite3_step(st) == SQLITE_DONE
            sqlite3_finalize(st)
        } else { ok = false }
        if !ok { log("sql error: \(errmsg)") }
        sqlite3_exec(db, ok ? "COMMIT" : "ROLLBACK", nil, nil, nil)
    }

    /// Every stored vector of `model` in the time range, without the text (that is read for the best few only).
    func scanMoments(model: String, start: String?, end: String?,
                     _ each: (_ id: Int64, _ audio: Bool, _ app: String, _ window: String, _ url: String, _ vector: UnsafeBufferPointer<Int8>) -> Void) {
        var parts = ["model = ?"], args: [Arg] = [.text(model)]
        if let start { parts.append("end_time >= ?"); args.append(.text(start)) }
        if let end { parts.append("timestamp <= ?"); args.append(.text(end)) }
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        let sql = "SELECT id, kind, app_name, window_name, browser_url, vector FROM meaning_moments WHERE \(parts.joined(separator: " AND "))"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        while sqlite3_step(st) == SQLITE_ROW {
            let n = Int(sqlite3_column_bytes(st, 5))
            let p = sqlite3_column_blob(st, 5)?.assumingMemoryBound(to: Int8.self)
            each(sqlite3_column_int64(st, 0), col(st, 1) == "audio", col(st, 2), col(st, 3), col(st, 4), UnsafeBufferPointer(start: p, count: p == nil ? 0 : n))
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
        return MeaningProgress(moments: scalarInt("SELECT COUNT(*) FROM meaning_moments", []), done: done, total: total, waiting: waiting)
    }

    /// Retention and Delete Recordings: vectors go with the screenshots they were made from.
    func forgetScreenMoments(from: Date?, to: Date?) {
        var parts = ["kind = 'screen'"], args: [Arg] = []
        if let from { parts.append("end_time >= ?"); args.append(.text(Time.iso.string(from: from))) }
        if let to { parts.append("timestamp < ?"); args.append(.text(Time.iso.string(from: to))) }
        run("DELETE FROM meaning_moments WHERE \(parts.joined(separator: " AND "))", args)
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
    /// Nil until turned on (or when this Mac has no sentence model).
    var embedder: Embedder? {
        get { lock.lock(); defer { lock.unlock() }; return _embedder }
        set { lock.lock(); _embedder = newValue; lock.unlock() }
    }
    var pace: () -> MeaningPace = { .active }
    var blocked: () -> Blocklist = { BlocklistStore.shared.current }
    private var thread: Thread?
    private let wake = NSCondition()
    private var cancelled = false

    /// Screenshots newer than this are left for the next round: the window may still be changing.
    static let settle: TimeInterval = 120
    static let frameBatch = 400, chunkBatch = 100

    init(db: Database) { self.db = db }

    var running: Bool { thread != nil }

    func start() {
        guard thread == nil else { return }
        wake.lock(); cancelled = false; wake.unlock()
        let t = Thread { [weak self] in self?.loop() }
        t.qualityOfService = .background
        t.name = "Search by Meaning"
        thread = t
        t.start()
        log("search by meaning: indexer on")
    }

    /// Stops the indexer and waits for it, so nothing is written after this returns.
    func stop() {
        guard let t = thread else { return }
        wake.lock(); cancelled = true; wake.broadcast(); wake.unlock()
        let until = Date().addingTimeInterval(10)
        while t.isExecuting && !t.isFinished && Date() < until { usleep(10_000) }
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

    private func loop() {
        while !isCancelled {
            var pause: TimeInterval
            if embedder == nil { pause = 5 } else {
                do { let worked = try autoreleasepool { try step() }; pause = worked ? 0 : 30 }
                catch is Stopped { return }
                catch { log("search by meaning: \(error)"); pause = 60 }
            }
            if pause > 0, rest(pause) { return }
        }
    }

    /// Index one batch of screenshots and one of audio. False when there was nothing to do.
    @discardableResult
    func step(now: Date = Date()) throws -> Bool {
        guard let e = embedder else { return false }
        db.meaningUseModel(e.modelId)
        let list = blocked()
        var worked = false

        let fc = db.meaningState(Database.frameCursor).flatMap { Int64($0) } ?? 0
        let frames = db.meaningFrames(after: fc, before: Time.iso.string(from: now.addingTimeInterval(-Self.settle)), limit: Self.frameBatch)
        if !frames.isEmpty {
            let (moments, done) = MomentChunker.screen(frames, complete: frames.count < Self.frameBatch) {
                Self.hides(list, app: $0.appName, window: $0.windowName, url: $0.url)
            }
            for m in moments { try save(e, m, cursorKey: Database.frameCursor, cursor: m.lastId); worked = true }
            if done > fc { db.setMeaningState(Database.frameCursor, String(done)); worked = true }
        }

        let cc = db.meaningState(Database.chunkCursor).flatMap { Int64($0) } ?? 0
        let (segments, doneChunks) = db.meaningAudio(after: cc, limit: Self.chunkBatch)
        for m in MomentChunker.audio(segments) { try save(e, m, cursorKey: Database.chunkCursor, cursor: m.lastId); worked = true }
        if doneChunks > cc { db.setMeaningState(Database.chunkCursor, String(doneChunks)); worked = true }
        return worked
    }

    /// Make and store one vector, then rest a multiple of the time it took (more while the user is busy or on battery).
    private func save(_ e: Embedder, _ m: Moment, cursorKey: String, cursor: Int64) throws {
        if isCancelled { throw Stopped() }
        let t0 = Date()
        if let v = e.embed(MomentChunker.embedText(m)) {
            db.saveMoment(m, model: e.modelId, vector: Vectors.quantize(v), cursorKey: cursorKey, cursor: cursor)
        } else {
            db.setMeaningState(cursorKey, String(cursor))  // nothing the model can read: move on
        }
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
