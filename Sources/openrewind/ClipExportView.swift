import AppKit
import Combine
import SwiftUI

/// Date and time down to the second, with steppers (SwiftUI's DatePicker has no seconds on the Mac).
struct SecondsDatePicker: NSViewRepresentable {
    @Binding var date: Date

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSDatePicker {
        let p = NSDatePicker()
        p.datePickerStyle = .textFieldAndStepper
        p.datePickerElements = [.yearMonthDay, .hourMinuteSecond]
        p.dateValue = date
        p.target = context.coordinator
        p.action = #selector(Coordinator.changed(_:))
        return p
    }

    func updateNSView(_ p: NSDatePicker, context: Context) {
        context.coordinator.parent = self
        if p.dateValue != date { p.dateValue = date }
    }

    final class Coordinator: NSObject {
        var parent: SecondsDatePicker
        init(_ parent: SecondsDatePicker) { self.parent = parent }
        @objc func changed(_ sender: NSDatePicker) { parent.date = sender.dateValue }
    }
}

/// Export Clip…: pick the range, MP4 or GIF and a speed. Before exporting it says how long the clip is, how many
/// frames, and roughly how big and how long it takes; while exporting it shows the frame it is on, the time so far
/// and the time left, with Cancel. A card over the timeline, like the keyboard shortcuts.
struct ExportClipCard: View {
    @Environment(\.colorScheme) private var scheme
    private var theme: Theme { Theme(scheme) }
    @ObservedObject var model: TimelineModel
    @State private var start: Date
    @State private var end: Date
    @State private var format: ClipFormat = .mp4
    @State private var speed: Double = 1
    /// The clip for the current choices, or why there is none; nil while counting.
    @State private var info: ClipInfo?
    /// Bumped on every change, so an older count that finishes late is ignored.
    @State private var asking = 0
    @State private var exporter: ClipExporter?
    @State private var cancelling = false
    @State private var doneFrames = 0
    @State private var totalFrames = 0
    @State private var startedAt = Date()
    @State private var now = Date()
    @State private var status: String?
    @State private var saved: URL?
    private let tick = Timer.publish(every: 0.5, on: .main, in: .common).autoconnect()

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
                        SecondsDatePicker(date: $start)
                    }
                    GridRow {
                        label("To")
                        SecondsDatePicker(date: $end)
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
                if running { progressView }
                if let status, !running {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(status).font(.system(size: 12, weight: .medium))
                            .fixedSize(horizontal: false, vertical: true)
                        if let saved {
                            HStack(spacing: 14) {
                                Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([saved]) }
                                Button("Open") { NSWorkspace.shared.open(saved) }
                            }
                            .buttonStyle(.link).font(.system(size: 12))
                        }
                    }
                }
                HStack {
                    Spacer()
                    if running {
                        Button("Cancel") { cancel() }.keyboardShortcut(.cancelAction).disabled(cancelling)
                    } else {
                        Button("Close") { close() }
                        Button("Export…") { export() }.keyboardShortcut(.defaultAction).disabled(info?.plan == nil)
                    }
                }
            }
            .padding(24)
            .frame(width: 460)
            .background(RoundedRectangle(cornerRadius: 16).fill(theme.card))
            .overlay(RoundedRectangle(cornerRadius: 16).stroke(theme.stroke))
            .shadow(color: theme.shadow, radius: 30)
        }
        .onAppear(perform: update)
        .onChange(of: start) { _ in update() }
        .onChange(of: end) { _ in update() }
        .onChange(of: format) { _ in update() }
        .onChange(of: speed) { _ in update() }
        .onReceive(tick) { if running { now = $0 } }
        .onDisappear { exporter?.cancel() }
    }

    /// "Preparing…" until the first frame, then a bar with the frame it is on, the time so far and the time left.
    @ViewBuilder private var progressView: some View {
        let elapsed = max(0, now.timeIntervalSince(startedAt))
        VStack(alignment: .leading, spacing: 6) {
            if cancelling {
                ProgressView().progressViewStyle(.linear)
                Text("Cancelling…").font(.system(size: 12, weight: .medium))
            } else if totalFrames == 0 || doneFrames == 0 {
                ProgressView().progressViewStyle(.linear)
                Text(totalFrames == 0 ? "Preparing… \(ClipPlan.length(elapsed)) elapsed"
                                      : ClipPlan.progressText(done: 0, total: totalFrames, elapsed: elapsed))
                    .font(.system(size: 12, weight: .medium)).monospacedDigit()
            } else {
                ProgressView(value: Double(doneFrames), total: Double(max(1, totalFrames))).progressViewStyle(.linear)
                Text(ClipPlan.progressText(done: doneFrames, total: totalFrames, elapsed: elapsed))
                    .font(.system(size: 12, weight: .medium)).monospacedDigit()
            }
        }
    }

    private func label(_ s: String) -> some View {
        Text(s).font(.system(size: 12, weight: .medium)).foregroundColor(theme.dim)
    }

    /// What the clip will be, or why there is none.
    private var summary: String {
        guard let info else { return "Counting the frames…" }
        guard let plan = info.plan, let size = info.size else { return info.problem ?? "" }
        var s = ClipPlan.summary(plan, format: format, width: size.width, height: size.height)
        if let app = model.appFilter { s += "\nOnly \(app), as the timeline shows it." }
        if plan.blocked > 0 {
            s += "\n\(plan.blocked) screenshot\(plan.blocked == 1 ? " is" : "s are") from blocked apps or sites and left out."
        }
        return s
    }

    /// Count again a moment after the last change, off the main thread, so stepping through times stays quick.
    private func update() {
        guard !running else { return }
        asking += 1
        let n = asking
        let s = start, e = end, sp = speed, f = format, db = model.db, app = model.appFilter
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) {
            guard n == asking else { return }
            DispatchQueue.global(qos: .userInitiated).async {
                let result = TimelineModel.clipPlan(db: db, appFilter: app, s, e, speed: sp, format: f)
                DispatchQueue.main.async {
                    if n == asking && !running { info = result }
                }
            }
        }
    }

    private func close() {
        if running { cancel(); return }
        model.exportClip = nil
    }

    private func cancel() {
        guard let exporter else { return }
        exporter.cancel()
        cancelling = true
    }

    private func export() {
        let f = format
        let panel = NSSavePanel()
        panel.title = "Export Clip"
        panel.allowedContentTypes = [f.type]
        panel.nameFieldStringValue = ClipPlan.fileName(start, ext: f.rawValue)
        panel.directoryURL = FileManager.default.urls(for: f == .gif ? .picturesDirectory : .moviesDirectory, in: .userDomainMask).first
        guard Dialogs.run(panel) == .OK, let url = panel.url else { return }
        // Something on screen right away, before anything is read.
        let job = ClipExporter()
        exporter = job
        cancelling = false
        doneFrames = 0
        totalFrames = 0
        startedAt = Date()
        now = startedAt
        saved = nil
        status = nil
        // Counted again, so a block list changed since is applied.
        let s = start, e = end, sp = speed, db = model.db, app = model.appFilter
        DispatchQueue.global(qos: .userInitiated).async {
            let result = TimelineModel.clipPlan(db: db, appFilter: app, s, e, speed: sp, format: f)
            DispatchQueue.main.async {
                guard exporter === job else { return }
                if job.isCancelled { finished(CancellationError(), url: url); return }
                guard let plan = result.plan, let size = result.size else {
                    finished(ClipExporter.Failure(message: result.problem ?? "No screenshots in that range."), url: url)
                    return
                }
                totalFrames = plan.frames.count
                job.export(plan, db: db, format: f, size: size, to: url, progress: { doneFrames = $0 }) { finished($0, url: url) }
            }
        }
    }

    private func finished(_ error: Error?, url: URL) {
        let took = ClipPlan.length(Date().timeIntervalSince(startedAt))
        exporter = nil
        cancelling = false
        if error == nil {
            let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int) ?? 0
            status = "Saved \(url.lastPathComponent) (\(ClipPlan.fileSize(size))) in \(took)."
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
