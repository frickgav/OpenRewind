import Foundation

/// A small animated GIF encoder for exported clips, written as it goes. A port of the Windows app's GifWriter.cs,
/// which has the tests (round trips through a separate decoder); keep the two the same.
/// Each picture gets its own palette of up to 255 colors, and only the part that changed since the last picture is
/// written; unchanged pixels inside it are transparent. Screen recordings change little from one screenshot to the
/// next, so this keeps files small. Pictures that did not change at all only make the one before them last longer.
/// Loops forever.
final class GifWriter {
    let width: Int, height: Int
    private let output: FileHandle
    /// The colors on screen after the last picture (0xRRGGBB), and the source pixels it came from.
    private let shown: UnsafeMutablePointer<Int32>
    private let source: UnsafeMutablePointer<Int32>
    private var first = true, finished = false
    /// The last picture, written once we know how long it lasts.
    private var pending: [UInt8]?
    private var pendingSeconds = 0.0
    /// Rounding left over from earlier delays, in 1/100 s, so a long clip does not drift.
    private var carry = 0.0

    // Per-picture scratch space: a 15-bit color histogram and the palette index for each 15-bit color.
    private let counts: UnsafeMutablePointer<Int32>
    private let sumR: UnsafeMutablePointer<Int64>, sumG: UnsafeMutablePointer<Int64>, sumB: UnsafeMutablePointer<Int64>
    private let lookup: UnsafeMutablePointer<Int32>

    init(output: FileHandle, width: Int, height: Int) throws {
        precondition(width >= 1 && height >= 1 && width <= 65535 && height <= 65535, "GIF size")
        self.output = output
        self.width = width
        self.height = height
        let n = width * height
        shown = .allocate(capacity: n); shown.initialize(repeating: 0, count: n)
        source = .allocate(capacity: n); source.initialize(repeating: 0, count: n)
        counts = .allocate(capacity: 32768); counts.initialize(repeating: 0, count: 32768)
        sumR = .allocate(capacity: 32768); sumR.initialize(repeating: 0, count: 32768)
        sumG = .allocate(capacity: 32768); sumG.initialize(repeating: 0, count: 32768)
        sumB = .allocate(capacity: 32768); sumB.initialize(repeating: 0, count: 32768)
        lookup = .allocate(capacity: 32768); lookup.initialize(repeating: 0, count: 32768)
        var head: [UInt8] = Array("GIF89a".utf8)
        Self.le16(&head, width); Self.le16(&head, height)
        head += [0, 0, 0]  // no global palette
        // NETSCAPE2.0 application block: loop forever.
        head += [0x21, 0xFF, 0x0B]
        head += Array("NETSCAPE2.0".utf8)
        head += [0x03, 0x01, 0x00, 0x00, 0x00]
        try output.write(contentsOf: head)
    }

    deinit {
        shown.deallocate(); source.deallocate(); counts.deallocate()
        sumR.deallocate(); sumG.deallocate(); sumB.deallocate(); lookup.deallocate()
    }

    /// Add a picture of width×height pixels in BGRA byte order (`bytesPerRow` bytes per row) that stays `seconds`.
    func addFrame(_ bgra: UnsafePointer<UInt8>, bytesPerRow: Int, seconds: Double) throws {
        precondition(!finished && bytesPerRow >= width * 4, "GIF frame")
        // Where this picture differs from the last one.
        var x0 = width, y0 = height, x1 = -1, y1 = -1
        for y in 0..<height {
            let row = bgra + y * bytesPerRow
            let o = y * width
            for x in 0..<width {
                let r = Int32(row[x * 4 + 2]), g = Int32(row[x * 4 + 1]), b = Int32(row[x * 4])
                let c = (r << 16) | (g << 8) | b
                if !first && source[o + x] == c { continue }
                source[o + x] = c
                if x < x0 { x0 = x }
                if x > x1 { x1 = x }
                if y < y0 { y0 = y }
                y1 = y
            }
        }
        if x1 < 0 { pendingSeconds += seconds; return }  // nothing changed: the last picture stays longer
        try flush()
        pending = encode(x0, y0, x1 - x0 + 1, y1 - y0 + 1)
        pendingSeconds = seconds
        first = false
    }

    /// Write the last picture and the end of the file. The file handle stays open.
    func finish() throws {
        if finished { return }
        if first {
            // No pictures: a black one keeps the file valid.
            var black = [UInt8](repeating: 0, count: width * height * 4)
            for i in Swift.stride(from: 3, to: black.count, by: 4) { black[i] = 255 }
            try black.withUnsafeBufferPointer { try addFrame($0.baseAddress!, bytesPerRow: width * 4, seconds: 0.1) }
        }
        try flush()
        try output.write(contentsOf: [UInt8(0x3B)])
        finished = true
    }

    private func flush() throws {
        guard let p = pending else { return }
        let cs = carry + pendingSeconds * 100
        let delay = min(max(Int(cs.rounded(.toNearestOrEven)), 2), 65535)
        // Only rounding carries over: when a picture lasts longer than a GIF can say (655 s), the rest is dropped
        // instead of stretching the pictures after it.
        carry = delay == 65535 ? 0 : cs - Double(delay)
        // Graphic control: keep the picture under the next one (so its transparent pixels show this one), delay, transparency.
        var block: [UInt8] = [0x21, 0xF9, 0x04, 0x05, UInt8(delay & 255), UInt8(delay >> 8), p[0], 0x00]
        block.append(contentsOf: p[1...])
        try output.write(contentsOf: block)
        pending = nil
    }

    @inline(__always) private static func key(_ c: Int32) -> Int {
        let r: Int32 = (c >> 19) & 31
        let g: Int32 = (c >> 11) & 31
        let b: Int32 = (c >> 3) & 31
        return Int((r << 10) | (g << 5) | b)
    }

    /// Image descriptor, palette and LZW data for one rectangle, after a leading byte with its transparent index.
    private func encode(_ rx: Int, _ ry: Int, _ rw: Int, _ rh: Int) -> [UInt8] {
        // The colors in the rectangle, counted at 5 bits per channel.
        var used: [Int] = []
        for y in ry..<(ry + rh) {
            for x in rx..<(rx + rw) {
                let c = source[y * width + x]
                let k = Self.key(c)
                if counts[k] == 0 { used.append(k) }
                counts[k] += 1
                sumR[k] += Int64((c >> 16) & 255); sumG[k] += Int64((c >> 8) & 255); sumB[k] += Int64(c & 255)
            }
        }
        let palette = makePalette(&used, pixels: rw * rh)
        let colors = palette.count

        let transparent = colors
        var indices = [UInt8](repeating: 0, count: rw * rh)
        var n = 0
        for y in ry..<(ry + rh) {
            for x in rx..<(rx + rw) {
                let o = y * width + x
                let idx = Int(lookup[Self.key(source[o])])
                let q = palette[idx]
                if !first && shown[o] == q { indices[n] = UInt8(transparent) } else { indices[n] = UInt8(idx); shown[o] = q }
                n += 1
            }
        }
        for k in used { counts[k] = 0; sumR[k] = 0; sumG[k] = 0; sumB[k] = 0 }

        var bits = 1
        while (1 << bits) < colors + 1 { bits += 1 }
        var out: [UInt8] = [UInt8(transparent), 0x2C]
        out.reserveCapacity(rw * rh / 2 + 1024)
        Self.le16(&out, rx); Self.le16(&out, ry); Self.le16(&out, rw); Self.le16(&out, rh)
        out.append(UInt8(0x80 | (bits - 1)))  // local palette of 2^bits colors
        for i in 0..<(1 << bits) {
            let p = i < colors ? palette[i] : 0
            out.append(UInt8((p >> 16) & 255)); out.append(UInt8((p >> 8) & 255)); out.append(UInt8(p & 255))
        }
        let codeSize = max(2, bits)
        out.append(UInt8(codeSize))
        var lzw = Lzw()
        lzw.compress(indices, initBits: codeSize + 1, into: &out)
        out.append(0)  // end of image data
        return out
    }

    /// Up to 255 colors for the 15-bit colors in `used`, and `lookup` filled with each one's palette index.
    /// Colors that cover much of the picture (backgrounds, text) keep their exact value; the rest are split
    /// into boxes by median cut and each box gets its average color.
    private func makePalette(_ used: inout [Int], pixels: Int) -> [Int32] {
        let maxColors = 255, maxExact = 64
        var colors: [Int32] = []
        func average(_ keys: [Int]) -> Int32 {
            var r: Int64 = 0, g: Int64 = 0, b: Int64 = 0, n: Int64 = 0
            for k in keys { r += sumR[k]; g += sumG[k]; b += sumB[k]; n += Int64(counts[k]) }
            let ar = Int32(r / n), ag = Int32(g / n), ab = Int32(b / n)
            return (ar << 16) | (ag << 8) | ab
        }
        if used.count <= maxColors {
            for k in used { lookup[k] = Int32(colors.count); colors.append(average([k])) }
            return colors
        }
        let cnt = counts
        used.sort { cnt[$0] != cnt[$1] ? cnt[$0] > cnt[$1] : $0 < $1 }
        var exact = 0
        while exact < maxExact && Int64(counts[used[exact]]) * 512 >= Int64(pixels) {
            lookup[used[exact]] = Int32(colors.count)
            colors.append(average([used[exact]]))
            exact += 1
        }

        func channel(_ k: Int, _ c: Int) -> Int { (k >> (10 - 5 * c)) & 31 }
        func widest(_ box: [Int]) -> (channel: Int, range: Int) {
            var best = 0, range = -1
            for c in 0..<3 {
                var lo = 31, hi = 0
                for k in box { let v = channel(k, c); if v < lo { lo = v }; if v > hi { hi = v } }
                if hi - lo > range { range = hi - lo; best = c }
            }
            return (best, range)
        }
        func score(_ box: [Int]) -> Int64 {
            if box.count < 2 { return 0 }
            var n: Int64 = 0
            for k in box { n += Int64(counts[k]) }
            return Int64(widest(box).range + 1) * Int64(Double(n).squareRoot())
        }
        var boxes: [[Int]] = [Array(used[exact...])]
        var scores: [Int64] = [score(boxes[0])]
        while exact + boxes.count < maxColors {
            // Split the box with the widest spread of colors, weighted by how many pixels it holds.
            var pick = -1
            var best: Int64 = 0
            for i in 0..<boxes.count where scores[i] > best { best = scores[i]; pick = i }
            if pick < 0 { break }
            var box = boxes[pick]
            let ch = widest(box).channel
            box.sort { channel($0, ch) < channel($1, ch) }
            var total: Int64 = 0, half: Int64 = 0
            for k in box { total += Int64(counts[k]) }
            var cut = 1
            while cut < box.count - 1 {
                half += Int64(counts[box[cut - 1]])
                if half * 2 >= total { break }
                cut += 1
            }
            boxes[pick] = Array(box[..<cut])
            boxes.append(Array(box[cut...]))
            scores[pick] = score(boxes[pick])
            scores.append(score(boxes[boxes.count - 1]))
        }
        for box in boxes {
            for k in box { lookup[k] = Int32(colors.count) }
            colors.append(average(box))
        }
        return colors
    }

    private static func le16(_ b: inout [UInt8], _ v: Int) { b.append(UInt8(v & 255)); b.append(UInt8((v >> 8) & 255)) }

    /// GIF's variable-length LZW, after the well-known compress/GIFEncoder code (12-bit codes, open-addressing hash).
    private struct Lzw {
        private static let maxBits = 12, maxMaxCode = 1 << 12, hsize = 5003
        private var htab = [Int](repeating: -1, count: Lzw.hsize), codetab = [Int](repeating: 0, count: Lzw.hsize)
        private var packet = [UInt8](repeating: 0, count: 256)
        private var packetCount = 0, nBits = 0, maxCode = 0, initBits = 0, clearCode = 0, eofCode = 0
        private var freeEnt = 0, curAccum = 0, curBits = 0
        private var clearFlag = false

        private static func mask(_ bits: Int) -> Int { (1 << bits) - 1 }

        mutating func compress(_ pixels: [UInt8], initBits: Int, into out: inout [UInt8]) {
            let hsize = Lzw.hsize
            self.initBits = initBits
            nBits = initBits
            maxCode = Lzw.mask(nBits)
            clearCode = 1 << (initBits - 1)
            eofCode = clearCode + 1
            freeEnt = clearCode + 2
            var hshift = 0
            var fc = hsize
            while fc < 65536 { hshift += 1; fc *= 2 }
            hshift = 8 - hshift
            for i in 0..<hsize { htab[i] = -1 }
            output(clearCode, &out)
            var ent = Int(pixels[0])
            var p = 1
            next: while p < pixels.count {
                let c = Int(pixels[p])
                p += 1
                let fcode = (c << Lzw.maxBits) + ent
                var i = (c << hshift) ^ ent
                if htab[i] == fcode { ent = codetab[i]; continue }
                if htab[i] >= 0 {
                    let disp = i == 0 ? 1 : hsize - i
                    repeat {
                        i -= disp
                        if i < 0 { i += hsize }
                        if htab[i] == fcode { ent = codetab[i]; continue next }
                    } while htab[i] >= 0
                }
                output(ent, &out)
                ent = c
                if freeEnt < Lzw.maxMaxCode {
                    codetab[i] = freeEnt
                    freeEnt += 1
                    htab[i] = fcode
                } else {
                    clearBlock(&out)
                }
            }
            output(ent, &out)
            output(eofCode, &out)
        }

        private mutating func clearBlock(_ out: inout [UInt8]) {
            for i in 0..<Lzw.hsize { htab[i] = -1 }
            freeEnt = clearCode + 2
            clearFlag = true
            output(clearCode, &out)
        }

        private mutating func output(_ code: Int, _ out: inout [UInt8]) {
            curAccum &= (1 << curBits) - 1
            curAccum = curBits > 0 ? curAccum | (code << curBits) : code
            curBits += nBits
            while curBits >= 8 { char(UInt8(curAccum & 255), &out); curAccum >>= 8; curBits -= 8 }
            // Wider codes once the table outgrows them; back to the start after a clear.
            if freeEnt > maxCode || clearFlag {
                if clearFlag {
                    nBits = initBits; maxCode = Lzw.mask(nBits); clearFlag = false
                } else {
                    nBits += 1; maxCode = nBits == Lzw.maxBits ? Lzw.maxMaxCode : Lzw.mask(nBits)
                }
            }
            if code == eofCode {
                while curBits > 0 { char(UInt8(curAccum & 255), &out); curAccum >>= 8; curBits -= 8 }
                flushPacket(&out)
            }
        }

        private mutating func char(_ b: UInt8, _ out: inout [UInt8]) {
            packet[packetCount] = b
            packetCount += 1
            if packetCount >= 254 { flushPacket(&out) }
        }

        private mutating func flushPacket(_ out: inout [UInt8]) {
            if packetCount == 0 { return }
            out.append(UInt8(packetCount))
            out.append(contentsOf: packet[0..<packetCount])
            packetCount = 0
        }
    }
}

extension GifWriter {
    /// Write the test GIF that scripts/gif-check.py checks (the Mac CI runs `OpenRewind gif-check <file>`).
    /// The pictures are the ones in that script: a gradient, the same with a white box, the same again,
    /// a three-color checkerboard and four-color noise that fills the LZW code table.
    static func writeCheck(to url: URL) throws {
        let w = 320, h = 200
        let durations = [0.5, 0.25, 0.25, 1.0, 0.3]
        let checker: [(Int, Int, Int)] = [(255, 0, 0), (0, 128, 255), (20, 20, 20)]
        let noise: [(Int, Int, Int)] = [(0, 0, 0), (255, 255, 255), (0, 200, 0), (200, 0, 200)]
        guard FileManager.default.createFile(atPath: url.path, contents: nil) else { throw CocoaError(.fileWriteUnknown) }
        let handle = try FileHandle(forWritingTo: url)
        defer { try? handle.close() }
        let gif = try GifWriter(output: handle, width: w, height: h)
        var px = [UInt8](repeating: 255, count: w * h * 4)
        for f in 0..<durations.count {
            var s: UInt32 = 1
            for y in 0..<h {
                for x in 0..<w {
                    let c: (Int, Int, Int)
                    if f <= 2 {
                        let inBox = f >= 1 && x >= 10 && x < 40 && y >= 10 && y < 30
                        let gr: Int = x * 255 / 319, gg: Int = y * 255 / 199, gb: Int = (x + y) * 255 / 518
                        c = inBox ? (255, 255, 255) : (gr, gg, gb)
                    } else if f == 3 {
                        c = checker[(x / 8 + y / 8) % 3]
                    } else {
                        s = (s &* 1103515245 &+ 12345) & 0x7FFF_FFFF
                        c = noise[Int((s >> 16) & 3)]
                    }
                    let o = (y * w + x) * 4
                    px[o] = UInt8(c.2); px[o + 1] = UInt8(c.1); px[o + 2] = UInt8(c.0)
                }
            }
            try px.withUnsafeBufferPointer { try gif.addFrame($0.baseAddress!, bytesPerRow: w * 4, seconds: durations[f]) }
        }
        try gif.finish()
    }
}
