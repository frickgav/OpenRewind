import AppKit
import CoreImage
import CryptoKit
import Foundation
import Network
import SQLCipher

/// A phone paired with Phone Access. `secret` is the 256-bit key shared through the pairing QR code;
/// `lastSeq` is the highest request number accepted from it (anything not higher is a replay).
/// A device that has never been seen is still pairing, and is forgotten after 15 minutes.
struct PhoneDevice: Identifiable, Equatable {
    var id: String
    var name: String
    var created: Date
    var lastSeen: Date?
    var lastSeq: Int64
    var secret: Data
    var paired: Bool { lastSeen != nil }
}

/// Why a request was refused. The phone only ever hears "not paired" (401), whatever the reason.
enum PhoneFailure: Error { case malformed, unknownDevice, badSeal, replayed, stale }

/// A request that passed every check: sealed with the device's key, fresh, never seen before.
struct PhoneRequest {
    var device: PhoneDevice
    var seq: Int64
    var op: String
    var args: [String: Any]
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
        guard let plain = unseal(keys(d.secret, d.id).req, nonce, sealed, requestAAD(d.id)) else { throw PhoneFailure.badSeal }
        guard let msg = try? JSONSerialization.jsonObject(with: plain) as? [String: Any] else { throw PhoneFailure.malformed }
        let seq = (msg["seq"] as? NSNumber)?.int64Value ?? -1
        let ts = (msg["ts"] as? NSNumber)?.doubleValue ?? 0
        if seq <= d.lastSeq { throw PhoneFailure.replayed }
        if abs(ts / 1000 - now.timeIntervalSince1970) > maxSkew { throw PhoneFailure.stale }
        return PhoneRequest(device: d, seq: seq, op: msg["op"] as? String ?? "", args: msg["args"] as? [String: Any] ?? [:])
    }

    /// The sealed answer to request `seq` from `d`.
    static func sealResponse(_ d: PhoneDevice, _ seq: Int64, _ result: [String: Any]) -> Data {
        let plain = (try? JSONSerialization.data(withJSONObject: result)) ?? Data("{}".utf8)
        guard let s = seal(keys(d.secret, d.id).res, plain, responseAAD(d.id, seq)) else { return Data() }
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

    func failed(_ ip: String, _ now: Date) {
        lock.lock(); defer { lock.unlock() }
        var l = (fails[ip] ?? []).filter { now.timeIntervalSince($0) <= Self.window }
        l.append(now)
        if l.count >= Self.perAddress { blockedUntil[ip] = now.addingTimeInterval(Self.block); l = [] }
        fails[ip] = l
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

    /// This Mac's private IPv4 addresses on interfaces that are up, Wi-Fi and Ethernet (en…) first.
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
            let ip = bytes.map(String.init).joined(separator: ".")
            if !found.contains(where: { $0.ip == ip }) { found.append((ip, name.hasPrefix("en") ? 0 : 1)) }
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
            last_seq INTEGER NOT NULL DEFAULT 0
        );
        """)
    }

    /// Paired phones, and ones still pairing (never seen) from the last 15 minutes; older pending ones are removed.
    func phoneDevices() -> [PhoneDevice] {
        run("DELETE FROM phone_devices WHERE last_seen IS NULL AND created_at < ?", [.text(Time.iso.string(from: Date().addingTimeInterval(-15 * 60)))])
        return rowsChecked("SELECT id, name, created_at, last_seen, last_seq, secret FROM phone_devices ORDER BY created_at", [], logErrors: false) { st in
            guard let secret = Data(base64Encoded: self.col(st, 5)) else { return nil }
            return PhoneDevice(id: self.col(st, 0), name: self.col(st, 1), created: Time.iso.date(from: self.col(st, 2)) ?? Date(timeIntervalSince1970: 0),
                               lastSeen: sqlite3_column_type(st, 3) == SQLITE_NULL ? nil : Time.iso.date(from: self.col(st, 3)),
                               lastSeq: sqlite3_column_int64(st, 4), secret: secret)
        } ?? []
    }

    func phoneDevice(_ id: String) -> PhoneDevice? { phoneDevices().first { $0.id == id } }

    func addPhoneDevice(_ id: String, secret: Data, name: String = "") {
        run("INSERT INTO phone_devices (id, name, secret, created_at) VALUES (?, ?, ?, ?)",
            [.text(id), .text(name), .text(secret.base64EncodedString()), .text(Time.iso.string(from: Date()))])
    }

    /// Revoke: the phone's key is deleted, so nothing it sends is accepted again.
    func removePhoneDevice(_ id: String) { run("DELETE FROM phone_devices WHERE id = ?", [.text(id)]) }

    func renamePhoneDevice(_ id: String, _ name: String) { run("UPDATE phone_devices SET name = ? WHERE id = ?", [.text(name), .text(id)]) }

    /// Records request `seq` as used, only if it is higher than every one before (in one statement, so two
    /// copies of a request sent at once cannot both pass). False means a replay.
    func acceptPhoneRequest(_ id: String, _ seq: Int64) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return runLocked("UPDATE phone_devices SET last_seq = ?, last_seen = ? WHERE id = ? AND last_seq < ?",
                         [.int(seq), .text(Time.iso.string(from: Date())), .text(id), .int(seq)]) == 1
    }
}

/// Phone Access: a small web server on this Mac's private network address (port 3032), apart from the
/// localhost-only API on 3031. It serves the phone page, and answers only sealed requests from paired
/// phones (see PhoneCrypto). Connections from addresses outside the local network are dropped.
final class PhoneServer {
    static let port: UInt16 = 3032
    let db: Database
    let ops: (PhoneRequest) async -> [String: Any]
    let locked: () -> Bool
    private let guardList = PhoneGuard()
    private var listeners: [NWListener] = []
    private let queue = DispatchQueue(label: "openrewind.phone", attributes: .concurrent)
    private let stateLock = NSLock()
    private var used = Date()
    private(set) var urls: [String] = []

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
            params.allowLocalEndpointReuse = true
            guard let l = try? NWListener(using: params) else { log("phone access: cannot listen on \(ip):\(Self.port)"); continue }
            l.newConnectionHandler = { [weak self] c in self?.accept(c) }
            l.stateUpdateHandler = { state in
                if case .failed(let e) = state { log("phone access: \(ip):\(Self.port) failed: \(e)") }
            }
            l.start(queue: queue)
            listeners.append(l)
            urls.append("http://\(ip):\(Self.port)")
        }
        if !urls.isEmpty { log("phone access on \(urls.joined(separator: ", "))") }
        return !urls.isEmpty
    }

    func stop() {
        listeners.forEach { $0.cancel() }
        listeners = []
        urls = []
    }

    private func accept(_ c: NWConnection) {
        // Only the local network; anything else is dropped without a word.
        guard let ip = Lan.remote(c), Lan.isPrivate(ip) else { c.cancel(); return }
        c.start(queue: queue)
        queue.asyncAfter(deadline: .now() + 15) { [weak c] in c?.cancel() }
        receive(c, Data(), remote: ip.map(String.init).joined(separator: "."))
    }

    private func receive(_ c: NWConnection, _ buffer: Data, remote: String) {
        c.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, done, err in
            guard let self else { return }
            var buf = buffer
            if let data { buf.append(data) }
            guard let end = buf.range(of: Data("\r\n\r\n".utf8)) else {
                if done || err != nil || buf.count > 16384 { c.cancel() } else { self.receive(c, buf, remote: remote) }
                return
            }
            let head = String(decoding: buf[..<end.lowerBound], as: UTF8.self)
            let lines = head.components(separatedBy: "\r\n")
            let first = lines.first?.split(separator: " ").map(String.init) ?? []
            var length = 0
            for h in lines.dropFirst() where h.lowercased().hasPrefix("content-length:") {
                guard let n = Int(h.dropFirst(15).trimmingCharacters(in: .whitespaces)) else { c.cancel(); return }
                length = n
            }
            guard first.count >= 2, (0...65536).contains(length) else { c.cancel(); return }
            let body = buf[end.upperBound...]
            if body.count >= length {
                let req = Data(body.prefix(length))
                Task {
                    let r = await self.handle(first[0], first[1], req, remote: remote)
                    self.respond(c, r)
                }
            } else if done || err != nil {
                c.cancel()
            } else {
                self.receive(c, buf, remote: remote)
            }
        }
    }

    private func respond(_ c: NWConnection, _ r: (status: Int, type: String, body: Data)) {
        let reason = [200: "OK", 401: "Unauthorized", 404: "Not Found", 405: "Method Not Allowed", 429: "Too Many Requests"][r.status] ?? "Error"
        var head = "HTTP/1.1 \(r.status) \(reason)\r\nContent-Type: \(r.type)\r\nContent-Length: \(r.body.count)\r\nCache-Control: no-store\r\n"
        head += "X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\nConnection: close\r\n\r\n"
        c.send(content: Data(head.utf8) + r.body, completion: .contentProcessed { _ in c.cancel() })
    }

    private static func plain(_ status: Int, _ msg: String) -> (Int, String, Data) {
        (status, "application/json", (try? JSONSerialization.data(withJSONObject: ["error": msg])) ?? Data())
    }

    /// One request, without the socket.
    func handle(_ method: String, _ target: String, _ body: Data, remote: String) async -> (status: Int, type: String, body: Data) {
        let path = target.split(separator: "?").first.map(String.init) ?? target
        if method == "GET" && (path == "/" || path == "/index.html") { return (200, "text/html; charset=utf-8", Data(PhonePage.html.utf8)) }
        guard path == "/api" else { return Self.plain(404, "not found") }
        guard method == "POST" else { return Self.plain(405, "method not allowed") }
        let now = Date()
        guard guardList.allowed(remote, now) else { return Self.plain(429, "too many tries; wait a few minutes") }
        let req: PhoneRequest
        do {
            req = try PhoneCrypto.open(body, device: { self.db.phoneDevice($0) }, now: now)
            guard db.acceptPhoneRequest(req.device.id, req.seq) else { throw PhoneFailure.replayed }
        } catch {
            guardList.failed(remote, now)
            log("phone access: refused a request from \(remote) (\(error))")
            return Self.plain(401, "not paired")
        }
        stateLock.lock(); used = now; stateLock.unlock()
        var result: [String: Any] = ["ok": false, "error": "locked", "message": "OpenRewind is locked. Unlock it on the Mac, then try again."]
        if !locked() { result = await ops(req) }
        return (200, "application/json", PhoneCrypto.sealResponse(req.device, req.seq, result))
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

    init(makeServer: @escaping () -> PhoneServer) { self.makeServer = makeServer }

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
            problem = "No Wi-Fi or Ethernet network with a private address, so phones cannot reach this Mac."
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

    /// A new pairing code, shown until the phone has used it or the user closes it.
    func pair(db: Database) {
        guard let base = urls.first else { return }
        let (id, secret) = PhoneCrypto.newDevice()
        db.addPhoneDevice(id, secret: secret)
        let url = PhoneCrypto.pairingURL(base, id, secret)
        let alert = NSAlert()
        alert.messageText = "Scan with your phone's camera"
        alert.informativeText = "Your phone must be on the same Wi-Fi as this Mac. The code works for one phone and expires in 15 minutes; "
            + "it holds the key, so don't share it.\n\nAfter scanning, add the page to your home screen to open it again."
        if let image = Self.qrCode(url, size: 260) {
            let view = NSImageView(frame: NSRect(x: 0, y: 0, width: 260, height: 260))
            view.image = image
            alert.accessoryView = view
        }
        alert.addButton(withTitle: "Done")
        alert.addButton(withTitle: "Cancel")
        if Dialogs.run(alert) != .alertFirstButtonReturn, db.phoneDevice(id)?.paired != true { db.removePhoneDevice(id) }
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
