// Draws the app icon and writes an .iconset folder. Usage: swift make-icon.swift <out.iconset>
import AppKit

let out = URL(fileURLWithPath: CommandLine.arguments[1])
try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

func draw(_ px: Int) -> Data {
    let s = CGFloat(px)
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let inset = s * 0.1
    let body = NSRect(x: inset, y: inset, width: s - 2 * inset, height: s - 2 * inset)
    // Squircle background, deep blue gradient.
    let bg = NSBezierPath(roundedRect: body, xRadius: s * 0.2, yRadius: s * 0.2)
    NSGradient(colors: [NSColor(red: 0.13, green: 0.22, blue: 0.55, alpha: 1),
                        NSColor(red: 0.05, green: 0.07, blue: 0.18, alpha: 1)])!.draw(in: bg, angle: -90)
    // Screen.
    let scr = NSRect(x: s * 0.24, y: s * 0.40, width: s * 0.52, height: s * 0.34)
    NSColor(white: 1, alpha: 0.95).setFill()
    NSBezierPath(roundedRect: scr, xRadius: s * 0.04, yRadius: s * 0.04).fill()
    NSColor(red: 0.18, green: 0.43, blue: 0.96, alpha: 1).setFill()
    NSBezierPath(roundedRect: scr.insetBy(dx: s * 0.025, dy: s * 0.025), xRadius: s * 0.025, yRadius: s * 0.025).fill()
    // Timeline with playhead.
    NSColor(white: 1, alpha: 0.35).setFill()
    NSBezierPath(roundedRect: NSRect(x: s * 0.24, y: s * 0.28, width: s * 0.52, height: s * 0.035),
                 xRadius: s * 0.0175, yRadius: s * 0.0175).fill()
    NSColor(red: 1, green: 0.42, blue: 0.35, alpha: 1).setFill()
    NSBezierPath(ovalIn: NSRect(x: s * 0.58 - s * 0.045, y: s * 0.2975 - s * 0.045, width: s * 0.09, height: s * 0.09)).fill()
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for (name, px) in [("16x16", 16), ("16x16@2x", 32), ("32x32", 32), ("32x32@2x", 64), ("128x128", 128),
                   ("128x128@2x", 256), ("256x256", 256), ("256x256@2x", 512), ("512x512", 512), ("512x512@2x", 1024)] {
    try! draw(px).write(to: out.appendingPathComponent("icon_\(name).png"))
}
