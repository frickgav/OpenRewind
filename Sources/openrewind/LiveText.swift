import AppKit
import SwiftUI
import VisionKit

/// Live Text on the shown screenshot: drag across words to select them and ⌘C copies them, like in Preview.
/// VisionKit reads the words on this Mac once scrolling settles; nothing leaves the computer.
/// The Windows app does the same with Windows OCR (FrameActions.cs).
struct LiveTextOverlay: NSViewRepresentable {
    let model: TimelineModel
    let frameId: Int64
    let image: NSImage

    static var isSupported: Bool { ImageAnalyzer.isSupported }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> ImageAnalysisOverlayView {
        let v = ImageAnalysisOverlayView()
        v.preferredInteractionTypes = .textSelection
        // No Live Text button in the corner: selecting works without it.
        v.isSupplementaryInterfaceHidden = true
        v.delegate = context.coordinator
        return v
    }

    func updateNSView(_ v: ImageAnalysisOverlayView, context: Context) {
        model.liveText = v
        context.coordinator.show(image, id: frameId, on: v)
    }

    static func dismantleNSView(_ v: ImageAnalysisOverlayView, coordinator: Coordinator) {
        coordinator.cancel()
        v.analysis = nil
    }

    @MainActor final class Coordinator: NSObject, ImageAnalysisOverlayViewDelegate {
        private let analyzer = ImageAnalyzer()
        private var shownId: Int64?
        private var task: Task<Void, Never>?
        private var cache: [Int64: ImageAnalysis] = [:]
        private var order: [Int64] = []

        /// Read the words of a screenshot (or use what was read before) and put them on the overlay.
        func show(_ image: NSImage, id: Int64, on view: ImageAnalysisOverlayView) {
            guard id != shownId else { return }
            shownId = id
            task?.cancel()
            if let cached = cache[id] { view.analysis = cached; return }
            view.analysis = nil
            task = Task { [weak self, weak view] in
                // Wait until scrolling settles: reading a screenshot takes a moment.
                try? await Task.sleep(nanoseconds: 350_000_000)
                guard let self, !Task.isCancelled else { return }
                let config = ImageAnalyzer.Configuration([.text])
                guard let analysis = try? await self.analyzer.analyze(image, orientation: .up, configuration: config),
                      !Task.isCancelled, self.shownId == id else { return }
                self.cache[id] = analysis
                self.order.append(id)
                if self.order.count > 16 { self.cache.removeValue(forKey: self.order.removeFirst()) }
                view?.analysis = analysis
            }
        }

        func cancel() {
            task?.cancel()
            task = nil
            shownId = nil
        }

        /// The overlay covers exactly the screenshot.
        nonisolated func contentsRect(for overlayView: ImageAnalysisOverlayView) -> CGRect {
            CGRect(x: 0, y: 0, width: 1, height: 1)
        }

        /// The right-click menu keeps only Copy and Select All. Look Up, Translate, Search With Google, Share and
        /// Services would send the text off this Mac.
        nonisolated func overlayView(_ overlayView: ImageAnalysisOverlayView, updatedMenuFor menu: NSMenu,
                                     for event: NSEvent, at point: CGPoint) -> NSMenu {
            MainActor.assumeIsolated {
                let keep: Set<Selector> = [#selector(NSText.copy(_:)), #selector(NSText.selectAll(_:))]
                for item in menu.items.reversed() where item.action.map({ !keep.contains($0) }) ?? true {
                    menu.removeItem(item)
                }
            }
            return menu
        }
    }
}
