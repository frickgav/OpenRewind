import CryptoKit
import Foundation
import ImageIO
import Security

/// Every screenshot and audio clip is read and written through here, so encrypted and plain
/// files can sit side by side. `read(_:)` is the one loader: anything that opens a recording
/// (timeline, search, API, Ask AI, MCP, Whisper, new features) calls it instead of reading the file.
///
/// Encrypted file (AES-256-GCM, the same on Mac and Windows, RecordingFile.cs):
///   "ORENC" | version 2 | key id (4 bytes) | nonce (12 bytes) | ciphertext | tag (16 bytes)
/// The key id is the first 4 bytes of SHA-256(master key). The cipher key is
/// HKDF-SHA256(master, salt "OpenRewind", info "openrewind/files/v2"); the database gets its own
/// key from the same master (`Database.dbKey`). The authenticated data is the 22-byte header followed
/// by the file's place in the data folder (`identity`, e.g. "2026-10-19/14-30-05.heic"), so a file
/// moved, swapped with another or put in another's place does not open.
/// Plain files (HEIC, JPEG, WAV) never start with "ORENC".
enum RecordingFile {
    static let magic = Data("ORENC".utf8)
    static let version: UInt8 = 2
    static let headerSize = 22
    static let tagSize = 16
    static let overhead = headerSize + tagSize
    /// Half-written files (a conversion or a write that was cut short) end in this.
    static let tempSuffix = ".orenc-tmp"
    /// Decrypted copies for whisper-cli and its JSON in the temp folder start with this.
    static let tempCopyPrefix = "openrewind-"
    private static let salt = Data("OpenRewind".utf8)

    enum Failure: LocalizedError {
        case noKey, otherKey, damaged, cutShort, newer(UInt8), unsupported(UInt8)
        var errorDescription: String? {
            switch self {
            case .noKey: return "this recording is encrypted and the key is not loaded"
            case .otherKey: return "this recording was encrypted with a different key"
            case .damaged: return "this recording was changed, damaged or moved"
            case .cutShort: return "the encrypted file is cut short"
            case .newer(let v): return "encrypted with a newer OpenRewind (format \(v))"
            case .unsupported(let v): return "encrypted with a test version of OpenRewind (format \(v)) that this one cannot read"
            }
        }
    }

    private static let state = NSLock()
    private static var currentKey: Data?
    private static var encrypting = false
    private static var lastLoadTry = Date.distantPast
    /// Held while a file is converted or deleted, so retention never races a conversion.
    static let fileGate = NSLock()
    /// What the plain log may say about an app (or a call's times): nothing while Encrypt Recordings is
    /// on, since the log is not encrypted.
    static func forLog(_ s: String) -> String { encryptNew ? "(not logged while encrypted)" : s }

    /// Held by a writer that decides "encrypted or plain?" and then puts a finished file in place (the
    /// compactor's new videos). The migration takes it before its last look for new files, so a file
    /// put in place with the old setting is always seen and converted, and none comes in after.
    static let commitGate = NSLock()

    /// The master data key, once loaded from the Keychain or a recovery key. Nil = none.
    /// It stays in memory for the life of the process (recording needs it in the background); under
    /// the threat model, an app running as the user could read it anyway, so it is not wiped.
    static var key: Data? {
        get { state.lock(); defer { state.unlock() }; return currentKey }
        set { state.lock(); currentKey = newValue; state.unlock() }
    }

    /// Encrypt new recordings (Settings → Encryption & Lock → Encrypt Recordings).
    static var encryptNew: Bool {
        get { state.lock(); defer { state.unlock() }; return encrypting }
        set { state.lock(); encrypting = newValue; state.unlock() }
    }

    /// Loads the key on demand (the app reads the Keychain) when an encrypted file turns up and none is loaded.
    static var keyLoader: (() -> Data?)?

    // MARK: - the loader

    /// The recording's bytes, decrypted if needed. Throws if the file is missing, damaged, moved, or
    /// encrypted with a key we don't have.
    static func read(_ path: String) throws -> Data {
        let raw = try Data(contentsOf: URL(fileURLWithPath: path))
        return try open(raw, key: isEncrypted(raw) ? currentKeyOrLoad() : key, identity: identity(path))
    }

    static func read(_ path: String, key: Data?) throws -> Data {
        try open(Data(contentsOf: URL(fileURLWithPath: path)), key: key, identity: identity(path))
    }

    /// The loaded key, or one loaded now (at most every 30 s) when none is.
    private static func currentKeyOrLoad() -> Data? {
        if let k = key { return k }
        guard let load = keyLoader else { return nil }
        state.lock()
        let due = Date().timeIntervalSince(lastLoadTry) > 30
        if due { lastLoadTry = Date() }
        state.unlock()
        guard due, let k = load() else { return nil }
        key = k
        log("encryption key loaded for an encrypted recording")
        return k
    }

    /// `read` for callers that just show nothing on failure.
    static func data(_ path: String) -> Data? {
        do { return try read(path) } catch {
            if FileManager.default.fileExists(atPath: path) { log("cannot read \((path as NSString).lastPathComponent): \(error.localizedDescription)") }
            return nil
        }
    }

    /// A screenshot as an image, decrypted if needed.
    static func image(_ path: String) -> CGImage? { data(path).flatMap(ImageTools.image(from:)) }

    static func isNoKey(_ error: Error) -> Bool {
        if case Failure.noKey = error { return true }
        return false
    }

    /// Write a new recording: encrypted when Encrypt Recordings is on. Atomic (temp file, then rename).
    static func write(_ plain: Data, to url: URL) throws {
        try write(plain, to: url, encryptWith: encryptNew ? key : nil)
    }

    /// `durable`: flush to the disk itself before the rename (conversions replace a good original, so
    /// a power cut must not leave the new name pointing at data that never reached the disk).
    static func write(_ plain: Data, to url: URL, encryptWith: Data?, durable: Bool = false) throws {
        let out = try encryptWith.map { try seal(plain, key: $0, identity: identity(url.path)) } ?? plain
        let tmp = url.path + tempSuffix
        do {
            try out.write(to: URL(fileURLWithPath: tmp))
            if durable {
                let fd = Darwin.open(tmp, O_RDONLY)
                guard fd >= 0 else { throw NSError(domain: "openrewind", code: 43, userInfo: [NSLocalizedDescriptionKey: "cannot flush \(tmp)"]) }
                if fcntl(fd, F_FULLFSYNC) != 0 { fsync(fd) }
                close(fd)
            }
        } catch {
            try? FileManager.default.removeItem(atPath: tmp)
            throw error
        }
        guard rename(tmp, url.path) == 0 else {
            try? FileManager.default.removeItem(atPath: tmp)
            throw NSError(domain: "openrewind", code: 41, userInfo: [NSLocalizedDescriptionKey: "cannot replace \(url.path)"])
        }
    }

    /// Delete a recording (retention and Delete Recordings).
    static func delete(_ path: String) {
        fileGate.lock(); defer { fileGate.unlock() }
        try? FileManager.default.removeItem(atPath: path)
        try? FileManager.default.removeItem(atPath: path + tempSuffix)
    }

    /// A plain copy for tools that need a file path (whisper-cli). For a plain file it is the file
    /// itself; an encrypted one is decrypted into the user's temp folder (0600) and deleted after.
    static func withPlainCopy<T>(_ path: String, _ body: (String) throws -> T) throws -> T {
        guard isEncrypted(path: path) else { return try body(path) }
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent("\(tempCopyPrefix)\(UUID().uuidString).\((path as NSString).pathExtension)")
        let plain = try read(path)
        guard FileManager.default.createFile(atPath: tmp.path, contents: plain, attributes: [.posixPermissions: 0o600]) else {
            throw NSError(domain: "openrewind", code: 42, userInfo: [NSLocalizedDescriptionKey: "cannot write \(tmp.path)"])
        }
        defer { try? FileManager.default.removeItem(at: tmp) }
        return try body(tmp.path)
    }

    /// At launch: remove decrypted audio and videos and whisper transcripts a crash or kill left in the temp
    /// folder. Files from the last 10 minutes are left alone (another OpenRewind may be using them).
    @discardableResult
    static func cleanTempCopies(in dir: URL = FileManager.default.temporaryDirectory) -> Int {
        let fm = FileManager.default
        guard let names = try? fm.contentsOfDirectory(atPath: dir.path) else { return 0 }
        var removed = 0
        for name in names where name.hasPrefix(tempCopyPrefix) && [".wav", ".json", ".mp4", ".mov"].contains(where: { name.hasSuffix($0) }) {
            let p = dir.appendingPathComponent(name).path
            guard let m = (try? fm.attributesOfItem(atPath: p))?[.modificationDate] as? Date, m < Date().addingTimeInterval(-600) else { continue }
            if (try? fm.removeItem(atPath: p)) != nil { removed += 1 }
        }
        if removed > 0 { log("removed \(removed) leftover temporary audio and video files") }
        return removed
    }

    // MARK: - format

    static func isEncrypted(_ data: Data) -> Bool { data.count >= magic.count && data.prefix(magic.count).elementsEqual(magic) }

    static func isEncrypted(path: String) -> Bool {
        guard let h = FileHandle(forReadingAtPath: path) else { return false }
        defer { try? h.close() }
        return isEncrypted((try? h.read(upToCount: magic.count)) ?? Data())
    }

    /// 4 bytes that name a master key, so a file made with another key says so instead of "damaged".
    /// Also the check digits of the recovery key.
    static func keyId(_ key: Data) -> Data { Data(SHA256.hash(data: key).prefix(4)) }

    /// A key derived from the master for one purpose; the master itself never encrypts anything.
    static func derive(_ master: Data, _ purpose: String) -> SymmetricKey {
        precondition(master.count == 32, "the data key must be 32 bytes")
        return HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: master), salt: salt,
                                      info: Data(purpose.utf8), outputByteCount: 32)
    }

    static func fileKey(_ master: Data) -> SymmetricKey { derive(master, "openrewind/files/v2") }

    /// Where a recording sits in the data folder, with "/" between folders: everything after the last
    /// folder named "data" (".../data/2026-10-19/a.heic" → "2026-10-19/a.heic"), or the file name when
    /// the path has no such folder. It does not depend on where the data folder is, so moving the
    /// whole folder (a new home, another Mac) keeps every file readable.
    static func identity(_ path: String) -> String {
        let parts = path.split(whereSeparator: { $0 == "/" || $0 == "\\" }).map(String.init)
        let rel: String
        if let i = parts.lastIndex(where: { $0.caseInsensitiveCompare("data") == .orderedSame }), i < parts.count - 1 {
            rel = parts[(i + 1)...].joined(separator: "/")
        } else {
            rel = parts.last ?? ""
        }
        return rel.precomposedStringWithCanonicalMapping
    }

    static func seal(_ plain: Data, key: Data, identity: String) throws -> Data {
        let nonce = AES.GCM.Nonce()
        var header = magic
        header.append(version)
        header.append(keyId(key))
        header.append(nonce.withUnsafeBytes { Data($0) })
        let box = try AES.GCM.seal(plain, using: fileKey(key), nonce: nonce, authenticating: header + Data(identity.utf8))
        return header + box.ciphertext + box.tag
    }

    /// Decrypt an encrypted file's bytes; plain bytes come back as they are.
    static func open(_ raw: Data, key: Data?, identity: String) throws -> Data {
        guard isEncrypted(raw) else { return raw }
        let d = Data(raw)  // indices from 0
        guard d.count >= overhead else { throw Failure.cutShort }
        guard d[5] <= version else { throw Failure.newer(d[5]) }
        guard d[5] == version else { throw Failure.unsupported(d[5]) }
        guard let key else { throw Failure.noKey }
        guard d.subdata(in: 6..<10) == keyId(key) else { throw Failure.otherKey }
        do {
            let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: d.subdata(in: 10..<22)),
                                            ciphertext: d.subdata(in: headerSize..<(d.count - tagSize)),
                                            tag: d.subdata(in: (d.count - tagSize)..<d.count))
            return try AES.GCM.open(box, using: fileKey(key), authenticating: d.subdata(in: 0..<headerSize) + Data(identity.utf8))
        } catch {
            throw Failure.damaged
        }
    }

    /// Bring one file to the wanted state (encrypted with `key`, or plain). Returns true if it changed.
    /// Safe to stop at any point: the file is either the old or the new version, never half of one.
    static func convert(_ path: String, key: Data, encrypt: Bool) throws -> Bool {
        fileGate.lock(); defer { fileGate.unlock() }
        guard let data = FileManager.default.contents(atPath: path) else { return false }  // deleted meanwhile
        guard isEncrypted(data) != encrypt else { return false }
        let plain = try open(data, key: key, identity: identity(path))
        try write(plain, to: URL(fileURLWithPath: path), encryptWith: encrypt ? key : nil, durable: true)
        return true
    }

    /// True when `key` opens the encrypted file at `path` (a recovery key typed at launch).
    static func keyOpens(_ path: String, key: Data) -> Bool { (try? read(path, key: key)) != nil }

    /// The first encrypted recording under `dir` (looks at no more than `limit` files), or nil.
    static func firstEncrypted(in dir: URL) -> String? {
        // The whole tree, not a sample: a decrypt cut short can leave its encrypted files after any number
        // of plain ones, and "none" decides whether the recovery key is asked for. Only the first bytes of
        // each file are read, and it stops at the first encrypted one; it runs only when the key is missing.
        guard let e = FileManager.default.enumerator(at: dir, includingPropertiesForKeys: [.isRegularFileKey]) else { return nil }
        for case let u as URL in e {
            guard (try? u.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true, !u.path.hasSuffix(tempSuffix) else { continue }
            if isEncrypted(path: u.path) { return u.path }
        }
        return nil
    }

    static func newKey() -> Data { SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) } }

    // MARK: - self-test

    /// `OpenRewind selftest-encryption` (run by the Mac CI): the same vectors as the Windows tests,
    /// so both apps read each other's files. Returns the problems found; empty means all good.
    static func selfTest() -> [String] {
        var problems: [String] = []
        func hex(_ d: Data) -> String { d.map { String(format: "%02X", $0) }.joined() }
        func unhex(_ s: String) -> Data {
            var out = Data()
            var i = s.startIndex
            while i < s.endIndex { let j = s.index(i, offsetBy: 2); out.append(UInt8(s[i..<j], radix: 16)!); i = j }
            return out
        }
        let master = Data((0..<32).map { UInt8($0) })
        let fk = fileKey(master).withUnsafeBytes { Data($0) }
        if hex(fk) != "59B7C32A10B7CF03E2F945651E2246679A0811D289C866A168A9C485D54D23E8" { problems.append("file key \(hex(fk))") }
        let dk = Database.dbKey(master)
        if hex(dk) != "666E51B33491C38D6CD711438375F95265F289C70E24096D4724F568F45EFEA6" { problems.append("db key \(hex(dk))") }
        if hex(keyId(master)) != "630DCD29" { problems.append("key id \(hex(keyId(master)))") }
        let golden = unhex("4F52454E4302630DCD296465666768696A6B6C6D6E6F0D735A50A82340FCB94E8B260C25BD6D4C9D1238BD873F0735740AC39D139B4097F0C8B9405B")
        let id = "2026-10-19/14-30-05.jpg"
        if let plain = try? open(golden, key: master, identity: id), String(decoding: plain, as: UTF8.self) == "hello, encrypted world" {} else {
            problems.append("cannot open the Windows-made file")
        }
        if (try? open(golden, key: master, identity: "2026-10-19/14-30-07.jpg")) != nil { problems.append("a moved file opened") }
        if identity("/Users/a/.openrewind/data/audio/2026-10-19/x.wav") != "audio/2026-10-19/x.wav" { problems.append("identity") }
        let key = newKey()
        let sample = Data("round trip".utf8)
        if let s = try? seal(sample, key: key, identity: id), (try? open(s, key: key, identity: id)) == sample {} else { problems.append("round trip") }
        if let s = try? seal(sample, key: key, identity: id) {
            var bad = s
            bad[bad.count - 1] ^= 1
            if (try? open(bad, key: key, identity: id)) != nil { problems.append("a changed byte was not caught") }
            if (try? open(s, key: newKey(), identity: id)) != nil { problems.append("another key opened it") }
        }
        if RecoveryKey.parse(RecoveryKey.text(key)) != key { problems.append("recovery key") }
        problems += Database.selfTest(key: key)
        return problems
    }
}

/// The data key written out for the user to keep: 32 bytes and a 4-byte check, as 72 hex digits
/// in groups of 8. The same text works in the Mac and the Windows app (RecoveryKey.cs).
enum RecoveryKey {
    static func text(_ key: Data) -> String {
        let hex = (key + RecordingFile.keyId(key)).map { String(format: "%02X", $0) }.joined()
        return stride(from: 0, to: hex.count, by: 8).map { i in
            let a = hex.index(hex.startIndex, offsetBy: i)
            return String(hex[a..<hex.index(a, offsetBy: 8)])
        }.joined(separator: "-")
    }

    /// The file "Export Recovery Key…" saves.
    static func file(_ key: Data, now: Date = Date()) -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        return """
        OpenRewind recovery key

        \(text(key))

        This key opens your encrypted OpenRewind recordings (screenshots, audio and the database).
        Keep it somewhere safe and private, like a password manager.
        - If this Mac's Keychain is lost or reset, or you move your recordings to another computer,
          OpenRewind asks for this key. Without it the recordings cannot be opened.
        - Anyone with this key and a copy of your recordings can read them.

        Saved \(f.string(from: now)).

        """
    }

    /// The key in pasted text or a saved file, or nil if there is none or a digit is wrong.
    /// Spaces, dashes and line breaks are ignored.
    static func parse(_ text: String) -> Data? {
        let skip: Set<Character> = ["-", " ", "\t", "\r", "\n", "\r\n"]
        for candidate in [text] + text.components(separatedBy: "\n") {
            var hex = ""
            var ok = true
            for c in candidate {
                if c.isASCII && c.isHexDigit { hex.append(c) } else if !skip.contains(c) { ok = false; break }
            }
            guard ok, hex.count == 72 else { continue }
            var raw = Data()
            var i = hex.startIndex
            while i < hex.endIndex {
                let j = hex.index(i, offsetBy: 2)
                guard let b = UInt8(hex[i..<j], radix: 16) else { ok = false; break }
                raw.append(b)
                i = j
            }
            guard ok else { continue }
            let key = raw.prefix(32)
            if Data(raw.suffix(4)) == RecordingFile.keyId(Data(key)) { return Data(key) }
        }
        return nil
    }
}

/// The data key in the login Keychain (protected by your Mac login password), so OpenRewind can
/// record after you log in without asking for a password. Never written to settings or disk.
///
/// It is the only copy besides the recovery key, so there is never a moment without one stored:
/// a replacement is added under a second name first, and a key that can't be read (a denied
/// prompt, a locked keychain) is never taken for "no key".
enum DataKeyStore {
    static let service = "ai.agilityresources.openrewind.data-key"
    static let account = "recordings"
    /// Holds a copy while the main item is replaced; read when the main item is missing.
    static let pendingAccount = "recordings.pending"

    enum Lookup {
        case found(Data)
        case missing
        case failed(OSStatus)
    }

    private static func query(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
    }

    private static func item(_ account: String) -> Lookup {
        var q = query(account)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &out)
        if status == errSecItemNotFound { return .missing }
        guard status == errSecSuccess, let data = out as? Data, data.count == 32 else {
            log("cannot read the encryption key from the Keychain (\(status))")
            return .failed(status == errSecSuccess ? errSecDecode : status)
        }
        return .found(data)
    }

    private static func add(_ key: Data, account: String) -> OSStatus {
        var q = query(account)
        q[kSecValueData as String] = key
        q[kSecAttrLabel as String] = "OpenRewind recordings key"
        // Note: this item lives in the file-based login keychain, which ignores "this device only";
        // it is protected by the login password and travels with login.keychain-db (see README).
        q[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        return SecItemAdd(q as CFDictionary, nil)
    }

    /// The stored key, whether there is none, or why it could not be read.
    static func read() -> Lookup {
        switch item(account) {
        case .found(let k):
            takeOwnership(k)
            return .found(k)
        case .failed(let s):
            return .failed(s)
        case .missing:
            // A replacement that was cut short: the copy is under the second name.
            switch item(pendingAccount) {
            case .found(let k):
                log("restoring the encryption key from its spare copy in the Keychain")
                _ = replace(k)
                return .found(k)
            case .failed(let s):
                // A spare that is there but can't be read is not "no key": making a new one would strand it.
                return .failed(s)
            case .missing:
                return .missing
            }
        }
    }

    static func load() -> Data? {
        if case .found(let k) = read() { return k }
        return nil
    }

    /// Save a new key when none is stored. Never overwrites one.
    @discardableResult
    static func store(_ key: Data) -> Bool {
        let status = add(key, account: account)
        if status != errSecSuccess { log("cannot save the encryption key to the Keychain (\(status))") }
        return status == errSecSuccess
    }

    /// Replace the stored key (a recovery key that opens the recordings, or the same key saved again
    /// so this build owns it). The spare copy is written first and removed only when the main one is back.
    @discardableResult
    static func replace(_ key: Data) -> Bool {
        _ = SecItemDelete(query(pendingAccount) as CFDictionary)
        let spare = add(key, account: pendingAccount)
        guard spare == errSecSuccess else {
            log("cannot save a spare copy of the encryption key (\(spare)); the stored key was left as it was")
            return false
        }
        _ = SecItemDelete(query(account) as CFDictionary)
        let status = add(key, account: account)
        guard status == errSecSuccess else {
            log("cannot save the encryption key to the Keychain (\(status)); the spare copy is kept")
            return false
        }
        _ = SecItemDelete(query(pendingAccount) as CFDictionary)
        return true
    }

    /// Like the AI keys (see KeyStore): after the first read in a new build, save the key again so
    /// this build owns it and later launches don't bring up the Keychain prompt. If that fails, the
    /// stored key stays as it was and the next launch tries again.
    private static func takeOwnership(_ key: Data) {
        let build = KeyStore.buildID, flag = "dataKeyOwner"
        guard UserDefaults.standard.string(forKey: flag) != build else { return }
        if replace(key) { UserDefaults.standard.set(build, forKey: flag) }
    }
}

/// Moves existing recordings to the chosen state: the database first, then every file under
/// data/. Each file is converted on its own and is never half-written, so stopping at any point
/// (quit, crash, power cut) is safe and the next run carries on where this one stopped.
enum VaultMigration {
    struct Result {
        var converted = 0, failed = 0, cancelled = false
        /// The database could not be converted (left as it was, tried again next launch).
        var databaseFailed = false
        var finished: Bool { !cancelled && failed == 0 && !databaseFailed }
    }

    private static let state = NSLock()
    private static var progress = (done: 0, total: 0, running: false)
    /// Files done and files in all, while a migration runs (for the menu).
    static var status: (done: Int, total: Int, running: Bool) { state.lock(); defer { state.unlock() }; return progress }
    private static func set(_ p: (done: Int, total: Int, running: Bool)) { state.lock(); progress = p; state.unlock() }

    static func run(db: Database, dataDir: URL, key: Data, encrypt: Bool, cancelled: () -> Bool) -> Result {
        set((0, 0, true))
        defer { let s = status; set((s.done, s.total, false)) }
        var r = Result()
        do {
            if db.isEncrypted != encrypt { try db.convert(key: encrypt ? key : nil) }
        } catch {
            log("cannot \(encrypt ? "encrypt" : "decrypt") the database: \(error)")
            r.databaseFailed = true
            return r
        }
        let files = self.files(dataDir)
        var done = 0, total = files.count
        set((0, total, true))
        var failedFiles: [String] = []
        func each(_ list: [String]) -> Bool {
            for f in list {
                if cancelled() { return false }
                do { if try RecordingFile.convert(f, key: key, encrypt: encrypt) { r.converted += 1 } } catch {
                    r.failed += 1
                    failedFiles.append(f)
                    log("cannot \(encrypt ? "encrypt" : "decrypt") \((f as NSString).lastPathComponent): \(error.localizedDescription)")
                }
                done += 1
                set((done, total, true))
            }
            return true
        }
        guard each(files) else { r.cancelled = true; return r }
        // A recording saved just as the switch flipped (it read the old setting) can land after the
        // list above was made: look once more for files that were not on it. A video the compactor is
        // putting in place right now is waited for (commitGate); later ones read the new setting.
        RecordingFile.commitGate.lock(); RecordingFile.commitGate.unlock()
        let seen = Set(files)
        let late = self.files(dataDir).filter { !seen.contains($0) }
        total += late.count
        guard each(late) else { r.cancelled = true; return r }
        // Files that failed (most often one being read or written at that moment): once more, a moment later.
        if !failedFiles.isEmpty {
            Thread.sleep(forTimeInterval: 2)
            let again = failedFiles
            failedFiles = []
            r.failed -= again.count
            total += again.count
            guard each(again) else { r.cancelled = true; return r }
        }
        log("\(encrypt ? "encrypted" : "decrypted") \(r.converted) recordings" + (r.failed > 0 ? ", \(r.failed) failed (tried again next launch)" : ""))
        return r
    }

    /// The kinds of file OpenRewind records (stills, audio, videos). Nothing else under data/ is touched.
    static let recordingExtensions: Set<String> = ["heic", "jpg", "jpeg", "png", "wav", "mp4", "mov"]

    /// Every recording under data/, after removing temp files left by a write that was cut short.
    private static func files(_ dir: URL) -> [String] {
        let fm = FileManager.default
        guard let e = fm.enumerator(at: dir, includingPropertiesForKeys: [.isRegularFileKey, .contentModificationDateKey]) else { return [] }
        var out: [String] = []
        for case let u as URL in e {
            let v = try? u.resourceValues(forKeys: [.isRegularFileKey, .contentModificationDateKey])
            guard v?.isRegularFile == true else { continue }
            // A video the compactor is still writing: it is checked and moved into place (or deleted) by it.
            if u.path.hasSuffix(Compaction.tempSuffix) { continue }
            if u.path.hasSuffix(RecordingFile.tempSuffix) {
                // Older than a minute: a write that is still going on is left alone.
                if let m = v?.contentModificationDate, m < Date().addingTimeInterval(-60) { try? fm.removeItem(at: u) }
                continue
            }
            guard recordingExtensions.contains(u.pathExtension.lowercased()) else { continue }
            out.append(u.path)
        }
        return out.sorted()
    }
}
