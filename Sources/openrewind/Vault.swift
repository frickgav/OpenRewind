import CryptoKit
import Foundation
import SQLCipher

/// Database encryption with SQLCipher. Whether the file is encrypted is read from the file itself
/// (a plain SQLite file starts with "SQLite format 3"), so a half-finished switch is never mistaken.
/// The SQLCipher key is derived from the master data key (HKDF, "openrewind/db/v2"), so the
/// database and the files never share a key. Same as Vault.cs on Windows.
extension Database {
    private static let sqliteHeader = Data("SQLite format 3\0".utf8)

    /// True when the file exists, has data, and is not plain SQLite.
    static func isEncryptedFile(_ url: URL) -> Bool {
        guard hasData(url), let h = FileHandle(forReadingAtPath: url.path) else { return false }
        defer { try? h.close() }
        let head = (try? h.read(upToCount: 16)) ?? Data()
        return head != sqliteHeader
    }

    static func hasData(_ url: URL) -> Bool {
        let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.intValue ?? 0
        return size > 0
    }

    /// The SQLCipher key for a master data key.
    static func dbKey(_ master: Data) -> Data {
        RecordingFile.derive(master, "openrewind/db/v2").withUnsafeBytes { Data($0) }
    }

    /// SQLCipher reads x'…' as a raw 256-bit key (no password stretching: it is random already).
    static func keyLiteral(_ master: Data) -> String {
        "x'" + dbKey(master).map { String(format: "%02X", $0) }.joined() + "'"
    }

    /// True while `convert(key:)` rewrites the database. Everything else waits on the lock
    /// meanwhile, so the menu shows a note instead of asking for counts (which would hang it).
    var isConverting: Bool {
        convertState.lock(); defer { convertState.unlock() }
        return converting
    }

    private func setConverting(_ on: Bool) {
        convertState.lock(); converting = on; convertState.unlock()
    }

    /// Open the file, with `key` (the master data key) when it is encrypted. A wrong key (or an
    /// encrypted file opened without one) fails the probe with "file is not a database" and throws `.locked`.
    static func connect(_ url: URL, key: Data?, readOnly: Bool = false) throws -> OpaquePointer {
        var h: OpaquePointer?
        let flags = (readOnly ? SQLITE_OPEN_READONLY : SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE) | SQLITE_OPEN_FULLMUTEX
        guard sqlite3_open_v2(url.path, &h, flags, nil) == SQLITE_OK, let handle = h else {
            let m = h.map { String(cString: sqlite3_errmsg($0)) } ?? "cannot open"
            sqlite3_close(h)
            throw DBError.open(m)
        }
        var ok = true
        if let key {
            let lit = keyLiteral(key).replacingOccurrences(of: "'", with: "''")
            ok = sqlite3_exec(handle, "PRAGMA key = '\(lit)';", nil, nil, nil) == SQLITE_OK
        }
        if ok { ok = sqlite3_exec(handle, "SELECT count(*) FROM sqlite_master;", nil, nil, nil) == SQLITE_OK }
        if !ok {
            let m = String(cString: sqlite3_errmsg(handle))
            sqlite3_close(handle)
            throw DBError.locked(key == nil ? "the database is encrypted and the key is not on this Mac"
                                            : "the key does not open this database (\(m))")
        }
        return handle
    }

    /// True when `key` opens the database at `url` (for a recovery key typed at launch).
    /// Only opens it: no schema changes, no home move.
    static func keyOpens(_ url: URL, key: Data) -> Bool {
        guard let h = try? connect(url, key: key, readOnly: true) else { return false }
        sqlite3_close(h)
        return true
    }

    /// The files that go with a database in WAL mode: its newest changes can still be in the -wal.
    private static let walSuffixes = ["-wal", "-shm"]

    /// A switch that was cut short between its two renames: put the original back, with its WAL.
    static func recoverSwap(_ url: URL) {
        let fm = FileManager.default
        let p = url.path, old = p + ".old"
        if !fm.fileExists(atPath: p) && fm.fileExists(atPath: old) {
            _ = rename(old, p)
            for s in walSuffixes where fm.fileExists(atPath: old + s) { _ = rename(old + s, p + s) }
        }
        for f in [p + ".converting", p + ".converting-journal", old, old + "-wal", old + "-shm"]
        where fm.fileExists(atPath: f) && fm.fileExists(atPath: p) {
            try? fm.removeItem(atPath: f)
        }
    }

    /// Write the WAL into the database file (call with `lock` held). A reader in another process (the MCP
    /// server, a CLI) can hold part of it back for a moment: try for about 5 s. True when all of it went in.
    private func checkpointLocked() -> Bool {
        for attempt in 1...10 {
            var st: OpaquePointer?
            guard sqlite3_prepare_v2(db, "PRAGMA wal_checkpoint(TRUNCATE);", -1, &st, nil) == SQLITE_OK else { return false }
            var done = false
            // busy, frames in the WAL, frames written into the database (-1, -1 when not in WAL mode).
            if sqlite3_step(st) == SQLITE_ROW {
                done = sqlite3_column_int(st, 0) == 0 && sqlite3_column_int(st, 1) == sqlite3_column_int(st, 2)
            }
            sqlite3_finalize(st)
            if done { return true }
            if attempt < 10 { Thread.sleep(forTimeInterval: 0.5) }
        }
        return false
    }

    /// Room for the new copy: its size plus 10% and 100 MB.
    private func checkFreeSpace() throws {
        let fm = FileManager.default
        guard let size = (try? fm.attributesOfItem(atPath: path.path)[.size] as? NSNumber)?.int64Value,
              let free = (try? fm.attributesOfFileSystem(forPath: path.deletingLastPathComponent().path)[.systemFreeSize] as? NSNumber)?.int64Value
        else { return }  // can't tell: try anyway
        let need = size + size / 10 + (100 << 20)
        if free < need {
            throw DBError.step("not enough free disk space to convert the database (needs \(need >> 20) MB, \(free >> 20) MB free)")
        }
    }

    /// Rewrite the whole database encrypted with `key`, or as plain SQLite (`key` nil).
    /// Everything else waits on the lock meanwhile (recording pauses until it is done). On any
    /// failure the database stays usable: whichever file is in place is opened again.
    func convert(key: Data?) throws {
        lock.lock(); defer { lock.unlock() }
        if (key != nil) == isEncrypted { return }
        setConverting(true)
        defer { setConverting(false) }
        try checkFreeSpace()
        let fm = FileManager.default
        let p = path.path, next = p + ".converting", old = p + ".old"
        for f in [next, next + "-journal", next + "-wal", next + "-shm"] { try? fm.removeItem(atPath: f) }
        // The export below reads through the WAL either way; when part of it stays behind, the WAL is kept
        // with the original until the new file is in place, so a failed swap loses nothing.
        if !checkpointLocked() { log("the database is open in another program; its newest changes stay in its WAL until the switch is done") }
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, "ATTACH DATABASE ? AS conv KEY ?;", -1, &st, nil) == SQLITE_OK else { throw DBError.prepare(errmsg) }
        bind(st, 1, next)
        bind(st, 2, key.map(Database.keyLiteral) ?? "")
        let attached = sqlite3_step(st) == SQLITE_DONE
        sqlite3_finalize(st)
        guard attached else { throw DBError.step(errmsg) }
        do {
            try execLocked("SELECT sqlcipher_export('conv');")
        } catch {
            try? execLocked("DETACH DATABASE conv;")
            try? fm.removeItem(atPath: next)
            throw error
        }
        try execLocked("DETACH DATABASE conv;")
        let before = openKey
        sqlite3_close(db)
        db = nil
        // Swap: database → .old, new → database, then drop .old. recoverSwap undoes a crash in between.
        try? fm.removeItem(atPath: old)
        var swapped = false
        for s in Self.walSuffixes { try? fm.removeItem(atPath: old + s) }
        if rename(p, old) == 0 {
            // The WAL goes with the original: it is the original's, and may hold changes not in it yet.
            for s in Self.walSuffixes where fm.fileExists(atPath: p + s) { _ = rename(p + s, old + s) }
            swapped = rename(next, p) == 0
            if swapped {
                for f in [old, old + "-wal", old + "-shm"] { try? fm.removeItem(atPath: f) }
            } else {
                _ = rename(old, p)
                for s in Self.walSuffixes where fm.fileExists(atPath: old + s) { _ = rename(old + s, p + s) }
            }
        }
        if !swapped { try? fm.removeItem(atPath: next) }
        if !fm.fileExists(atPath: p) && fm.fileExists(atPath: old) {
            _ = rename(old, p)
            for s in Self.walSuffixes where fm.fileExists(atPath: old + s) { _ = rename(old + s, p + s) }
        }
        // Open whichever file is in place, so the app keeps working even if the swap failed.
        db = try Database.connect(path, key: swapped ? key : before)
        if swapped { isEncrypted = key != nil; openKey = key }
        try execLocked("PRAGMA journal_mode=WAL;")
        try execLocked("PRAGMA busy_timeout=5000;")
        guard swapped else { throw DBError.step("could not replace the database file; it was left as it was") }
        log(key == nil ? "database decrypted" : "database encrypted")
    }

    /// For `OpenRewind selftest-encryption`: a throwaway database encrypted, searched, reopened,
    /// refused with another key, opened read-only and decrypted again. Returns the problems found.
    static func selfTest(key: Data) -> [String] {
        var problems: [String] = []
        let fm = FileManager.default
        let dir = fm.temporaryDirectory.appendingPathComponent("openrewind-selftest-\(UUID().uuidString)")
        defer { try? fm.removeItem(at: dir) }
        do {
            try fm.createDirectory(at: dir, withIntermediateDirectories: true)
            let url = dir.appendingPathComponent("db.sqlite")
            let f = Frame(id: 0, timestamp: "2026-10-19T14:30:05Z", appName: "Notes", windowName: "w", focused: true,
                          snapshotPath: "", text: "quarterly zebra plan", textSource: "ocr", hash: 1)
            do {
                let db = try Database(path: url)
                try db.insert(f)
                try db.convert(key: key)
                if !db.isEncrypted || !isEncryptedFile(url) { problems.append("database not encrypted after convert") }
                if db.search(SearchQuery(q: "zebra")).total != 1 { problems.append("search after encrypt") }
            }
            do {
                let db = try Database(path: url, key: key)
                if db.search(SearchQuery(q: "zebra")).total != 1 { problems.append("search after reopen") }
            }
            if (try? Database(path: url, key: RecordingFile.newKey())) != nil { problems.append("another key opened the database") }
            if (try? Database(path: url)) != nil { problems.append("opened without a key") }
            if !keyOpens(url, key: key) { problems.append("keyOpens") }
            do {
                let ro = try Database(readOnly: url, key: key)
                if ro.search(SearchQuery(q: "zebra")).total != 1 { problems.append("read-only search") }
            }
            do {
                let db = try Database(path: url, key: key)
                try db.convert(key: nil)
                if db.isEncrypted || isEncryptedFile(url) { problems.append("database still encrypted after decrypt") }
                if db.search(SearchQuery(q: "zebra")).total != 1 { problems.append("search after decrypt") }
            }
        } catch {
            problems.append("database: \(error)")
        }
        return problems
    }
}
