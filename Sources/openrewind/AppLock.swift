import AppKit
import LocalAuthentication

/// Settings → Encryption & Lock → Require Touch ID or Password to Open Timeline. The timeline,
/// search, Ask AI and the windows that show your history ask for Touch ID or your Mac password
/// first. An unlock lasts until the Mac locks or sleeps, or until the chosen time has passed with
/// none of them in use; then they close. Recording keeps going while locked. Same as AppLock.cs on Windows.
///
/// This guards against someone using the Mac while you are away from it. It is not a wall against
/// programs running as you: they could change the setting with `defaults write` or run
/// `OpenRewind serve` themselves. The Mac password always works, so it never locks you out.
/// The state is also written to the data folder (LockState), so the MCP server follows the lock too.
final class AppLock {
    static let enabledKey = "requireUnlock", minutesKey = "lockAfterMinutes"
    /// Minutes an unlock lasts; 0 = until the Mac locks.
    static let timeoutChoices: [(minutes: Int, title: String)] =
        [(5, "5 Minutes"), (15, "15 Minutes"), (60, "1 Hour"), (0, "Only When the Mac Locks")]

    private let defaults = UserDefaults.standard
    private let state = NSLock()
    /// When a guarded window was last opened after an unlock; nil = locked.
    private var lastUsed: Date?
    private var asking = false
    /// The lock closed: hide everything that shows history. Called on the main thread.
    var onLock: (() -> Void)?
    /// The data folder to write the lock state to (LockState), for the MCP server. Starts locked.
    var stateHome: URL? { didSet { publish() } }

    private func publish() {
        guard let stateHome else { return }
        state.lock(); let t = lastUsed; state.unlock()
        LockState.write(home: stateHome, lastUsed: t, pid: getpid())
    }

    init() {
        defaults.register(defaults: [Self.minutesKey: 15])
        DistributedNotificationCenter.default().addObserver(forName: Notification.Name("com.apple.screenIsLocked"), object: nil,
                                                            queue: .main) { [weak self] _ in self?.lock() }
        let ws = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.sessionDidResignActiveNotification] {
            ws.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in self?.lock() }
        }
    }

    var enabled: Bool {
        get { defaults.bool(forKey: Self.enabledKey) }
        set {
            // A fresh switch starts locked, so turning it on asks once right away.
            state.lock(); lastUsed = nil; state.unlock()
            defaults.set(newValue, forKey: Self.enabledKey)
            publish()
        }
    }

    var minutes: Int {
        get { defaults.integer(forKey: Self.minutesKey) }
        set { defaults.set(newValue, forKey: Self.minutesKey) }
    }

    /// Also read by the local search server's thread.
    var isUnlocked: Bool {
        guard enabled else { return true }
        state.lock(); defer { state.unlock() }
        guard let t = lastUsed else { return false }
        let m = minutes
        return m <= 0 || Date().timeIntervalSince(t) < Double(m) * 60
    }

    func lock() {
        state.lock(); lastUsed = nil; state.unlock()
        publish()
        if enabled { onLock?() }
    }

    /// Forget an unlock without closing anything (quitting: AI apps get nothing once the app is gone).
    func forget() {
        state.lock(); lastUsed = nil; state.unlock()
        publish()
    }

    private func touch() {
        guard enabled else { return }
        state.lock(); lastUsed = Date(); state.unlock()
        publish()
    }

    /// Called every few seconds by the menu bar app. A guarded window in use keeps the unlock going;
    /// once the time has passed with none in use, everything that shows history closes.
    func tick(historyInUse: Bool) {
        guard enabled else { return }
        state.lock(); let open = lastUsed != nil; state.unlock()
        guard open else { return }
        if isUnlocked { if historyInUse { touch() } } else { lock() }
    }

    /// Run `action` once the user has unlocked (right away when the lock is off or still open).
    func run(_ reason: String, _ action: @escaping () -> Void) {
        unlock(reason) { if $0 { action() } }
    }

    /// Ask for Touch ID or the Mac password if needed; `done` gets the answer on the main thread.
    func unlock(_ reason: String, _ done: @escaping (Bool) -> Void) {
        if isUnlocked { touch(); done(true); return }
        guard !asking else { done(false); return }
        asking = true
        NSApp.activate(ignoringOtherApps: true)
        let ctx = LAContext()
        ctx.evaluatePolicy(.deviceOwnerAuthentication, localizedReason: reason) { ok, error in
            DispatchQueue.main.async {
                self.asking = false
                if let error, !ok { log("unlock not given: \(error.localizedDescription)") }
                if ok { self.touch() }
                done(ok)
            }
        }
    }

    /// Touch ID or a login password can be checked on this Mac. Nil when it can, else why not.
    static func unavailableReason() -> String? {
        var error: NSError?
        if LAContext().canEvaluatePolicy(.deviceOwnerAuthentication, error: &error) { return nil }
        return error?.localizedDescription ?? "This Mac cannot check Touch ID or your password."
    }
}

/// App Lock's state for the other OpenRewind processes, mainly the MCP server that AI apps start: the
/// app writes a small file in the data folder whenever it locks or is unlocked, and the MCP server reads
/// it for each message. While App Lock is on, AI apps get history only while the app runs and is
/// unlocked. Anything else (no file, a damaged one, the app not running) counts as locked.
/// Same as LockState.cs on Windows.
///
/// File: "locked", or "unlocked <app process id> <last use, seconds since 1970>". Like the lock itself,
/// this keeps out someone at the computer, not programs running as you (they could write the file).
enum LockState {
    static let fileName = "app-lock"
    static let lockedMessage =
        "OpenRewind is locked (App Lock). Open OpenRewind's timeline on this Mac to unlock it, then try again."

    static func url(_ home: URL) -> URL { home.appendingPathComponent(fileName) }

    /// Record the app's lock: `lastUsed` is when it was unlocked or last used, nil = locked.
    static func write(home: URL, lastUsed: Date?, pid: Int32) {
        let text = lastUsed.map { "unlocked \(pid) \(Int64($0.timeIntervalSince1970))" } ?? "locked"
        do {
            try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
            try Data(text.utf8).write(to: url(home), options: .atomic)
        } catch {
            log("cannot save the lock state: \(error.localizedDescription)")
        }
    }

    /// True only when the file says the app is unlocked, that app still runs, and the unlock has not run
    /// out (`minutes` as in Lock Again After; 0 = until the Mac locks).
    static func isUnlocked(home: URL, minutes: Int, now: Date = Date(), running: (Int32) -> Bool = processRunning) -> Bool {
        guard let text = try? String(contentsOf: url(home), encoding: .utf8) else { return false }
        let parts = text.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: " ")
        guard parts.count == 3, parts[0] == "unlocked", let pid = Int32(parts[1]), let secs = Int64(parts[2]) else { return false }
        guard running(pid) else { return false }
        return minutes <= 0 || now.timeIntervalSince1970 - Double(secs) < Double(minutes) * 60
    }

    /// The app is still running under that id (an id reused by another program after a crash doesn't count).
    static func processRunning(_ pid: Int32) -> Bool {
        guard pid > 0, kill(pid, 0) == 0, let app = NSRunningApplication(processIdentifier: pid), !app.isTerminated else { return false }
        return app.bundleIdentifier != nil && app.bundleIdentifier == Bundle.main.bundleIdentifier
    }

    /// For `selftest-encryption`: the state fails closed (same cases as LockStateTests.cs on Windows).
    static func selfTest() -> [String] {
        var problems: [String] = []
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("openrewind-lock-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: home) }
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let running: (Int32) -> Bool = { $0 == 42 }
        func check(_ ok: Bool, _ what: String) { if !ok { problems.append("lock state: \(what)") } }
        check(!isUnlocked(home: home, minutes: 15, now: now, running: running), "no file counts as locked")
        try? FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try? Data("unlocked banana".utf8).write(to: url(home))
        check(!isUnlocked(home: home, minutes: 15, now: now, running: running), "a damaged file counts as locked")
        write(home: home, lastUsed: now.addingTimeInterval(-300), pid: 42)
        check(isUnlocked(home: home, minutes: 15, now: now, running: running), "unlocked within Lock Again After")
        check(!isUnlocked(home: home, minutes: 15, now: now.addingTimeInterval(660), running: running), "Lock Again After ran out")
        check(isUnlocked(home: home, minutes: 0, now: now.addingTimeInterval(18_000), running: running), "until the Mac locks")
        write(home: home, lastUsed: now, pid: 7)
        check(!isUnlocked(home: home, minutes: 0, now: now, running: running), "the app is not running")
        write(home: home, lastUsed: nil, pid: 42)
        check(!isUnlocked(home: home, minutes: 0, now: now, running: running), "locked again")
        check(!processRunning(Int32.max), "a made-up process id")
        return problems
    }

    /// The MCP server's check: the lock is off, or the app is unlocked now.
    static func allowsAIApps(home: URL) -> Bool {
        let d = UserDefaults.standard
        guard d.bool(forKey: AppLock.enabledKey) else { return true }
        let minutes = d.object(forKey: AppLock.minutesKey) as? Int ?? 15
        return isUnlocked(home: home, minutes: minutes)
    }
}
