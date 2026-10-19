import CryptoKit
import Foundation
import ImageIO
import Security

/// Every screenshot and audio clip is read and written through here, so encrypted and plain
/// files can sit side by side. `read(_:)` is the one loader: anything that opens a recording
/// (timeline, search, API, Ask AI, Whisper, new features) calls it instead of reading the file.
///
/// Encrypted file (AES-256-GCM, the same on Mac and Windows):
///   "ORENC" | version 1 | key id (4 bytes) | nonce (12 bytes) | ciphertext | tag (16 bytes)
/// The first 22 bytes are authenticated too. Plain files (HEIC, JPEG, WAV) never start with "ORENC".
enum RecordingFile {
    static let magic = Data("ORENC".utf8)
    static let version: UInt8 = 1
    static let headerSize = 22
    static let tagSize = 16
    static let overhead = headerSize + tagSize
    /// Half-written files (a conversion or a write that was cut short) end in this.
    static let tempSuffix = ".orenc-tmp"

    enum Failure: LocalizedError {
        case noKey, otherKey, damaged, cutShort, newer(UInt8)
        var errorDescription: String? {
            switch self {
            case .noKey: return "this recording is encrypted and the key is not loaded"
            case .otherKey: return "this recording was encrypted with a different key"
            case .damaged: return "this recording was changed or damaged"
            case .cutShort: return "the encrypted file is cut short"
            case .newer(let v): return "encrypted with a newer OpenRewind (format \(v))"
            }
        }
    }

    private static let state = NSLock()
    private static var currentKey: Data?
    private static var encrypting = false
    /// Held while a file is converted or deleted, so retention never races a conversion.
    static let fileGate = NSLock()

    /// The data key, once loaded from the Keychain or a recovery key. Nil = none.
    static var key: Data? {
        get { state.lock(); defer { state.unlock() }; return currentKey }
        set { state.lock(); currentKey = newValue; state.unlock() }
    }

    /// Encrypt new recordings (Settings → Encryption & Lock → Encrypt Recordings).
    static var encryptNew: Bool {
        get { state.lock(); defer { state.unlock() }; return encrypting }
        set { state.lock(); encrypting = newValue; state.unlock() }
    }

    // MARK: - the loader

    /// The recording's bytes, decrypted if needed. Throws if the file is missing, damaged, or
    /// encrypted with a key we don't have.
    static func read(_ path: String, key: Data? = RecordingFile.key) throws -> Data {
        try open(Data(contentsOf: URL(fileURLWithPath: path)), key: key)
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

    /// Size of the recording once decrypted (the timeline works out audio length from it). 0 if missing.
    static func plainLength(_ path: String) -> Int {
        guard let size = (try? FileManager.default.attributesOfItem(atPath: path)[.size] as? Int) else { return 0 }
        return isEncrypted(path: path) ? max(0, size - overhead) : size
    }

    /// Write a new recording: encrypted when Encrypt Recordings is on. Atomic (temp file, then rename).
    static func write(_ plain: Data, to url: URL) throws {
        try write(plain, to: url, encryptWith: encryptNew ? key : nil)
    }

    static func write(_ plain: Data, to url: URL, encryptWith: Data?) throws {
        let out = try encryptWith.map { try seal(plain, key: $0) } ?? plain
        let tmp = url.path + tempSuffix
        guard FileManager.default.createFile(atPath: tmp, contents: out) else {
            throw NSError(domain: "openrewind", code: 40, userInfo: [NSLocalizedDescriptionKey: "cannot write \(tmp)"])
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
    /// itself; an encrypted one is decrypted into the user's temp folder, and `done` deletes it.
    static func withPlainCopy<T>(_ path: String, _ body: (String) throws -> T) throws -> T {
        guard isEncrypted(path: path) else { return try body(path) }
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent("openrewind-\(UUID().uuidString).\((path as NSString).pathExtension)")
        let plain = try read(path)
        guard FileManager.default.createFile(atPath: tmp.path, contents: plain, attributes: [.posixPermissions: 0o600]) else {
            throw NSError(domain: "openrewind", code: 42, userInfo: [NSLocalizedDescriptionKey: "cannot write \(tmp.path)"])
        }
        defer { try? FileManager.default.removeItem(at: tmp) }
        return try body(tmp.path)
    }

    // MARK: - format

    static func isEncrypted(_ data: Data) -> Bool { data.count >= magic.count && data.prefix(magic.count).elementsEqual(magic) }

    static func isEncrypted(path: String) -> Bool {
        guard let h = FileHandle(forReadingAtPath: path) else { return false }
        defer { try? h.close() }
        return isEncrypted((try? h.read(upToCount: magic.count)) ?? Data())
    }

    /// 4 bytes that name a key, so a file made with another key says so instead of "damaged".
    static func keyId(_ key: Data) -> Data { Data(SHA256.hash(data: key).prefix(4)) }

    static func seal(_ plain: Data, key: Data) throws -> Data {
        precondition(key.count == 32, "the data key must be 32 bytes")
        let nonce = AES.GCM.Nonce()
        var header = magic
        header.append(version)
        header.append(keyId(key))
        header.append(nonce.withUnsafeBytes { Data($0) })
        let box = try AES.GCM.seal(plain, using: SymmetricKey(data: key), nonce: nonce, authenticating: header)
        return header + box.ciphertext + box.tag
    }

    /// Decrypt an encrypted file's bytes; plain bytes come back as they are.
    static func open(_ raw: Data, key: Data?) throws -> Data {
        guard isEncrypted(raw) else { return raw }
        let d = Data(raw)  // indices from 0
        guard d.count >= overhead else { throw Failure.cutShort }
        guard d[5] == version else { throw Failure.newer(d[5]) }
        guard let key else { throw Failure.noKey }
        guard d.subdata(in: 6..<10) == keyId(key) else { throw Failure.otherKey }
        do {
            let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: d.subdata(in: 10..<22)),
                                            ciphertext: d.subdata(in: headerSize..<(d.count - tagSize)),
                                            tag: d.subdata(in: (d.count - tagSize)..<d.count))
            return try AES.GCM.open(box, using: SymmetricKey(data: key), authenticating: d.subdata(in: 0..<headerSize))
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
        try write(open(data, key: key), to: URL(fileURLWithPath: path), encryptWith: encrypt ? key : nil)
        return true
    }

    static func newKey() -> Data { SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) } }
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
enum DataKeyStore {
    static let service = "ai.agilityresources.openrewind.data-key"
    static let account = "recordings"

    static func load() -> Data? {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account, kSecReturnData as String: true,
                                kSecMatchLimit as String: kSecMatchLimitOne]
        var out: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &out)
        guard status == errSecSuccess, let data = out as? Data, data.count == 32 else {
            if status != errSecItemNotFound { log("cannot read the encryption key from the Keychain (\(status))") }
            return nil
        }
        takeOwnership(data)
        return data
    }

    @discardableResult
    static func save(_ key: Data) -> Bool {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account]
        _ = SecItemDelete(q as CFDictionary)
        var add = q
        add[kSecValueData as String] = key
        add[kSecAttrLabel as String] = "OpenRewind recordings key"
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(add as CFDictionary, nil)
        if status != errSecSuccess { log("cannot save the encryption key to the Keychain (\(status))") }
        return status == errSecSuccess
    }

    /// Like the AI keys (see KeyStore): after the first read in a new build, save the key again so
    /// this build owns it and later launches don't bring up the Keychain prompt.
    private static func takeOwnership(_ key: Data) {
        let build = KeyStore.buildID, flag = "dataKeyOwner"
        guard UserDefaults.standard.string(forKey: flag) != build else { return }
        if save(key) { UserDefaults.standard.set(build, forKey: flag) }
    }
}

/// Moves existing recordings to the chosen state: the database first, then every file under
/// data/. Each file is converted on its own and is never half-written, so stopping at any point
/// (quit, crash, power cut) is safe and the next run carries on where this one stopped.
enum VaultMigration {
    struct Result {
        var converted = 0, failed = 0, cancelled = false
        var finished: Bool { !cancelled && failed == 0 }
    }

    private static let state = NSLock()
    private static var progress = (done: 0, total: 0, running: false)
    /// Files done and files in all, while a migration runs (for the menu).
    static var status: (done: Int, total: Int, running: Bool) { state.lock(); defer { state.unlock() }; return progress }
    private static func set(_ p: (done: Int, total: Int, running: Bool)) { state.lock(); progress = p; state.unlock() }

    static func run(db: Database, dataDir: URL, key: Data, encrypt: Bool, cancelled: () -> Bool) -> Result {
        set((0, 0, true))
        defer { set((status.done, status.total, false)) }
        var r = Result()
        do {
            if db.isEncrypted != encrypt { try db.convert(key: encrypt ? key : nil) }
        } catch {
            log("cannot \(encrypt ? "encrypt" : "decrypt") the database: \(error)")
            r.failed += 1
            return r
        }
        let files = self.files(dataDir)
        set((0, files.count, true))
        for (i, f) in files.enumerated() {
            if cancelled() { r.cancelled = true; return r }
            do { if try RecordingFile.convert(f, key: key, encrypt: encrypt) { r.converted += 1 } } catch {
                r.failed += 1
                log("cannot \(encrypt ? "encrypt" : "decrypt") \(f): \(error.localizedDescription)")
            }
            set((i + 1, files.count, true))
        }
        log("\(encrypt ? "encrypted" : "decrypted") \(r.converted) recordings" + (r.failed > 0 ? ", \(r.failed) failed (tried again next launch)" : ""))
        return r
    }

    /// Every recording under data/, after removing temp files left by a write that was cut short.
    private static func files(_ dir: URL) -> [String] {
        let fm = FileManager.default
        guard let e = fm.enumerator(at: dir, includingPropertiesForKeys: [.isRegularFileKey, .contentModificationDateKey]) else { return [] }
        var out: [String] = []
        for case let u as URL in e {
            let v = try? u.resourceValues(forKeys: [.isRegularFileKey, .contentModificationDateKey])
            guard v?.isRegularFile == true else { continue }
            if u.path.hasSuffix(RecordingFile.tempSuffix) {
                // Older than a minute: a write that is still going on is left alone.
                if let m = v?.contentModificationDate, m < Date().addingTimeInterval(-60) { try? fm.removeItem(at: u) }
                continue
            }
            out.append(u.path)
        }
        return out.sorted()
    }
}
