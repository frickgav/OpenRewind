import SwiftUI

/// Settings → Transcription Model, for a model that is not on this Mac yet: says how big it is and
/// where it comes from, downloads it with a progress bar, and checks it before it is used.
/// Closing the window stops the download; the next try carries on where it stopped.
/// Its state is only read and changed on the main thread; the download task hops there to report.
final class ModelDownload: ObservableObject, @unchecked Sendable {
    let model: SpeechModel
    let home: URL
    /// Runs on the main thread once the model is downloaded and checked.
    let done: () -> Void
    @Published var running = false
    @Published var fraction = 0.0
    @Published var line = ""
    @Published var failed = false
    private var task: Task<Void, Never>?

    init(model: SpeechModel, home: URL, done: @escaping () -> Void) {
        self.model = model; self.home = home; self.done = done
    }

    static let windowKey = "modelDownload"
    private static var current: ModelDownload?

    /// One download window at a time; a running download keeps its window.
    static func show(_ model: SpeechModel, home: URL, done: @escaping () -> Void) {
        if let c = current, c.running || c.model.id == model.id {
            SetupWindows.shared.show(windowKey, title: "", size: .zero) { EmptyView() }  // brings it to the front
            return
        }
        SetupWindows.shared.close(windowKey)
        let d = ModelDownload(model: model, home: home, done: done)
        current = d
        SetupWindows.shared.show(windowKey, title: "Download \(model.name)", size: NSSize(width: 520, height: 340),
                                 onClose: { d.cancel(); if current === d { current = nil } }) {
            ModelDownloadView(download: d)
        }
    }

    func start() {
        guard !running else { return }
        running = true
        failed = false
        line = "Starting…"
        startedAt = Date()
        firstBytes = -1
        let me = self, model = model, home = home
        task = Task.detached {
            do {
                try await SpeechModels.download(model, home: home) { got, total in
                    DispatchQueue.main.async { me.show(got, of: total) }
                }
                await MainActor.run { me.line = "Checking the files…" }
                if let problem = SpeechModels.problem(model, home: home) { throw SpeechModels.DownloadError(problem) }
                await MainActor.run {
                    me.running = false
                    SetupWindows.shared.close(ModelDownload.windowKey)
                    me.done()
                }
            } catch is CancellationError {
            } catch {
                log("downloading \(model.name) failed: \(error.localizedDescription)")
                await MainActor.run {
                    me.running = false
                    me.failed = true
                    me.line = "The download stopped: \(error.localizedDescription)"
                }
            }
        }
    }

    private var startedAt = Date()
    /// Bytes already there from an earlier try, so the time left counts only what still comes.
    private var firstBytes: Int64 = -1

    /// On the main thread.
    private func show(_ got: Int64, of total: Int64) {
        guard running else { return }
        if firstBytes < 0 { firstBytes = got }
        fraction = Double(got) / Double(max(1, total))
        let secs = Date().timeIntervalSince(startedAt)
        let rate = Double(got - firstBytes) / max(0.5, secs)
        let left = rate > 0 && secs > 3 ? " · \(SpeechModels.about(Double(total - got) / rate)) left" : ""
        line = "\(SpeechModels.formatSize(got)) of \(SpeechModels.formatSize(total))\(left)"
    }

    func cancel() {
        task?.cancel()
        task = nil
        running = false
    }
}

struct ModelDownloadView: View {
    @ObservedObject var download: ModelDownload

    private var whisperMissing: Bool {
        download.model.engine == .whisper && Transcriber.findBinary(nil) == nil
    }

    var body: some View {
        let m = download.model
        VStack(alignment: .leading, spacing: 12) {
            Text("Download \(m.name)?").font(.system(size: 18, weight: .semibold))
            Text(m.detail).font(.system(size: 12)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)
            Text("\(SpeechModels.formatSize(m.size)) from Hugging Face, saved in \(m.dir(download.home).path). It runs on this Mac: your audio never leaves it. Each file is checked against its known fingerprint before it is used.")
                .font(.system(size: 12)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)
            if m.engine == .parakeet {
                Text(SpeechModels.parakeetCredit).font(.system(size: 11)).foregroundColor(SetupTheme.faint).fixedSize(horizontal: false, vertical: true)
            }
            if whisperMissing {
                Text("Whisper also needs whisper-cli: brew install whisper-cpp. NVIDIA Parakeet v3 needs nothing else.")
                    .font(.system(size: 12)).foregroundColor(SetupTheme.warn).fixedSize(horizontal: false, vertical: true)
            }
            if download.running || download.fraction > 0 {
                ProgressView(value: download.fraction)
            }
            Text(download.line)
                .font(.system(size: 12)).foregroundColor(download.failed ? SetupTheme.warn : SetupTheme.dim)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            HStack {
                Spacer()
                Button("Cancel") { SetupWindows.shared.close(ModelDownload.windowKey) }.buttonStyle(PillButtonStyle())
                Button(download.running ? "Downloading" : download.failed ? "Try Again" : "Download") { download.start() }
                    .buttonStyle(PillButtonStyle(primary: true))
                    .disabled(download.running)
            }
        }
        .padding(24).padding(.top, 14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(SetupTheme.bg)
        .foregroundColor(.white)
        .preferredColorScheme(.dark)
    }
}
