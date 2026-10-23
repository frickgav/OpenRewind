import AppKit
import CoreImage
import CryptoKit
import Foundation
import Network
import SQLCipher

/// A phone paired with Phone Access. `secret` is its 256-bit key: first the one-time pairing key from the
/// QR code, then, from the phone's first request on, a new key this Mac sends it sealed (`nextSecret`
/// until the phone has used it once). `lastSeq` is the highest request number accepted from it and
/// `seenMask` which of the 64 numbers below it were used (bit 0 = lastSeq - 1), so requests that cross on
/// the way are each accepted once. One that has not finished pairing within 15 minutes is forgotten.
struct PhoneDevice: Identifiable, Equatable {
    var id: String
    var name: String
    var created: Date
    var lastSeen: Date?
    var lastSeq: Int64
    var secret: Data
    var nextSecret: Data? = nil
    var seenMask: Int64 = 0
    /// The phone has used the key it got after pairing, so the QR code's key is gone.
    var paired: Bool { lastSeen != nil && nextSecret == nil }
}

/// Why a request was refused. malformed, unknownDevice, expired and badSeal answer "not paired" (401) and
/// count as tries at guessing; replayed and stale come from a phone that has the key, so they answer
/// "try again" (409) and do not count. Same as the Windows app.
enum PhoneFailure: Error { case malformed, unknownDevice, expired, badSeal, replayed, stale }

/// A request that passed every check: sealed with the device's key, fresh, never seen before. `key` is the
/// secret it was sealed with (its answer is sealed with the same); `newKey` is true when that was the key
/// sent after pairing, used for the first time.
struct PhoneRequest {
    var device: PhoneDevice
    var seq: Int64
    var op: String
    var args: [String: Any]
    var key: Data
    var newKey = false
}

/// Phone Access messages: end-to-end encrypted between the phone's browser and this Mac, so the Wi-Fi,
/// the router and anyone else on the network see only noise. Exactly the same as the Windows app's
/// PhoneCrypto (Phone.cs) and the phone page (PhonePage.swift); see docs/phone-access.md.
///
/// Keys: HKDF-SHA256 of the shared secret (salt "OpenRewind phone"), one for requests and one for answers.
/// Every message is AES-256-GCM with a fresh random 96-bit nonce; the extra data ties a request to its
/// device and an answer to the request it answers.
enum PhoneCrypto {
    static let salt = "OpenRewind phone"
    static let maxSkew: TimeInterval = 5 * 60
    /// How long a QR code can be used to pair.
    static let pairingWindow: TimeInterval = 15 * 60
    /// How far below the highest request number a request may still arrive.
    static let window: Int64 = 64

    /// True when request `seq` was not used yet: above `last`, or in the window below it and not marked.
    static func fresh(_ last: Int64, _ mask: Int64, _ seq: Int64) -> Bool {
        if seq <= 0 { return false }
        if seq > last { return true }
        let back = last - seq
        return back >= 1 && back <= window && (mask & (Int64(1) << (back - 1))) == 0
    }

    /// `last` and `mask` with `seq` marked as used (call only when fresh).
    static func mark(_ last: Int64, _ mask: Int64, _ seq: Int64) -> (last: Int64, mask: Int64) {
        if seq > last {
            let shift = seq - last
            let m: Int64 = shift > window ? 0 : shift == window ? Int64.min : (mask << shift) | (Int64(1) << (shift - 1))
            return (seq, m)
        }
        return (last, mask | (Int64(1) << (last - seq - 1)))
    }

    static func keys(_ secret: Data, _ deviceId: String) -> (req: SymmetricKey, res: SymmetricKey) {
        let ikm = SymmetricKey(data: secret)
        func key(_ info: String) -> SymmetricKey {
            HKDF<SHA256>.deriveKey(inputKeyMaterial: ikm, salt: Data(salt.utf8), info: Data(info.utf8), outputByteCount: 32)
        }
        return (key("openrewind/phone/v1/req/" + deviceId), key("openrewind/phone/v1/res/" + deviceId))
    }

    static func requestAAD(_ deviceId: String) -> String { "openrewind-phone-v1|req|" + deviceId }
    static func responseAAD(_ deviceId: String, _ seq: Int64) -> String { "openrewind-phone-v1|res|\(deviceId)|\(seq)" }

    static func random(_ bytes: Int) -> Data {
        SymmetricKey(size: SymmetricKeySize(bitCount: bytes * 8)).withUnsafeBytes { Data($0) }
    }

    /// A new device id and secret for pairing.
    static func newDevice() -> (id: String, secret: Data) {
        (random(8).map { String(format: "%02x", $0) }.joined(), random(32))
    }

    /// What the QR code holds: the page address, with the device and secret after # so the browser
    /// never sends them anywhere (a URL fragment stays on the phone).
    static func pairingURL(_ base: String, _ deviceId: String, _ secret: Data) -> String {
        var b = base
        while b.hasSuffix("/") { b.removeLast() }
        return "\(b)/#d=\(deviceId)&k=\(b64(secret))"
    }

    /// URL-safe base64 without padding.
    static func b64(_ d: Data) -> String {
        d.base64EncodedString().replacingOccurrences(of: "=", with: "").replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
    }

    static func fromB64(_ s: String) -> Data? {
        var t = s.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while t.count % 4 != 0 { t += "=" }
        return Data(base64Encoded: t)
    }

    /// (nonce, ciphertext with the 16-byte tag at the end).
    static func seal(_ key: SymmetricKey, _ plain: Data, _ aad: String) -> (nonce: Data, sealed: Data)? {
        guard let box = try? AES.GCM.seal(plain, using: key, nonce: AES.GCM.Nonce(), authenticating: Data(aad.utf8)) else { return nil }
        return (Data(box.nonce), box.ciphertext + box.tag)
    }

    /// The plain text, or nil when the key, nonce, extra data or any byte is wrong.
    static func unseal(_ key: SymmetricKey, _ nonce: Data, _ sealed: Data, _ aad: String) -> Data? {
        guard nonce.count == 12, sealed.count >= 16, let n = try? AES.GCM.Nonce(data: nonce),
              let box = try? AES.GCM.SealedBox(nonce: n, ciphertext: sealed.prefix(sealed.count - 16), tag: sealed.suffix(16)) else { return nil }
        return try? AES.GCM.open(box, using: key, authenticating: Data(aad.utf8))
    }

    /// Checks and opens a request. Does not check the sequence against the database; the server does
    /// that in one statement (Database.acceptPhoneRequest).
    static func open(_ body: Data, device: (String) -> PhoneDevice?, now: Date) throws -> PhoneRequest {
        guard let env = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
              let id = env["d"] as? String, !id.isEmpty, id.count <= 64, (env["v"] as? NSNumber)?.intValue == 1 else { throw PhoneFailure.malformed }
        guard let d = device(id) else { throw PhoneFailure.unknownDevice }
        guard let nonce = (env["n"] as? String).flatMap(fromB64), let sealed = (env["c"] as? String).flatMap(fromB64) else { throw PhoneFailure.malformed }
        var key = d.secret, newKey = false
        var opened: Data?
        if let next = d.nextSecret, let p = unseal(keys(next, d.id).req, nonce, sealed, requestAAD(d.id)) {
            opened = p; key = next; newKey = true
        }
        if opened == nil { opened = unseal(keys(d.secret, d.id).req, nonce, sealed, requestAAD(d.id)) }
        guard let plain = opened else { throw PhoneFailure.badSeal }
        // The QR code's key works only while pairing, for 15 minutes.
        if !newKey && !d.paired && now.timeIntervalSince(d.created) > pairingWindow { throw PhoneFailure.expired }
        guard let msg = try? JSONSerialization.jsonObject(with: plain) as? [String: Any] else { throw PhoneFailure.malformed }
        let seq = (msg["seq"] as? NSNumber)?.int64Value ?? -1
        let ts = (msg["ts"] as? NSNumber)?.doubleValue ?? 0
        if !fresh(d.lastSeq, d.seenMask, seq) { throw PhoneFailure.replayed }
        if abs(ts / 1000 - now.timeIntervalSince1970) > maxSkew { throw PhoneFailure.stale }
        return PhoneRequest(device: d, seq: seq, op: msg["op"] as? String ?? "", args: msg["args"] as? [String: Any] ?? [:], key: key, newKey: newKey)
    }

    /// The sealed answer to request `seq` from `d`, with `key` (the secret the request was sealed with).
    static func sealResponse(_ d: PhoneDevice, _ seq: Int64, _ result: [String: Any], key: Data? = nil) -> Data {
        let plain = (try? JSONSerialization.data(withJSONObject: result)) ?? Data("{}".utf8)
        guard let s = seal(keys(key ?? d.secret, d.id).res, plain, responseAAD(d.id, seq)) else { return Data() }
        return (try? JSONSerialization.data(withJSONObject: ["n": b64(s.nonce), "c": b64(s.sealed)])) ?? Data()
    }

    /// Opens an answer as the phone does (for the self-check). Nil when it was changed or answers another request.
    static func openResponse(_ body: Data, _ deviceId: String, _ secret: Data, _ seq: Int64) -> [String: Any]? {
        guard let env = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
              let n = (env["n"] as? String).flatMap(fromB64), let c = (env["c"] as? String).flatMap(fromB64),
              let plain = unseal(keys(secret, deviceId).res, n, c, responseAAD(deviceId, seq)) else { return nil }
        return try? JSONSerialization.jsonObject(with: plain) as? [String: Any]
    }

    /// Checks against the vectors the Windows tests use, made by the phone page's own SJCL (run by the Mac CI).
    static func selfTest() -> [String] {
        var problems: [String] = []
        let device = "0123456789abcdef"
        let key = fromB64("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8") ?? Data()
        let then = Date(timeIntervalSince1970: 1_700_000_000)
        let req = Data(#"{"v":1,"d":"0123456789abcdef","n":"AQIDBAUGBwgJCgsM","c":"_kuJvJDzdJ10FVU9EghPfdGAQIMUZr3mEDbGrFDNFyg-P-bwTzzYatp3Tw2W9tdLbGHFN-8gl2dF6KlKFY7MmwtidN6-PvNKrerAgToEIxYcCdL4metp1-H4eB_rNcXZ9pvWgt7AaaSu"}"#.utf8)
        let res = Data(#"{"n":"AQIDBAUGBwgJCgsM","c":"3wnAXb0k9br9GnhIOkhkMgYniAhcpnaj7bGi"}"#.utf8)
        func dev(_ seq: Int64 = 0) -> PhoneDevice { PhoneDevice(id: device, name: "", created: then, lastSeen: nil, lastSeq: seq, secret: key) }
        if key != Data((0..<32).map { UInt8($0) }) { problems.append("base64url key") }
        do {
            let r = try open(req, device: { $0 == device ? dev() : nil }, now: then)
            if r.op != "search" || r.seq != 1_700_000_000_000 || r.args["q"] as? String != "héllo" { problems.append("page request read wrong") }
        } catch { problems.append("page request refused: \(error)") }
        func refused(_ f: PhoneFailure, _ body: Data, _ d: PhoneDevice?, _ now: Date) {
            do { _ = try open(body, device: { _ in d }, now: now); problems.append("\(f) accepted") } catch let e as PhoneFailure {
                if e != f { problems.append("\(f) refused as \(e)") }
            } catch { problems.append("\(f): \(error)") }
        }
        refused(.replayed, req, dev(1_700_000_000_000), then)
        refused(.stale, req, dev(), then.addingTimeInterval(6 * 60))
        refused(.unknownDevice, req, nil, then)
        var wrong = dev(); wrong.secret = Data(repeating: 7, count: 32)
        refused(.badSeal, req, wrong, then)
        var tampered = String(decoding: req, as: UTF8.self)
        tampered = tampered.replacingOccurrences(of: "\"c\":\"_k", with: "\"c\":\"_j")
        refused(.badSeal, Data(tampered.utf8), dev(), then)
        if openResponse(res, device, key, 1_700_000_000_000)?["ok"] as? Bool != true { problems.append("page answer did not open") }
        if openResponse(res, device, key, 1_700_000_000_001) != nil { problems.append("answer opened for another request") }
        let back = sealResponse(dev(), 5, ["ok": true, "text": "round trip"])
        if openResponse(back, device, key, 5)?["text"] as? String != "round trip" { problems.append("round trip") }
        if String(decoding: back, as: UTF8.self).contains("round trip") { problems.append("answer not sealed") }
        if !pairingURL("http://192.168.1.5:3032", device, key).hasPrefix("http://192.168.1.5:3032/#d=0123456789abcdef&k=AAEC") { problems.append("pairing url") }
        if !Lan.isPrivate([192, 168, 1, 5]) || !Lan.isPrivate([10, 0, 0, 1]) || !Lan.isPrivate([172, 16, 0, 1]) || !Lan.isPrivate([100, 64, 0, 1])
            || Lan.isPrivate([8, 8, 8, 8]) || Lan.isPrivate([172, 32, 0, 1]) || Lan.isPrivate([127, 0, 0, 1]) || Lan.isPrivate([100, 128, 0, 1]) {
            problems.append("private addresses")
        }
        var last: Int64 = 0, mask: Int64 = 0
        for seq: Int64 in [5, 3, 70, 7, 69, 6] {
            if !fresh(last, mask, seq) { problems.append("window: \(seq) refused") }
            (last, mask) = mark(last, mask, seq)
            if fresh(last, mask, seq) { problems.append("window: \(seq) twice") }
        }
        if fresh(last, mask, 5) || !fresh(last, mask, 8) || fresh(last, mask, 0) { problems.append("window edges") }
        (last, mask) = mark(last, mask, 70 + 64)
        if fresh(last, mask, 70) || !fresh(last, mask, 71) { problems.append("window shift") }
        var old = dev(); old.created = then.addingTimeInterval(-16 * 60)
        refused(.expired, req, old, then)
        var rotated = dev(); rotated.nextSecret = Data(repeating: 9, count: 32); rotated.lastSeen = then
        do {
            let r = try open(req, device: { _ in rotated }, now: then)
            if r.newKey || r.key != key { problems.append("old key while pairing") }
        } catch { problems.append("old key while pairing refused: \(error)") }
        if !PhonePage.html.hasPrefix("<!doctype html>") || !PhonePage.html.contains("sjcl.mode.gcm") { problems.append("phone page") }
        return problems
    }
}

/// Slows down guessing: 10 refused requests from one address in 5 minutes block it for 15 minutes,
/// and 100 from everyone together block all phones for 5 minutes. Same as the Windows app.
final class PhoneGuard {
    private var fails: [String: [Date]] = [:]
    private var blockedUntil: [String: Date] = [:]
    private var all: [Date] = []
    private var allBlockedUntil = Date.distantPast
    private let lock = NSLock()
    static let perAddress = 10, total = 100
    private static let window: TimeInterval = 5 * 60, block: TimeInterval = 15 * 60

    func allowed(_ ip: String, _ now: Date) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return now >= allBlockedUntil && (blockedUntil[ip].map { now >= $0 } ?? true)
    }

    /// A refused try from `ip`. Only wrong-key tries (`global`) count toward blocking every phone, so stray
    /// requests from web pages on the network cannot shut phones out.
    func failed(_ ip: String, _ now: Date, global: Bool = true) {
        lock.lock(); defer { lock.unlock() }
        var l = (fails[ip] ?? []).filter { now.timeIntervalSince($0) <= Self.window }
        l.append(now)
        if l.count >= Self.perAddress { blockedUntil[ip] = now.addingTimeInterval(Self.block); l = [] }
        fails[ip] = l
        guard global else { return }
        all = all.filter { now.timeIntervalSince($0) <= Self.window } + [now]
        if all.count >= Self.total { allBlockedUntil = now.addingTimeInterval(Self.window); all = [] }
        if fails.count > 1000 { fails = [:] }  // memory bound; blocks stay
    }
}

/// Private network addresses: the only ones Phone Access listens on and answers.
enum Lan {
    /// 10/8, 172.16/12, 192.168/16, and 100.64/10 (carrier-grade NAT, used by Tailscale).
    static func isPrivate(_ b: [UInt8]) -> Bool {
        guard b.count == 4 else { return false }
        return b[0] == 10 || (b[0] == 172 && (16...31).contains(b[1])) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && (64...127).contains(b[1]))
    }

    /// Interfaces of virtual machines, Internet Sharing and Apple's peer-to-peer links: never listened on.
    static let virtualPrefixes = ["bridge", "vmnet", "vboxnet", "awdl", "llw", "anpi", "ap1"]

    /// This Mac's private IPv4 addresses a phone can reach: Wi-Fi and Ethernet (en…) first, then other
    /// interfaces, and VPN tunnels (utun) only with Tailscale's 100.64/10 addresses. Virtual machine and
    /// bridge interfaces are left out.
    static func addresses() -> [String] {
        var found: [(ip: String, rank: Int)] = []
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return [] }
        defer { freeifaddrs(ifaddr) }
        for p in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let i = p.pointee
            guard let sa = i.ifa_addr, sa.pointee.sa_family == UInt8(AF_INET),
                  (i.ifa_flags & UInt32(IFF_UP)) != 0, (i.ifa_flags & UInt32(IFF_LOOPBACK)) == 0 else { continue }
            let bytes: [UInt8] = sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { s in
                withUnsafeBytes(of: s.pointee.sin_addr.s_addr) { Array($0) }
            }
            guard isPrivate(bytes) else { continue }
            let name = String(cString: i.ifa_name)
            if virtualPrefixes.contains(where: { name.hasPrefix($0) }) { continue }
            let tailscale = bytes[0] == 100 && (64...127).contains(bytes[1])
            if name.hasPrefix("utun") && !tailscale { continue }
            let ip = bytes.map(String.init).joined(separator: ".")
            let rank = name.hasPrefix("en") && !tailscale ? 0 : tailscale ? 2 : 1
            if !found.contains(where: { $0.ip == ip }) { found.append((ip, rank)) }
        }
        return found.sorted { $0.rank < $1.rank }.map(\.ip)
    }

    /// The address a connection came from, when it is IPv4 (or IPv4 inside IPv6).
    static func remote(_ c: NWConnection) -> [UInt8]? {
        guard case .hostPort(let host, _) = c.endpoint else { return nil }
        switch host {
        case .ipv4(let a): return [UInt8](a.rawValue)
        case .ipv6(let a): return a.asIPv4.map { [UInt8]($0.rawValue) }
        default: return nil
        }
    }
}

extension Database {
    /// The Windows app creates the same table. With Encrypt Recordings on, the whole database (secrets too) is encrypted.
    func migratePhone() throws {
        try exec("""
        CREATE TABLE IF NOT EXISTS phone_devices (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL DEFAULT '',
            secret TEXT NOT NULL,
            created_at TEXT NOT NULL,
            last_seen TEXT,
            last_seq INTEGER NOT NULL DEFAULT 0,
            next_secret TEXT,
            seen_mask INTEGER NOT NULL DEFAULT 0
        );
        """)
        // Tables made by earlier test builds lack the last two columns.
        let cols = strings("SELECT name FROM pragma_table_info('phone_devices')", [])
        if !cols.contains("next_secret") { try exec("ALTER TABLE phone_devices ADD COLUMN next_secret TEXT") }
        if !cols.contains("seen_mask") { try exec("ALTER TABLE phone_devices ADD COLUMN seen_mask INTEGER NOT NULL DEFAULT 0") }
    }

    private static let phoneCols = "id, name, created_at, last_seen, last_seq, secret, next_secret, seen_mask"

    private func readPhone(_ st: OpaquePointer?) -> PhoneDevice? {
        guard let secret = Data(base64Encoded: col(st, 5)) else { return nil }
        return PhoneDevice(id: col(st, 0), name: col(st, 1), created: Time.iso.date(from: col(st, 2)) ?? Date(timeIntervalSince1970: 0),
                           lastSeen: sqlite3_column_type(st, 3) == SQLITE_NULL ? nil : Time.iso.date(from: col(st, 3)),
                           lastSeq: sqlite3_column_int64(st, 4), secret: secret,
                           nextSecret: sqlite3_column_type(st, 6) == SQLITE_NULL ? nil : Data(base64Encoded: col(st, 6)),
                           seenMask: sqlite3_column_int64(st, 7))
    }

    /// Paired phones, and ones still pairing from the last 15 minutes; older unfinished pairings are removed.
    func phoneDevices() -> [PhoneDevice] {
        run("DELETE FROM phone_devices WHERE (last_seen IS NULL OR next_secret IS NOT NULL) AND created_at < ?",
            [.text(Time.iso.string(from: Date().addingTimeInterval(-PhoneCrypto.pairingWindow)))])
        return rowsChecked("SELECT \(Self.phoneCols) FROM phone_devices ORDER BY created_at", [], logErrors: false) { self.readPhone($0) } ?? []
    }

    func phoneDevice(_ id: String) -> PhoneDevice? { phoneDevices().first { $0.id == id } }

    func addPhoneDevice(_ id: String, secret: Data, name: String = "") {
        run("INSERT INTO phone_devices (id, name, secret, created_at) VALUES (?, ?, ?, ?)",
            [.text(id), .text(name), .text(secret.base64EncodedString()), .text(Time.iso.string(from: Date()))])
    }

    /// Revoke: the phone's key is deleted, so nothing it sends is accepted again.
    func removePhoneDevice(_ id: String) { run("DELETE FROM phone_devices WHERE id = ?", [.text(id)]) }

    func renamePhoneDevice(_ id: String, _ name: String) { run("UPDATE phone_devices SET name = ? WHERE id = ?", [.text(name), .text(id)]) }

    /// Records request `seq` as used, if it was not used before (checked and written under the lock, so two
    /// copies sent at once cannot both pass). `newKey`: the request was sealed with the key sent after
    /// pairing, which now replaces the QR code's key. While pairing, returns the key to send to the phone
    /// (`candidate` the first time, the same one after). accepted is false for a replay or a revoked phone.
    func acceptPhoneRequest(_ id: String, _ seq: Int64, newKey: Bool, candidate: Data) -> (accepted: Bool, sendKey: Data?) {
        lock.lock(); defer { lock.unlock() }
        let rows = rowsLocked("SELECT \(Self.phoneCols) FROM phone_devices WHERE id = ?", [.text(id)]) { self.readPhone($0) }
        guard let row = rows.first, PhoneCrypto.fresh(row.lastSeq, row.seenMask, seq) else { return (false, nil) }
        let (last, mask) = PhoneCrypto.mark(row.lastSeq, row.seenMask, seq)
        let now = Arg.text(Time.iso.string(from: Date()))
        if newKey && row.nextSecret != nil {
            let ok = runLocked("UPDATE phone_devices SET secret = next_secret, next_secret = NULL, last_seq = ?, seen_mask = ?, last_seen = ? WHERE id = ?",
                               [.int(last), .int(mask), now, .text(id)]) == 1
            return (ok, nil)
        }
        if !row.paired {
            let next = row.nextSecret ?? candidate
            let ok = runLocked("UPDATE phone_devices SET next_secret = ?, last_seq = ?, seen_mask = ?, last_seen = ? WHERE id = ?",
                               [.text(next.base64EncodedString()), .int(last), .int(mask), now, .text(id)]) == 1
            return (ok, ok ? next : nil)
        }
        let ok = runLocked("UPDATE phone_devices SET last_seq = ?, seen_mask = ?, last_seen = ? WHERE id = ?",
                           [.int(last), .int(mask), now, .text(id)]) == 1
        return (ok, nil)
    }

    /// Rows with `lock` held.
    private func rowsLocked<T>(_ sql: String, _ args: [Arg], _ make: (OpaquePointer?) -> T?) -> [T] {
        var st: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK else { log("sql error: \(errmsg)"); return [] }
        defer { sqlite3_finalize(st) }
        bindAll(st, args)
        var out: [T] = []
        while sqlite3_step(st) == SQLITE_ROW { if let v = make(st) { out.append(v) } }
        return out
    }
}

/// Phone Access: a small web server on this Mac's private network address (port 3032), apart from the
/// localhost-only API on 3031. It serves the phone page, and answers only sealed requests from paired
/// phones (see PhoneCrypto). Connections from addresses outside the local network are dropped.
final class PhoneServer {
    static let port: UInt16 = 3032
    /// Time to send a whole request, then time to answer it (Ask AI can take a minute; same as Windows).
    static let readTimeout: TimeInterval = 15, answerTimeout: TimeInterval = 120
    let db: Database
    let ops: (PhoneRequest) async -> [String: Any]
    let locked: () -> Bool
    private let guardList = PhoneGuard()
    private var listeners: [NWListener] = []
    private let queue = DispatchQueue(label: "openrewind.phone", attributes: .concurrent)
    private let stateLock = NSLock()
    private var used = Date()
    private(set) var urls: [String] = []
    /// The Host headers it answers ("192.168.1.5:3032"): its own addresses only, so a web page that renames
    /// itself to this address (DNS rebinding) is refused.
    private var allowedHosts: Set<String> = []
    private var hosts: Set<String> {
        get { stateLock.lock(); defer { stateLock.unlock() }; return allowedHosts }
        set { stateLock.lock(); allowedHosts = newValue; stateLock.unlock() }
    }
    /// The addresses it listens on, to notice when the network changed.
    private(set) var addresses: [String] = []

    /// When a paired phone last asked something; for turning Phone Access off after hours unused.
    var lastUsed: Date { stateLock.lock(); defer { stateLock.unlock() }; return used }

    init(db: Database, locked: @escaping () -> Bool, ops: @escaping (PhoneRequest) async -> [String: Any]) {
        self.db = db
        self.locked = locked
        self.ops = ops
    }

    /// Listens on each private address. False when there is none (no Wi-Fi or Ethernet).
    func start() -> Bool {
        for ip in Lan.addresses() {
            let params = NWParameters.tcp
            params.requiredLocalEndpoint = .hostPort(host: NWEndpoint.Host(ip), port: NWEndpoint.Port(rawValue: Self.port)!)
            // No address reuse: no other program can take the port while it is ours.
            guard let l = try? NWListener(using: params) else { log("phone access: cannot listen on \(ip):\(Self.port)"); continue }
            l.newConnectionHandler = { [weak self] c in self?.accept(c) }
            l.stateUpdateHandler = { state in
                if case .failed(let e) = state { log("phone access: \(ip):\(Self.port) failed: \(e)") }
            }
            l.start(queue: queue)
            listeners.append(l)
            addresses.append(ip)
            urls.append("http://\(ip):\(Self.port)")
            hosts.insert("\(ip):\(Self.port)")
        }
        if !urls.isEmpty { log("phone access on \(urls.joined(separator: ", "))") }
        return !urls.isEmpty
    }

    func stop() {
        listeners.forEach { $0.cancel() }
        listeners = []
        urls = []
        hosts = []
        addresses = []
    }

    private func accept(_ c: NWConnection) {
        // Only the local network; anything else is dropped without a word.
        guard let ip = Lan.remote(c), Lan.isPrivate(ip) else { c.cancel(); return }
        c.start(queue: queue)
        // Until the whole request is in; then answerTimeout (see receive).
        let reading = DispatchWorkItem { c.cancel() }
        queue.asyncAfter(deadline: .now() + Self.readTimeout, execute: reading)
        receive(c, Data(), remote: ip.map(String.init).joined(separator: "."), reading: reading)
    }

    private func receive(_ c: NWConnection, _ buffer: Data, remote: String, reading: DispatchWorkItem) {
        c.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, done, err in
            guard let self else { return }
            var buf = buffer
            if let data { buf.append(data) }
            guard let end = buf.range(of: Data("\r\n\r\n".utf8)) else {
                if done || err != nil || buf.count > 16384 { c.cancel() } else { self.receive(c, buf, remote: remote, reading: reading) }
                return
            }
            let head = String(decoding: buf[..<end.lowerBound], as: UTF8.self)
            let lines = head.components(separatedBy: "\r\n")
            let first = lines.first?.split(separator: " ").map(String.init) ?? []
            var length = 0
            var host = "", origin: String?
            for h in lines.dropFirst() {
                let lower = h.lowercased()
                func value(_ name: String) -> String { String(h.dropFirst(name.count)).trimmingCharacters(in: .whitespaces) }
                if lower.hasPrefix("content-length:") {
                    guard let n = Int(value("content-length:")) else { c.cancel(); return }
                    length = n
                } else if lower.hasPrefix("host:") {
                    host = value("host:")
                } else if lower.hasPrefix("origin:") {
                    origin = value("origin:")
                }
            }
            guard first.count >= 2, (0...65536).contains(length) else { c.cancel(); return }
            let body = buf[end.upperBound...]
            if body.count >= length {
                reading.cancel()
                let answering = DispatchWorkItem { c.cancel() }
                self.queue.asyncAfter(deadline: .now() + Self.answerTimeout, execute: answering)
                let req = Data(body.prefix(length))
                Task {
                    let r = await self.handle(first[0], first[1], req, remote: remote, host: host, origin: origin)
                    self.respond(c, r) { answering.cancel() }
                }
            } else if done || err != nil {
                c.cancel()
            } else {
                self.receive(c, buf, remote: remote, reading: reading)
            }
        }
    }

    private func respond(_ c: NWConnection, _ r: (status: Int, type: String, body: Data), sent: @escaping () -> Void) {
        let reasons = [200: "OK", 401: "Unauthorized", 403: "Forbidden", 404: "Not Found", 405: "Method Not Allowed", 409: "Conflict", 429: "Too Many Requests"]
        var head = "HTTP/1.1 \(r.status) \(reasons[r.status] ?? "Error")\r\nContent-Type: \(r.type)\r\nContent-Length: \(r.body.count)\r\nCache-Control: no-store\r\n"
        head += "X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\nConnection: close\r\n\r\n"
        c.send(content: Data(head.utf8) + r.body, completion: .contentProcessed { _ in sent(); c.cancel() })
    }

    private static func plain(_ status: Int, _ msg: String) -> (Int, String, Data) {
        (status, "application/json", (try? JSONSerialization.data(withJSONObject: ["error": msg])) ?? Data())
    }

    /// One request, without the socket. `host` and `origin` are the request's headers.
    func handle(_ method: String, _ target: String, _ body: Data, remote: String, host: String, origin: String?) async -> (status: Int, type: String, body: Data) {
        // Only requests addressed to this Mac, and only from the phone page it served (or none).
        guard hosts.contains(host.lowercased()) else { return Self.plain(403, "wrong address") }
        if let origin, origin.lowercased() != "http://" + host.lowercased() { return Self.plain(403, "wrong page") }
        let path = target.split(separator: "?").first.map(String.init) ?? target
        if method == "GET" && (path == "/" || path == "/index.html") { return (200, "text/html; charset=utf-8", Data(PhonePage.html.utf8)) }
        guard path == "/api" else { return Self.plain(404, "not found") }
        guard method == "POST" else { return Self.plain(405, "method not allowed") }
        let now = Date()
        guard guardList.allowed(remote, now) else { return Self.plain(429, "too many tries; wait a few minutes") }
        let req: PhoneRequest
        let sendKey: Data?
        do {
            req = try PhoneCrypto.open(body, device: { self.db.phoneDevice($0) }, now: now)
            let r = db.acceptPhoneRequest(req.device.id, req.seq, newKey: req.newKey, candidate: PhoneCrypto.random(32))
            guard r.accepted else { throw PhoneFailure.replayed }
            sendKey = r.sendKey
        } catch let f as PhoneFailure where f == .replayed || f == .stale {
            // Sealed with the right key, so it is the phone itself: requests that crossed, or a wrong clock.
            log("phone access: \(f) request from \(remote)")
            return Self.plain(409, f == .stale ? "clock" : "retry")
        } catch {
            guardList.failed(remote, now, global: (error as? PhoneFailure) == .badSeal)
            log("phone access: refused a request from \(remote) (\(error))")
            return Self.plain(401, "not paired")
        }
        stateLock.lock(); used = now; stateLock.unlock()
        var result: [String: Any] = ["ok": false, "error": "locked", "message": "OpenRewind is locked. Unlock it on the Mac, then try again."]
        if !locked() { result = await ops(req) }
        // Pairing: the phone's own key, replacing the QR code's (sealed, so only that phone reads it).
        if let sendKey { result["key"] = PhoneCrypto.b64(sendKey) }
        return (200, "application/json", PhoneCrypto.sealResponse(req.device, req.seq, result, key: req.key))
    }
}

/// What a paired phone may ask for, answered with the same rules as the Mac: the search goes through the
/// local API's /search (so the block list and App Lock apply exactly as there), Ask AI uses the user's
/// provider with the Ask AI lookups (no screenshots), and promises leave out blocked sources. Answers carry
/// text only: no screenshots, audio or file paths. Windows: PhoneOps in Phone.cs.
struct PhoneOps {
    let db: Database
    let api: (HTTPRequest) -> HTTPResponse
    let computer: String
    var blocklist: () -> Blocklist = { BlocklistStore.shared.current }
    /// Settings for Ask AI when the user set up a provider and accepted what Ask AI sends; else nil.
    var ai: () -> AISettings? = { nil }

    private static func arg(_ a: [String: Any], _ k: String, _ max: Int) -> String {
        String((a[k] as? String ?? "").prefix(max)).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func fail(_ error: String, _ message: String) -> [String: Any] { ["ok": false, "error": error, "message": message] }

    func run(_ r: PhoneRequest) async -> [String: Any] {
        switch r.op {
        case "hello":
            let name = Self.arg(r.args, "name", 60)
            if !name.isEmpty && name != r.device.name { db.renamePhoneDevice(r.device.id, name) }
            return ["ok": true, "computer": computer, "ai": ai() != nil]
        case "search":
            return search(Self.arg(r.args, "q", 200), Self.arg(r.args, "kind", 10))
        case "promises":
            return promises()
        case "ask":
            return await ask(Self.arg(r.args, "question", 2000))
        default:
            return Self.fail("unknown", "This phone page is newer or older than OpenRewind. Reload it.")
        }
    }

    private func search(_ q: String, _ kind: String) -> [String: Any] {
        guard !q.isEmpty else { return Self.fail("empty", "Type something to search for.") }
        let type = kind == "screen" ? "ocr" : kind == "audio" ? "audio" : "all"
        let res = api(HTTPRequest(method: "GET", path: "/search", query: ["q": q, "limit": "30", "max_content_length": "400", "content_type": type]))
        if res.status == 423 { return Self.fail("locked", "OpenRewind is locked. Unlock it on the Mac, then try again.") }
        guard res.status == 200, let obj = try? JSONSerialization.jsonObject(with: res.body) as? [String: Any] else {
            return Self.fail("failed", "The search did not work.")
        }
        let results: [[String: Any]] = (obj["data"] as? [[String: Any]] ?? []).compactMap { item in
            guard let c = item["content"] as? [String: Any] else { return nil }
            let audio = item["type"] as? String == "Audio"
            return [
                "kind": audio ? "audio" : "screen",
                "time": c["timestamp"] as? String ?? "",
                "app": audio ? (c["device_type"] as? String == "Input" ? "Microphone" : "Computer audio") : c["app_name"] as? String ?? "",
                "title": audio ? "" : c["window_name"] as? String ?? "",
                "text": (audio ? c["transcription"] : c["text"]) as? String ?? "",
            ]
        }
        return ["ok": true, "results": results]
    }

    private func promises() -> [String: Any] {
        let list = blocklist()
        let items: [[String: Any]] = db.promises(status: Promises.open, limit: 100).filter { !Promises.hidden($0, list) }.map { p in
            ["what": p.what, "who": p.who, "due": p.remindAt.map { Time.iso.string(from: $0) as Any } ?? NSNull(),
             "time": Time.iso.string(from: p.time), "source": p.source]
        }
        return ["ok": true, "promises": items]
    }

    private func ask(_ question: String) async -> [String: Any] {
        guard !question.isEmpty else { return Self.fail("empty", "Type a question.") }
        guard let settings = ai() else { return Self.fail("no-ai", "Set up Ask AI on the Mac first (Settings → AI).") }
        var tools = AITools(db: db)
        tools.blocklist = blocklist()
        let client = AIClient(settings: settings)
        var history = [AIMessage(role: .user, text: question)]
        var usage = AIUsage()
        var answer: [String] = []
        var problem: String?
        do {
            rounds: for round in 0..<AIChatModel.maxRounds {
                let reply = try await client.send(system: AIChatModel.systemPrompt(), messages: history, tools: tools.offered)
                usage = usage + reply.usage
                history.append(AIMessage(role: .assistant, text: reply.text, toolCalls: reply.toolCalls, raw: reply.raw))
                if !reply.text.isEmpty { answer.append(reply.text) }
                if reply.toolCalls.isEmpty { break rounds }
                if round == AIChatModel.maxRounds - 1 { problem = "Stopped after \(AIChatModel.maxRounds) lookups. Try asking something narrower." }
                for call in reply.toolCalls {
                    let result = tools.execute(call)
                    history.append(AIMessage(role: .tool, text: result.text, toolCallId: call.id, toolName: call.name))
                }
            }
        } catch {
            problem = error.localizedDescription
        }
        // Tokens are paid for even when the question fails halfway.
        let cost = usage.cost(AISettings.price(for: settings.model))
        let used = usage, kind = settings.kind
        await MainActor.run { AISettings.add(used, cost: cost, to: kind) }
        guard !answer.isEmpty else { return Self.fail("failed", problem ?? "No answer came back.") }
        return ["ok": true, "answer": answer.joined(separator: "\n\n"), "model": settings.model]
    }
}

/// Turns Phone Access on and off, pairs phones, and turns it off by itself after hours unused.
/// Off by default. The menu bar app owns one.
final class PhoneAccess {
    private let d = UserDefaults.standard
    private let makeServer: () -> PhoneServer
    private(set) var server: PhoneServer?
    private var timer: Timer?
    /// Why it is not on although it should be ("no Wi-Fi…"), for Settings.
    private(set) var problem: String?

    /// Hours unused before it turns off; 0 is never.
    static let autoOffChoices = [1, 4, 12, 0]

    private let monitor = NWPathMonitor()

    init(makeServer: @escaping () -> PhoneServer) {
        self.makeServer = makeServer
        // A new address (other Wi-Fi, DHCP renewal): listen again on the addresses there are now.
        monitor.pathUpdateHandler = { [weak self] _ in DispatchQueue.main.async { self?.networkChanged() } }
        monitor.start(queue: DispatchQueue(label: "openrewind.phone.path"))
    }

    private func networkChanged() {
        guard enabled else { return }
        let now = Lan.addresses()
        if let s = server, s.addresses == now { return }
        if server == nil && now.isEmpty { return }
        log("phone access: network changed, listening again")
        stop()
        start()
    }

    var enabled: Bool {
        get { d.bool(forKey: "phoneAccess") }
        set { d.set(newValue, forKey: "phoneAccess") }
    }

    var autoOffHours: Int {
        get { d.object(forKey: "phoneAutoOffHours") == nil ? 4 : d.integer(forKey: "phoneAutoOffHours") }
        set { d.set(newValue, forKey: "phoneAutoOffHours") }
    }

    var isOn: Bool { server != nil }
    var urls: [String] { server?.urls ?? [] }

    /// At launch: on again if it was on.
    func resume() { if enabled { start() } }

    func start() {
        guard server == nil else { return }
        let s = makeServer()
        guard s.start() else {
            problem = "No Wi-Fi or Ethernet network with a private address, so phones cannot reach this Mac. It starts again by itself when there is one."
            return
        }
        problem = nil
        server = s
        let t = Timer(timeInterval: 60, repeats: true) { [weak self] _ in self?.checkIdle() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    func stop() {
        server?.stop()
        server = nil
        timer?.invalidate()
        timer = nil
        problem = nil
    }

    func toggle() {
        enabled.toggle()
        if enabled { start() } else { stop() }
    }

    private func checkIdle() {
        guard let s = server, autoOffHours > 0, Date().timeIntervalSince(s.lastUsed) > Double(autoOffHours) * 3600 else { return }
        log("phone access: off after \(autoOffHours) h unused")
        enabled = false
        stop()
    }

    /// A new pairing code, shown until the phone has finished pairing or the user closes it. The code is a
    /// one-time key: the phone gets its own key in the first answer, and once it has used it the code is
    /// worthless. The window is never recorded (OpenRewind leaves its own windows out of screenshots) and
    /// is hidden from other screen capture too.
    func pair(db: Database) {
        guard let base = urls.first else { return }
        let (id, secret) = PhoneCrypto.newDevice()
        db.addPhoneDevice(id, secret: secret)
        let url = PhoneCrypto.pairingURL(base, id, secret)
        let alert = NSAlert()
        alert.messageText = "Scan with your phone's camera"
        alert.informativeText = "Your phone must be on the same Wi-Fi as this Mac (\(base.replacingOccurrences(of: "http://", with: ""))). "
            + "The code works once, for one phone, within 15 minutes; it closes by itself when the phone is paired.\n\n"
            + "After scanning, add the page to your home screen to open it again."
        if let image = Self.qrCode(url, size: 260) {
            let view = NSImageView(frame: NSRect(x: 0, y: 0, width: 260, height: 260))
            view.image = image
            alert.accessoryView = view
        }
        alert.addButton(withTitle: "Done")
        alert.addButton(withTitle: "Cancel")
        alert.window.sharingType = .none
        final class Flag { var on = false }
        let paired = Flag()
        let poll = Timer(timeInterval: 1, repeats: true) { t in
            guard db.phoneDevice(id)?.paired == true else { return }
            paired.on = true
            t.invalidate()
            NSApp.stopModal(withCode: .alertFirstButtonReturn)
        }
        RunLoop.main.add(poll, forMode: .modalPanel)
        let answer = Dialogs.run(alert)
        poll.invalidate()
        if paired.on {
            let done = NSAlert()
            done.messageText = "Phone paired"
            done.informativeText = "It has its own key now; the code you scanned no longer works. Revoke it in Settings → Security if you lose the phone."
            Dialogs.run(done)
        } else if answer != .alertFirstButtonReturn, db.phoneDevice(id)?.paired != true {
            db.removePhoneDevice(id)
        }
    }

    /// The QR code of `text`, sharp at `size` points.
    static func qrCode(_ text: String, size: CGFloat) -> NSImage? {
        guard let f = CIFilter(name: "CIQRCodeGenerator") else { return nil }
        f.setValue(Data(text.utf8), forKey: "inputMessage")
        f.setValue("M", forKey: "inputCorrectionLevel")
        guard let out = f.outputImage else { return nil }
        let scale = (size * 2 / out.extent.width).rounded(.down)
        let big = out.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        guard let cg = CIContext().createCGImage(big, from: big.extent) else { return nil }
        return NSImage(cgImage: cg, size: NSSize(width: size, height: size))
    }
}
