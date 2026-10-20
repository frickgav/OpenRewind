import AppKit
import Foundation

let usage = """
openrewind — record your screen, OCR it, search it locally.

Usage:
  openrewind [record]        Record + serve API/UI (default)
  openrewind app             Run the menu bar app (what OpenRewind.app runs)
  openrewind serve           Serve API/UI only, no recording
  openrewind once            Capture one frame and exit
  openrewind ingest <image> [--app NAME] [--window TITLE] [--url ADDRESS] [--blocked]
                             OCR an image file and store it (for testing; --blocked marks it
                             as saved while a blocked window was in front)
  openrewind ingest-audio <file>
                             Transcribe an audio file and store it (for testing)
  openrewind delete <15m|1h|today|all|older:week>
                             Delete recordings in that range
  openrewind doctor          Check and request permissions
  openrewind video-check     Check that this Mac can write and read the videos of Compress Older Recordings
  openrewind mcp             MCP server for AI apps, over stdin/stdout (also --mcp).
                             Turn on Settings → Allow AI Apps to Read My History first
  openrewind selftest-encryption
                             Check the encryption against the shared test vectors

Options:
  --port N        API port (default 3031)
  --interval S    Seconds between captures (default 2)
  --scale X       Snapshot scale 0.1–1.0 (default 0.5)
  --no-ax         Skip accessibility text, use OCR only
  --no-audio      Do not record audio (or --no-mic / --no-system-audio)
  --chunk S       Audio chunk length in seconds (default 30)
  --whisper-model PATH   ggml Whisper model (default: auto-detect)
  --language L    Whisper language code, or auto (default auto)
  --model ID      Transcription model for this run: whisper-auto, whisper-base, whisper-small,
                  whisper-large-v3-turbo or parakeet-v3 (default: the one picked in Settings)
  --home DIR      Data folder (default ~/.openrewind, or $OPENREWIND_HOME)

Env:
  OPENREWIND_EXCLUDE   Comma list of extra app names to blur (on top of Blocked Apps in Settings)
  OPENREWIND_WHISPER_MODEL   Path to a ggml Whisper model
  (The old OPENSCREEN_ names still work.)
"""

let args = Array(CommandLine.arguments.dropFirst())
/// Launched from Finder / Dock as OpenRewind.app with no command → run the menu bar app.
let launchedAsApp = Bundle.main.bundleURL.pathExtension == "app" && !args.contains(where: { !$0.hasPrefix("-") })
let command = args.contains("--mcp") ? "mcp" : launchedAsApp ? "app" : (args.first(where: { !$0.hasPrefix("-") }) ?? "record")
if args.contains("-h") || args.contains("--help") || command == "help" {
    print(usage); exit(0)
}

// Checks the file format and SQLCipher against the vectors the Windows tests use (run by the Mac CI).
if command == "selftest-encryption" {
    let problems = RecordingFile.selfTest() + LockState.selfTest()
    for p in problems { print("FAIL: \(p)") }
    print(problems.isEmpty ? "encryption self-test passed" : "encryption self-test failed (\(problems.count))")
    exit(problems.isEmpty ? 0 : 1)
}
// Hidden: Search by Meaning end to end with a made-up model (MeaningSelfTest.swift), run by the Mac CI.
if command == "meaning-check" {
    exit(MeaningSelfTest.run(encrypted: false) && MeaningSelfTest.run(encrypted: true) ? 0 : 1)
}

// Hidden: checks Parakeet's 30 s audio windows on edge-case clip lengths, without the model (run by the Mac CI).
if command == "parakeet-check" {
    let problems = ParakeetMath.selfTest()
    for p in problems { print("FAIL: \(p)") }
    print(problems.isEmpty ? "parakeet check passed" : "parakeet check failed (\(problems.count))")
    exit(problems.isEmpty ? 0 : 1)
}

let config = Config.load(arguments: args)
// Hidden: writes a test GIF with the clip exporter's GIF encoder, for scripts/gif-check.py in CI.
if command == "gif-check" {
    guard let path = args.filter({ !$0.hasPrefix("-") }).dropFirst().first else { print("usage: openrewind gif-check <file.gif>"); exit(2) }
    do { try GifWriter.writeCheck(to: URL(fileURLWithPath: path)); exit(0) } catch { print("gif-check: \(error)"); exit(1) }
}
// Hidden: checks the one-click setup editors (MCPConnectCheck.swift) in an empty folder, in CI.
if command == "connect-check" {
    guard let path = args.filter({ !$0.hasPrefix("-") }).dropFirst().first else { print("usage: openrewind connect-check <empty folder>"); exit(2) }
    exit(MCPConnectCheck.run(in: URL(fileURLWithPath: path)) ? 0 : 1)
}
// Decrypted copies (audio handed to Whisper, videos being shown) left behind by a crash or kill.
if ["app", "record", "serve", "mcp"].contains(command) { RecordingFile.cleanTempCopies() }
// Started by an AI app with stdin/stdout as pipes: no menu bar icon, no recording, read-only.
if command == "mcp" { MCPServer.serveStdio(config: config) }
if command == "app" { openLogFile(in: config.home) }
let db: Database
do {
    // Encrypt Recordings: load the data key before the database, which may be encrypted with it.
    let key = try Encryption.loadKey(config: config, interactive: command == "app")
    RecordingFile.key = key
    RecordingFile.encryptNew = Encryption.isOn && key != nil
    // An encrypted file found later (a switch off still decrypting) loads the key then.
    RecordingFile.keyLoader = { DataKeyStore.load() }
    db = try Database(path: config.dbPath, key: key, createEncrypted: RecordingFile.encryptNew)
} catch {
    log("cannot open database at \(config.dbPath.path): \(error)"); exit(1)
}
let engine = Engine(config: config, db: db)
let recorder = engine.recorder

func value(after flag: String) -> String? {
    guard let i = args.firstIndex(of: flag), i + 1 < args.count else { return nil }
    return args[i + 1]
}

func startServer() {
    engine.serverBlocklist = { BlocklistStore.shared.reload() }
    do { try engine.startServer() } catch { log("cannot start server: \(error)"); exit(1) }
}

/// Which codec Compress Older Recordings would use: encodes and reads back a short test clip.
func videoCheck() -> (text: String, ok: Bool) {
    let codec = AVCodec()
    switch codec.usable {
    case "hevc": return ("HEVC (H.265, hardware)", true)
    case "h264": return ("H.264 (no hardware HEVC encoder)", true)
    default: return ("not available (\(codec.probeError ?? "unknown"))", false)
    }
}

func requirePermissions() {
    if !Capture.hasScreenPermission {
        log("screen recording permission missing. Asking macOS now.")
        log("grant it in System Settings → Privacy & Security → Screen Recording, then restart openrewind.")
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
    Database.buildSlowIndexesInBackground(path: config.dbPath)
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
        log("usage: openrewind ingest <image>"); exit(2)
    }
    let win = ActiveWindow(appName: value(after: "--app") ?? "Imported", windowName: value(after: "--window") ?? path, pid: 0)
    do {
        let id = try recorder.ingest(image: img, window: win, pid: nil, blocked: args.contains("--blocked"), url: value(after: "--url") ?? "")
        print(id.map { "stored frame \($0)" } ?? "skipped (duplicate)")
    } catch { log("ingest failed: \(error)"); exit(1) }

case "ingest-audio":
    guard let path = args.drop(while: { $0 != "ingest-audio" }).dropFirst().first(where: { !$0.hasPrefix("-") }) else {
        log("usage: openrewind ingest-audio <file>"); exit(2)
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
    // openrewind delete <15m|1h|today|all> [--screen-only|--audio-only]
    guard let what = args.drop(while: { $0 != "delete" }).dropFirst().first(where: { !$0.hasPrefix("-") }) else {
        log("usage: openrewind delete <15m|1h|today|all|older:day|older:week|older:month|older:year>"); exit(2)
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
    print("encryption:      \(Encryption.isOn ? "on" : "off")\(Encryption.isDone ? "" : " (converting)") · database \(db.isEncrypted ? "encrypted" : "plain")")
    print("screen record:   \(Capture.hasScreenPermission ? "ok" : "MISSING")")
    print("accessibility:   \(Accessibility.isTrusted ? "ok" : "missing (OCR fallback)")")
    let a = db.audioStats()
    print("audio chunks:    \(a.chunks) (\(a.pending) pending), \(a.segments) transcript segments")
    print("whisper-cli:     \(Transcriber.findBinary(config.whisperBinary) ?? "MISSING (brew install whisper-cpp)")")
    print("whisper model:   \(Transcriber.findModel(config.whisperModel, home: config.home) ?? "MISSING")")
    print("video storage:   \(videoCheck().text)")
    let v = db.videoStats()
    print("videos:          \(v.segments) (\(v.bytes >> 20) MB, saving \(v.saved >> 20) MB)")
    if v.segments > 0 {
        let c = AVCodec.checkVideos(db)
        print(c.bad == 0 ? "videos play:     ok (\(c.checked) checked)"
                         : "videos play:     \(c.bad) of \(c.checked) checked DO NOT OPEN on this Mac")
    }
    let stuck = db.stuckRewrites()
    if stuck > 0 {
        print("videos to redo:  \(stuck) still hold deleted frames (rewritten in the background; turned back into pictures if that keeps failing)")
    }
    let picked = SpeechModels.byId(config.transcriptionModel)
    let ready = picked.files.isEmpty || SpeechModels.isDownloaded(picked, home: config.home)
    print("transcription:   \(picked.name)\(ready ? "" : " (not downloaded: pick it in Settings → Transcription Model)")")
    if !Capture.hasScreenPermission { Capture.requestScreenPermission() }
    if !Accessibility.isTrusted { Accessibility.promptForTrust() }

case "video-check":
    let check = videoCheck()
    print("video storage: \(check.text)")
    guard check.ok else { exit(1) }
    // Once as stored plainly, once with Encrypt Recordings on.
    exit(VideoSelfTest.run() && VideoSelfTest.run(encrypted: true) ? 0 : 1)

default:
    print(usage); exit(2)
}
