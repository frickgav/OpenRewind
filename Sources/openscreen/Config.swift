import Foundation

struct Config {
    var home: URL
    var port: UInt16 = 3031
    var interval: TimeInterval = 2.0
    /// A frame is new when at least this many of the 192x120 thumbnail pixels changed.
    var minChangedPixels = 4
    /// Snapshot scale (1.0 = full resolution).
    var snapshotScale: CGFloat = 0.5
    var useAccessibility = true
    var recordMic = true
    var recordSystemAudio = true
    /// Length of each audio chunk sent to Whisper (screenpipe uses 30 s).
    var audioChunkSeconds: Double = 30
    var whisperModel: String?
    var whisperBinary: String?
    var whisperLanguage = "auto"

    var dbPath: URL { home.appendingPathComponent("db.sqlite") }
    var dataDir: URL { home.appendingPathComponent("data") }

    static func load(arguments: [String]) -> Config {
        let env = ProcessInfo.processInfo.environment
        let homeDir = env["OPENSCREEN_HOME"].map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".openscreen")
        var c = Config(home: homeDir)
        c.whisperModel = env["OPENSCREEN_WHISPER_MODEL"]
        var it = arguments.makeIterator()
        while let a = it.next() {
            switch a {
            case "--port": if let v = it.next(), let p = UInt16(v) { c.port = p }
            case "--interval": if let v = it.next(), let s = Double(v) { c.interval = s }
            case "--scale": if let v = it.next(), let s = Double(v) { c.snapshotScale = CGFloat(s) }
            case "--no-ax": c.useAccessibility = false
            case "--no-audio": c.recordMic = false; c.recordSystemAudio = false
            case "--no-mic": c.recordMic = false
            case "--no-system-audio": c.recordSystemAudio = false
            case "--chunk": if let v = it.next(), let s = Double(v), s >= 5 { c.audioChunkSeconds = s }
            case "--whisper-model": c.whisperModel = it.next()
            case "--whisper-bin": c.whisperBinary = it.next()
            case "--language": if let v = it.next() { c.whisperLanguage = v }
            case "--home": if let v = it.next() { c.home = URL(fileURLWithPath: v) }
            default: break
            }
        }
        try? FileManager.default.createDirectory(at: c.dataDir, withIntermediateDirectories: true)
        return c
    }
}

func log(_ msg: String) {
    let ts = ISO8601DateFormatter().string(from: Date())
    FileHandle.standardError.write("[\(ts)] \(msg)\n".data(using: .utf8)!)
}
