import AppKit
import CoreGraphics
import CoreText

/// Hides blocked apps and windows in a screenshot before anything reads or saves it.
/// Two layers: ScreenCaptureKit leaves blocked windows out of the capture, then the parts of the
/// screen they cover are blurred and labeled, so the timeline shows something was hidden there.
/// Which parts: back to front, a blocked window adds its rectangle, a normal window on top takes it away.
enum Redaction {
    struct Window {
        var id: CGWindowID
        /// Global coordinates, points, top-left origin (same as CGDisplayBounds).
        var frame: CGRect
        var blocked: Bool
        /// Normal opaque windows hide what is under them. Floating, see-through ones do not count.
        var occludes: Bool
        var label: String
    }

    /// Windows on screen now, back to front. Our own windows are left out: they are never captured either.
    /// `sites` knows which browser windows show a blocked site.
    static func windows(_ list: Blocklist, sites: SiteWindows? = nil) -> [Window] {
        guard let info = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return [] }
        let me = ProcessInfo.processInfo.processIdentifier
        var apps: [pid_t: (bundle: String, path: String)] = [:]
        var out: [Window] = []
        for w in info {
            guard let pid = w[kCGWindowOwnerPID as String] as? pid_t, pid != me,
                  let b = w[kCGWindowBounds as String] as? NSDictionary,
                  let frame = CGRect(dictionaryRepresentation: b as CFDictionary), frame.width > 1, frame.height > 1 else { continue }
            let alpha = w[kCGWindowAlpha as String] as? Double ?? 1
            guard alpha > 0 else { continue }
            let layer = w[kCGWindowLayer as String] as? Int ?? 0
            let owner = w[kCGWindowOwnerName as String] as? String ?? ""
            let title = w[kCGWindowName as String] as? String ?? ""
            if apps[pid] == nil {
                let app = NSRunningApplication(processIdentifier: pid)
                apps[pid] = (app?.bundleIdentifier ?? "", app?.bundleURL?.path ?? "")
            }
            let bundle = apps[pid]?.bundle ?? "", path = apps[pid]?.path ?? ""
            let id = w[kCGWindowNumber as String] as? CGWindowID ?? 0
            let site = sites?.blocks(id, list) == true
            let blocked = site || list.blocksWindow(bundleID: bundle, appName: owner, path: path, title: title)
            let label = list.blocksApp(bundleID: bundle, name: owner, path: path) && !owner.isEmpty ? owner
                : site ? "Blocked site"
                : Blocklist.isPrivateTitle(title) ? "Private window" : "Hidden window"
            out.append(Window(id: id, frame: frame, blocked: blocked,
                              occludes: layer == 0 && alpha >= 1, label: label))
        }
        return out.reversed()  // CGWindowList is front to back
    }

    /// Blur what blocked windows cover in `image`, a capture of the display at `bounds`.
    /// Returns nil if that fails, so the caller can drop the frame rather than store it unblurred.
    static func apply(_ image: CGImage, bounds: CGRect, windows: [Window]) -> CGImage? {
        guard windows.contains(where: { $0.blocked && $0.frame.intersects(bounds) }) else { return image }
        let w = image.width, h = image.height
        let sx = CGFloat(w) / bounds.width, sy = CGFloat(h) / bounds.height
        let full = CGRect(x: 0, y: 0, width: w, height: h)
        // Into bitmap coordinates (pixels, bottom-left origin).
        func local(_ r: CGRect) -> CGRect {
            CGRect(x: (r.minX - bounds.minX) * sx, y: CGFloat(h) - (r.maxY - bounds.minY) * sy,
                   width: r.width * sx, height: r.height * sy).integral
        }

        guard let mctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                   space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return nil }
        mctx.setFillColor(gray: 0, alpha: 1)
        mctx.fill(full)
        for win in windows where win.blocked || win.occludes {
            mctx.setFillColor(gray: win.blocked ? 1 : 0, alpha: 1)
            mctx.fill(local(win.frame))
        }
        guard let mask = mctx.makeImage(),
              let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return nil }
        ctx.draw(image, in: full)
        ctx.saveGState()
        ctx.clip(to: full, mask: mask)
        // Shrink to 1/40 and stretch back: nothing readable survives, the colors still hint at the layout.
        ctx.interpolationQuality = .high
        ctx.draw(ImageTools.scaled(image, by: 1.0 / 40), in: full)
        ctx.setFillColor(CGColor(red: 0.08, green: 0.08, blue: 0.1, alpha: 0.6))
        ctx.fill(full)

        // Say what was hidden, in the middle of each blocked window.
        let px = max(14, CGFloat(h) / 60)
        for win in windows where win.blocked {
            let r = local(win.frame).intersection(full)
            guard r.width > px * 8, r.height > px * 3 else { continue }
            draw("\u{1F512} " + win.label, size: px, weight: .semibold, alpha: 1, centerX: r.midX, baselineY: r.midY + px * 0.1, in: ctx)
            draw("Hidden by OpenRewind", size: px * 0.75, weight: .regular, alpha: 0.75, centerX: r.midX, baselineY: r.midY - px * 1.1, in: ctx)
        }
        ctx.restoreGState()
        return ctx.makeImage()
    }

    /// Pixelate secrets in `image`: `boxes` are fractions of the picture (top-left origin, from OCR),
    /// `fields` are password fields in global screen points (top-left origin) on the display at `bounds`.
    /// Returns nil if that fails, so the caller can drop the frame rather than store it unblurred.
    static func blurSecrets(_ image: CGImage, boxes: [CGRect], fields: [CGRect], bounds: CGRect?) -> CGImage? {
        let w = image.width, h = image.height
        let full = CGRect(x: 0, y: 0, width: w, height: h)
        // Into bitmap coordinates (pixels, bottom-left origin).
        var rects = boxes.map { b in
            CGRect(x: b.minX * CGFloat(w), y: (1 - b.maxY) * CGFloat(h), width: b.width * CGFloat(w), height: b.height * CGFloat(h))
        }
        if let bounds, bounds.width > 0, bounds.height > 0 {
            let sx = CGFloat(w) / bounds.width, sy = CGFloat(h) / bounds.height
            rects += fields.map { r in
                CGRect(x: (r.minX - bounds.minX) * sx, y: CGFloat(h) - (r.maxY - bounds.minY) * sy, width: r.width * sx, height: r.height * sy)
            }
        }
        guard !rects.isEmpty else { return image }
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return nil }
        ctx.draw(image, in: full)
        ctx.interpolationQuality = .none
        for raw in rects {
            let r = raw.insetBy(dx: -max(3, raw.height / 4), dy: -max(2, raw.height / 6)).integral.intersection(full)
            guard r.width >= 1, r.height >= 1, let part = image.cropping(to: CGRect(x: r.minX, y: CGFloat(h) - r.maxY, width: r.width, height: r.height)) else { continue }
            // One block per line height across, one down: no letter survives.
            let bw = max(1, Int(r.width / max(6, r.height)))
            guard let tiny = CGContext(data: nil, width: bw, height: 1, bitsPerComponent: 8, bytesPerRow: 0,
                                       space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return nil }
            tiny.interpolationQuality = .high
            tiny.draw(part, in: CGRect(x: 0, y: 0, width: bw, height: 1))
            guard let small = tiny.makeImage() else { return nil }
            ctx.draw(small, in: r)
        }
        return ctx.makeImage()
    }

    /// Core Text, so this is safe off the main thread.
    private static func draw(_ s: String, size: CGFloat, weight: NSFont.Weight, alpha: CGFloat, centerX: CGFloat, baselineY: CGFloat, in ctx: CGContext) {
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: size, weight: weight),
            NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(gray: 1, alpha: alpha),
        ]
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: s, attributes: attrs))
        let b = CTLineGetBoundsWithOptions(line, [])
        ctx.textMatrix = .identity
        ctx.textPosition = CGPoint(x: centerX - b.width / 2, y: baselineY)
        CTLineDraw(line, ctx)
    }
}
