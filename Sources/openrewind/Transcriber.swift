import Foundation

/// Runs local Whisper (whisper.cpp `whisper-cli`) on pending audio chunks, one at a time.
final class Transcriber {
    let config: Config
    let db: Database
    let binary: String
    let model: String

    init?(config: Config, db: Database) {
        self.config = config
        self.db = db
        guard let bin = Transcriber.findBinary(config.whisperBinary) else {
            log("whisper-cli not found → audio is saved but not transcribed. Install: brew install whisper-cpp")
            return nil
        }
        guard let m = Transcriber.findModel(config.whisperModel, home: config.home) else {
            log("no Whisper model found → audio is saved but not transcribed. Put a ggml-*.bin in \(config.home.path)/models or pass --whisper-model")
            return nil
        }
        binary = bin
        model = m
        log("whisper: \(URL(fileURLWithPath: m).lastPathComponent)")
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

    func run() async {
        while !Task.isCancelled {
            if let chunk = db.nextPendingChunk() {
                transcribe(chunk)
            } else {
                try? await Task.sleep(nanoseconds: 3_000_000_000)
            }
        }
    }

    func transcribe(_ chunk: AudioChunk) {
        let started = Date()
        do {
            // whisper-cli needs a file: an encrypted clip is decrypted to a private temp file for the run.
            let segments = try RecordingFile.withPlainCopy(chunk.filePath) { try whisper($0) }
            try db.saveTranscripts(chunk: chunk, segments: segments)
            let words = segments.reduce(0) { $0 + $1.text.split(separator: " ").count }
            log("transcribed chunk \(chunk.id): \(segments.count) segments, \(words) words in \(String(format: "%.1f", Date().timeIntervalSince(started)))s")
        } catch {
            log("transcribe chunk \(chunk.id) failed: \(error.localizedDescription)")
            db.markChunk(chunk.id, status: "pending", reason: error.localizedDescription)
        }
    }

    private func whisper(_ path: String) throws -> [(start: Double, end: Double, text: String)] {
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("openrewind-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: tmp.appendingPathExtension("json")) }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: binary)
        p.arguments = ["-m", model, "-f", path, "-l", config.whisperLanguage, "-oj", "-of", tmp.path, "-np",
                       "-t", String(max(2, ProcessInfo.processInfo.activeProcessorCount / 2))]
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
