import AVFoundation
import CoreMedia
import ScreenCaptureKit

/// Whisper wants 16 kHz mono. Everything we capture is converted to this.
let whisperFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false)!

/// Converts any PCM buffer to 16 kHz mono Float32 samples.
final class Resampler {
    private var converter: AVAudioConverter?
    private var inputFormat: AVAudioFormat?

    func convert(_ buffer: AVAudioPCMBuffer) -> [Float] {
        if inputFormat != buffer.format {
            inputFormat = buffer.format
            converter = AVAudioConverter(from: buffer.format, to: whisperFormat)
        }
        guard let converter else { return [] }
        let ratio = whisperFormat.sampleRate / buffer.format.sampleRate
        let cap = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 64
        guard let out = AVAudioPCMBuffer(pcmFormat: whisperFormat, frameCapacity: cap) else { return [] }
        var fed = false
        var err: NSError?
        converter.convert(to: out, error: &err) { _, status in
            if fed { status.pointee = .noDataNow; return nil }
            fed = true
            status.pointee = .haveData
            return buffer
        }
        if let err { log("resample error: \(err.localizedDescription)"); return [] }
        guard let ch = out.floatChannelData else { return [] }
        return Array(UnsafeBufferPointer(start: ch[0], count: Int(out.frameLength)))
    }
}

/// Collects 16 kHz samples and cuts them into fixed-length WAV chunks.
/// Silent chunks are dropped.
final class ChunkWriter {
    let device: String
    let isInput: Bool
    let seconds: Double
    let config: Config
    let db: Database
    private var samples: [Float] = []
    private var startedAt = Date()
    private let lock = NSLock()

    init(device: String, isInput: Bool, seconds: Double, config: Config, db: Database) {
        self.device = device; self.isInput = isInput; self.seconds = seconds
        self.config = config; self.db = db
    }

    /// When the last samples arrived. The engine's watchdog restarts a stream that went quiet.
    private(set) var lastAppend = Date()

    func append(_ s: [Float]) {
        lock.lock()
        lastAppend = Date()
        if samples.isEmpty { startedAt = Date() }
        samples.append(contentsOf: s)
        let full = Double(samples.count) >= seconds * whisperFormat.sampleRate
        let take = full ? samples : []
        let at = startedAt
        if full { samples.removeAll(keepingCapacity: true) }
        lock.unlock()
        if full { flush(take, startedAt: at) }
    }

    /// Write whatever is buffered (used on shutdown).
    func flushRemaining() {
        lock.lock()
        let take = samples, at = startedAt
        samples.removeAll()
        lock.unlock()
        if Double(take.count) > whisperFormat.sampleRate { flush(take, startedAt: at) }
    }

    private func flush(_ s: [Float], startedAt: Date) {
        let rms = sqrt(s.reduce(0) { $0 + $1 * $1 } / Float(max(s.count, 1)))
        if rms < 0.002 {  // basically silence: do not store or transcribe
            log("audio \(device): skipped silent \(Int(Double(s.count) / whisperFormat.sampleRate))s clip (level \(String(format: "%.4f", rms)))")
            return
        }
        let ts = Time.iso.string(from: startedAt)
        let dir = config.dataDir.appendingPathComponent("audio").appendingPathComponent(String(ts.prefix(10)))
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let file = dir.appendingPathComponent("\(ts.replacingOccurrences(of: ":", with: "-"))-\(isInput ? "mic" : "system").wav")
            try WAV.write(s, sampleRate: Int(whisperFormat.sampleRate), to: file)
            let id = try db.insertChunk(filePath: file.path, timestamp: ts, device: device, isInput: isInput)
            log("audio chunk \(id) \(device) \(String(format: "%.0f", Double(s.count) / whisperFormat.sampleRate))s")
        } catch {
            log("audio write failed: \(error)")
        }
    }
}

enum WAV {
    /// 16-bit PCM mono WAV.
    static func write(_ samples: [Float], sampleRate: Int, to url: URL) throws {
        var d = Data()
        func u32(_ v: UInt32) { withUnsafeBytes(of: v.littleEndian) { d.append(contentsOf: $0) } }
        func u16(_ v: UInt16) { withUnsafeBytes(of: v.littleEndian) { d.append(contentsOf: $0) } }
        let bytes = UInt32(samples.count * 2)
        d.append(contentsOf: Array("RIFF".utf8)); u32(36 + bytes)
        d.append(contentsOf: Array("WAVE".utf8))
        d.append(contentsOf: Array("fmt ".utf8)); u32(16); u16(1); u16(1)
        u32(UInt32(sampleRate)); u32(UInt32(sampleRate * 2)); u16(2); u16(16)
        d.append(contentsOf: Array("data".utf8)); u32(bytes)
        d.reserveCapacity(d.count + samples.count * 2)
        for s in samples { u16(UInt16(bitPattern: Int16(max(-1, min(1, s)) * 32767))) }
        try RecordingFile.write(d, to: url)  // encrypted when Encrypt Recordings is on
    }

    /// Decode any audio file macOS can read into 16 kHz mono samples.
    static func load16k(_ url: URL) throws -> [Float] {
        let file = try AVAudioFile(forReading: url)
        guard let buf = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)) else { return [] }
        try file.read(into: buf)
        return Resampler().convert(buf)
    }
}

/// Microphone via AVAudioEngine.
final class MicCapture {
    private let engine = AVAudioEngine()
    private let resampler = Resampler()
    let writer: ChunkWriter

    init(writer: ChunkWriter) { self.writer = writer }

    func start() throws {
        let input = engine.inputNode
        let fmt = input.outputFormat(forBus: 0)
        guard fmt.sampleRate > 0 else { throw NSError(domain: "openrewind", code: 20, userInfo: [NSLocalizedDescriptionKey: "no microphone input"]) }
        input.installTap(onBus: 0, bufferSize: 4096, format: fmt) { [weak self] buf, _ in
            guard let self else { return }
            self.writer.append(self.resampler.convert(buf))
        }
        try engine.start()
        watchConfigChanges()
    }

    func stop() {
        if let o = configObserver { NotificationCenter.default.removeObserver(o); configObserver = nil }
        engine.stop(); engine.inputNode.removeTap(onBus: 0); writer.flushRemaining()
    }

    private var configObserver: NSObjectProtocol?

    /// The input device changed (headset plugged in, lid closed, new default mic): restart on it.
    private func watchConfigChanges() {
        guard configObserver == nil else { return }
        configObserver = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange,
                                                                object: engine, queue: .main) { [weak self] _ in
            guard let self else { return }
            log("microphone device changed, restarting")
            self.engine.stop()
            self.engine.inputNode.removeTap(onBus: 0)
            do { try self.start() } catch { log("microphone restart failed: \(error.localizedDescription)") }
        }
    }
}

/// System audio (what you hear) via ScreenCaptureKit. Needs Screen Recording permission.
final class SystemAudioCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    private var stream: SCStream?
    private let resampler = Resampler()
    private let queue = DispatchQueue(label: "openrewind.sysaudio")
    let writer: ChunkWriter

    init(writer: ChunkWriter) { self.writer = writer }

    func start() async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else {
            throw NSError(domain: "openrewind", code: 21, userInfo: [NSLocalizedDescriptionKey: "no display for audio stream"])
        }
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let cfg = SCStreamConfiguration()
        cfg.capturesAudio = true
        cfg.excludesCurrentProcessAudio = true
        cfg.sampleRate = 48_000
        cfg.channelCount = 2
        // We only want audio; keep the video side as small and slow as possible.
        cfg.width = 2; cfg.height = 2
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        let s = SCStream(filter: filter, configuration: cfg, delegate: self)
        try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue)
        try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        try await s.startCapture()
        stream = s
    }

    func stop() async {
        try? await stream?.stopCapture()
        writer.flushRemaining()
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, sb.isValid,
              let desc = sb.formatDescription,
              let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(desc),
              let fmt = AVAudioFormat(streamDescription: asbd) else { return }
        let frames = AVAudioFrameCount(CMSampleBufferGetNumSamples(sb))
        guard frames > 0, let pcm = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: frames) else { return }
        pcm.frameLength = frames
        guard CMSampleBufferCopyPCMDataIntoAudioBufferList(sb, at: 0, frameCount: Int32(frames), into: pcm.mutableAudioBufferList) == noErr else { return }
        writer.append(resampler.convert(pcm))
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        log("system audio stopped: \(error.localizedDescription)")
    }
}
