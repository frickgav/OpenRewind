import Foundation
import SQLCipher

let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

struct Frame: Codable {
    var id: Int64
    var timestamp: String
    var appName: String
    var windowName: String
    var focused: Bool
    var snapshotPath: String
    var text: String
    var textSource: String
    var hash: Int64
    /// Address of the browser tab in this frame, the open document (file://), or "".
    var browserUrl = ""
    /// Saved while a blocked window or site was in front (only the app name is kept; Insights leaves it out).
    var blocked = false
    /// The screen it was taken on.
    var display = ""
    /// After Compress Older Recordings the picture is frame `videoFrame` of this video, `videoTime` seconds in,
    /// and `snapshotPath` is "". Read it with FrameImages, never from the paths directly.
    var videoPath = ""
    var videoFrame = 0
    var videoTime = 0.0

    var source: FrameSource { FrameSource(stillPath: snapshotPath, videoPath: videoPath, videoFrame: videoFrame, videoTime: videoTime) }
}

struct SearchQuery {
    var q: String?
    var appName: String?
    var windowName: String?
    var startTime: String?
    var endTime: String?
    var limit = 20
    var offset = 0
}

enum DBError: Error {
    case open(String), prepare(String), step(String)
    /// The database is encrypted and the key is not loaded, or the key does not open it.
    case locked(String)
}

/// Thin SQLite wrapper around the `frames` + `frames_fts` tables:
/// one row per captured frame, FTS5 index over text, app and window.
/// The file is plain SQLite, or SQLCipher-encrypted with the data key (see Vault.swift).
final class Database {
    var db: OpaquePointer?
    let lock = NSLock()
    let path: URL
    /// Set by `init` and `convert(key:)` (Vault.swift).
    var isEncrypted = false
    /// The master key the open connection was opened with (nil for a plain database).
    var openKey: Data?
    /// `isConverting` (Vault.swift), read by the menu without waiting on `lock`.
    let convertState = NSLock()
    var converting = false
    /// Held for a whole frame delete and a whole video commit, so neither runs halfway through the other
    /// (a video must never be committed for frames that are being deleted). See VideoStore.swift.
    let videoGate = NSLock()
    /// Videos are about to be deleted: forget cached decoders for them.
    var releasingVideos: (([String]) -> Void)?
    /// Frames of a video were deleted while others in it are kept: the compactor should write it again soon.
    var videosNeedRewrite: (() -> Void)?
    /// The frame columns to read. A read-only open (the MCP server) may see a database the recording app
    /// has not migrated yet; it then reads every frame as a still.
    private var cols = Database.fullCols

    /// `key` opens an encrypted database; `createEncrypted` makes a new one encrypted with it.
    init(path: URL, key: Data? = nil, createEncrypted: Bool = false) throws {
        self.path = path
        Database.recoverSwap(path)
        let encrypted = Database.isEncryptedFile(path) || (createEncrypted && key != nil && !Database.hasData(path))
        if encrypted && key == nil { throw DBError.locked("the database is encrypted and the key is not on this Mac") }
        db = try Database.connect(path, key: encrypted ? key : nil)
        isEncrypted = encrypted
        openKey = encrypted ? key : nil
        try exec("PRAGMA journal_mode=WAL;")
        try exec("PRAGMA busy_timeout=5000;")
        try migrate()
        try migrateAudio()
        try migrateMeaning()
        try migrateJournal()
        try migrateMeetings()
        finishHomeMove(home: path.deletingLastPathComponent())
    }

    /// For the MCP server: reads while the app records, and never changes the database
    /// (no schema changes and no home move, which are the recording app's job). `key` (the data
    /// key from the Keychain) opens it when Encrypt Recordings is on; without it that throws `.locked`.
    init(readOnly path: URL, key: Data?) throws {
        self.path = path
        let encrypted = Database.isEncryptedFile(path)
        if encrypted && key == nil { throw DBError.locked("the database is encrypted and the key is not on this Mac") }
        db = try Database.connect(path, key: encrypted ? key : nil, readOnly: true)
        isEncrypted = encrypted
        openKey = encrypted ? key : nil
        try exec("PRAGMA busy_timeout=5000;")
        if scalarInt("SELECT COUNT(*) FROM pragma_table_info('frames') WHERE name IN ('display', 'video_path', 'video_frame', 'video_time')", []) < 4 {
            cols = Database.stillCols
        }
    }

    /// After the data folder moved from ~/.openscreen (see `Config.defaultHome`), point the stored
    /// screenshot and audio paths at the new folder. Runs again next launch if it is cut short.
    private func finishHomeMove(home: URL) {
        let marker = home.appendingPathComponent(Config.movedFromFile)
        guard let old = try? String(contentsOf: marker, encoding: .utf8), !old.isEmpty else { return }
        let new = home.path + "/"
        do {
            try exec("BEGIN")
            for (table, col) in [("frames", "snapshot_path"), ("frames", "video_path"), ("video_segments", "path"), ("audio_chunks", "file_path")] {
                run("UPDATE \(table) SET \(col) = ? || substr(\(col), ?) WHERE substr(\(col), 1, ?) = ?",
                    [.text(new), .int(Int64(old.unicodeScalars.count + 1)), .int(Int64(old.unicodeScalars.count)), .text(old)])
            }
            try exec("COMMIT")
            try? FileManager.default.removeItem(at: marker)
            log("updated recording paths for the new data folder")
        } catch {
            try? exec("ROLLBACK")
            log("could not update recording paths: \(error)")
        }
    }

    deinit { sqlite3_close(db) }

    func migrate() throws {
        try exec("""
        CREATE TABLE IF NOT EXISTS frames (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            app_name TEXT NOT NULL DEFAULT '',
            window_name TEXT NOT NULL DEFAULT '',
            focused INTEGER NOT NULL DEFAULT 1,
            snapshot_path TEXT NOT NULL DEFAULT '',
            text TEXT NOT NULL DEFAULT '',
            text_source TEXT NOT NULL DEFAULT 'ocr',
            hash INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_frames_app_ts ON frames(app_name, timestamp);
        CREATE VIRTUAL TABLE IF NOT EXISTS frames_fts USING fts5(
            text, app_name, window_name,
            content='frames', content_rowid='id', tokenize='unicode61'
        );
        CREATE TRIGGER IF NOT EXISTS frames_ai AFTER INSERT ON frames BEGIN
            INSERT INTO frames_fts(rowid, text, app_name, window_name)
            VALUES (new.id, new.text, new.app_name, new.window_name);
        END;
        CREATE TRIGGER IF NOT EXISTS frames_ad AFTER DELETE ON frames BEGIN
            INSERT INTO frames_fts(frames_fts, rowid, text, app_name, window_name)
            VALUES ('delete', old.id, old.text, old.app_name, old.window_name);
        END;
        """)
        // Added later: older databases do not have these columns yet. Windows adds the same ones.
        if scalarInt("SELECT COUNT(*) FROM pragma_table_info('frames') WHERE name = 'browser_url'", []) == 0 {
            try exec("ALTER TABLE frames ADD COLUMN browser_url TEXT NOT NULL DEFAULT ''")
        }
        // Time order. Replaced by idx_frames_insights, which starts with the time too (see
        // buildSlowIndexesInBackground); only a database that does not have that one yet needs this.
        if scalarInt("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_frames_insights'", []) == 0 {
            try exec("CREATE INDEX IF NOT EXISTS idx_frames_ts ON frames(timestamp)")
        }
        // Frames saved while a blocked window or site was in front. Older frames say 0. Windows: the same column.
        if scalarInt("SELECT COUNT(*) FROM pragma_table_info('frames') WHERE name = 'blocked'", []) == 0 {
            try exec("ALTER TABLE frames ADD COLUMN blocked INTEGER NOT NULL DEFAULT 0")
        }
        try migrateVideo()
    }

    /// Indexes that take a while on a big database (the whole frames table is read, screen text and
    /// all: seconds, or about a minute on a slow disk). Built after launch on a connection of its own,
    /// in the background, so the app starts at once and reads keep working meanwhile. A failure (full
    /// disk, another process writing) is logged and tried again next launch; Insights still works,
    /// only slower. Windows does the same.
    /// With Encrypt Recordings on, the connection opens with the data key (`RecordingFile.key`).
    static func buildSlowIndexesInBackground(path: URL) {
        DispatchQueue.global(qos: .utility).async {
            let c: OpaquePointer
            do {
                guard FileManager.default.fileExists(atPath: path.path) else { throw DBError.open("no database") }
                let encrypted = Database.isEncryptedFile(path)
                let key = encrypted ? RecordingFile.key : nil
                if encrypted && key == nil { throw DBError.locked("the data key is not loaded") }
                c = try Database.connect(path, key: key)
            } catch {
                log("could not build the Insights index (will try again next launch): cannot open the database (\(error))"); return
            }
            defer { sqlite3_close(c) }
            func run(_ sql: String) -> Bool {
                var err: UnsafeMutablePointer<CChar>?
                guard sqlite3_exec(c, sql, nil, nil, &err) == SQLITE_OK else {
                    log("could not build the Insights index (will try again next launch): \(err.map { String(cString: $0) } ?? "unknown")")
                    sqlite3_free(err)
                    return false
                }
                return true
            }
            sqlite3_busy_timeout(c, 30_000)
            var st: OpaquePointer?
            var has = false
            if sqlite3_prepare_v2(c, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_frames_insights'", -1, &st, nil) == SQLITE_OK,
               sqlite3_step(st) == SQLITE_ROW {
                has = sqlite3_column_int64(st, 0) > 0
            }
            sqlite3_finalize(st)
            if has { return }
            let started = Date()
            // Insights reads time, app, window, address and the blocked mark in time order; with this
            // index it never touches the screen text.
            guard run("CREATE INDEX IF NOT EXISTS idx_frames_insights ON frames(timestamp, app_name, window_name, browser_url, blocked)") else { return }
            // The new index starts with the time, so the old time index only costs disk space and inserts.
            _ = run("DROP INDEX IF EXISTS idx_frames_ts")
            _ = run("DROP INDEX IF EXISTS idx_frames_activity")  // an earlier test build of Insights
            log("built the Insights index in \(String(format: "%.1f", Date().timeIntervalSince(started))) s")
        }
    }

    func exec(_ sql: String) throws {
        lock.lock(); defer { lock.unlock() }
        try execLocked(sql)
    }

    /// `exec` for callers that already hold `lock`.
    func execLocked(_ sql: String) throws {
        var err: UnsafeMutablePointer<CChar>?
        if sqlite3_exec(db, sql, nil, nil, &err) != SQLITE_OK {
            let m = err.map { String(cString: $0) } ?? "unknown"
            sqlite3_free(err)
            throw DBError.step(m)
        }
    }

    @discardableResult
    func insert(_ f: Frame) throws -> Int64 {
        lock.lock(); defer { lock.unlock() }
        let sql = "INSERT INTO frames (timestamp, app_name, window_name, focused, snapshot_path, text, text_source, hash, browser_url, blocked, display) VALUES (?,?,?,?,?,?,?,?,?,?,?)"
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { throw DBError.prepare(errmsg) }
        defer { sqlite3_finalize(st) }
        bind(st, 1, f.timestamp); bind(st, 2, f.appName); bind(st, 3, f.windowName)
        sqlite3_bind_int(st, 4, f.focused ? 1 : 0)
        bind(st, 5, f.snapshotPath); bind(st, 6, f.text); bind(st, 7, f.textSource)
        sqlite3_bind_int64(st, 8, f.hash)
        bind(st, 9, f.browserUrl)
        sqlite3_bind_int(st, 10, f.blocked ? 1 : 0)
        bind(st, 11, f.display)
        guard sqlite3_step(st) == SQLITE_DONE else { throw DBError.step(errmsg) }
        return sqlite3_last_insert_rowid(db)
    }

    func frame(id: Int64) -> Frame? {
        query("SELECT \(cols) FROM frames f WHERE f.id = ?", [.int(id)]).first
    }

    func search(_ s: SearchQuery) -> (rows: [Frame], total: Int) {
        var whereParts: [String] = []
        var args: [Arg] = []
        var from = "frames f"
        if let q = s.q, let match = Self.ftsQuery(q) {
            from = "frames_fts JOIN frames f ON f.id = frames_fts.rowid"
            whereParts.append("frames_fts MATCH ?"); args.append(.text(match))
        }
        if let a = s.appName, !a.isEmpty { whereParts.append("f.app_name LIKE ?"); args.append(.text("%\(a)%")) }
        if let w = s.windowName, !w.isEmpty { whereParts.append("f.window_name LIKE ?"); args.append(.text("%\(w)%")) }
        if let t = s.startTime { whereParts.append("f.timestamp >= ?"); args.append(.text(t)) }
        if let t = s.endTime { whereParts.append("f.timestamp <= ?"); args.append(.text(t)) }
        let whereSQL = whereParts.isEmpty ? "" : "WHERE " + whereParts.joined(separator: " AND ")
        let total = scalarInt("SELECT COUNT(*) FROM \(from) \(whereSQL)", args)
        let rows = query("SELECT \(cols) FROM \(from) \(whereSQL) ORDER BY f.timestamp DESC LIMIT ? OFFSET ?",
                         args + [.int(Int64(s.limit)), .int(Int64(s.offset))])
        return (rows, total)
    }

    func apps(since: String?) -> [(String, Int)] {
        var out: [(String, Int)] = []
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        let sql = "SELECT app_name, COUNT(*) FROM frames \(since == nil ? "" : "WHERE timestamp >= ?") GROUP BY app_name ORDER BY 2 DESC"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { return [] }
        defer { sqlite3_finalize(st) }
        if let since { bind(st, 1, since) }
        while sqlite3_step(st) == SQLITE_ROW {
            out.append((col(st, 0), Int(sqlite3_column_int64(st, 1))))
        }
        return out
    }

    /// Web pages seen lately, newest first, one row per address: for picking sites to block.
    func recentPages(limit: Int = 3000) -> [RecentPage] {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        // SQLite fills the bare window_name from the row that has the MAX(timestamp).
        let sql = "SELECT browser_url, window_name, MAX(timestamp) FROM frames WHERE browser_url LIKE 'http%' GROUP BY browser_url ORDER BY 3 DESC LIMIT ?"
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return [] }
        defer { sqlite3_finalize(st) }
        sqlite3_bind_int64(st, 1, Int64(limit))
        var out: [RecentPage] = []
        while sqlite3_step(st) == SQLITE_ROW {
            out.append(RecentPage(url: col(st, 0), title: col(st, 1), lastSeen: col(st, 2)))
        }
        return out
    }

    func stats() -> (count: Int, last: String?) {
        let n = scalarInt("SELECT COUNT(*) FROM frames", [])
        let last = query("SELECT \(cols) FROM frames f ORDER BY id DESC LIMIT 1", []).first?.timestamp
        return (n, last)
    }

    /// The saved frame closest to `t`, within 10 minutes.
    func frame(near t: Date) -> Frame? {
        let iso = Time.iso.string(from: t)
        let before = query("SELECT \(cols) FROM frames f WHERE f.timestamp <= ? ORDER BY f.timestamp DESC LIMIT 1", [.text(iso)]).first
        let after = query("SELECT \(cols) FROM frames f WHERE f.timestamp > ? ORDER BY f.timestamp ASC LIMIT 1", [.text(iso)]).first
        func gap(_ f: Frame?) -> Double {
            guard let f, let d = Time.iso.date(from: f.timestamp) else { return .greatestFiniteMagnitude }
            return abs(d.timeIntervalSince(t))
        }
        let best = gap(before) <= gap(after) ? before : after
        return gap(best) <= 600 ? best : nil
    }

    // MARK: - helpers

    private static let fullCols = "f.id, f.timestamp, f.app_name, f.window_name, f.focused, f.snapshot_path, f.text, f.text_source, f.hash, f.browser_url, f.display, f.video_path, f.video_frame, f.video_time"
    private static let stillCols = "f.id, f.timestamp, f.app_name, f.window_name, f.focused, f.snapshot_path, f.text, f.text_source, f.hash, f.browser_url, '', '', 0, 0.0"

    /// Turn free text into a safe FTS5 query: every word becomes a quoted prefix term.
    static func ftsQuery(_ raw: String) -> String? {
        let words = raw.split(whereSeparator: { $0.isWhitespace })
            .map { $0.replacingOccurrences(of: "\"", with: "") }
            .filter { !$0.isEmpty }
        guard !words.isEmpty else { return nil }
        return words.map { "\"\($0)\"*" }.joined(separator: " ")
    }

    enum Arg { case text(String), int(Int64), real(Double) }

    var errmsg: String { String(cString: sqlite3_errmsg(db)) }

    func bind(_ st: OpaquePointer?, _ i: Int32, _ s: String) {
        sqlite3_bind_text(st, i, s, -1, SQLITE_TRANSIENT)
    }

    func bindAll(_ st: OpaquePointer?, _ args: [Arg]) {
        for (i, a) in args.enumerated() {
            switch a {
            case .text(let s): bind(st, Int32(i + 1), s)
            case .int(let n): sqlite3_bind_int64(st, Int32(i + 1), n)
            case .real(let d): sqlite3_bind_double(st, Int32(i + 1), d)
            }
        }
    }

    func col(_ st: OpaquePointer?, _ i: Int32) -> String {
        sqlite3_column_text(st, i).map { String(cString: $0) } ?? ""
    }

    private func query(_ sql: String, _ args: [Arg]) -> [Frame] {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return [] }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        var out: [Frame] = []
        while sqlite3_step(st) == SQLITE_ROW {
            out.append(Frame(id: sqlite3_column_int64(st, 0), timestamp: col(st, 1), appName: col(st, 2),
                             windowName: col(st, 3), focused: sqlite3_column_int(st, 4) != 0,
                             snapshotPath: col(st, 5), text: col(st, 6), textSource: col(st, 7),
                             hash: sqlite3_column_int64(st, 8), browserUrl: col(st, 9), display: col(st, 10),
                             videoPath: col(st, 11), videoFrame: Int(sqlite3_column_int64(st, 12)), videoTime: sqlite3_column_double(st, 13)))
        }
        return out
    }

    func scalarInt(_ sql: String, _ args: [Arg]) -> Int {
        lock.lock(); defer { lock.unlock() }
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return 0 }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        return sqlite3_step(st) == SQLITE_ROW ? Int(sqlite3_column_int64(st, 0)) : 0
    }
}
