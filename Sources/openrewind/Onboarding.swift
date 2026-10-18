import AppKit
import AVFoundation
import ServiceManagement
import SwiftUI

/// First-run setup: welcome, permissions, what to block, and a short animated demo.
/// macOS may quit and reopen the app after Screen Recording is allowed, so the step is saved
/// and setup continues there on the next launch.
final class OnboardingModel: ObservableObject {
    static let stepKey = "onboardingStep"
    @Published var step: Int { didSet { UserDefaults.standard.set(step, forKey: Self.stepKey) } }
    @Published var screenOK = Capture.hasScreenPermission
    @Published var axOK = Accessibility.isTrusted
    @Published var micStatus = AVCaptureDevice.authorizationStatus(for: .audio)
    @Published var launchAtLogin = true

    // Block choices.
    @Published var passwordManagers: Bool
    @Published var moneyApps: Bool
    @Published var privateWindows: Bool
    @Published var bankSites: Bool
    @Published var picked: Set<String>
    var found: [FoundApp] = []

    /// Called once at the end; true when the user wants the guided tour in the real timeline.
    var finished: (Bool) -> Void = { _ in }
    private var done = false
    private var timer: Timer?

    init() {
        let store = BlocklistStore.shared, list = store.current
        let first = !store.onboardingDone
        step = first ? min(UserDefaults.standard.integer(forKey: Self.stepKey), 3) : 0
        let ids = Set(list.apps.map { $0.id.isEmpty ? $0.name.lowercased() : $0.id })
        func has(_ c: BlockCatalog.Category) -> Bool { BlockCatalog.apps(c).allSatisfy { ids.contains($0.id.isEmpty ? $0.name.lowercased() : $0.id) } }
        passwordManagers = first || has(.passwordManager)
        moneyApps = first || has(.finance)
        privateWindows = list.privateWindows
        bankSites = first || BlockCatalog.bankingSites.allSatisfy { w in list.keywords.contains { $0.caseInsensitiveCompare(w) == .orderedSame } }
        let catalog = Set(BlockCatalog.mac.map { $0.id.isEmpty ? $0.name.lowercased() : $0.id })
        picked = ids.subtracting(catalog)
        launchAtLogin = SMAppService.mainApp.status == .enabled || first
        // Permissions are granted in System Settings; watch for them.
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            guard let self else { return }
            let s = Capture.hasScreenPermission, a = Accessibility.isTrusted, m = AVCaptureDevice.authorizationStatus(for: .audio)
            if s != self.screenOK { self.screenOK = s }
            if a != self.axOK { self.axOK = a }
            if m != self.micStatus { self.micStatus = m }
        }
    }

    deinit { timer?.invalidate() }

    func allowScreen() {
        Capture.requestScreenPermission()
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
    }

    func allowAccessibility() {
        Accessibility.promptForTrust()
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }

    func allowMic() {
        if micStatus == .notDetermined {
            AVCaptureDevice.requestAccess(for: .audio) { _ in
                DispatchQueue.main.async { self.micStatus = AVCaptureDevice.authorizationStatus(for: .audio) }
            }
        } else {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone")!)
        }
    }

    /// Save the choices. Closing the window early keeps whatever is picked so far.
    func finish(tour: Bool) {
        guard !done else { return }
        done = true
        timer?.invalidate()
        let store = BlocklistStore.shared, old = store.current
        var apps: [Blocklist.App] = []
        if passwordManagers { apps += BlockCatalog.apps(.passwordManager) }
        if moneyApps { apps += BlockCatalog.apps(.finance) }
        // Apps picked from the list, plus earlier picks the list did not show (not installed any more).
        let catalog = Set(BlockCatalog.mac.map { $0.id.isEmpty ? $0.name.lowercased() : $0.id })
        let listed = Set(found.map(\.id))
        for a in old.apps {
            let id = a.id.isEmpty ? a.name.lowercased() : a.id
            if !catalog.contains(id) && !listed.contains(id) { apps.append(a) }
        }
        apps += found.filter { picked.contains($0.id) }.map { Blocklist.App(id: $0.bundleID, name: $0.name) }
        var seen = Set<String>()
        apps = apps.filter { seen.insert(($0.id.isEmpty ? $0.name : $0.id).lowercased()).inserted }

        var words = old.keywords.filter { w in !BlockCatalog.bankingSites.contains { $0.caseInsensitiveCompare(w) == .orderedSame } }
        if bankSites { words += BlockCatalog.bankingSites }
        store.save(Blocklist(apps: apps, keywords: words, privateWindows: privateWindows, extraNames: old.extraNames))
        store.onboardingDone = true
        UserDefaults.standard.removeObject(forKey: Self.stepKey)
        do {
            let enabled = SMAppService.mainApp.status == .enabled
            if launchAtLogin && !enabled { try SMAppService.mainApp.register() }
            if !launchAtLogin && enabled { try SMAppService.mainApp.unregister() }
        } catch { log("launch at login failed: \(error.localizedDescription)") }
        finished(tour)
    }
}

enum Onboarding {
    /// The timeline shortcut as the user set it in Settings.
    static var hotkey: String { Shortcut.load(.standard).label }

    static func show(onDone: @escaping (Bool) -> Void) {
        let model = OnboardingModel()
        model.finished = { tour in
            SetupWindows.shared.close("onboarding")
            onDone(tour)
        }
        SetupWindows.shared.show("onboarding", title: "Welcome to OpenRewind", size: NSSize(width: 860, height: 640),
                                 onClose: { model.finish(tour: false) }) {
            OnboardingView(model: model)
        }
    }
}

struct OnboardingView: View {
    @ObservedObject var model: OnboardingModel

    var body: some View {
        VStack(spacing: 0) {
            Group {
                switch model.step {
                case 0: welcome
                case 1: permissions
                case 2: block
                default: tour
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            .padding(.horizontal, 40).padding(.top, 36)
            .transition(.opacity)
            footer
        }
        .background(SetupTheme.bg)
        .foregroundColor(.white)
        .preferredColorScheme(.dark)
        .animation(.easeInOut(duration: 0.2), value: model.step)
    }

    private var footer: some View {
        HStack {
            if model.step > 0 { Button("Back") { model.step -= 1 }.buttonStyle(PillButtonStyle()) }
            Spacer()
            HStack(spacing: 8) {
                ForEach(0..<4) { i in
                    Capsule().fill(i == model.step ? SetupTheme.accent : SetupTheme.stroke)
                        .frame(width: i == model.step ? 24 : 8, height: 8)
                }
            }
            Spacer()
            if model.step == 3 {
                Button("Finish") { model.finish(tour: false) }.buttonStyle(PillButtonStyle())
                Button("Try it now") { model.finish(tour: true) }.buttonStyle(PillButtonStyle(primary: true))
            } else {
                Button(model.step == 0 ? "Get started" : "Continue") { model.step += 1 }
                    .buttonStyle(PillButtonStyle(primary: true))
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(.horizontal, 40).padding(.vertical, 22)
    }

    // MARK: pages

    private var welcome: some View {
        VStack(spacing: 14) {
            Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 104, height: 104)
            Text("Welcome to OpenRewind").font(.system(size: 32, weight: .bold))
            Text("Rewind anything you've seen or heard on this Mac, and search it in a second. Everything stays on your computer.")
                .font(.system(size: 15)).foregroundColor(SetupTheme.dim).multilineTextAlignment(.center).frame(maxWidth: 560)
            HStack(spacing: 14) {
                feature("backward.fill", "Rewind", "Press \(Onboarding.hotkey) to scroll back through your day.")
                feature("magnifyingglass", "Search", "Find any word you saw on screen or heard in a meeting.")
                feature("lock.fill", "Private", "Nothing is uploaded. You choose what it never sees.")
            }
            .padding(.top, 22)
        }
    }

    private func feature(_ symbol: String, _ title: String, _ text: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Image(systemName: symbol).font(.system(size: 22)).foregroundColor(SetupTheme.accent)
            Text(title).font(.system(size: 16, weight: .semibold)).padding(.top, 8)
            Text(text).font(.system(size: 13)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(18)
        .frame(maxWidth: .infinity, minHeight: 150, alignment: .topLeading)
        .background(RoundedRectangle(cornerRadius: 14).fill(SetupTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(SetupTheme.stroke))
    }

    private var permissions: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Allow access").font(.system(size: 28, weight: .bold))
            Text("macOS asks you to allow these in System Settings. Only Screen Recording is required.")
                .font(.system(size: 15)).foregroundColor(SetupTheme.dim)
            VStack(spacing: 10) {
                permission("rectangle.dashed.badge.record", "Screen Recording", "Required. Lets OpenRewind take a picture of your screen every few seconds.",
                           ok: model.screenOK, action: model.allowScreen)
                permission("accessibility", "Accessibility", "Recommended. Reads text exactly, instead of guessing it from pixels.",
                           ok: model.axOK, action: model.allowAccessibility)
                permission("mic", "Microphone", "Optional. Turns your meetings and calls into text you can search.",
                           ok: model.micStatus == .authorized, action: model.allowMic)
                CheckRowView(emoji: "🚀", title: "Open OpenRewind when you log in", detail: "So your timeline has no gaps after a restart.",
                             on: $model.launchAtLogin)
            }
            .padding(.top, 14)
            if !model.screenOK {
                Label("After you allow Screen Recording, macOS may ask to quit and reopen OpenRewind. Setup picks up right here.",
                      systemImage: "info.circle")
                    .font(.system(size: 12)).foregroundColor(SetupTheme.faint).padding(.top, 6)
            }
        }
    }

    private func permission(_ symbol: String, _ title: String, _ text: String, ok: Bool, action: @escaping () -> Void) -> some View {
        HStack(spacing: 14) {
            Image(systemName: symbol).font(.system(size: 20)).frame(width: 32).foregroundColor(SetupTheme.accent)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.system(size: 15, weight: .semibold))
                Text(text).font(.system(size: 12.5)).foregroundColor(SetupTheme.dim)
            }
            Spacer()
            if ok {
                Label("Allowed", systemImage: "checkmark.circle.fill").font(.system(size: 13, weight: .semibold)).foregroundColor(SetupTheme.good)
            } else {
                Button("Allow…", action: action).buttonStyle(PillButtonStyle(primary: true))
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 14)
        .background(RoundedRectangle(cornerRadius: 12).fill(SetupTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(SetupTheme.stroke))
    }

    private var block: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("What should OpenRewind never see?").font(.system(size: 28, weight: .bold))
            Text("Anything you pick is blurred in every screenshot and its text is never saved. Change it anytime in Settings → Blocked Apps.")
                .font(.system(size: 15)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)
            HStack(alignment: .top, spacing: 20) {
                VStack(spacing: 8) {
                    CheckRowView(emoji: "🔑", title: "Password managers", detail: "1Password, Bitwarden, Passwords, Keychain and more", on: $model.passwordManagers)
                    CheckRowView(emoji: "💰", title: "Money and crypto apps", detail: "Quicken, Banktivity, Ledger Live, Exodus", on: $model.moneyApps)
                    CheckRowView(emoji: "🕶", title: "Private browsing windows", detail: "Incognito, InPrivate and private windows", on: $model.privateWindows)
                    CheckRowView(emoji: "🏦", title: "Bank and payment websites", detail: "Chase, PayPal, Bank of America, Coinbase and more", on: $model.bankSites)
                }
                .frame(maxWidth: .infinity)
                VStack(alignment: .leading, spacing: 6) {
                    Text("Other apps on this Mac").font(.system(size: 13, weight: .semibold)).foregroundColor(SetupTheme.dim)
                    AppPickerList(selected: $model.picked,
                                  hide: Set(BlockCatalog.mac.flatMap { [$0.id, $0.name.lowercased()] }.filter { !$0.isEmpty }),
                                  loaded: { model.found = $0 })
                }
                .frame(maxWidth: .infinity)
            }
            .padding(.top, 12)
        }
    }

    private var tour: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Here's how it works").font(.system(size: 28, weight: .bold))
            Text("Press \(Onboarding.hotkey) anytime. Scroll to go back, type to search. Click Try it now for a quick guided tour of the real thing.")
                .font(.system(size: 15)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)
            DemoView(hotkey: ["⌥", "⌘", "T"]).padding(.top, 10)
        }
    }
}
