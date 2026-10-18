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
        let homeDir = Config.env("HOME").map { URL(fileURLWithPath: $0) } ?? defaultHome()
        var c = Config(home: homeDir)
        c.whisperModel = Config.env("WHISPER_MODEL")
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

    /// OPENREWIND_<name>, or the old OPENSCREEN_<name> so existing setups keep working.
    static func env(_ name: String) -> String? {
        let e = ProcessInfo.processInfo.environment
        return e["OPENREWIND_" + name] ?? e["OPENSCREEN_" + name]
    }

    /// ~/.openrewind. The app was called openscreen and kept its data in ~/.openscreen: move that
    /// folder over once. The database still holds the old file paths, so leave a note for
    /// Database to rewrite them (see `Database.finishHomeMove`).
    static func defaultHome() -> URL {
        let fm = FileManager.default
        let home = fm.homeDirectoryForCurrentUser
        let new = home.appendingPathComponent(".openrewind"), old = home.appendingPathComponent(".openscreen")
        guard !fm.fileExists(atPath: new.path), fm.fileExists(atPath: old.path) else { return new }
        do {
            try fm.moveItem(at: old, to: new)
            try (old.path + "/").write(to: new.appendingPathComponent(movedFromFile), atomically: true, encoding: .utf8)
            log("moved your recordings from \(old.path) to \(new.path)")
            return new
        } catch {
            log("could not move \(old.path) to \(new.path) (\(error.localizedDescription)); using the old folder")
            return old
        }
    }

    /// Written into the new data folder after a move; holds the old folder path.
    static let movedFromFile = ".moved-from"
}

private var logFile: FileHandle?

/// The app has no terminal, so it also writes its log to ~/.openrewind/openrewind.log.
func openLogFile(in home: URL) {
    try? FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
    let url = home.appendingPathComponent("openrewind.log")
    if !FileManager.default.fileExists(atPath: url.path) { FileManager.default.createFile(atPath: url.path, contents: nil) }
    logFile = try? FileHandle(forWritingTo: url)
    _ = try? logFile?.seekToEnd()
}

func log(_ msg: String) {
    let ts = ISO8601DateFormatter().string(from: Date())
    let line = "[\(ts)] \(msg)\n".data(using: .utf8)!
    FileHandle.standardError.write(line)
    logFile?.write(line)
}
