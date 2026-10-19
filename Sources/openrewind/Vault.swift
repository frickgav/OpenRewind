import Foundation
import SQLCipher

/// Database encryption with SQLCipher. Whether the file is encrypted is read from the file itself
/// (a plain SQLite file starts with "SQLite format 3"), so a half-finished switch is never mistaken.
/// Same as Vault.cs on Windows.
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

    /// SQLCipher reads x'…' as a raw 256-bit key (no password stretching: it is random already).
    static func keyLiteral(_ key: Data) -> String {
        "x'" + key.map { String(format: "%02X", $0) }.joined() + "'"
    }

    /// Open the file, with `key` when it is encrypted. A wrong key (or an encrypted file opened
    /// without one) fails the probe with "file is not a database" and throws `.locked`.
    static func connect(_ url: URL, key: Data?) throws -> OpaquePointer {
        var h: OpaquePointer?
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX
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
    static func keyOpens(_ url: URL, key: Data) -> Bool {
        guard let h = try? connect(url, key: key) else { return false }
        sqlite3_close(h)
        return true
    }

    /// A switch that was cut short between its two renames: put the original back.
    static func recoverSwap(_ url: URL) {
        let fm = FileManager.default
        let p = url.path, old = p + ".old"
        if !fm.fileExists(atPath: p) && fm.fileExists(atPath: old) { _ = rename(old, p) }
        for f in [p + ".converting", p + ".converting-journal", old] where fm.fileExists(atPath: f) && fm.fileExists(atPath: p) {
            try? fm.removeItem(atPath: f)
        }
    }

    /// Rewrite the whole database encrypted with `key`, or as plain SQLite (`key` nil).
    /// Everything else waits on the lock meanwhile; on any failure the old file stays in use.
    func convert(key: Data?) throws {
        lock.lock(); defer { lock.unlock() }
        if (key != nil) == isEncrypted { return }
        let fm = FileManager.default
        let p = path.path, next = p + ".converting"
        for f in [next, next + "-journal", next + "-wal", next + "-shm"] { try? fm.removeItem(atPath: f) }
        try execLocked("PRAGMA wal_checkpoint(TRUNCATE);")
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
        let oldKey = isEncrypted ? RecordingFile.key : nil
        sqlite3_close(db)
        db = nil
        // Swap: database → .old, new → database, then drop .old. recoverSwap undoes a crash in between.
        let old = p + ".old"
        try? fm.removeItem(atPath: old)
        var swapped = rename(p, old) == 0
        if swapped {
            for f in [p + "-wal", p + "-shm"] { try? fm.removeItem(atPath: f) }
            swapped = rename(next, p) == 0
            if swapped { try? fm.removeItem(atPath: old) } else { _ = rename(old, p) }
        }
        // Reopen whichever file is in place now, so the app keeps working even if the swap failed.
        db = try Database.connect(path, key: swapped ? key : oldKey)
        if swapped { isEncrypted = key != nil }
        try execLocked("PRAGMA journal_mode=WAL;")
        try execLocked("PRAGMA busy_timeout=5000;")
        guard swapped else { throw DBError.step("could not replace the database file") }
        log(key == nil ? "database decrypted" : "database encrypted")
    }
}
