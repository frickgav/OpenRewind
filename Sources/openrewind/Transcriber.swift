import Foundation

/// What the transcriber did last, for the Settings menu.
struct TranscriberStatus {
    var lastAt: Date?
    var lastSeconds = 0.0
    var lastAudioSeconds = 0.0
    var problem: String?
}

/// Turns pending audio chunks into text, one at a time, with the model picked in Settings →
/// Transcription Model: local Whisper (whisper.cpp `whisper-cli`) or NVIDIA Parakeet v3 (ONNX Runtime).
/// One run loop uses it at a time (chunks go one by one); `status` has its own lock.
final class Transcriber: @unchecked Sendable {
    let config: Config
    let db: Database
    let model: SpeechModel
    /// whisper-cli (Whisper only).
    let binary: String
    /// The ggml file (Whisper) or the model folder (Parakeet).
    let modelPath: String
    /// Stored with each transcript line (audio_transcriptions.engine), so it is known which model wrote it.
    let engineTag: String
    /// Shown in Settings: "Whisper Small", "Whisper (ggml-base.en.bin)", "NVIDIA Parakeet v3".
    let displayName: String
    private let statusLock = NSLock()
    private var _status = TranscriberStatus()
    var status: TranscriberStatus {
        get { statusLock.lock(); defer { statusLock.unlock() }; return _status }
        set { statusLock.lock(); _status = newValue; statusLock.unlock() }
    }
    private var parakeet: ParakeetEngine?
    private var lastUsed = Date()
    private var loadFailed = false
    /// Parakeet holds about 1 GB of memory while loaded and takes a few seconds to load; let go of it after this long with nothing to do.
    private static let unloadAfter: TimeInterval = 5 * 60
    private static var threads: Int { max(2, ProcessInfo.processInfo.activeProcessorCount / 2) }

    /// Why the last `init` returned nil ("whisper-cli not found", …), for the menu.
    private(set) static var lastProblem: String?

    /// The picked model (`config.transcriptionModel`), ready to run; or nil (the reason is in `lastProblem` and the log).
    init?(config: Config, db: Database) {
        self.config = config
        self.db = db
        let m = SpeechModels.byId(config.transcriptionModel)
        model = m
        Transcriber.lastProblem = nil
        if m.engine == .parakeet {
            guard SpeechModels.isDownloaded(m, home: config.home) else {
                Transcriber.lastProblem = "\(m.name) is not downloaded"
                log("\(m.name) is not downloaded → audio is saved but not transcribed. Pick it again in Settings → Transcription Model to download it")
                return nil
            }
            binary = ""
            modelPath = m.dir(config.home).path
            engineTag = ParakeetEngine.tag
            displayName = m.name
            log("transcription model: \(m.name)")
            return
        }
        guard let bin = Transcriber.findBinary(config.whisperBinary) else {
            Transcriber.lastProblem = "whisper-cli not found"
            log("whisper-cli not found → audio is saved but not transcribed. Install: brew install whisper-cpp, or pick NVIDIA Parakeet v3 in Settings → Transcription Model")
            return nil
        }
        // --whisper-model wins; then the picked size if it is downloaded; then whatever is on this Mac.
        var path: String?
        var name: String?
        if config.whisperModel == nil, let f = m.files.first {
            let picked = m.path(config.home, f).path
            if FileManager.default.fileExists(atPath: picked) { path = picked; name = m.name }
            else { log("\(m.name) is not downloaded; using the Whisper model that is") }
        }
        guard let found = path ?? Transcriber.findModel(config.whisperModel, home: config.home) else {
            Transcriber.lastProblem = "no Whisper model found"
            log("no Whisper model found → audio is saved but not transcribed. Put a ggml-*.bin in \(config.home.path)/models, pass --whisper-model, or pick a model in Settings → Transcription Model")
            return nil
        }
        binary = bin
        modelPath = found
        let file = URL(fileURLWithPath: found)
        var stem = file.deletingPathExtension().lastPathComponent
        if stem.hasSuffix(SpeechModels.ownSuffix) { stem = String(stem.dropLast(SpeechModels.ownSuffix.count)) }
        engineTag = "whisper.cpp:" + stem
        displayName = name ?? "Whisper (\(file.lastPathComponent))"
        log("whisper: \(file.lastPathComponent)")
    }

    static func findBinary(_ override: String?) -> String? {
        let fm = FileManager.default
        let candidates = [override, "/opt/homebrew/bin/whisper-cli", "/usr/local/bin/whisper-cli"].compactMap { $0 }
        if let hit = candidates.first(where: { fm.isExecutableFile(atPath: $0) }) { return hit }
        for dir in (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":") {
            let p = "\(dir)/whisper-cli"
            if fm.isExecutableFile(atPath: p) { return p }
        }
        return nil
    }

    /// Our own models folder first, then models other local apps already downloaded.
    static func findModel(_ override: String?, home: URL) -> String? {
        let fm = FileManager.default
        if let override { return fm.fileExists(atPath: override) ? override : nil }
        let dirs = [
            home.appendingPathComponent("models").path,
            NSHomeDirectory() + "/Library/Application Support/com.prakashjoshipax.VoiceInk/WhisperModels",
            "/opt/homebrew/share/whisper-cpp",
        ]
        let preferred = ["large-v3-turbo", "medium", "small", "base", "tiny"]
        for dir in dirs {
            let bins = ((try? fm.contentsOfDirectory(atPath: dir)) ?? [])
                .filter { $0.hasPrefix("ggml-") && $0.hasSuffix(".bin") && !$0.contains("for-tests") }
            for p in preferred {
                if let hit = bins.first(where: { $0.contains(p) }) { return "\(dir)/\(hit)" }
            }
            if let any = bins.first { return "\(dir)/\(any)" }
        }
        return nil
    }

    /// Transcription is seconds of work at a time: it runs here, not on a Swift concurrency thread
    /// (there are only as many of those as cores, and other tasks need them).
    private static let queue = DispatchQueue(label: "openrewind.transcriber", qos: .utility)

    func run() async {
        defer { unload() }
        while !Task.isCancelled {
            if let chunk = db.nextPendingChunk() {
                // An encrypted clip and no key yet (the Keychain is locked): wait, without using up its tries.
                let done: Bool = await withCheckedContinuation { (finished: CheckedContinuation<Bool, Never>) in
                    Transcriber.queue.async {
                        finished.resume(returning: self.transcribe(chunk))
                    }
                }
                if !done { try? await Task.sleep(nanoseconds: 60_000_000_000) }
            } else {
                if parakeet != nil, Date().timeIntervalSince(lastUsed) > Transcriber.unloadAfter { unload() }
                try? await Task.sleep(nanoseconds: 3_000_000_000)
            }
            // A model that cannot load would fail every chunk: stop until the user picks a model again.
            if loadFailed { return }
        }
    }

    private struct ModelLoadError: LocalizedError {
        let errorDescription: String?
    }

    /// The one place both engines read a recorded clip: 16 kHz mono samples. An encrypted clip is
    /// decrypted in memory (Encrypt Recordings); it throws RecordingFile's no-key error without the key.
    static func loadClipSamples(_ path: String) throws -> [Float] {
        if RecordingFile.isEncrypted(path: path) {
            let plain = try RecordingFile.read(path)
            if let samples = WAV.samples16kMono(plain) { return samples }
            // Not the 16 kHz mono 16-bit WAV the recorder writes: let macOS decode a private copy.
            return try RecordingFile.withPlainCopy(path) { try WAV.load16k(URL(fileURLWithPath: $0)) }
        }
        return try WAV.load16k(URL(fileURLWithPath: path))
    }

    /// False when the clip is encrypted and the key is not loaded; the chunk is left as it was.
    @discardableResult
    func transcribe(_ chunk: AudioChunk) -> Bool {
        let started = Date()
        let segments: [(start: Double, end: Double, text: String)]
        let clipSeconds: Double
        do {
            let samples = try Transcriber.loadClipSamples(chunk.filePath)
            clipSeconds = Double(samples.count) / Double(ParakeetMath.sampleRate)
            segments = try model.engine == .parakeet ? parakeetLines(samples) : whisper(samples)
            guard try db.saveTranscripts(chunk: chunk, segments: segments, engine: engineTag) else {
                log("chunk \(chunk.id) was deleted while it was transcribed; its text is dropped")
                return true
            }
        } catch let error where RecordingFile.isNoKey(error) {
            log("transcribe chunk \(chunk.id) waits: the encryption key is not loaded")
            return false
        } catch let e as ModelLoadError {
            log("cannot load \(model.name): \(e.localizedDescription)")
            loadFailed = true
            status.problem = e.localizedDescription
            return true
        } catch {
            log("transcribe chunk \(chunk.id) failed: \(error.localizedDescription)")
            db.markChunk(chunk.id, status: "pending", reason: error.localizedDescription)
            status.problem = error.localizedDescription
            return true
        }
        loadFailed = false
        let secs = Date().timeIntervalSince(started)
        status = TranscriberStatus(lastAt: Date(), lastSeconds: secs, lastAudioSeconds: clipSeconds, problem: nil)
        let words = segments.reduce(0) { $0 + $1.text.split(separator: " ").count }
        log("transcribed chunk \(chunk.id): \(segments.count) segments, \(words) words in \(String(format: "%.1f", secs))s")
        return true
    }

    private func parakeetLines(_ samples: [Float]) throws -> [(start: Double, end: Double, text: String)] {
        if parakeet == nil {
            // Check the files against their pinned hashes before loading them (once per run of the app).
            if let bad = SpeechModels.problem(model, home: config.home) {
                throw ModelLoadError(errorDescription: "\(bad); pick \(model.name) again in Settings → Transcription Model to download it again")
            }
            let started = Date()
            do { parakeet = try ParakeetEngine(dir: URL(fileURLWithPath: modelPath), threads: Transcriber.threads) }
            catch { throw ModelLoadError(errorDescription: error.localizedDescription) }
            log("loaded \(model.name) in \(String(format: "%.1f", Date().timeIntervalSince(started)))s")
        }
        guard let engine = parakeet else { return [] }
        lastUsed = Date()
        let lines = try engine.transcribe(samples)
        lastUsed = Date()
        return lines.filter { !Transcriber.isNoise($0.text) }
    }

    private func unload() {
        guard parakeet != nil else { return }
        parakeet = nil
        log("unloaded \(model.name) until there is audio to transcribe")
    }

    private var whisperChecked = false

    private func whisper(_ samples: [Float]) throws -> [(start: Double, end: Double, text: String)] {
        // A Whisper file OpenRewind downloaded is checked against its hash before its first use in each run.
        if !whisperChecked, let f = model.files.first, modelPath == model.path(config.home, f).path,
           let bad = SpeechModels.problem(model, home: config.home) {
            throw ModelLoadError(errorDescription: "\(bad); pick \(model.name) again in Settings → Transcription Model to download it again")
        }
        whisperChecked = true
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("openrewind-\(UUID().uuidString)")
        let path = tmp.appendingPathExtension("wav").path
        defer {
            try? FileManager.default.removeItem(at: tmp.appendingPathExtension("json"))
            try? FileManager.default.removeItem(atPath: path)
        }
        // whisper-cli reads a file: give it the clip as loadClipSamples read it.
        try WAV.write(samples, sampleRate: ParakeetMath.sampleRate, to: URL(fileURLWithPath: path), recording: false)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: binary)
        p.arguments = ["-m", modelPath, "-f", path, "-l", config.whisperLanguage, "-oj", "-of", tmp.path, "-np",
                       "-t", String(Transcriber.threads)]
        let errPipe = Pipe()
        p.standardError = errPipe
        p.standardOutput = FileHandle.nullDevice
        try p.run()
        let errData = errPipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        guard p.terminationStatus == 0 else {
            let msg = String(decoding: errData.suffix(400), as: UTF8.self)
            throw NSError(domain: "openrewind", code: 30, userInfo: [NSLocalizedDescriptionKey: "whisper-cli exit \(p.terminationStatus): \(msg)"])
        }
        let data = try Data(contentsOf: tmp.appendingPathExtension("json"))
        return Transcriber.parse(data)
    }

    /// whisper.cpp JSON: {"transcription":[{"offsets":{"from":ms,"to":ms},"text":"..."}]}
    static func parse(_ data: Data) -> [(start: Double, end: Double, text: String)] {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let segs = obj["transcription"] as? [[String: Any]] else { return [] }
        let kept: [(start: Double, end: Double, text: String)] = segs.compactMap { seg in
            let text = (seg["text"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            guard !isNoise(text), !isFakeLine(text) else { return nil }
            let off = seg["offsets"] as? [String: Any]
            let from = (off?["from"] as? NSNumber)?.doubleValue ?? 0
            let to = (off?["to"] as? NSNumber)?.doubleValue ?? from
            return (from / 1000, to / 1000, text)
        }
        // A clip that only says "Thank you." or "You" is almost always Whisper guessing on silence.
        if !kept.isEmpty, kept.allSatisfy({ weakLines.contains(normalize($0.text)) }) { return [] }
        return kept
    }

    /// Lines Whisper makes up on quiet audio. It learned them from video subtitle credits.
    static let fakeLines: [String] = [
        "transcription by castingwords", "transcribed by", "subtitles by", "subtitled by",
        "captions by", "captioning by", "amaraorg", "thanks for watching", "thank you for watching",
        "thanks for listening", "thank you for listening", "please subscribe", "like and subscribe",
        "dont forget to subscribe", "see you in the next video", "see you next time",
        "translated by", "copyright", "all rights reserved", "www.", ".com",
    ]

    /// Real speech can be just these words, so they are dropped only when they are the whole clip.
    static let weakLines: Set<String> = ["thank you", "thanks", "you", "bye", "okay", "ok", "so", "yeah", "hmm", "uh", "um"]

    static func normalize(_ t: String) -> String {
        t.lowercased()
            .filter { $0.isLetter || $0.isNumber || $0 == " " || $0 == "." }
            .replacingOccurrences(of: ".", with: "")
            .split(separator: " ").joined(separator: " ")
    }

    static func isFakeLine(_ t: String) -> Bool {
        let n = normalize(t)
        let raw = t.lowercased()
        // Only short lines: a long sentence that happens to contain "copyright" is real speech.
        guard n.split(separator: " ").count <= 12 else { return false }
        return fakeLines.contains { f in
            f.contains(".") ? raw.contains(f) : n.contains(f.replacingOccurrences(of: ".", with: ""))
        }
    }

    /// Drop Whisper's usual filler on silence: "[BLANK_AUDIO]", "(music)", "♪", etc.
    static func isNoise(_ t: String) -> Bool {
        if t.isEmpty { return true }
        if (t.hasPrefix("[") && t.hasSuffix("]")) || (t.hasPrefix("(") && t.hasSuffix(")")) || (t.hasPrefix("*") && t.hasSuffix("*")) { return true }
        if t.allSatisfy({ !$0.isLetter && !$0.isNumber }) { return true }
        return false
    }
}
