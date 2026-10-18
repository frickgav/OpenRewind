import AppKit
import Foundation

let usage = """
openscreen — record your screen, OCR it, search it locally.

Usage:
  openscreen [record]        Record + serve API/UI (default)
  openscreen app             Run the menu bar app (what OpenScreen.app runs)
  openscreen serve           Serve API/UI only, no recording
  openscreen once            Capture one frame and exit
  openscreen ingest <image> [--app NAME] [--window TITLE]
                             OCR an image file and store it (for testing)
  openscreen ingest-audio <file>
                             Transcribe an audio file and store it (for testing)
  openscreen delete <15m|1h|today|all|older:week>
                             Delete recordings in that range
  openscreen doctor          Check and request permissions

Options:
  --port N        API port (default 3031)
  --interval S    Seconds between captures (default 2)
  --scale X       Snapshot scale 0.1–1.0 (default 0.5)
  --no-ax         Skip accessibility text, use OCR only
  --no-audio      Do not record audio (or --no-mic / --no-system-audio)
  --chunk S       Audio chunk length in seconds (default 30)
  --whisper-model PATH   ggml Whisper model (default: auto-detect)
  --language L    Whisper language code, or auto (default auto)
  --home DIR      Data folder (default ~/.openscreen, or $OPENSCREEN_HOME)

Env:
  OPENSCREEN_EXCLUDE   Comma list of app names to never record
  OPENSCREEN_WHISPER_MODEL   Path to a ggml Whisper model
"""

let args = Array(CommandLine.arguments.dropFirst())
/// Launched from Finder / Dock as OpenScreen.app with no command → run the menu bar app.
let launchedAsApp = Bundle.main.bundleURL.pathExtension == "app" && !args.contains(where: { !$0.hasPrefix("-") })
let command = launchedAsApp ? "app" : (args.first(where: { !$0.hasPrefix("-") }) ?? "record")
if args.contains("-h") || args.contains("--help") || command == "help" {
    print(usage); exit(0)
}

let config = Config.load(arguments: args)
if command == "app" { openLogFile(in: config.home) }
let db: Database
do { db = try Database(path: config.dbPath) } catch {
    log("cannot open database at \(config.dbPath.path): \(error)"); exit(1)
}
let engine = Engine(config: config, db: db)
let recorder = engine.recorder

func value(after flag: String) -> String? {
    guard let i = args.firstIndex(of: flag), i + 1 < args.count else { return nil }
    return args[i + 1]
}

func startServer() {
    do { try engine.startServer() } catch { log("cannot start server: \(error)"); exit(1) }
}

func requirePermissions() {
    if !Capture.hasScreenPermission {
        log("screen recording permission missing. Asking macOS now.")
        log("grant it in System Settings → Privacy & Security → Screen Recording, then restart openscreen.")
        Capture.requestScreenPermission()
    }
    if config.useAccessibility && !Accessibility.isTrusted {
        log("accessibility permission missing → using OCR only. Grant it in Privacy & Security → Accessibility for better text.")
    }
}

switch command {
case "app":
    let app = NSApplication.shared
    let delegate = MenuBarApp(engine: engine)
    app.delegate = delegate
    app.setActivationPolicy(.accessory)  // menu bar only, no Dock icon
    app.run()

case "record":
    requirePermissions()
    startServer()
    engine.setScreen(true)
    if config.recordMic || config.recordSystemAudio { engine.setAudio(true) }
    RunLoop.main.run()

case "serve":
    startServer()
    RunLoop.main.run()

case "once":
    requirePermissions()
    Task.detached {
        do {
            let win = Capture.activeWindow()
            let img = try await Capture.screenshot()
            let id = try recorder.ingest(image: img, window: win, pid: win.pid)
            print(id.map { "stored frame \($0)" } ?? "skipped (duplicate)")
            exit(0)
        } catch {
            log("capture failed: \(error.localizedDescription)"); exit(1)
        }
    }
    RunLoop.main.run()

case "ingest":
    guard let path = args.drop(while: { $0 != "ingest" }).dropFirst().first(where: { !$0.hasPrefix("-") }),
          let img = ImageTools.load(URL(fileURLWithPath: path)) else {
        log("usage: openscreen ingest <image>"); exit(2)
    }
    let win = ActiveWindow(appName: value(after: "--app") ?? "Imported", windowName: value(after: "--window") ?? path, pid: 0)
    do {
        let id = try recorder.ingest(image: img, window: win, pid: nil)
        print(id.map { "stored frame \($0)" } ?? "skipped (duplicate)")
    } catch { log("ingest failed: \(error)"); exit(1) }

case "ingest-audio":
    guard let path = args.drop(while: { $0 != "ingest-audio" }).dropFirst().first(where: { !$0.hasPrefix("-") }) else {
        log("usage: openscreen ingest-audio <file>"); exit(2)
    }
    guard let tx = Transcriber(config: config, db: db) else { exit(1) }
    do {
        let samples = try WAV.load16k(URL(fileURLWithPath: path))
        let ts = Time.now()
        let dir = config.dataDir.appendingPathComponent("audio").appendingPathComponent(String(ts.prefix(10)))
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let file = dir.appendingPathComponent(ts.replacingOccurrences(of: ":", with: "-") + "-import.wav")
        try WAV.write(samples, sampleRate: 16_000, to: file)
        let id = try db.insertChunk(filePath: file.path, timestamp: ts, device: value(after: "--device") ?? "imported", isInput: true)
        guard let chunk = db.chunk(id: id) else { exit(1) }
        tx.transcribe(chunk)
        print("stored audio chunk \(id) → \(db.chunk(id: id)?.status ?? "?")")
    } catch { log("ingest-audio failed: \(error)"); exit(1) }

case "delete":
    // openscreen delete <15m|1h|today|all> [--screen-only|--audio-only]
    guard let what = args.drop(while: { $0 != "delete" }).dropFirst().first(where: { !$0.hasPrefix("-") }) else {
        log("usage: openscreen delete <15m|1h|today|all|older:day|older:week|older:month|older:year>"); exit(2)
    }
    let now = Date()
    var from: Date? = nil, to: Date? = now
    switch what {
    case "all": from = nil
    case "today": from = Calendar.current.startOfDay(for: now)
    case let w where w.hasPrefix("older:"):
        guard let k = KeepFor(rawValue: String(w.dropFirst(6))), let secs = k.seconds else { log("bad value \(w)"); exit(2) }
        from = nil; to = now.addingTimeInterval(-secs)
    default:
        guard let t = Time.parse(what + " ago").flatMap({ Time.iso.date(from: $0) }) else { log("bad value \(what)"); exit(2) }
        from = t
    }
    let f = args.contains("--audio-only") ? 0 : db.deleteFrames(from: from, to: to)
    let a = args.contains("--screen-only") ? 0 : db.deleteAudio(from: from, to: to)
    print("deleted \(f) screenshots, \(a) audio clips")

case "doctor":
    print("data dir:        \(config.home.path)")
    print("frames stored:   \(db.stats().count)")
    print("screen record:   \(Capture.hasScreenPermission ? "ok" : "MISSING")")
    print("accessibility:   \(Accessibility.isTrusted ? "ok" : "missing (OCR fallback)")")
    let a = db.audioStats()
    print("audio chunks:    \(a.chunks) (\(a.pending) pending), \(a.segments) transcript segments")
    print("whisper-cli:     \(Transcriber.findBinary(config.whisperBinary) ?? "MISSING (brew install whisper-cpp)")")
    print("whisper model:   \(Transcriber.findModel(config.whisperModel, home: config.home) ?? "MISSING")")
    if !Capture.hasScreenPermission { Capture.requestScreenPermission() }
    if !Accessibility.isTrusted { Accessibility.promptForTrust() }

default:
    print(usage); exit(2)
}
