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
                             Turn on Settings → AI → Allow AI apps to read my history first
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

// Hidden: secret redaction, Phone Access messages (against the phone page's SJCL vectors) and Restore
// Workspace's saved form, on cases the Windows tests share (run by the Mac CI).
if command == "features-check" {
    var problems = PhoneCrypto.selfTest()
    let stripe = "sk" + "_live_" + "4eC39HqLyjWDarjtT1zdp7dc"  // split so it is not taken for a real key
    let awsSecret = "wJalrXUtnFEMI" + "/K7MDENG/" + "bPxRfiCYEXAMPLEKEY"
    let azureKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsu" + "Fq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="
    let cases: [(String, String)] = [
        ("password: hunter2!", "password: [redacted]"),
        ("card 4111 1111 1111 1111 exp", "card [redacted] exp"),
        ("key " + stripe, "key [redacted]"),
        ("Meeting at 10:30 on 2026-10-18, call 555-123-4567", "Meeting at 10:30 on 2026-10-18, call 555-123-4567"),
        ("Forgot password? Reset it here", "Forgot password? Reset it here"),
        ("AWS_SECRET_ACCESS_KEY=" + awsSecret, "AWS_SECRET_ACCESS_KEY=[redacted]"),
        ("aws_secret_access_key = " + awsSecret, "aws_secret_access_key = [redacted]"),
        ("AccountName=acme;AccountKey=" + azureKey + ";EndpointSuffix=core.windows.net", "AccountName=acme;AccountKey=[redacted];EndpointSuffix=core.windows.net"),
        ("export GITHUB_TOKEN=" + "abc123def456" + "ghi789", "export GITHUB_TOKEN=[redacted]"),
        ("db at postgres://app:" + "Tr0ub4dor" + "@db.internal:5432/main", "db at postgres://app:[redacted]@db.internal:5432/main"),
        ("key " + awsSecret + " end", "key [redacted] end"),
        ("/Users/sam/Projects/OpenRewind2024/Sources/openrewind/Recorder.swift", "/Users/sam/Projects/OpenRewind2024/Sources/openrewind/Recorder.swift"),
        ("https://github.com/frickgav/OpenRewind/pull/57/files", "https://github.com/frickgav/OpenRewind/pull/57/files"),
        ("SORT_KEY=timestamp PRIMARY_KEY=id CACHE_KEY=user_profile", "SORT_KEY=timestamp PRIMARY_KEY=id CACHE_KEY=user_profile"),
    ]
    // Named without ':' or '=', hex keys near their name, more prefixes and OCR quirks (SecretsNamedAndPrefixed).
    let hex = "8f14e45fceea167a" + "5a36dedd4bea2543"
    let whsec = "whsec_" + "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw"
    let named: [(String, String)] = [
        ("Signing Secret  " + hex, "Signing Secret  [redacted]"),
        ("Signing secret\n" + whsec, "Signing secret\n[redacted]"),
        ("Webhook secret  " + "Ab3dE5fG7hI9" + "jK1lM3nO5pQ7", "Webhook secret  [redacted]"),
        ("Client secret\t" + "GOCSPX-" + "1a2b3c4d5e6f7g8h9i0jKLMN", "Client secret\t[redacted]"),
        ("Secret key " + "Zx9Qm2Lp7Wv4" + "Rt8Yb3", "Secret key [redacted]"),
        ("Auth token  " + hex, "Auth token  [redacted]"),
        ("API key " + "AT9x8w7v6u" + "5t4s3r2q1p", "API key [redacted]"),
        ("Access token " + "Vc1Xa9Rt5Y" + "m2Kp8Lq3Wz7N", "Access token [redacted]"),
        ("Token\n" + "Vc1Xa9Rt5Y" + "m2Kp8Lq3Wz7N", "Token\n[redacted]"),
        ("Shared secret Copy " + "Ab3dE5fG7hI9" + "jK1lM3nO5pQ7", "Shared secret Copy [redacted]"),
        ("App secret: " + hex, "App secret: [redacted]"),
        ("Consumer secret " + hex, "Consumer secret [redacted]"),
        ("Signing key " + hex, "Signing key [redacted]"),
        ("Encryption key (AES-256) " + hex, "Encryption key (AES-256) [redacted]"),
        ("Auth Token  Reveal\n" + hex + " (hidden)", "Auth Token  Reveal\n[redacted] (hidden)"),
        ("Account SID " + "AC" + hex, "Account SID [redacted]"),
        ("API key SID " + "SK" + hex, "API key SID [redacted]"),
        ("endpoint " + whsec, "endpoint [redacted]"),
        ("glpat-" + "xYz12AbC34dEf56GhI78", "[redacted]"),
        ("npm_" + "a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8", "[redacted]"),
        ("hf_" + "aBcDeFgHiJkLmNoPqRsTuVwXyZ012345", "[redacted]"),
        ("pypi-AgE" + "IcHlwaS5vcmcCJDEyMzQ1Njc4", "[redacted]"),
        ("SG." + "aBcDeFgHiJkLmNoPqRsTuV" + "." + "wXyZ0123456789aBcDeFgHiJkLmNoPqRsTuVwXyZ0123", "[redacted]"),
        ("shpat_" + hex, "[redacted]"),
        ("xapp-" + "1-A0123456789-0123456789-abcdef", "[redacted]"),
        ("dop_v1_" + hex + hex, "[redacted]"),
        ("ya29." + "a0AfH6SMBx1y2z3AbCdEfGh", "[redacted]"),
        ("EAAG" + "m0PX4ZCpsBAaBcDeFgHiJkLmNoPqRsTuVwXyZ0123", "[redacted]"),
        ("whsec_" + "MfKQ9r8GKYqrTwjUPD8I" + " LPZIa1b2c3 done", "[redacted] done"),
        ("Signing secret " + "8f14e45fceea167a" + " 5a36dedd4bea2543", "Signing secret [redacted]"),
    ]
    let leave = [
        "commit 3f786850e387550fdab836ed7e6dc881de23001b",
        "Merged 3f786850e387550fdab836ed7e6dc881de23001b into main",
        "sha256 e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        "id 123e4567-e89b-12d3-a456-426614174000",
        hex,
        "Signing secret Reveal",
        "Signing secret  Reveal  Copy",
        "API key ••••••••",
        "API key •••••••••••• Reveal",
        "Webhook secret: Reveal",
        "Your access token expires in 30 days",
        "Keep the secret recipe safe until 2026",
        "The token was revoked on 2026-10-25",
        "API key rotation is scheduled for Q4 2026",
        "Token count: 1532 tokens used",
        "Access token\nSettings",
        "password\n/Users/sam/Projects/App2024/Sources",
        "Create a token for the release-2026-q4-build pipeline",
    ]
    for (input, want) in cases + named + leave.map({ ($0, $0) }) where Secrets.redact(input) != want { problems.append("redact \"\(input.prefix(12))…\" gave \"\(Secrets.redact(input))\"") }
    if Secrets.redactURL("https://user:hunter22@example.com/a?token=abc123def456&q=x").contains("hunter22") { problems.append("redactURL password") }
    // OCR quirks: a quick read turned "whsec_P30x…" into "whsec P30x… asEC… j PP" (fake keys, SecretsTests.HidesKeysOcrBrokeUp).
    for line in ["whsec P30x05bXkdXj asECmrtf5xNDbvB0E j PP", "whsec_P30x05bXkdXj asECmrtf5xNDbvB0E",
                 "ghp 1a2B3c4D5e6F7g8H9i0J kLmNoPqRsTuVwXyZ12", "sk proj 4fG7hJ2kL9mN3pQ8 rS1tU6vW0xY5zA"]
        where !Secrets.findInLines(["Settings", line]).contains(where: { $0.line == 1 && $0.range.location == 0 }) {
        problems.append("broken-up key \"\(line.prefix(8))…\" not found")
    }
    if !Secrets.findInLines(["Signing Secret", "P30x05bXkdXj asECmrtf5xNDbvB0E"]).contains(where: { $0.line == 1 }) {
        problems.append("broken-up key under its name not found")
    }
    for pair in [["Password", "Must be 8 characters with 2 numbers"], ["API key", "Created on Oct 25 2026 by Gavien"],
                 ["Settings", "npm install react-dom typescript"], ["Settings", "github.com/frickgav/OpenRewind pull 57"],
                 ["Token", "Expires in 30 days"], ["Meeting notes", "sk 2 items left for Q4 review"]]
        where !Secrets.findInLines(pair).isEmpty {
        problems.append("ordinary line taken for a key: \(pair[1])")
    }
    let knownKey = "whsec_" + "P30x05bXkdXjlasECmrtf5xNDbvB0E"
    let located = Secrets.locate([knownKey], in: ["Webhooks", "whsec P3Ox05bXkdXj1asEC mrtf5xNDbvBOE", "Done"])
    if located.count != 1 || located.first?.line != 1 { problems.append("known secret not located where OCR misread it") }
    if !Secrets.locate([knownKey], in: ["README", "Install with brew"]).isEmpty { problems.append("known secret located where it is not") }
    if !Secrets.mightHave("Signing Secret ••••••••") || !Secrets.mightHave("whsec P30x05bX") || Secrets.mightHave("Weekly report draft") {
        problems.append("mightHave")
    }
    let items = [WorkspaceItem(kind: Workspace.tab, app: "Safari", appID: "com.apple.Safari", title: "Docs", url: "https://example.com/a", path: "")]
    if Workspace.parse(Workspace.json(items)) != items { problems.append("workspace json round trip") }
    for p in problems { print("FAIL: \(p)") }
    print(problems.isEmpty ? "features check passed" : "features check failed (\(problems.count))")
    exit(problems.isEmpty ? 0 : 1)
}

// Hidden: Delete on the timeline on a throwaway database, deleted text included (DeleteSelfTest.swift), run by the Mac CI.
if command == "delete-check" {
    exit(DeleteSelfTest.run() ? 0 : 1)
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
    print("transcription:   \(picked.name)\(ready ? "" : " (not downloaded: pick it in Settings → Audio → Transcription)")")
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
