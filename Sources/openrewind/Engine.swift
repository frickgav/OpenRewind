import AVFoundation
import Foundation

/// Owns everything that records. Screen and audio can each be turned on and off on their own.
/// The Whisper worker runs for the whole life of the process; the API server only while it is switched on.
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
    private var watchdog: Timer?
    /// The user wants audio on, even while a stream is between restarts.
    private var audioWanted = false
    private var systemRestarting = false

    var screenOn: Bool { screenTask != nil }
    var audioOn: Bool { audioWanted }

    init(config: Config, db: Database) {
        self.config = config
        self.db = db
        self.recorder = Recorder(config: config, db: db)
    }

    var serverOn: Bool { server != nil }
    /// True while the app is locked; the local server then serves no history (see AppLock).
    var serverLocked: () -> Bool = { false }

    func startServer(exitOnFailure: Bool = true) throws {
        guard server == nil else { return }
        var api = API(db: db, config: config)
        api.locked = { [weak self] in self?.serverLocked() == true }
        let s = Server(port: config.port, exitOnFailure: exitOnFailure, router: api.handle)
        try s.start()
        server = s
    }

    func stopServer() {
        guard let s = server else { return }
        s.stop()
        server = nil
        log("local search server off")
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
            audioWanted = true
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
            startWatchdog()
        } else if !on, audioOn {
            watchdog?.invalidate(); watchdog = nil
            audioWanted = false
            mic?.stop(); mic = nil
            if let sys = system { system = nil; Task.detached { await sys.stop() } }
            log("audio recording off")
        }
    }

    /// Audio streams can stop without an error (device switch, sleep, another app taking the
    /// device). If one sends nothing for 60 s, restart it.
    private func startWatchdog() {
        guard watchdog == nil else { return }
        watchdog = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            guard let self else { return }
            let stale = { (w: ChunkWriter) in Date().timeIntervalSince(w.lastAppend) > 60 }
            if self.config.recordMic, AVCaptureDevice.authorizationStatus(for: .audio) == .authorized,
               self.mic.map({ stale($0.writer) }) ?? true {
                log(self.mic == nil ? "starting microphone again" : "microphone sent no sound for 60 s, restarting")
                self.mic?.stop(); self.mic = nil
                self.startMic()
            }
            let sysStale = self.system.map { stale($0.writer) } ?? true
            if self.config.recordSystemAudio, sysStale, !self.systemRestarting {
                let old = self.system
                log(old == nil ? "starting system audio again" : "system audio sent no sound for 60 s, restarting")
                self.system = nil
                self.systemRestarting = true
                Task.detached {
                    defer { Task { @MainActor in self.systemRestarting = false } }
                    await old?.stop()
                    let fresh = SystemAudioCapture(writer: ChunkWriter(device: "system audio", isInput: false,
                                                                       seconds: self.config.audioChunkSeconds,
                                                                       config: self.config, db: self.db))
                    do {
                        try await fresh.start()
                        await MainActor.run { self.system = fresh }
                        log("recording system audio")
                    } catch { log("system audio restart failed: \(error.localizedDescription)") }
                }
            }
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
