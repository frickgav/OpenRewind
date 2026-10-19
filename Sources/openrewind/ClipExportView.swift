import AppKit
import SwiftUI

/// Export Clip…: pick the range, MP4 or GIF and a speed; writes the file with progress and Cancel.
/// A card over the timeline, like the keyboard shortcuts.
struct ExportClipCard: View {
    @Environment(\.colorScheme) private var scheme
    private var theme: Theme { Theme(scheme) }
    @ObservedObject var model: TimelineModel
    @State private var start: Date
    @State private var end: Date
    @State private var format: ClipFormat = .mp4
    @State private var speed: Double = 1
    @State private var plan: ClipPlan.Result?
    @State private var problem: String?
    @State private var exporter: ClipExporter?
    @State private var progress: Double = 0
    @State private var status: String?
    @State private var saved: URL?

    init(model: TimelineModel, range: ClipRange) {
        self.model = model
        _start = State(initialValue: range.start)
        _end = State(initialValue: range.end)
    }

    private var running: Bool { exporter != nil }

    var body: some View {
        ZStack {
            theme.scrim.ignoresSafeArea().onTapGesture { if !running { close() } }
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    Text("Export Clip").font(.system(size: 17, weight: .semibold))
                    Spacer()
                    IconButton(symbol: "xmark", help: "Close (esc)") { close() }
                }
                Grid(alignment: .leading, horizontalSpacing: 14, verticalSpacing: 10) {
                    GridRow {
                        label("From")
                        DatePicker("", selection: $start, displayedComponents: [.date, .hourAndMinute])
                            .labelsHidden().datePickerStyle(.stepperField)
                    }
                    GridRow {
                        label("To")
                        DatePicker("", selection: $end, displayedComponents: [.date, .hourAndMinute])
                            .labelsHidden().datePickerStyle(.stepperField)
                    }
                    GridRow {
                        label("Format")
                        Picker("", selection: $format) {
                            ForEach(ClipFormat.allCases) { Text($0.title).tag($0) }
                        }
                        .labelsHidden().pickerStyle(.segmented).frame(width: 240)
                    }
                    GridRow {
                        label("Speed")
                        Picker("", selection: $speed) {
                            ForEach(ClipPlan.speeds, id: \.self) { Text(ClipPlan.speedLabel($0)).tag($0) }
                        }
                        .labelsHidden().frame(width: 240)
                    }
                }
                .disabled(running)
                Text(summary).font(.system(size: 12)).foregroundColor(theme.dim)
                    .fixedSize(horizontal: false, vertical: true)
                if running {
                    ProgressView(value: progress).progressViewStyle(.linear)
                }
                if let status {
                    HStack(spacing: 10) {
                        Text(status).font(.system(size: 12, weight: .medium))
                        if let saved {
                            Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([saved]) }
                                .buttonStyle(.link).font(.system(size: 12))
                        }
                    }
                }
                HStack {
                    Spacer()
                    if running {
                        Button("Cancel") { exporter?.cancel() }.keyboardShortcut(.cancelAction)
                    } else {
                        Button("Close") { close() }
                        Button("Export…") { export() }.keyboardShortcut(.defaultAction).disabled(plan == nil)
                    }
                }
            }
            .padding(24)
            .frame(width: 440)
            .background(RoundedRectangle(cornerRadius: 16).fill(theme.card))
            .overlay(RoundedRectangle(cornerRadius: 16).stroke(theme.stroke))
            .shadow(color: theme.shadow, radius: 30)
        }
        .onAppear(perform: update)
        .onChange(of: start) { _ in update() }
        .onChange(of: end) { _ in update() }
        .onChange(of: format) { _ in update() }
        .onChange(of: speed) { _ in update() }
        .onDisappear { exporter?.cancel() }
    }

    private func label(_ s: String) -> some View {
        Text(s).font(.system(size: 12, weight: .medium)).foregroundColor(theme.dim)
    }

    /// What the clip will be, or why there is none.
    private var summary: String {
        guard let plan else { return problem ?? "" }
        var s = "\(plan.frames.count) picture\(plan.frames.count == 1 ? "" : "s") · \(ClipPlan.length(plan.realSeconds)) of screen time → a \(ClipPlan.length(plan.duration)) clip"
        if let app = model.appFilter { s += "\nOnly \(app), as the timeline shows it." }
        if plan.blocked > 0 {
            s += "\n\(plan.blocked) screenshot\(plan.blocked == 1 ? " is" : "s are") from blocked apps or sites and left out."
        }
        return s
    }

    private func update() {
        guard !running else { return }
        (plan, problem) = model.clipPlan(start, end, speed: speed, format: format)
    }

    private func close() {
        if running { exporter?.cancel(); return }
        model.exportClip = nil
    }

    private func export() {
        update()
        guard let plan else { return }
        let panel = NSSavePanel()
        panel.title = "Export Clip"
        panel.allowedContentTypes = [format.type]
        panel.nameFieldStringValue = ClipPlan.fileName(start, ext: format.rawValue)
        panel.directoryURL = FileManager.default.urls(for: format == .gif ? .picturesDirectory : .moviesDirectory, in: .userDomainMask).first
        // Above the timeline, which floats over everything.
        panel.level = NSWindow.Level(rawValue: NSWindow.Level.screenSaver.rawValue + 1)
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let job = ClipExporter()
        exporter = job
        progress = 0
        saved = nil
        status = "Exporting…"
        job.export(plan, format: format, to: url, progress: { progress = $0 }) { error in
            exporter = nil
            if error == nil {
                let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int) ?? 0
                status = "Saved \(url.lastPathComponent) (\(String(format: "%.1f", Double(size) / 1_048_576)) MB)."
                saved = url
            } else if error is CancellationError {
                status = "Cancelled. Nothing was saved."
            } else {
                log("clip export failed: \(error!.localizedDescription)")
                status = "Could not export the clip: \(error!.localizedDescription)"
            }
            update()
        }
    }
}
