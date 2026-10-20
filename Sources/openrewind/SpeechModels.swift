import CryptoKit
import Foundation

enum SpeechEngine { case whisper, parakeet }

/// One file of a model: where it comes from (a fixed Hugging Face commit) and what it must hash to.
struct ModelFile {
    let name: String
    let url: URL
    let sha256: String
    let size: Int64
}

/// A speech-to-text model the user can pick in Settings → Transcription Model.
/// `folder` is under ~/.openrewind/models ("" = that folder itself, where Whisper looks for ggml-*.bin).
/// Downloaded Whisper files get their own names (ggml-small.openrewind.bin), so a ggml-small.bin the
/// user put there is never replaced.
struct SpeechModel {
    let id: String
    let name: String
    let engine: SpeechEngine
    let detail: String
    let folder: String
    let files: [ModelFile]

    var size: Int64 { files.reduce(0) { $0 + $1.size } }
    func dir(_ home: URL) -> URL { folder.isEmpty ? SpeechModels.modelsDir(home) : SpeechModels.modelsDir(home).appendingPathComponent(folder) }
    func path(_ home: URL, _ f: ModelFile) -> URL { dir(home).appendingPathComponent(f.name) }
}

/// The models OpenRewind knows. Same list, ids, files and hashes as the Windows app (SpeechModels.cs).
enum SpeechModels {
    /// Whatever ggml Whisper model is already on this computer (what OpenRewind always did).
    static let defaultId = "whisper-auto"
    static let parakeetId = "parakeet-v3"
    static let defaultsKey = "transcriptionModel"

    private static let whisperRepo = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/"
    private static let parakeetRepo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/2bda32ec70b097a55adaa07d9a7173915b43cc78/"

    /// Ending of the Whisper files OpenRewind downloads ("ggml-small.openrewind.bin").
    static let ownSuffix = ".openrewind"

    private static func whisper(_ name: String, _ sha: String, _ size: Int64) -> ModelFile {
        let local = (name as NSString).deletingPathExtension + ownSuffix + "." + (name as NSString).pathExtension
        return ModelFile(name: local, url: URL(string: whisperRepo + name)!, sha256: sha, size: size)
    }

    private static func parakeet(_ name: String, _ sha: String, _ size: Int64) -> ModelFile {
        ModelFile(name: name, url: URL(string: parakeetRepo + name)!, sha256: sha, size: size)
    }

    static let all: [SpeechModel] = [
        SpeechModel(id: defaultId, name: "Whisper (Automatic)", engine: .whisper,
                    detail: "Uses the Whisper model already on this computer. Needs whisper-cli.", folder: "", files: []),
        SpeechModel(id: "whisper-base", name: "Whisper Base", engine: .whisper,
                    detail: "Fast, less accurate. 99 languages. Needs whisper-cli.", folder: "",
                    files: [whisper("ggml-base.bin", "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe", 147951465)]),
        SpeechModel(id: "whisper-small", name: "Whisper Small", engine: .whisper,
                    detail: "Good balance. 99 languages. Needs whisper-cli.", folder: "",
                    files: [whisper("ggml-small.bin", "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b", 487601967)]),
        SpeechModel(id: "whisper-large-v3-turbo", name: "Whisper Large v3 Turbo", engine: .whisper,
                    detail: "Most accurate Whisper, slower. 99 languages. Needs whisper-cli.", folder: "",
                    files: [whisper("ggml-large-v3-turbo-q5_0.bin", "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", 574041195)]),
        SpeechModel(id: parakeetId, name: "NVIDIA Parakeet v3", engine: .parakeet,
                    detail: "Very accurate and fast, with punctuation. 25 European languages. Nothing else to install.",
                    folder: "parakeet-tdt-0.6b-v3-int8",
                    files: [
                        parakeet("encoder.int8.onnx", "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247", 652184281),
                        parakeet("decoder.int8.onnx", "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e", 11845275),
                        parakeet("joiner.int8.onnx", "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3", 6355277),
                        parakeet("tokens.txt", "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d", 93939),
                    ]),
    ]

    /// Shown wherever Parakeet can be picked, and in the README (the model's license asks for it).
    static let parakeetCredit = "NVIDIA Parakeet TDT 0.6B v3 by NVIDIA, licensed CC-BY-4.0 (huggingface.co/nvidia/parakeet-tdt-0.6b-v3); ONNX export by the sherpa-onnx project."

    /// The picked model, or the default for an unknown or empty id.
    static func byId(_ id: String?) -> SpeechModel { all.first { $0.id == id } ?? all[0] }

    /// Settings → Transcription Model, kept in UserDefaults like the other menu settings.
    static var picked: String {
        get { UserDefaults.standard.string(forKey: defaultsKey) ?? defaultId }
        set { UserDefaults.standard.set(newValue, forKey: defaultsKey) }
    }

    static func modelsDir(_ home: URL) -> URL { home.appendingPathComponent("models") }

    /// What Parakeet v3 understands (ISO 639-1). It finds the language by itself and ignores --language.
    static let parakeetLanguages: Set<String> = [
        "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it",
        "lv", "lt", "mt", "pl", "pt", "ro", "ru", "sk", "sl", "es", "sv", "uk",
    ]

    /// A warning when `language` (the Whisper --language setting) is one Parakeet does not know, else nil.
    static func languageWarning(_ m: SpeechModel, language: String?) -> String? {
        guard m.engine == .parakeet, let language else { return nil }
        let code = language.trimmingCharacters(in: .whitespaces).lowercased()
        if code.isEmpty || code == "auto" || parakeetLanguages.contains(code) { return nil }
        return "\(m.name) does not understand the language set for transcription (\(code)): it knows 25 European languages. "
            + "Its transcripts of other languages are not usable; keep a Whisper model for them."
    }

    /// All files are there at the right size. Cheap, for menus; loading checks the hashes (`ensureVerified`).
    static func isDownloaded(_ m: SpeechModel, home: URL) -> Bool {
        m.files.allSatisfy { fileSize(m.path(home, $0)) == $0.size }
    }

    /// Size of the file, following a symbolic link (someone may keep the models on another disk).
    static func fileSize(_ url: URL) -> Int64? {
        (try? FileManager.default.attributesOfItem(atPath: url.resolvingSymlinksInPath().path)[.size] as? NSNumber)?.int64Value
    }

    static func formatSize(_ bytes: Int64) -> String {
        bytes >= 1_000_000_000 ? String(format: "%.1f GB", Double(bytes) / 1e9) : "\(max(1, Int((Double(bytes) / 1e6).rounded()))) MB"
    }

    /// "under a minute", "about 25 minutes", "about 3 hours": for download time left and re-transcribe estimates.
    static func about(_ seconds: Double) -> String {
        if seconds < 60 { return "under a minute" }
        if seconds < 5400 {
            let m = max(1, Int((seconds / 60).rounded()))
            return "about \(m) minute\(m >= 2 ? "s" : "")"
        }
        return "about \(Int((seconds / 3600).rounded())) hours"
    }

    // MARK: verification

    /// What a file looked like when this run of the app hashed it. The inode change time cannot be set
    /// back by hand, so a swapped file is always hashed again. (A stamp file next to the model would let
    /// anyone who can write there skip the hash by restoring the date.)
    private struct Identity: Equatable { let size: Int64, inode: UInt64, changed: Int, changedNs: Int, modified: Int, modifiedNs: Int }
    private static let verifiedLock = NSLock()
    private static var verified: [String: Identity] = [:]

    private static func identity(_ path: String) -> Identity? {
        var st = stat()
        guard stat(path, &st) == 0 else { return nil }
        return Identity(size: Int64(st.st_size), inode: UInt64(st.st_ino),
                        changed: st.st_ctimespec.tv_sec, changedNs: st.st_ctimespec.tv_nsec,
                        modified: st.st_mtimespec.tv_sec, modifiedNs: st.st_mtimespec.tv_nsec)
    }

    /// True when the file on disk is exactly the pinned one. Hashes it the first time in each run of the app
    /// (about a second for 650 MB), and again whenever it changed.
    static func ensureVerified(_ url: URL, _ f: ModelFile) -> Bool {
        let path = url.resolvingSymlinksInPath().path
        guard let id = identity(path), id.size == f.size else { return false }
        verifiedLock.lock()
        let known = verified[path]
        verifiedLock.unlock()
        if known == id { return true }
        guard sha256(of: URL(fileURLWithPath: path)) == f.sha256.lowercased() else { return false }
        remember(path)
        return true
    }

    private static func remember(_ path: String) {
        guard let id = identity(path) else { return }
        verifiedLock.lock(); verified[path] = id; verifiedLock.unlock()
    }

    static func sha256(of url: URL) -> String? {
        guard let h = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? h.close() }
        var hasher = SHA256()
        while let data = try? h.read(upToCount: 1 << 20), !data.isEmpty { hasher.update(data: data) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    /// Every file of the model is there and matches its hash; names the first one that is not.
    /// A file that cannot be read counts as missing.
    static func problem(_ m: SpeechModel, home: URL) -> String? {
        for f in m.files where !ensureVerified(m.path(home, f), f) { return "\(f.name) is missing or damaged" }
        return nil
    }

    /// A connection that sends nothing for this long fails with "no data …", keeping what came.
    static let stallSeconds: TimeInterval = 60

    // MARK: download

    struct DownloadError: LocalizedError {
        let errorDescription: String?
        init(_ s: String) { errorDescription = s }
    }

    /// Download every file of the model that is not there yet, check each against its pinned SHA-256,
    /// and only then move it into place. A cut-off download carries on from the `.part` file next time.
    /// `progress` gets (bytes done, bytes in total) over the whole model, on a background thread.
    static func download(_ m: SpeechModel, home: URL, progress: @escaping (Int64, Int64) -> Void) async throws {
        try FileManager.default.createDirectory(at: m.dir(home), withIntermediateDirectories: true)
        let total = m.size
        var before: Int64 = 0
        for f in m.files {
            let url = m.path(home, f)
            if ensureVerified(url, f) { before += f.size; progress(before, total); continue }
            let start = before
            try await downloadFile(f, to: url) { progress(start + $0, total) }
            before += f.size
        }
        progress(total, total)
        log("downloaded \(m.name) to \(m.dir(home).path)")
    }

    private static func downloadFile(_ f: ModelFile, to url: URL, progress: @escaping (Int64) -> Void) async throws {
        let fm = FileManager.default
        let part = URL(fileURLWithPath: url.path + ".part")
        let fetch = FileFetch(file: f, part: part, progress: progress)
        // Carry on from an earlier try: hash what is already there, then ask for the rest.
        if let n = fileSize(part) {
            if n >= f.size { try? fm.removeItem(at: part) } else if let h = try? FileHandle(forReadingFrom: part) {
                while let data = try h.read(upToCount: 1 << 20), !data.isEmpty { fetch.hasher.update(data: data) }
                try? h.close()
                fetch.have = n
            }
        }
        var req = URLRequest(url: f.url)
        req.setValue("OpenRewind", forHTTPHeaderField: "User-Agent")
        if fetch.have > 0 { req.setValue("bytes=\(fetch.have)-", forHTTPHeaderField: "Range") }
        let config = URLSessionConfiguration.default
        // The longest wait for the next bytes; the whole download may take as long as it takes.
        config.timeoutIntervalForRequest = stallSeconds
        config.timeoutIntervalForResource = 7 * 24 * 3600
        let session = URLSession(configuration: config, delegate: fetch, delegateQueue: nil)
        let task = session.dataTask(with: req)
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
                fetch.finish = c
                task.resume()
            }
        } onCancel: { task.cancel() }

        let sha = fetch.hasher.finalize().map { String(format: "%02x", $0) }.joined()
        let size = fileSize(part) ?? 0
        // Cut short: keep what came, the next try asks for the rest (and hashes the start again).
        if size < f.size {
            throw DownloadError("\(f.name) stopped at \(formatSize(size)) of \(formatSize(f.size)); Try Again carries on where it stopped")
        }
        guard sha == f.sha256.lowercased() else {
            try? fm.removeItem(at: part)
            throw DownloadError("\(f.name) does not match its checksum, so it was thrown away; try again")
        }
        try? fm.removeItem(at: url)
        try fm.moveItem(at: part, to: url)
        // Hashed on the way in: no need to read it again before loading it in this run.
        remember(url.resolvingSymlinksInPath().path)
    }
}

/// Writes one download to its `.part` file and hashes it as the data comes in (URLSession's delegate
/// queue calls these one at a time).
private final class FileFetch: NSObject, URLSessionDataDelegate {
    let file: ModelFile
    let part: URL
    let progress: (Int64) -> Void
    var hasher = SHA256()
    /// Bytes already in the `.part` file from an earlier try.
    var have: Int64 = 0
    var finish: CheckedContinuation<Void, Error>?
    private var out: FileHandle?
    private var done: Int64 = 0, lastReport: Int64 = 0
    private var failure: Error?

    init(file: ModelFile, part: URL, progress: @escaping (Int64) -> Void) {
        self.file = file; self.part = part; self.progress = progress
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else {
            failure = SpeechModels.DownloadError("\(file.name): the server answered \(status)")
            completionHandler(.cancel)
            return
        }
        if have > 0 && status != 206 {
            // The server sent the whole file again: start over.
            have = 0
            hasher = SHA256()
        }
        do {
            if have == 0 { FileManager.default.createFile(atPath: part.path, contents: nil) }
            let h = try FileHandle(forWritingTo: part)
            if have > 0 { try h.seekToEnd() } else { try h.truncate(atOffset: 0) }
            out = h
        } catch {
            failure = error
            completionHandler(.cancel)
            return
        }
        done = have
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard failure == nil, let out else { return }
        if done + Int64(data.count) > file.size {
            failure = SpeechModels.DownloadError("\(file.name) is bigger than expected")
            dataTask.cancel()
            return
        }
        do { try out.write(contentsOf: data) } catch {
            failure = error
            dataTask.cancel()
            return
        }
        hasher.update(data: data)
        done += Int64(data.count)
        if done - lastReport >= 1 << 18 || done == file.size { progress(done); lastReport = done }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        try? out?.close()
        out = nil
        session.finishTasksAndInvalidate()
        let code = (error as? URLError)?.code
        if let failure { finish?.resume(throwing: failure) }
        else if code == .cancelled { finish?.resume(throwing: CancellationError()) }
        else if code == .timedOut {
            finish?.resume(throwing: SpeechModels.DownloadError(
                "no data from the server for \(Int(SpeechModels.stallSeconds)) seconds; Try Again carries on where it stopped"))
        }
        else if let error { finish?.resume(throwing: error) }
        else { finish?.resume() }
        finish = nil
    }
}
