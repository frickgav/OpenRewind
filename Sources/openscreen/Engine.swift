import AVFoundation
import Foundation

/// Owns everything that records. Screen and audio can each be turned on and off on their own.
/// The API server and the Whisper worker run for the whole life of the process.
final class Engine {
    let config: Config
    let db: Database
    let recorder: Recorder
    private(set) var transcriber: Transcriber?
    private var server: Server?
    private var screenTask: Task<Void, Never>?
    private var mic: MicCapture?
    private var system: SystemAudioCapture?
    private var transcriberTask: Task<Void, Never>?

    var screenOn: Bool { screenTask != nil }
    var audioOn: Bool { mic != nil || system != nil }

    init(config: Config, db: Database) {
        self.config = config
        self.db = db
        self.recorder = Recorder(config: config, db: db)
    }

    func startServer() throws {
        let api = API(db: db, config: config)
        let s = Server(port: config.port, router: api.handle)
        try s.start()
        server = s
    }

    func startTranscriber() {
        guard transcriberTask == nil, let tx = Transcriber(config: config, db: db) else { return }
        transcriber = tx
        transcriberTask = Task.detached { await tx.run() }
    }

    func setScreen(_ on: Bool) {
        if on, screenTask == nil {
            if !Capture.hasScreenPermission { Capture.requestScreenPermission() }
            let r = recorder
            screenTask = Task.detached { await r.run() }
            log("screen recording on")
        } else if !on, let t = screenTask {
            t.cancel()
            screenTask = nil
            log("screen recording off")
        }
    }

    func setAudio(_ on: Bool) {
        if on, !audioOn {
            if config.recordMic { startMic() }
            if config.recordSystemAudio {
                let sys = SystemAudioCapture(writer: ChunkWriter(device: "system audio", isInput: false,
                                                                 seconds: config.audioChunkSeconds, config: config, db: db))
                system = sys
                Task.detached {
                    do { try await sys.start(); log("recording system audio") }
                    catch { log("system audio not available: \(error.localizedDescription)") }
                }
            }
            startTranscriber()
        } else if !on, audioOn {
            mic?.stop(); mic = nil
            if let sys = system { system = nil; Task.detached { await sys.stop() } }
            log("audio recording off")
        }
    }

    private func startMic() {
        let begin = { [self] in
            let m = MicCapture(writer: ChunkWriter(device: "microphone", isInput: true,
                                                   seconds: config.audioChunkSeconds, config: config, db: db))
            do { try m.start(); mic = m; log("recording microphone") }
            catch { log("microphone not available: \(error.localizedDescription)") }
        }
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: begin()
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .audio) { ok in
                DispatchQueue.main.async {
                    if ok { begin() } else { log("microphone access denied") }
                }
            }
        default:
            log("microphone access denied → turn it on in System Settings → Privacy & Security → Microphone")
        }
    }
}
