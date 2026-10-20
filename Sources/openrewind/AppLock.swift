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
        if enabled { onLock?() }
    }

    private func touch() {
        guard enabled else { return }
        state.lock(); lastUsed = Date(); state.unlock()
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
