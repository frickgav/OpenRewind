import AppKit
import SwiftUI

/// The tabs along the top of the Settings window, laid out like Rewind's.
enum SettingsTab: String, CaseIterable, Identifiable {
    case general, screen, audio, meetings, ai, privacy, security, storage, shortcuts

    var id: String { rawValue }

    var title: String {
        switch self {
        case .general: return "General"
        case .screen: return "Screen"
        case .audio: return "Audio"
        case .meetings: return "Meetings"
        case .ai: return "AI"
        case .privacy: return "Privacy"
        case .security: return "Security"
        case .storage: return "Storage"
        case .shortcuts: return "Shortcuts"
        }
    }

    var symbol: String {
        switch self {
        case .general: return "gearshape"
        case .screen: return "display"
        case .audio: return "waveform"
        case .meetings: return "calendar"
        case .ai: return "cpu"
        case .privacy: return "hand.raised"
        case .security: return "lock.shield"
        case .storage: return "internaldrive"
        case .shortcuts: return "keyboard"
        }
    }
}

/// Keeps the Settings window in step with the app: it re-reads every second while open, because
/// a change can finish later (Touch ID, a confirmation, a model download) or come from the menu.
final class SettingsModel: ObservableObject {
    @Published var tab: SettingsTab = .general
    @Published private(set) var tick = 0
    /// The storage lines read the database, so they are read every few seconds, not every tick.
    @Published private(set) var storage: [String] = []
    unowned let app: MenuBarApp
    private var timer: Timer?

    init(app: MenuBarApp) { self.app = app }

    func start() {
        guard timer == nil else { return }
        let t = Timer(timeInterval: 1, repeats: true) { [weak self] _ in self?.refresh() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    func refresh() {
        tick += 1
        if tab == .storage, storage.isEmpty || tick % 5 == 0 { readStorage() }
    }

    func readStorage() {
        storage = app.storageSummary
        app.measureDataFolder { [weak self] in self?.storage = self?.app.storageSummary ?? [] }
    }

    /// A check box whose click runs `flip` (which may ask first), then shows the real state.
    func toggle(_ get: @escaping () -> Bool, _ flip: @escaping () -> Void) -> Binding<Bool> {
        Binding(get: get, set: { [weak self] new in
            if new != get() { flip() }
            self?.refresh()
        })
    }

    /// A pop-up menu that shows `get` and calls `set` with a new pick.
    func choice<T: Equatable>(_ get: @escaping () -> T, _ set: @escaping (T) -> Void) -> Binding<T> {
        Binding(get: get, set: { [weak self] new in
            if new != get() { set(new) }
            self?.refresh()
        })
    }
}

struct SettingsView: View {
    @ObservedObject var model: SettingsModel
    private var app: MenuBarApp { model.app }

    var body: some View {
        VStack(spacing: 0) {
            tabBar
                .padding(.top, 30)  // under the window title
                .padding(.bottom, 10)
            Rectangle().fill(SetupTheme.stroke).frame(height: 1)
            content
        }
        .frame(minWidth: 760, minHeight: 600)
        .background(SetupTheme.bg)
        .foregroundColor(.white)
        .preferredColorScheme(.dark)
        .onChange(of: model.tab) { if model.tab == .storage { model.readStorage() } }
    }

    private var tabBar: some View {
        HStack(spacing: 2) {
            ForEach(SettingsTab.allCases) { t in
                let on = model.tab == t
                Button { model.tab = t } label: {
                    VStack(spacing: 5) {
                        Image(systemName: t.symbol)
                            .font(.system(size: 21, weight: .regular))
                            .frame(height: 26)
                        Text(t.title).font(.system(size: 11.5, weight: on ? .semibold : .medium))
                    }
                    .frame(width: 78, height: 58)
                    .foregroundColor(on ? SettingsLook.selected : SetupTheme.dim)
                    .background(RoundedRectangle(cornerRadius: 9).fill(on ? SetupTheme.cardHi : Color.clear))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(t.title)
            }
        }
    }

    @ViewBuilder private var content: some View {
        switch model.tab {
        case .privacy:
            // Has its own scrolling lists, so it fills the tab instead of scrolling as a whole.
            BlockedAppsView(db: app.engine.db, embedded: true)
        case .ai:
            page {
                AISettingsView(embedded: true) { app.savedAISettings() }
                Rectangle().fill(SetupTheme.stroke).frame(height: 1).padding(.vertical, 4)
                aiExtras
            }
        case .general: page { general }
        case .screen: page { screen }
        case .audio: page { audio }
        case .meetings: page { meetings }
        case .security: page { security }
        case .storage: page { storage }
        case .shortcuts: page { shortcuts }
        }
    }

    private func page<V: View>(@ViewBuilder _ body: () -> V) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) { body() }
                .padding(.horizontal, 36).padding(.vertical, 28)
                .frame(maxWidth: 720, alignment: .leading)
                .frame(maxWidth: .infinity)
        }
    }

    // MARK: General

    @ViewBuilder private var general: some View {
        SettingsHeading("OpenRewind records your screen and audio on this Mac. Nothing is sent to the cloud.")
        SettingsRow("Login") {
            Toggle("Open OpenRewind at login", isOn: model.toggle({ app.launchAtLogin }, app.toggleLaunchAtLogin))
        }
        SettingsRow("Updates") {
            Toggle("Download updates automatically", isOn: model.toggle({ Updater.shared.autoDownload }, { Updater.shared.autoDownload.toggle() }))
            SettingsNote("New versions download in the background. They are installed only when you click Update.")
            HStack(spacing: 10) {
                Button("Check for Updates…") { app.checkUpdatesNow() }
                Text("Version \(Updater.shared.current)").font(.system(size: 12)).foregroundColor(SetupTheme.faint)
            }
        }
        SettingsRow("Timeline look") {
            Picker("", selection: model.choice({ app.look }, { app.look = $0 })) {
                ForEach(OverlayLook.allCases, id: \.self) { Text($0.title).tag($0) }
            }
            .labelsHidden().frame(width: 220)
        }
        SettingsRow("Permissions") {
            ForEach(Permission.allCases, id: \.self) { p in
                HStack(alignment: .center, spacing: 10) {
                    Image(systemName: p.granted ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                        .foregroundColor(p.granted ? SetupTheme.good : SetupTheme.warn)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(p.title).font(.system(size: 13, weight: .semibold))
                        Text(p.why).font(.system(size: 11.5)).foregroundColor(SetupTheme.faint)
                    }
                    Spacer()
                    if !p.granted { Button("Turn On…") { PermissionHelper.shared.show(p) } }
                }
                .padding(.vertical, 2)
            }
            Button("Open Privacy & Security Settings…") { app.openPrivacyPane() }.buttonStyle(.link).font(.system(size: 12))
        }
        SettingsRow("Help") {
            HStack(spacing: 10) {
                Button("Show Tutorial") { app.openTutorial() }
                Button("Run Setup Again…") { app.openSetup() }
            }
        }
    }

    // MARK: Screen

    private static let scales: [(String, Double)] = [("Full", 1.0), ("High", 0.75), ("Medium", 0.5), ("Low", 0.33)]

    @ViewBuilder private var screen: some View {
        SettingsHeading("Screen recordings are stored on this Mac and never sent to the cloud.")
        SettingsRow("Recording") {
            Toggle("Record the screen", isOn: model.toggle({ app.recordsScreen }, app.toggleRecordScreen))
            SettingsNote("To stop for a while, use Pause Recording in the menu bar menu.")
        }
        SettingsRow("Exclude") {
            Text("Blocked apps, sites, private windows and title words are blurred in every screenshot, and their text is never saved.")
                .font(.system(size: 13)).fixedSize(horizontal: false, vertical: true)
            Button("Blocked Apps & Sites…") { model.tab = .privacy }
        }
        SettingsRow("Resolution") {
            Picker("", selection: model.choice({ Self.nearestScale(app.screenshotScale) }, { app.screenshotScale = $0 })) {
                ForEach(Self.scales.indices, id: \.self) { i in Text(Self.scaleTitle(Self.scales[i].0, Self.scales[i].1)).tag(Self.scales[i].1) }
            }
            .labelsHidden().frame(width: 320)
            SettingsNote("Higher looks sharper but uses more disk space. Applies to new screenshots.")
        }
        SettingsRow("While typing") {
            Picker("", selection: model.choice({ app.typingGap }, { app.typingGap = $0 })) {
                ForEach(FrameDedup.gapChoices, id: \.self) { secs in
                    Text("Save a frame every \(secs) seconds" + (secs == 2 ? " (default)" : "")).tag(secs)
                }
            }
            .labelsHidden().frame(width: 320)
            SettingsNote("While only a few words on screen change, save a frame this often. Longer uses less disk space; the screen you stop on is still saved.")
        }
        SettingsRow("Search by meaning") {
            Toggle("Find moments by what they were about, not only the exact words",
                   isOn: model.toggle({ app.meaningOn }, app.toggleSearchByMeaning))
            if app.meaningOn { Text(app.meaningLine).font(.system(size: 12)).foregroundColor(SetupTheme.dim) }
            SettingsNote("Uses the language model that comes with macOS. Works on this Mac; nothing is sent anywhere.")
        }
    }

    private static func nearestScale(_ s: Double) -> Double {
        scales.map(\.1).min { abs($0 - s) < abs($1 - s) } ?? 0.5
    }

    /// "Medium (50%) — 1512 × 982", from the main display's size in real pixels.
    private static func scaleTitle(_ title: String, _ scale: Double) -> String {
        var t = "\(title) (\(Int(scale * 100))%)"
        if let s = NSScreen.main {
            t += "  —  \(Int(s.frame.width * s.backingScaleFactor * scale)) × \(Int(s.frame.height * s.backingScaleFactor * scale))"
        }
        return t
    }

    // MARK: Audio

    @ViewBuilder private var audio: some View {
        SettingsHeading("Audio recordings are stored on this Mac and never sent to the cloud.")
        SettingsRow("Recording") {
            Toggle("Record audio (your microphone and the Mac's sound)", isOn: model.toggle({ app.recordsAudio }, app.toggleRecordAudio))
            SettingsNote("Audio is turned into text on this Mac, so you can search what was said.")
        }
        SettingsRow("Transcription") {
            Picker("", selection: model.choice({ SpeechModels.picked }, { app.useTranscriptionModel($0) })) {
                ForEach(SpeechModels.all, id: \.id) { m in Text(Self.modelTitle(m, home: app.engine.config.home)).tag(m.id) }
            }
            .labelsHidden().frame(width: 380)
            SettingsNote(SpeechModels.byId(SpeechModels.picked).detail)
            ForEach(app.transcriptionLines, id: \.self) { line in
                Text(line).font(.system(size: 12)).foregroundColor(SetupTheme.dim)
            }
            HStack(spacing: 12) {
                if !app.transcriberRunning, SpeechModels.byId(SpeechModels.picked).engine == .whisper {
                    Button("Set Up Whisper…") { app.openWhisperSetup() }
                }
                Button("About These Models…") { app.openTranscriptionHelp() }.buttonStyle(.link).font(.system(size: 12))
            }
        }
        SettingsRow("Re-transcribe") {
            HStack(spacing: 8) {
                Button("Last Hour…") { app.retranscribeRecent(hours: 1) }
                Button("Last Day…") { app.retranscribeRecent(hours: 24) }
                Button("Last Week…") { app.retranscribeRecent(hours: 24 * 7) }
            }
            .disabled(!app.transcriberRunning)
            SettingsNote("Run audio you already recorded through the model picked now.")
        }
    }

    /// "NVIDIA Parakeet v3 (default) — downloaded", "Whisper Small — 488 MB download".
    private static func modelTitle(_ m: SpeechModel, home: URL) -> String {
        let size = m.files.isEmpty ? "" : SpeechModels.isDownloaded(m, home: home) ? "  —  downloaded" : "  —  \(SpeechModels.formatSize(m.size)) download"
        return m.name + (m.id == SpeechModels.defaultId ? " (default)" : "") + size
    }

    // MARK: Meetings

    @ViewBuilder private var meetings: some View {
        SettingsSection("Recording Meetings",
                        "When a Zoom, Teams, Google Meet, Webex, FaceTime or Slack huddle call starts, OpenRewind can take notes: the call's transcript, kept on this Mac. Please ask for consent before recording others.")
        SettingsRow("When a call starts") {
            Picker("", selection: model.choice({ MeetingCenter.askFirst }, { MeetingCenter.askFirst = $0 })) {
                Text("Ask whether to take notes").tag(true)
                Text("Take notes for every call").tag(false)
            }
            .labelsHidden().frame(width: 300)
            SettingsNote("Apps you said Never for are always skipped.")
        }
        SettingsRow("When a meeting ends") {
            Picker("", selection: model.choice({ MeetingCenter.autoSummarize }, { _ in app.toggleSummaries() })) {
                Text("Keep the transcript").tag(false)
                Text("Summarize it with AI").tag(true)
            }
            .labelsHidden().frame(width: 300)
            SettingsNote("A summary sends the call's transcript to your AI provider (AI tab) with your key, for a summary, decisions and action items. It costs tokens. You can still summarize any meeting yourself in the Meetings window.")
        }
        SettingsRow("Remembered apps") {
            let remembered = MeetingCenter.choices.sorted { $0.key < $1.key }
            if remembered.isEmpty {
                Text("None yet. When you pick Always or Never for an app, it shows here.")
                    .font(.system(size: 13)).foregroundColor(SetupTheme.faint)
            } else {
                ForEach(remembered, id: \.key) { item in
                    Text("\(item.key): \(item.value == MeetingPrompt.always ? "always take notes" : "never take notes")").font(.system(size: 13))
                }
                Button("Forget Remembered Choices") { MeetingCenter.choices = [:]; model.refresh() }
            }
        }
    }

    // MARK: AI

    @ViewBuilder private var aiExtras: some View {
        SettingsRow("Daily recap") {
            Toggle("Write each day's recap automatically", isOn: model.toggle({ app.autoRecapOn }, app.toggleDailyRecap))
            SettingsNote(Journal.autoRecapInfo(AISettings.load().kind.title))
        }
        SettingsRow("AI apps") {
            Toggle("Allow AI apps to read my history (MCP)", isOn: model.toggle({ app.mcpOn }, app.toggleAIApps))
            SettingsNote("Lets Claude, ChatGPT, Cursor and other AI apps search your history on this Mac. " + ConnectAIAppsView.whatTheyGet)
            Button("Connect AI Apps…") { app.connectAIApps() }
        }
    }

    // MARK: Security

    @ViewBuilder private var security: some View {
        SettingsRow("Encryption") {
            Toggle("Encrypt recordings on disk", isOn: model.toggle({ app.encryptionOn }, app.toggleEncrypt))
            Text(app.encryptionStatus).font(.system(size: 12)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 10) {
                Button("Export Recovery Key…") { app.saveRecoveryKey() }.disabled(!app.hasRecoveryKey)
                if app.encryptionFailed > 0 {
                    Button("Stop Retrying \(app.encryptionFailed.formatted()) Files…") { app.stopRetryingFiles() }
                }
            }
            SettingsNote("Encrypts screenshots, audio and the database. The key stays in your login Keychain; keep the recovery key somewhere safe.")
        }
        SettingsRow("App Lock") {
            Toggle("Require Touch ID or password to open the timeline", isOn: model.toggle({ app.appLockOn }, app.toggleLock))
            SettingsNote("Asks before showing the timeline, search, Ask AI, Settings and other history. Recording keeps going.")
            HStack(spacing: 10) {
                Text("Lock again after").font(.system(size: 13))
                Picker("", selection: model.choice({ app.lockMinutes }, { app.lockMinutes = $0 })) {
                    ForEach(AppLock.timeoutChoices.indices, id: \.self) { i in
                        let c = AppLock.timeoutChoices[i]
                        Text(c.title + (c.minutes == 15 ? " (default)" : "")).tag(c.minutes)
                    }
                }
                .labelsHidden().frame(width: 220)
            }
            .disabled(!app.appLockOn)
        }
        SettingsRow("Local server") {
            Toggle("Local search server (127.0.0.1:\(app.serverPort))", isOn: model.toggle({ app.serverOn }, app.toggleLocalServer))
            SettingsNote("Search in the browser and let local scripts read your history through the API. Only this Mac can connect.")
        }
    }

    // MARK: Storage

    @ViewBuilder private var storage: some View {
        SettingsRow("Disk space used") {
            ForEach(Array(model.storage.enumerated()), id: \.offset) { i, line in
                Text(line).font(.system(size: 13, weight: i == 0 ? .semibold : .regular))
                    .foregroundColor(i == 0 ? .white : SetupTheme.dim)
            }
            Button("Show Data Folder") { app.showDataFolder() }
        }
        SettingsRow("Keep screen recordings") {
            keepPicker(screen: true)
        }
        SettingsRow("Keep audio recordings") {
            keepPicker(screen: false)
            SettingsNote("Older recordings are deleted every hour. A shorter time saves space, and also limits how far back you can search.")
        }
        SettingsRow("Compress") {
            Toggle(app.compressUnavailable == nil ? "Compress older recordings" : "Compress older recordings (not available on this Mac)",
                   isOn: model.toggle({ app.compressOn }, app.toggleCompression))
                .disabled(app.compressUnavailable != nil && !app.compressOn)  // can still be turned off
            SettingsNote(app.compressUnavailable.map { "This Mac has no working video encoder: \($0)" }
                         ?? "Keep screenshots older than an hour as video instead of one picture each: about 5 to 10 times less disk space. Videos already made keep playing when it is off.")
        }
        SettingsRow("Delete recordings") {
            HStack(spacing: 8) {
                Button("Last 15 Minutes…") { app.deleteRecordings("the last 15 minutes", from: Date().addingTimeInterval(-15 * 60)) }
                Button("Last Hour…") { app.deleteRecordings("the last hour", from: Date().addingTimeInterval(-3600)) }
                Button("Today…") { app.deleteRecordings("today", from: Calendar.current.startOfDay(for: Date())) }
            }
            HStack(spacing: 8) {
                Button("All Screen Recordings…") { app.deleteAllRecordings(screen: true, audio: false) }
                Button("All Audio Recordings…") { app.deleteAllRecordings(screen: false, audio: true) }
            }
            Button { app.deleteAllRecordings(screen: true, audio: true) } label: {
                Label("Delete Everything…", systemImage: "trash")
            }
            .buttonStyle(.plain).foregroundColor(SetupTheme.warn).padding(.top, 4)
        }
    }

    private func keepPicker(screen: Bool) -> some View {
        Picker("", selection: model.choice({ app.keepFor(screen: screen) }, { app.setKeepFor($0, screen: screen) })) {
            ForEach(KeepFor.allCases, id: \.self) { Text($0.title).tag($0) }
        }
        .labelsHidden().frame(width: 220)
    }

    // MARK: Shortcuts

    @ViewBuilder private var shortcuts: some View {
        SettingsHeading("Shortcuts work in every app. Click a shortcut to change it.")
        SettingsRow("Open Timeline") {
            ShortcutField(label: app.timelineShortcut.label, isDefault: app.timelineShortcut == .standard,
                          record: { app.recordTimelineShortcut(); model.refresh() },
                          restore: { app.restoreTimelineShortcut(); model.refresh() })
        }
        // More shortcuts (for example starring a moment) go here as ShortcutField rows.
    }
}

/// The Settings window's colors beyond SetupTheme.
enum SettingsLook {
    /// The selected tab's icon and title: the accent, a little lighter so it reads on the dark bar.
    static let selected = Color(red: 0.36, green: 0.58, blue: 1.0)
}

/// A bold label on the left, right-aligned, and its controls on the right (like Rewind's settings).
struct SettingsRow<Content: View>: View {
    let label: String
    let content: Content

    init(_ label: String, @ViewBuilder content: () -> Content) {
        self.label = label
        self.content = content()
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 16) {
            Text(label + ":")
                .font(.system(size: 13, weight: .bold))
                .multilineTextAlignment(.trailing)
                .frame(width: 170, alignment: .trailing)
            VStack(alignment: .leading, spacing: 7) { content }
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .toggleStyle(.checkbox)
        .font(.system(size: 13))
    }
}

/// A gray line under a control.
struct SettingsNote: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(text).font(.system(size: 11.5)).foregroundColor(SetupTheme.faint).fixedSize(horizontal: false, vertical: true)
    }
}

/// The bold line at the top of a tab.
struct SettingsHeading: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(text).font(.system(size: 13.5, weight: .semibold)).frame(maxWidth: .infinity).multilineTextAlignment(.center)
            .fixedSize(horizontal: false, vertical: true)
    }
}

/// A heading with a paragraph under it.
struct SettingsSection: View {
    let title: String, text: String
    init(_ title: String, _ text: String) { self.title = title; self.text = text }
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 16, weight: .bold))
            Text(text).font(.system(size: 13)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)
        }
    }
}

/// A shortcut as a field: click it to record a new one, or put the default back.
struct ShortcutField: View {
    let label: String
    let isDefault: Bool
    let record: () -> Void
    let restore: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Button(action: record) {
                Text(label.isEmpty ? "Record Shortcut" : label)
                    .font(.system(size: 14, weight: .medium))
                    .foregroundColor(label.isEmpty ? SetupTheme.faint : .white)
                    .frame(width: 170, height: 28)
                    .background(RoundedRectangle(cornerRadius: 7).fill(SetupTheme.card))
                    .overlay(RoundedRectangle(cornerRadius: 7).stroke(SetupTheme.stroke))
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help("Click, then press the new shortcut")
            Button("Restore Default", action: restore).disabled(isDefault)
        }
    }
}
