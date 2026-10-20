import AppKit
import UniformTypeIdentifiers

/// Settings → Encryption & Lock → Encrypt Recordings: switching it on and off, the background
/// migration of what is already recorded, and the recovery key. Same as Encryption.cs on Windows.
final class Encryption {
    static let onKey = "encryptRecordings", doneKey = "encryptionDone"

    /// The setting: new recordings are encrypted, and existing ones are (being) converted.
    static var isOn: Bool { UserDefaults.standard.bool(forKey: onKey) }
    /// False while a switch is still converting existing recordings (carried on at next launch).
    static var isDone: Bool { UserDefaults.standard.object(forKey: doneKey) as? Bool ?? true }

    /// Set from another thread to stop a running migration.
    private final class Cancel {
        private let l = NSLock()
        private var v = false
        var isSet: Bool { l.lock(); defer { l.unlock() }; return v }
        func set() { l.lock(); v = true; l.unlock() }
    }

    let engine: Engine
    private let defaults = UserDefaults.standard
    /// One migration at a time; a newer switch waits for the one before to stop.
    private let queue = DispatchQueue(label: "openrewind.encryption", qos: .utility)
    private var cancel: Cancel?
    /// Files the last run could not convert (it tries again next launch).
    private(set) var failed = 0
    /// The last run could not convert the database (it is left as it was and tried again next launch).
    private var dbFailed = false

    init(engine: Engine) { self.engine = engine }

    var on: Bool { Self.isOn }

    /// At launch: finish a switch that was cut short (quit, crash, power cut).
    func resume() {
        if !Self.isDone { migrate(Self.isOn) }
    }

    /// The grey line under the switch. Read from what is on disk, not only from the setting.
    func status() -> String {
        let verb = on ? "Encrypting" : "Decrypting"
        if engine.db.isConverting { return "\(verb) the database… (recording waits until it is done)" }
        let s = VaultMigration.status
        if s.running {
            return s.total == 0 ? "\(verb) the database…" : "\(verb) recordings… \(s.done.formatted()) of \(s.total.formatted())"
        }
        if dbFailed { return "The database could not be \(on ? "encrypted" : "decrypted"); trying again next launch" }
        if failed > 0 { return "\(failed.formatted()) files could not be \(on ? "encrypted" : "decrypted"); trying again next launch" }
        if !Self.isDone || engine.db.isEncrypted != on { return "\(verb) recordings…" }
        return on ? "Recordings and the database are encrypted" : "Recordings are stored as plain files"
    }

    /// Files that keep failing (damaged, or encrypted with another key) would be tried at every
    /// launch. The menu offers to stop: they stay as they are and the switch counts as done.
    func skipFailed() {
        guard failed > 0 else { return }
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = "Stop retrying?"
        a.informativeText = "\(failed.formatted()) recordings could not be \(on ? "encrypted" : "decrypted"); they may be damaged or encrypted with another key. Stop trying? They stay as they are, and OpenRewind no longer tries them at every launch."
        a.addButton(withTitle: "Stop Retrying")
        a.addButton(withTitle: "Cancel")
        guard Dialogs.run(a) == .alertFirstButtonReturn else { return }
        log("stopped retrying \(failed) recordings that could not be converted")
        failed = 0
        defaults.set(true, forKey: Self.doneKey)
    }

    var hasKey: Bool { RecordingFile.key != nil }

    func turnOn() {
        guard !on else { return }
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = "Encrypt your recordings?"
        a.informativeText = """
        OpenRewind will encrypt every new screenshot, audio clip and the database, and encrypt what is already recorded in the background. Recording keeps going.

        The key is kept in your login Keychain, so OpenRewind records after you log in without asking for a password. This protects your history if someone copies the disk, a backup or the .openrewind folder, or logs in as another user. It does not protect against apps running as you.

        If your Keychain is reset or you move your recordings to another Mac, you will need the recovery key. Without it, encrypted recordings are lost for good. Save it on the next screen and keep it somewhere safe.
        """
        a.addButton(withTitle: "Encrypt")
        a.addButton(withTitle: "Cancel")
        guard Dialogs.run(a) == .alertFirstButtonReturn else { return }

        // Never make a new key over one that could not be read (a locked or denied Keychain): the
        // old key may still be needed for recordings encrypted with it.
        var key = RecordingFile.key
        if key == nil {
            switch DataKeyStore.read() {
            case .found(let k):
                key = k
            case .failed(let status):
                alert("Encrypt Recordings", "OpenRewind could not read its encryption key from the Keychain (error \(status)). Nothing was changed. Unlock the Keychain or allow access, then try again.")
                return
            case .missing:
                let k = RecordingFile.newKey()
                guard DataKeyStore.store(k) else {
                    alert("Encrypt Recordings", "OpenRewind could not save the encryption key to the Keychain. Nothing was encrypted.")
                    return
                }
                key = k
            }
        }
        RecordingFile.key = key
        RecordingFile.encryptNew = true
        defaults.set(true, forKey: Self.onKey)
        defaults.set(false, forKey: Self.doneKey)
        log("encrypt recordings: on")
        migrate(true)
        if !exportKey() {
            alert("Encrypt Recordings", "Encryption is on. Save the recovery key soon: Settings → Encryption & Lock → Export Recovery Key…")
        }
    }

    func turnOff() {
        guard on else { return }
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = "Stop encrypting recordings?"
        a.informativeText = "OpenRewind will decrypt your recordings and the database in the background and store new ones as plain files. Anyone who can read the .openrewind folder will be able to see them."
        a.alertStyle = .warning
        a.addButton(withTitle: "Stop Encrypting")
        a.addButton(withTitle: "Cancel")
        guard Dialogs.run(a) == .alertFirstButtonReturn else { return }
        RecordingFile.encryptNew = false
        defaults.set(false, forKey: Self.onKey)
        defaults.set(false, forKey: Self.doneKey)
        log("encrypt recordings: off")
        migrate(false)
    }

    /// Convert in the background. A newer switch cancels the one before; each file is done on its own,
    /// so a switch that is cut short just leaves some files in each state, which both read fine.
    private func migrate(_ encrypt: Bool) {
        cancel?.set()
        let c = Cancel()
        cancel = c
        failed = 0
        dbFailed = false
        let db = engine.db, dir = engine.config.dataDir
        queue.async { [weak self] in
            if c.isSet { return }
            guard let key = RecordingFile.key ?? DataKeyStore.load() else {
                // Decrypting with no key and nothing encrypted left: there is nothing to do, so it is done.
                if !encrypt && !db.isEncrypted && RecordingFile.firstEncrypted(in: dir) == nil {
                    DispatchQueue.main.async { if !Self.isOn { UserDefaults.standard.set(true, forKey: Self.doneKey) } }
                    return
                }
                log("cannot convert recordings: the encryption key is not loaded")
                return
            }
            RecordingFile.key = key
            let r = VaultMigration.run(db: db, dataDir: dir, key: key, encrypt: encrypt, cancelled: { c.isSet })
            DispatchQueue.main.async {
                guard let self else { return }
                self.failed = r.failed
                self.dbFailed = r.databaseFailed
                if r.finished && Self.isOn == encrypt { self.defaults.set(true, forKey: Self.doneKey) }
            }
        }
    }

    func stop() { cancel?.set() }

    /// Save the recovery key as a text file. False when the user cancelled.
    @discardableResult
    func exportKey() -> Bool {
        guard let key = RecordingFile.key ?? DataKeyStore.load() else { return false }
        NSApp.activate(ignoringOtherApps: true)
        let p = NSSavePanel()
        p.title = "Save Recovery Key"
        p.nameFieldStringValue = "OpenRewind Recovery Key.txt"
        p.allowedContentTypes = [.plainText]
        p.directoryURL = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
        guard Dialogs.run(p) == .OK, let url = p.url else { return false }
        do {
            try RecoveryKey.file(key).write(to: url, atomically: true, encoding: .utf8)
            // Only this user can read it (the folder chosen may be shared).
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            log("recovery key saved")
            alert("Recovery key saved", "Keep this file somewhere safe and private, like a password manager or a USB stick in a drawer. Anyone with it and a copy of your recordings can read them.")
            return true
        } catch {
            alert("Save Recovery Key", "Could not save the recovery key: \(error.localizedDescription)")
            return false
        }
    }

    private func alert(_ title: String, _ text: String) {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        Dialogs.run(a)
    }

    // MARK: - at launch

    /// The data key, loaded before the database (which may be encrypted with it). Nil when nothing
    /// is encrypted. When recordings are encrypted and the Keychain has no key for them, the app asks
    /// for the recovery key; the command line, or a user who quits that prompt, gets `.locked`.
    /// Also while a switch off is still decrypting (`isDone` false): some files are still encrypted.
    static func loadKey(config: Config, interactive: Bool) throws -> Data? {
        let dbEncrypted = Database.isEncryptedFile(config.dbPath)
        // Read the Keychain only when it matters, so nobody who never turned this on sees a prompt.
        guard isOn || !isDone || dbEncrypted else { return nil }
        if let key = DataKeyStore.load() { return key }
        // A switch off with nothing encrypted left: it is done, carry on without a key.
        if !isOn && !dbEncrypted && RecordingFile.firstEncrypted(in: config.dataDir) == nil {
            UserDefaults.standard.set(true, forKey: doneKey)
            return nil
        }
        if interactive, let key = askForRecoveryKey(config: config) { return key }
        throw DBError.locked("the recordings are encrypted and the key is not in this Mac's Keychain; open OpenRewind.app to enter the recovery key")
    }

    /// Paste the recovery key, or open the file Export Recovery Key… saved. Returns the key
    /// (checked against the database and saved to the Keychain), or nil to quit.
    private static func askForRecoveryKey(config: Config) -> Data? {
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        app.activate(ignoringOtherApps: true)
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 440, height: 48))
        field.placeholderString = "XXXXXXXX-XXXXXXXX-…"
        field.font = .monospacedSystemFont(ofSize: 12, weight: .regular)
        field.usesSingleLineMode = false
        field.cell?.wraps = true
        while true {
            let a = NSAlert()
            a.messageText = "Your recordings are encrypted"
            a.informativeText = """
            The key is not in this Mac's Keychain (it may have been reset, or the recordings came from another computer). Paste the recovery key you saved, or open its file.

            Without it they can't be opened. To start over with an empty history, quit and move the .openrewind folder away.
            """
            a.accessoryView = field
            a.addButton(withTitle: "Unlock")
            a.addButton(withTitle: "Quit")
            a.addButton(withTitle: "Open File…")
            a.window.initialFirstResponder = field
            switch Dialogs.run(a) {
            case .alertSecondButtonReturn:
                return nil
            case .alertThirdButtonReturn:
                let p = NSOpenPanel()
                p.title = "Open Recovery Key"
                p.allowedContentTypes = [.plainText]
                if Dialogs.run(p) == .OK, let url = p.url {
                    do { field.stringValue = try String(contentsOf: url, encoding: .utf8) } catch {
                        problem("Could not open the file: \(error.localizedDescription)")
                    }
                }
                continue
            default:
                break
            }
            guard let key = RecoveryKey.parse(field.stringValue) else {
                problem("That is not a whole recovery key. It has 72 letters and digits in 9 groups; check for a missing or mistyped group.")
                continue
            }
            // Check it against the database, or (with a plain database) against an encrypted recording.
            let opens: Bool
            if Database.isEncryptedFile(config.dbPath) {
                opens = Database.keyOpens(config.dbPath, key: key)
            } else if let sample = RecordingFile.firstEncrypted(in: config.dataDir) {
                opens = RecordingFile.keyOpens(sample, key: key)
            } else {
                opens = true
            }
            if !opens {
                problem("This recovery key does not open these recordings. It may belong to another computer or an older key.")
                continue
            }
            DataKeyStore.replace(key)
            log("encryption key restored from the recovery key")
            return key
        }
    }

    private static func problem(_ text: String) {
        let a = NSAlert()
        a.messageText = "OpenRewind"
        a.informativeText = text
        a.alertStyle = .warning
        Dialogs.run(a)
    }
}
