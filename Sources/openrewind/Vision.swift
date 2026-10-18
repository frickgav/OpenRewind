import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers
import Vision

enum ImageTools {
    /// 64-bit difference hash (dHash). Close screens give close hashes,
    /// so we can skip frames that barely changed.
    static func dHash(_ image: CGImage) -> UInt64 {
        let w = 9, h = 8
        var pixels = [UInt8](repeating: 0, count: w * h)
        let ctx = CGContext(data: &pixels, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w,
                            space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue)!
        ctx.interpolationQuality = .medium
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        var hash: UInt64 = 0
        for y in 0..<h {
            for x in 0..<(w - 1) {
                hash <<= 1
                if pixels[y * w + x] > pixels[y * w + x + 1] { hash |= 1 }
            }
        }
        return hash
    }

    static func distance(_ a: UInt64, _ b: UInt64) -> Int { (a ^ b).nonzeroBitCount }

    /// Small grayscale thumbnail for change detection. 9x8 is too coarse to see
    /// a new line of text, so this keeps enough detail (192x120) to notice it.
    static func thumbnail(_ image: CGImage, width: Int = 192, height: Int = 120) -> [UInt8] {
        var px = [UInt8](repeating: 0, count: width * height)
        let ctx = CGContext(data: &px, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width,
                            space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue)!
        ctx.interpolationQuality = .low
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return px
    }

    /// Number of thumbnail pixels that changed by more than `tolerance` gray levels.
    static func changedPixels(_ a: [UInt8], _ b: [UInt8], tolerance: Int = 8) -> Int {
        guard a.count == b.count else { return Int.max }
        var n = 0
        for i in 0..<a.count where abs(Int(a[i]) - Int(b[i])) > tolerance { n += 1 }
        return n
    }

    static func scaled(_ image: CGImage, by scale: CGFloat) -> CGImage {
        guard scale < 1 else { return image }
        let w = Int(CGFloat(image.width) * scale), h = Int(CGFloat(image.height) * scale)
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return image }
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        return ctx.makeImage() ?? image
    }

    static func writeJPEG(_ image: CGImage, to url: URL, quality: Double = 0.7) throws {
        guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.jpeg.identifier as CFString, 1, nil) else {
            throw NSError(domain: "openrewind", code: 1, userInfo: [NSLocalizedDescriptionKey: "cannot create \(url.path)"])
        }
        CGImageDestinationAddImage(dest, image, [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
        if !CGImageDestinationFinalize(dest) {
            throw NSError(domain: "openrewind", code: 2, userInfo: [NSLocalizedDescriptionKey: "cannot write \(url.path)"])
        }
    }

    static func load(_ url: URL) -> CGImage? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        return CGImageSourceCreateImageAtIndex(src, 0, nil)
    }
}

enum OCR {
    /// On-device text recognition with Apple Vision. Returns lines top-to-bottom.
    static func recognize(_ image: CGImage) -> String {
        let req = VNRecognizeTextRequest()
        req.recognitionLevel = .accurate
        req.usesLanguageCorrection = true
        let handler = VNImageRequestHandler(cgImage: image, options: [:])
        do { try handler.perform([req]) } catch {
            log("ocr failed: \(error)")
            return ""
        }
        let obs = (req.results ?? []).sorted {
            // Vision uses bottom-left origin: higher y = higher on screen.
            abs($0.boundingBox.midY - $1.boundingBox.midY) > 0.01
                ? $0.boundingBox.midY > $1.boundingBox.midY
                : $0.boundingBox.minX < $1.boundingBox.minX
        }
        return obs.compactMap { $0.topCandidates(1).first?.string }.joined(separator: "\n")
    }

    /// Where the search terms appear in the image, as fractions of its width and height (0–1, top-left origin).
    static func find(_ terms: [String], in image: CGImage) -> [CGRect] {
        guard !terms.isEmpty else { return [] }
        let req = VNRecognizeTextRequest()
        req.recognitionLevel = .accurate
        req.usesLanguageCorrection = true
        do { try VNImageRequestHandler(cgImage: image, options: [:]).perform([req]) } catch {
            log("ocr failed: \(error)")
            return []
        }
        var found: [CGRect] = []
        for obs in req.results ?? [] {
            guard let text = obs.topCandidates(1).first else { continue }
            let s = text.string
            for t in terms {
                var from = s.startIndex
                while let r = s.range(of: t, options: .caseInsensitive, range: from..<s.endIndex) {
                    from = r.upperBound
                    guard let box = try? text.boundingBox(for: r)?.boundingBox else { continue }
                    // Vision uses bottom-left origin.
                    found.append(CGRect(x: box.minX, y: 1 - box.maxY, width: box.width, height: box.height))
                }
            }
        }
        return found
    }
}
