import Foundation
import Network

struct HTTPRequest {
    var method: String
    var path: String
    var query: [String: String]
}

struct HTTPResponse {
    var status = 200
    var contentType = "application/json"
    var body = Data()

    static func json(_ obj: Any, status: Int = 200) -> HTTPResponse {
        let data = (try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys])) ?? Data("{}".utf8)
        return HTTPResponse(status: status, contentType: "application/json", body: data)
    }
    static func error(_ msg: String, status: Int) -> HTTPResponse { json(["error": msg], status: status) }
}

/// Tiny HTTP/1.1 server on 127.0.0.1 only. Same idea as screenpipe's localhost:3030 API.
/// No CORS headers are sent, so other websites cannot read this data from the browser.
final class Server {
    let port: UInt16
    let router: (HTTPRequest) -> HTTPResponse
    private var listener: NWListener?
    private let queue = DispatchQueue(label: "openrewind.http", attributes: .concurrent)

    /// The CLI quits when the port is taken; the menu bar app only logs it and keeps running.
    let exitOnFailure: Bool

    init(port: UInt16, exitOnFailure: Bool = true, router: @escaping (HTTPRequest) -> HTTPResponse) {
        self.port = port
        self.exitOnFailure = exitOnFailure
        self.router = router
    }

    func stop() {
        listener?.cancel()
        listener = nil
    }

    func start() throws {
        let params = NWParameters.tcp
        params.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!)
        params.allowLocalEndpointReuse = true
        let l = try NWListener(using: params)
        l.newConnectionHandler = { [weak self] conn in self?.handle(conn) }
        l.stateUpdateHandler = { state in
            switch state {
            case .ready: log("api + ui at http://127.0.0.1:\(self.port)")
            case .failed(let e):
                log("server failed: \(e)")
                if self.exitOnFailure { exit(1) }
            default: break
            }
        }
        l.start(queue: queue)
        listener = l
    }

    private func handle(_ conn: NWConnection) {
        conn.start(queue: queue)
        receive(conn, buffer: Data())
    }

    private func receive(_ conn: NWConnection, buffer: Data) {
        conn.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, done, err in
            guard let self else { return }
            var buf = buffer
            if let data { buf.append(data) }
            if let end = buf.range(of: Data("\r\n\r\n".utf8)) {
                let head = String(decoding: buf[..<end.lowerBound], as: UTF8.self)
                self.respond(conn, self.route(head))
            } else if done || err != nil || buf.count > 1_000_000 {
                conn.cancel()
            } else {
                self.receive(conn, buffer: buf)
            }
        }
    }

    private func route(_ head: String) -> HTTPResponse {
        let first = head.split(separator: "\r\n").first.map(String.init) ?? ""
        let parts = first.split(separator: " ")
        guard parts.count >= 2 else { return .error("bad request", status: 400) }
        let target = String(parts[1])
        let comps = URLComponents(string: "http://x" + target)
        var q: [String: String] = [:]
        for item in comps?.queryItems ?? [] { q[item.name] = item.value ?? "" }
        return router(HTTPRequest(method: String(parts[0]), path: comps?.path ?? target, query: q))
    }

    private func respond(_ conn: NWConnection, _ r: HTTPResponse) {
        let reason = [200: "OK", 400: "Bad Request", 404: "Not Found", 405: "Method Not Allowed", 500: "Internal Server Error"][r.status] ?? "OK"
        var head = "HTTP/1.1 \(r.status) \(reason)\r\n"
        head += "Content-Type: \(r.contentType)\r\nContent-Length: \(r.body.count)\r\n"
        head += "Cache-Control: no-store\r\nConnection: close\r\n\r\n"
        var out = Data(head.utf8)
        out.append(r.body)
        conn.send(content: out, completion: .contentProcessed { _ in conn.cancel() })
    }
}

/// REST routes, shaped like screenpipe's so existing scripts can point at openrewind.
struct API {
    let db: Database
    let config: Config

    func handle(_ req: HTTPRequest) -> HTTPResponse {
        guard req.method == "GET" else { return .error("method not allowed", status: 405) }
        switch req.path {
        case "/", "/index.html":
            return HTTPResponse(contentType: "text/html; charset=utf-8", body: Data(WebUI.html.utf8))
        case "/health":
            let s = db.stats()
            let a = db.audioStats()
            return .json([
                "audio_chunks_total": a.chunks,
                "audio_chunks_pending": a.pending,
                "audio_segments_total": a.segments,
                "status": "healthy",
                "frames_total": s.count,
                "last_frame_at": s.last ?? NSNull(),
                "screen_permission": Capture.hasScreenPermission,
                "accessibility_permission": Accessibility.isTrusted,
                "data_dir": config.home.path,
            ])
        case "/search":
            return search(req.query)
        case "/apps":
            let since = req.query["start_time"].flatMap(Time.parse)
            return .json(["data": db.apps(since: since).map { ["app_name": $0.0, "frames": $0.1] }])
        default:
            if req.path.hasPrefix("/frames/") {
                return frameImage(String(req.path.dropFirst("/frames/".count)))
            }
            if req.path.hasPrefix("/audio/") {
                return audioFile(String(req.path.dropFirst("/audio/".count)))
            }
            return .error("not found", status: 404)
        }
    }

    private func search(_ q: [String: String]) -> HTTPResponse {
        let type = (q["content_type"] ?? "all").lowercased()
        let limit = min(max(Int(q["limit"] ?? "") ?? 20, 1), 100)
        let offset = max(Int(q["offset"] ?? "") ?? 0, 0)
        let empty: [String: Any] = ["data": [], "pagination": ["limit": limit, "offset": offset, "total": 0]]
        if type == "input" || type == "memory" || type == "parsed" { return .json(empty) }  // not captured by openrewind

        var s = SearchQuery()
        s.q = q["q"]
        s.appName = q["app_name"]
        s.windowName = q["window_name"]
        if let raw = q["start_time"] {
            guard let t = Time.parse(raw) else { return .error("bad start_time: \(raw)", status: 400) }
            s.startTime = t
        }
        if let raw = q["end_time"] {
            guard let t = Time.parse(raw) else { return .error("bad end_time: \(raw)", status: 400) }
            s.endTime = t
        }
        let maxLen = Int(q["max_content_length"] ?? "")
        func clip(_ t: String) -> String {
            guard let maxLen, t.count > maxLen else { return t }
            return String(t.prefix(maxLen)) + "…"
        }

        let wantScreen = type != "audio"
        // Audio has no app/window, so those filters mean "screen only".
        let wantAudio = (type == "audio" || type == "all") && (s.appName ?? "").isEmpty && (s.windowName ?? "").isEmpty
        // To page through a merged list: take the top (offset + limit) of each source, merge by time, then slice.
        let fetch = offset + limit
        var items: [(ts: String, item: [String: Any])] = []
        var total = 0

        if wantScreen {
            var sq = s
            sq.limit = fetch; sq.offset = 0
            let (rows, n) = db.search(sq)
            total += n
            items += rows.map { f in (f.timestamp, ["type": "OCR", "content": [
                "frame_id": f.id, "text": clip(f.text), "timestamp": f.timestamp,
                "app_name": f.appName, "window_name": f.windowName, "focused": f.focused,
                "file_path": f.snapshotPath, "text_source": f.textSource, "offset_index": 0, "tags": [],
            ] as [String: Any]]) }
        }
        if wantAudio {
            let (rows, n) = db.searchAudio(s, fetch: fetch)
            total += n
            items += rows.map { t in (t.timestamp, ["type": "Audio", "content": [
                "chunk_id": t.chunkId, "transcription_id": t.id, "transcription": clip(t.text),
                "timestamp": t.timestamp, "start_time": t.startTime, "end_time": t.endTime,
                "device_name": t.device, "device_type": t.isInput ? "Input" : "Output",
                "file_path": t.filePath, "speaker": NSNull(), "tags": [],
            ] as [String: Any]]) }
        }
        items.sort { $0.ts > $1.ts }
        let page = items.dropFirst(offset).prefix(limit).map { $0.item }
        return .json(["data": Array(page), "pagination": ["limit": limit, "offset": offset, "total": total]])
    }

    private func audioFile(_ idStr: String) -> HTTPResponse {
        guard let id = Int64(idStr), let c = db.chunk(id: id) else { return .error("audio not found", status: 404) }
        let path = URL(fileURLWithPath: c.filePath).standardizedFileURL.path
        guard path.hasPrefix(config.dataDir.standardizedFileURL.path),
              let data = FileManager.default.contents(atPath: path) else {
            return .error("audio file missing", status: 404)
        }
        return HTTPResponse(contentType: "audio/wav", body: data)
    }

    private func frameImage(_ idStr: String) -> HTTPResponse {
        guard let id = Int64(idStr), let f = db.frame(id: id) else { return .error("frame not found", status: 404) }
        // Only serve files inside our own data dir.
        let path = URL(fileURLWithPath: f.snapshotPath).standardizedFileURL.path
        guard path.hasPrefix(config.dataDir.standardizedFileURL.path),
              let data = FileManager.default.contents(atPath: path) else {
            return .error("snapshot missing", status: 404)
        }
        return HTTPResponse(contentType: "image/jpeg", body: data)
    }
}
