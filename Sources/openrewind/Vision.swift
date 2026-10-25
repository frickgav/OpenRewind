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
    /// `quality`: .low for the change check (fast); .high to compare a video frame with its screenshot.
    static func thumbnail(_ image: CGImage, width: Int = 192, height: Int = 120, quality: CGInterpolationQuality = .low) -> [UInt8] {
        var px = [UInt8](repeating: 0, count: width * height)
        let ctx = CGContext(data: &px, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width,
                            space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue)!
        ctx.interpolationQuality = quality
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

    /// HEIC is much smaller than JPEG for screenshots at the same sharpness. Macs that cannot
    /// encode it get JPEG. `base` has no extension; returns the file written. Saved through
    /// RecordingFile, so it is encrypted when Encrypt Recordings is on.
    static func writeSnapshot(_ image: CGImage, to base: URL) throws -> URL {
        if canWriteHEIC, let data = encode(image, type: .heic, quality: 0.6) {
            let heic = base.appendingPathExtension("heic")
            try RecordingFile.write(data, to: heic)
            return heic
        }
        guard let data = encode(image, type: .jpeg, quality: 0.7) else {
            throw NSError(domain: "openrewind", code: 2, userInfo: [NSLocalizedDescriptionKey: "cannot encode \(base.lastPathComponent)"])
        }
        let jpg = base.appendingPathExtension("jpg")
        try RecordingFile.write(data, to: jpg)
        return jpg
    }

    /// A plain image file somewhere the user picked (Save Screenshot). Not for recordings: those go
    /// through RecordingFile (writeSnapshot), so they are encrypted when Encrypt Recordings is on.
    static func write(_ image: CGImage, to url: URL, type: UTType, quality: Double) throws {
        guard let data = encode(image, type: type, quality: quality) else {
            throw NSError(domain: "openrewind", code: 2, userInfo: [NSLocalizedDescriptionKey: "cannot encode \(url.lastPathComponent)"])
        }
        try data.write(to: url, options: .atomic)
    }

    static func encode(_ image: CGImage, type: UTType, quality: Double) -> Data? {
        let data = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(data as CFMutableData, type.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(dest, image, [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
        return CGImageDestinationFinalize(dest) ? data as Data : nil
    }

    static let canWriteHEIC = (CGImageDestinationCopyTypeIdentifiers() as? [String] ?? []).contains(UTType.heic.identifier)

    /// JPEG bytes, for browsers that cannot show HEIC.
    static func jpegData(_ image: CGImage, quality: Double = 0.8) -> Data? { encode(image, type: .jpeg, quality: quality) }

    /// An image file that is not a recording (`ingest`). Recordings go through RecordingFile.image.
    static func load(_ url: URL) -> CGImage? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        return CGImageSourceCreateImageAtIndex(src, 0, nil)
    }

    static func image(from data: Data) -> CGImage? {
        guard let src = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
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

    /// Reads the image like recognize(_:) and also says where secrets are in it (see Secrets), as fractions
    /// of its width and height (0–1, top-left origin), so they can be blurred before it is saved.
    /// `fast` only looks for the secrets, for when the text came from Accessibility; a quick read that shows
    /// a key prefix or a secret's name is read again carefully, since a quick read breaks keys up with spaces.
    /// `known` are secrets found in the Accessibility text: they are blurred wherever OCR shows them, even
    /// misread a little.
    static func recognizeFindingSecrets(_ image: CGImage, fast: Bool = false, known: [String] = []) -> (text: String, secrets: [CGRect]) {
        let first = readFindingSecrets(image, level: fast ? .fast : .accurate, correct: !fast, known: known)
        guard fast, !first.secrets.isEmpty || Secrets.mightHave(first.text) else { return first }
        // No language correction: it "fixes" keys into words.
        let careful = readFindingSecrets(image, level: .accurate, correct: false, known: known)
        return (careful.text, first.secrets + careful.secrets)
    }

    private static func readFindingSecrets(_ image: CGImage, level: VNRequestTextRecognitionLevel, correct: Bool,
                                           known: [String]) -> (text: String, secrets: [CGRect]) {
        let req = VNRecognizeTextRequest()
        req.recognitionLevel = level
        req.usesLanguageCorrection = correct
        do { try VNImageRequestHandler(cgImage: image, options: [:]).perform([req]) } catch {
            log("ocr failed: \(error)")
            return ("", [])
        }
        let obs = (req.results ?? []).sorted {
            abs($0.boundingBox.midY - $1.boundingBox.midY) > 0.01
                ? $0.boundingBox.midY > $1.boundingBox.midY
                : $0.boundingBox.minX < $1.boundingBox.minX
        }
        let read = obs.compactMap { o in o.topCandidates(1).first.map { (o, $0) } }
        let lines = read.map(\.1)
        // All lines in one text, so a key over several lines or a card number in pieces is still found.
        var joined = ""
        var starts: [Int] = []
        for (i, line) in lines.enumerated() {
            if i > 0 { joined += "\n" }
            starts.append((joined as NSString).length)
            joined += line.string
        }
        // Everything to hide, as (line, range in that line).
        var parts: [(line: Int, range: NSRange)] = []
        for h in Secrets.find(joined) {
            for (i, line) in lines.enumerated() {
                let a = starts[i], len = (line.string as NSString).length
                let lo = max(h.location, a), hi = min(h.location + h.length, a + len)
                if hi > lo { parts.append((i, NSRange(location: lo - a, length: hi - lo))) }
            }
        }
        let strings = lines.map(\.string)
        parts += Secrets.findInLines(strings)
        parts += Secrets.locate(known, in: strings)
        var boxes: [CGRect] = []
        for p in parts {
            let line = lines[p.line]
            // The words' own box, or the whole line's when Vision can't give one for part of it.
            var box = read[p.line].0.boundingBox
            if let r = Range(p.range, in: line.string), let b = try? line.boundingBox(for: r)?.boundingBox { box = b }
            boxes.append(CGRect(x: box.minX, y: 1 - box.maxY, width: box.width, height: box.height))
        }
        return (joined, boxes)
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
