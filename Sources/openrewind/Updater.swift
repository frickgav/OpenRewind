import AppKit
import CryptoKit
import Foundation

/// New versions come from GitHub Releases (built by .github/workflows/release.yml). The app asks the
/// GitHub API for the latest release, downloads OpenRewind-mac.dmg (by itself when "Download Updates
/// Automatically" is on), and when you click Update it swaps the app bundle in place and relaunches.
/// Nothing is installed without a click. The Windows app does the same (windows/OpenRewind/Updater.cs).
final class Updater: ObservableObject {
    static let shared = Updater()

    struct Release: Equatable {
        let version: String
        let page: URL
        let asset: URL
        /// "sha256:<hex>" from the GitHub API, when it gives one.
        let digest: String?
    }

    enum State: Equatable {
        case idle
        case downloading(Double)
        /// The verified .dmg, waiting for a click.
        case ready(URL)
        case installing
        case failed(String)
    }

    @Published private(set) var latest: Release?
    @Published private(set) var state: State = .idle
    /// The version whose banner was closed; a newer version shows the banner again.
    @Published private(set) var dismissed: String?

    static let repo = "frickgav/openscreen"
    static let assetName = "OpenRewind-mac.dmg"
    static let autoDownloadKey = "autoDownloadUpdates"
    private let dismissedKey = "dismissedUpdateVersion"
    private let defaults = UserDefaults.standard
    private var lastCheck: Date?
    private var timer: Timer?
    private var progressWatch: NSKeyValueObservation?
    private var installWhenReady = false

    let current = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"

    private init() {
        defaults.register(defaults: [Self.autoDownloadKey: true])
        dismissed = defaults.string(forKey: dismissedKey)
    }

    var autoDownload: Bool {
        get { defaults.bool(forKey: Self.autoDownloadKey) }
        set {
            defaults.set(newValue, forKey: Self.autoDownloadKey)
            if newValue { downloadIfWanted() }
        }
    }

    /// The latest release, when it is newer than this app.
    var available: Release? {
        guard let r = latest, Self.isNewer(r.version, than: current) else { return nil }
        return r
    }

    var showBanner: Bool { available.map { $0.version != dismissed } ?? false }

    /// A real OpenRewind.app (not `swift run`).
    private var isApp: Bool { Bundle.main.bundleURL.pathExtension == "app" }

    /// Swapping the bundle needs a folder we can write: not the mounted .dmg, not a translocated copy.
    var canInstall: Bool {
        let parent = Bundle.main.bundleURL.deletingLastPathComponent().path
        return isApp && !parent.hasPrefix("/Volumes/") && !parent.contains("/AppTranslocation/")
            && FileManager.default.isWritableFile(atPath: parent)
    }

    // MARK: checking

    /// Check soon after launch, then every 6 hours.
    func start() {
        guard isApp else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 15) { [weak self] in self?.check() }
        timer = Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { [weak self] _ in self?.check() }
    }

    /// Opening the timeline checks again when the last check is over an hour old.
    func checkIfStale() {
        guard isApp else { return }
        if let t = lastCheck, Date().timeIntervalSince(t) < 3600 { return }
        check()
    }

    /// Ask GitHub for the latest release. `done` gets an error message, or nil when the check worked.
    func check(done: ((String?) -> Void)? = nil) {
        lastCheck = Date()
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(Self.repo)/releases/latest")!)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("OpenRewind/\(current)", forHTTPHeaderField: "User-Agent")
        req.timeoutInterval = 30
        URLSession.shared.dataTask(with: req) { [weak self] data, resp, err in
            let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
            let release = status == 200 ? data.flatMap(Self.parse) : nil
            DispatchQueue.main.async {
                guard let self else { return }
                guard let release else {
                    let why = err?.localizedDescription
                        ?? (status == 200 ? "the latest release has no \(Self.assetName)" : "GitHub answered \(status)")
                    log("update check failed: \(why)")
                    done?(why)
                    return
                }
                if release != self.latest {
                    log("latest release \(release.version), this app is \(self.current)")
                    self.latest = release
                }
                self.downloadIfWanted()
                done?(nil)
            }
        }.resume()
    }

    static func parse(_ data: Data) -> Release? {
        guard let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              o["draft"] as? Bool != true, o["prerelease"] as? Bool != true,
              let tag = o["tag_name"] as? String,
              let page = (o["html_url"] as? String).flatMap(URL.init(string:)),
              let assets = o["assets"] as? [[String: Any]],
              let a = assets.first(where: { $0["name"] as? String == assetName }),
              let url = (a["browser_download_url"] as? String).flatMap(URL.init(string:)) else { return nil }
        return Release(version: tag.hasPrefix("v") ? String(tag.dropFirst()) : tag, page: page, asset: url,
                       digest: a["digest"] as? String)
    }

    /// "0.10.0" is newer than "0.9.2". Anything after a "-" is ignored.
    static func isNewer(_ a: String, than b: String) -> Bool {
        func parts(_ s: String) -> [Int] {
            let core = s.trimmingCharacters(in: CharacterSet(charactersIn: "vV")).split(separator: "-").first ?? ""
            return core.split(separator: ".").map { Int($0) ?? 0 }
        }
        let x = parts(a), y = parts(b)
        for i in 0..<max(x.count, y.count) {
            let l = i < x.count ? x[i] : 0, r = i < y.count ? y[i] : 0
            if l != r { return l > r }
        }
        return false
    }

    // MARK: banner actions

    /// The banner's Update button: download if needed, then install. Without a writable app folder,
    /// open the download in the browser instead.
    func update() {
        guard let r = available else { return }
        guard canInstall else { NSWorkspace.shared.open(r.asset); return }
        if case .ready = state { install(); return }
        installWhenReady = true
        download()
    }

    func dismiss() {
        dismissed = available?.version
        defaults.set(dismissed, forKey: dismissedKey)
    }

    // MARK: downloading

    private func downloadIfWanted() {
        guard autoDownload, canInstall, available != nil else { return }
        download()
    }

    private static var cacheDir: URL {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("ai.agilityresources.openrewind/Updates")
    }

    func download() {
        guard let r = available else { return }
        switch state {
        case .downloading, .installing: return
        case .ready(let f) where f.lastPathComponent == "OpenRewind-\(r.version).dmg":
            if installWhenReady { install() }
            return
        default: break
        }
        let dest = Self.cacheDir.appendingPathComponent("OpenRewind-\(r.version).dmg")
        if FileManager.default.fileExists(atPath: dest.path), Self.verify(dest, r.digest) {
            downloaded(dest)
            return
        }
        state = .downloading(0)
        let task = URLSession.shared.downloadTask(with: r.asset) { [weak self] tmp, resp, err in
            let fm = FileManager.default
            let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
            var problem: String?
            if let tmp, status == 200 {
                // The temporary file is gone once this returns, so move it now.
                do {
                    try fm.createDirectory(at: Self.cacheDir, withIntermediateDirectories: true)
                    try? fm.removeItem(at: dest)
                    try fm.moveItem(at: tmp, to: dest)
                    if !Self.verify(dest, r.digest) { try? fm.removeItem(at: dest); problem = "the download was damaged" }
                } catch {
                    problem = error.localizedDescription
                }
            } else {
                problem = err?.localizedDescription ?? "GitHub answered \(status)"
            }
            DispatchQueue.main.async {
                guard let self else { return }
                self.progressWatch = nil
                if let problem { self.fail("download failed: \(problem)") } else { self.downloaded(dest) }
            }
        }
        progressWatch = task.progress.observe(\.fractionCompleted) { [weak self] p, _ in
            let f = p.fractionCompleted
            DispatchQueue.main.async {
                guard let self, case .downloading(let old) = self.state, Int(f * 100) != Int(old * 100) else { return }
                self.state = .downloading(f)
            }
        }
        task.resume()
    }

    private func downloaded(_ dmg: URL) {
        log("downloaded \(dmg.lastPathComponent)")
        state = .ready(dmg)
        if installWhenReady { install() }
    }

    private func fail(_ why: String) {
        log("update \(why)")
        installWhenReady = false
        state = .failed(why)
    }

    /// Compare against the SHA-256 GitHub lists for the file. Older API answers have none.
    static func verify(_ file: URL, _ digest: String?) -> Bool {
        guard let digest, digest.hasPrefix("sha256:") else { return true }
        guard let data = try? Data(contentsOf: file, options: .mappedIfSafe) else { return false }
        let hex = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        return hex == digest.dropFirst(7).lowercased()
    }

    // MARK: installing

    /// Copy the new app out of the .dmg next to this one, then quit; a small script swaps the two
    /// once this process is gone and opens the new one.
    func install() {
        guard case .ready(let dmg) = state, canInstall else { return }
        installWhenReady = false
        state = .installing
        let app = Bundle.main.bundleURL
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            do {
                let staged = try Self.stage(dmg: dmg, beside: app)
                try Self.swapAndRelaunch(app: app, staged: staged, dmg: dmg)
                DispatchQueue.main.async {
                    log("installing the update and relaunching")
                    NSApp.terminate(nil)
                }
            } catch {
                DispatchQueue.main.async { self?.fail("install failed: \(error.localizedDescription)") }
            }
        }
    }

    /// Mount the .dmg, copy OpenRewind.app out of it into the running app's folder, unmount.
    private static func stage(dmg: URL, beside app: URL) throws -> URL {
        let fm = FileManager.default
        let mount = fm.temporaryDirectory.appendingPathComponent("openrewind-update-\(UUID().uuidString)")
        try fm.createDirectory(at: mount, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: mount) }
        try run("/usr/bin/hdiutil", ["attach", dmg.path, "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount.path])
        defer { _ = try? run("/usr/bin/hdiutil", ["detach", mount.path, "-force"]) }
        let src = mount.appendingPathComponent("OpenRewind.app")
        guard Bundle(url: src)?.bundleIdentifier == Bundle.main.bundleIdentifier else {
            throw UpdateError("the download does not hold OpenRewind.app")
        }
        let staged = app.deletingLastPathComponent().appendingPathComponent(".OpenRewind-update.app")
        try? fm.removeItem(at: staged)
        try run("/usr/bin/ditto", [src.path, staged.path])
        return staged
    }

    private static func swapAndRelaunch(app: URL, staged: URL, dmg: URL) throws {
        // $1 = our pid, $2 = the app, $3 = the new copy, $4 = the .dmg. Puts the old app back if the swap fails.
        let script = """
        while /bin/kill -0 "$1" 2>/dev/null; do /bin/sleep 0.2; done
        /bin/rm -rf "$2.old"
        if /bin/mv "$2" "$2.old" && /bin/mv "$3" "$2"; then /bin/rm -rf "$2.old" "$4"; else /bin/mv "$2.old" "$2"; fi
        /usr/bin/xattr -dr com.apple.quarantine "$2" 2>/dev/null
        /usr/bin/open "$2"
        """
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", script, "openrewind-update",
                       String(ProcessInfo.processInfo.processIdentifier), app.path, staged.path, dmg.path]
        try p.run()
    }

    @discardableResult
    private static func run(_ tool: String, _ args: [String]) throws -> String {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        let out = Pipe()
        p.standardOutput = out
        p.standardError = out
        try p.run()
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        let text = String(data: data, encoding: .utf8) ?? ""
        guard p.terminationStatus == 0 else {
            throw UpdateError("\((tool as NSString).lastPathComponent) failed: \(text.trimmingCharacters(in: .whitespacesAndNewlines))")
        }
        return text
    }

    struct UpdateError: LocalizedError {
        let message: String
        init(_ message: String) { self.message = message }
        var errorDescription: String? { message }
    }
}
