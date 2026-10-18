import AppKit
import SwiftUI

struct TimelineView: View {
    @ObservedObject var model: TimelineModel
    var close: () -> Void

    var body: some View {
        ZStack {
            Color.black.opacity(0.9).ignoresSafeArea()
            VStack(spacing: 14) {
                header
                screenshot
                footer
            }
            .padding(.horizontal, 28)
            .padding(.vertical, 20)
        }
        .foregroundColor(.white)
    }

    // MARK: header

    private var header: some View {
        HStack(alignment: .center, spacing: 12) {
            if let f = model.current {
                Circle().fill(Color(TimelineModel.color(for: f.appName))).frame(width: 10, height: 10)
                VStack(alignment: .leading, spacing: 2) {
                    Text(f.appName.isEmpty ? "Unknown app" : f.appName).font(.system(size: 15, weight: .semibold))
                    Text(f.windowName).font(.system(size: 12)).foregroundColor(.white.opacity(0.6)).lineLimit(1)
                }
            }
            Spacer()
            VStack(spacing: 2) {
                Text(model.playhead, format: .dateTime.hour().minute().second())
                    .font(.system(size: 26, weight: .semibold, design: .rounded)).monospacedDigit()
                Text(relative(model.playhead)).font(.system(size: 12)).foregroundColor(.white.opacity(0.6))
            }
            Spacer()
            Button(action: close) {
                Text("esc").font(.system(size: 12, weight: .medium))
                    .padding(.horizontal, 10).padding(.vertical, 5)
                    .background(Capsule().stroke(Color.white.opacity(0.3)))
            }
            .buttonStyle(.plain)
        }
        .frame(maxWidth: .infinity)
    }

    // MARK: screenshot

    private var screenshot: some View {
        GeometryReader { geo in
            ZStack {
                if let img = model.image {
                    Image(nsImage: img)
                        .resizable()
                        .interpolation(.high)
                        .aspectRatio(contentMode: .fit)
                        .clipShape(RoundedRectangle(cornerRadius: 10))
                        .shadow(color: .black.opacity(0.6), radius: 24)
                } else {
                    VStack(spacing: 8) {
                        Image(systemName: "rectangle.dashed").font(.system(size: 40)).opacity(0.4)
                        Text(model.frames.isEmpty ? "Nothing recorded yet. Turn on screen recording in the menu bar."
                                                  : "Screenshot not found for this moment.")
                            .foregroundColor(.white.opacity(0.6))
                    }
                }
            }
            .frame(width: geo.size.width, height: geo.size.height)
        }
    }

    // MARK: footer

    private var footer: some View {
        VStack(spacing: 10) {
            if let c = model.caption {
                Text("“\(c)”")
                    .font(.system(size: 15))
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 14).padding(.vertical, 8)
                    .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.08)))
                    .frame(maxWidth: 900)
            }
            HStack(spacing: 8) {
                ForEach(model.audioHere, id: \.id) { a in
                    let on = model.playingChunk == a.id
                    Button { on ? model.stopAudio() : model.play(a) } label: {
                        Label(a.isInput ? "Microphone" : "System audio", systemImage: on ? "pause.fill" : "play.fill")
                            .font(.system(size: 12, weight: .medium))
                            .padding(.horizontal, 12).padding(.vertical, 6)
                            .background(Capsule().fill(on ? Color.accentColor : Color.white.opacity(0.12)))
                    }
                    .buttonStyle(.plain)
                }
                if model.audioHere.isEmpty {
                    Text("No audio at this moment").font(.system(size: 12)).foregroundColor(.white.opacity(0.45))
                }
                Spacer()
                Button("Now") { model.jumpToNow() }.buttonStyle(.plain).font(.system(size: 12, weight: .medium))
                Text("Zoom \(spanLabel)").font(.system(size: 12)).foregroundColor(.white.opacity(0.6))
                Button { zoom(0.5) } label: { Image(systemName: "plus.magnifyingglass") }.buttonStyle(.plain)
                Button { zoom(2) } label: { Image(systemName: "minus.magnifyingglass") }.buttonStyle(.plain)
            }
            TimelineBar(model: model).frame(height: 62)
            Text("Scroll or ← → to move  ·  ⇧ for 1 minute  ·  drag the bar  ·  space plays audio  ·  + − zoom  ·  N now  ·  esc close")
                .font(.system(size: 11)).foregroundColor(.white.opacity(0.4))
        }
    }

    private func zoom(_ k: Double) { model.span = min(max(model.span * k, 60), 12 * 3600) }

    private var spanLabel: String {
        model.span >= 3600 ? "\(Int(model.span / 3600))h" : "\(Int(model.span / 60))m"
    }

    private func relative(_ d: Date) -> String {
        let s = Date().timeIntervalSince(d)
        if s < 60 { return "just now" }
        let day = d.formatted(.dateTime.weekday(.wide).month().day())
        if s < 3600 { return "\(Int(s / 60)) min ago · \(day)" }
        return "\(Int(s / 3600)) h \(Int(s.truncatingRemainder(dividingBy: 3600) / 60)) min ago · \(day)"
    }
}

/// Horizontal strip: a tick per frame (colored by app), audio below, playhead in the middle.
struct TimelineBar: View {
    @ObservedObject var model: TimelineModel
    @State private var dragStart: Date?

    var body: some View {
        GeometryReader { geo in
            let w = geo.size.width
            let start = model.playhead.addingTimeInterval(-model.span / 2)
            let end = model.playhead.addingTimeInterval(model.span / 2)
            let x = { (d: Date) -> CGFloat in CGFloat(d.timeIntervalSince(start) / model.span) * w }
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.06))
                Canvas { ctx, size in
                    // Future (after now) is shaded.
                    let nowX = x(Date())
                    if nowX < size.width {
                        ctx.fill(Path(CGRect(x: max(0, nowX), y: 0, width: size.width - max(0, nowX), height: size.height)),
                                 with: .color(.black.opacity(0.35)))
                    }
                    let lo = model.frameIndex(at: start)
                    let hi = model.frameIndex(at: end)
                    if !model.frames.isEmpty {
                        for i in lo...hi {
                            let f = model.frames[i]
                            let fx = x(f.time)
                            guard fx >= 0, fx <= size.width else { continue }
                            ctx.fill(Path(CGRect(x: fx - 1, y: 8, width: 2, height: 26)),
                                     with: .color(Color(TimelineModel.color(for: f.appName)).opacity(0.85)))
                        }
                    }
                    for a in model.audio where a.end > start && a.start < end {
                        let ax = max(0, x(a.start)), bx = min(size.width, x(a.end))
                        let y: CGFloat = a.isInput ? 40 : 48
                        ctx.fill(Path(roundedRect: CGRect(x: ax, y: y, width: max(2, bx - ax), height: 6), cornerRadius: 3),
                                 with: .color(a.isInput ? Color.orange.opacity(0.8) : Color.green.opacity(0.8)))
                    }
                    // Playhead.
                    ctx.fill(Path(CGRect(x: size.width / 2 - 1, y: 0, width: 2, height: size.height)), with: .color(.white))
                }
                HStack {
                    Text(start, format: .dateTime.hour().minute())
                    Spacer()
                    Text(end, format: .dateTime.hour().minute())
                }
                .font(.system(size: 10)).foregroundColor(.white.opacity(0.45))
                .padding(.horizontal, 6).offset(y: 46)
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { g in
                        if dragStart == nil {
                            dragStart = model.playhead
                            // A click (no movement yet) jumps to where you clicked.
                            let t = start.addingTimeInterval(Double(g.startLocation.x / w) * model.span)
                            if abs(g.translation.width) < 2 { dragStart = t; model.jump(to: t); return }
                        }
                        if let d = dragStart {
                            model.jump(to: d.addingTimeInterval(-Double(g.translation.width / w) * model.span))
                        }
                    }
                    .onEnded { _ in dragStart = nil }
            )
        }
    }
}
