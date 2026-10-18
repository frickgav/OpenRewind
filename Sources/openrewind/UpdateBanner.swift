import AppKit
import SwiftUI

/// The "new version" strip at the top of the timeline. Closing it hides it until the next version.
struct UpdateBanner: View {
    @ObservedObject var updater = Updater.shared
    @Environment(\.colorScheme) private var scheme
    /// Black or white, whichever stands out on the timeline's light or dark look.
    private var ink: Color { scheme == .dark ? .white : .black }

    var body: some View {
        if updater.showBanner, let r = updater.available {
            HStack(spacing: 10) {
                Image(systemName: "arrow.down.circle.fill").foregroundColor(.accentColor)
                Text(message(r)).font(.system(size: 13))
                ForEach(buttons(r), id: \.title) { b in
                    Button(b.title, action: b.act)
                        .buttonStyle(.plain)
                        .font(.system(size: 12, weight: .semibold))
                        .padding(.horizontal, 10).padding(.vertical, 4)
                        .background(Capsule().fill(ink.opacity(scheme == .dark ? 0.16 : 0.1)))
                }
                Button { updater.dismiss() } label: { Image(systemName: "xmark") }
                    .buttonStyle(.plain).foregroundColor(ink.opacity(0.6))
                    .help("Hide until the next version")
            }
            .padding(.horizontal, 14).padding(.vertical, 8)
            .background(RoundedRectangle(cornerRadius: 8).fill(Color.accentColor.opacity(0.2)))
        }
    }

    private func message(_ r: Updater.Release) -> String {
        switch updater.state {
        case .downloading(let p): return "Downloading OpenRewind \(r.version)… \(Int(p * 100))%"
        case .ready: return "OpenRewind \(r.version) is downloaded and ready."
        case .installing: return "Installing OpenRewind \(r.version)…"
        case .failed(let why): return "Could not update to \(r.version): \(why)."
        case .idle: return "OpenRewind \(r.version) is out. You have \(updater.current)."
        }
    }

    private struct BannerButton {
        let title: String
        let act: () -> Void
    }

    private func buttons(_ r: Updater.Release) -> [BannerButton] {
        let u = updater
        let update = { u.update() }
        let notes = BannerButton(title: "What's New") { _ = NSWorkspace.shared.open(r.page) }
        switch u.state {
        case .downloading, .installing: return []
        case .ready: return [BannerButton(title: "Restart to Update", act: update), notes]
        case .failed: return [BannerButton(title: "Try Again", act: update),
                              BannerButton(title: "Download") { _ = NSWorkspace.shared.open(r.asset) }]
        case .idle: return [BannerButton(title: u.canInstall ? "Update" : "Download", act: update), notes]
        }
    }
}
