import Foundation
import OnnxRuntimeBindings

/// NVIDIA Parakeet TDT 0.6B v3 (CC-BY-4.0) on ONNX Runtime, from the sherpa-onnx int8 export:
/// encoder, decoder (prediction network) and joiner, plus tokens.txt. The Windows app does the same
/// steps in Parakeet.cs. Pure parts (features, decoding, lines) are in ParakeetMath.
final class ParakeetEngine {
    static let tag = "parakeet-tdt-0.6b-v3"
    /// Size of the encoder's output vectors and of the prediction network's output and state (this model).
    static let encDim = 1024, predDim = 640
    private let env: ORTEnv
    private let encoder: ORTSession
    private let decoder: ORTSession
    private let joiner: ORTSession
    private let tokens: [String]
    private let blank: Int
    private let stateNames: [String]
    private let decoderOutputs: [String]

    init(dir: URL, threads: Int) throws {
        env = try ORTEnv(loggingLevel: .error)
        let opts = try ORTSessionOptions()
        try opts.setIntraOpNumThreads(Int32(threads))
        try opts.setGraphOptimizationLevel(.all)
        encoder = try ORTSession(env: env, modelPath: dir.appendingPathComponent("encoder.int8.onnx").path, sessionOptions: opts)
        decoder = try ORTSession(env: env, modelPath: dir.appendingPathComponent("decoder.int8.onnx").path, sessionOptions: opts)
        joiner = try ORTSession(env: env, modelPath: dir.appendingPathComponent("joiner.int8.onnx").path, sessionOptions: opts)
        let text = try String(contentsOf: dir.appendingPathComponent("tokens.txt"), encoding: .utf8)
        tokens = ParakeetMath.readTokens(text.components(separatedBy: "\n"))
        blank = tokens.firstIndex(of: "<blk>") ?? max(0, tokens.count - 1)
        // The two state inputs have export-made names ("states.1", "onnx::Slice_3"): take them in order.
        stateNames = try decoder.inputNames().filter { $0 != "targets" && $0 != "target_length" }
        guard stateNames.count == 2 else { throw ParakeetError("unexpected decoder inputs \(stateNames)") }
        decoderOutputs = try decoder.outputNames()
        guard decoderOutputs.count == 4 else { throw ParakeetError("unexpected decoder outputs \(decoderOutputs)") }
        // A model that does not fit fails here, once, instead of on every clip.
        let probe = try Decoding(self, enc: [Float](repeating: 0, count: ParakeetEngine.encDim), frames: 1)
        let logits = try probe.joint(0)
        guard logits.count == tokens.count + ParakeetMath.durations.count else {
            throw ParakeetError("the joiner gives \(logits.count) scores for \(tokens.count) tokens")
        }
    }

    struct ParakeetError: LocalizedError {
        let errorDescription: String?
        init(_ s: String) { errorDescription = s }
    }

    /// 16 kHz mono samples → lines with times in seconds from the start. Long audio goes through the
    /// encoder in windows of at most 30 s (its memory grows with the square of the length).
    func transcribe(_ samples: [Float]) throws -> [(start: Double, end: Double, text: String)] {
        var hits: [(token: Int, frame: Int)] = []
        for w in ParakeetMath.windows(samples) where w.length >= ParakeetMath.hop * 10 {
            // A bad index here would stop the whole app, not just this clip: fail the clip instead.
            guard w.start >= 0, w.length > 0, w.start + w.length <= samples.count else {
                throw ParakeetError("audio window \(w.start)+\(w.length) is outside the \(samples.count) samples")
            }
            let (feats, frames) = ParakeetMath.features(Array(samples[w.start..<(w.start + w.length)]))
            let (enc, encFrames) = try encode(feats, frames)
            let offset = w.start / ParakeetMath.stepSamples
            for hit in try Decoding(self, enc: enc, frames: encFrames).run() { hits.append((hit.token, hit.frame + offset)) }
        }
        return ParakeetMath.segments(hits, tokens: tokens, audioSeconds: Double(samples.count) / Double(ParakeetMath.sampleRate))
    }

    fileprivate static func tensor<T>(_ values: [T], _ type: ORTTensorElementDataType, _ shape: [Int]) throws -> ORTValue {
        guard !values.isEmpty else { throw ParakeetError("empty input for the model") }
        let data = values.withUnsafeBufferPointer { NSMutableData(bytes: $0.baseAddress!, length: $0.count * MemoryLayout<T>.stride) }
        return try ORTValue(tensorData: data, elementType: type, shape: shape.map { NSNumber(value: $0) })
    }

    fileprivate static func floats(_ v: ORTValue) throws -> [Float] {
        let data = try v.tensorData() as Data
        return data.withUnsafeBytes { Array($0.bindMemory(to: Float.self)) }
    }

    private func encode(_ feats: [Float], _ frames: Int) throws -> (data: [Float], frames: Int) {
        let outs = try encoder.run(withInputs: [
            "audio_signal": Self.tensor(feats, .float, [1, ParakeetMath.mels, frames]),
            "length": Self.tensor([Int64(frames)], .int64, [1]),
        ], outputNames: ["outputs", "encoded_lengths"], runOptions: nil)
        guard let o = outs["outputs"], let l = outs["encoded_lengths"] else { throw ParakeetError("encoder gave no output") }
        let shape = try o.tensorTypeAndShapeInfo().shape.map { $0.intValue }
        guard shape.count == 3, shape[1] == ParakeetEngine.encDim else { throw ParakeetError("encoder output has shape \(shape)") }
        let dim = shape[1], t = shape[2]
        let len = (try l.tensorData() as Data).withUnsafeBytes { $0.bindMemory(to: Int64.self).first ?? 0 }
        // [1, 1024, T] → one 1024-vector per frame, so the joiner gets contiguous slices.
        let src = try Self.floats(o)
        guard src.count == dim * t else { throw ParakeetError("encoder output is \(src.count) values, not \(dim) × \(t)") }
        var data = [Float](repeating: 0, count: t * dim)
        for d in 0..<dim { for i in 0..<t { data[i * dim + d] = src[d * t + i] } }
        return (data, max(0, min(Int(len), t)))
    }

    /// One window's decoding: the prediction network's state and the encoder frames it scores.
    private final class Decoding {
        let e: ParakeetEngine
        let enc: [Float]
        let frames: Int
        var h = [Float](repeating: 0, count: 2 * ParakeetEngine.predDim)
        var c = [Float](repeating: 0, count: 2 * ParakeetEngine.predDim)
        var decOut: [Float] = []

        init(_ engine: ParakeetEngine, enc: [Float], frames: Int) throws {
            e = engine; self.enc = enc; self.frames = frames
            try predict(engine.blank)  // the prediction network starts from the blank token
        }

        func predict(_ token: Int) throws {
            let p = ParakeetEngine.predDim
            let outs = try e.decoder.run(withInputs: [
                "targets": ParakeetEngine.tensor([Int32(token)], .int32, [1, 1]),
                "target_length": ParakeetEngine.tensor([Int32(1)], .int32, [1]),
                e.stateNames[0]: ParakeetEngine.tensor(h, .float, [2, 1, p]),
                e.stateNames[1]: ParakeetEngine.tensor(c, .float, [2, 1, p]),
            ], outputNames: Set(e.decoderOutputs), runOptions: nil)
            // Outputs, in order: decoder output [1, 640, 1], lengths, then the two new states.
            guard let o = outs[e.decoderOutputs[0]], let nh = outs[e.decoderOutputs[2]], let nc = outs[e.decoderOutputs[3]] else {
                throw ParakeetError("unexpected decoder outputs \(e.decoderOutputs)")
            }
            decOut = try ParakeetEngine.floats(o)
            h = try ParakeetEngine.floats(nh)
            c = try ParakeetEngine.floats(nc)
            guard decOut.count == p, h.count == 2 * p, c.count == 2 * p else {
                throw ParakeetError("the decoder's outputs are not the expected size")
            }
        }

        func joint(_ t: Int) throws -> [Float] {
            let d = ParakeetEngine.encDim
            guard t >= 0, (t + 1) * d <= enc.count else { throw ParakeetError("encoder frame \(t) is past the end (\(enc.count / d) frames)") }
            let v = Array(enc[(t * d)..<((t + 1) * d)])
            let outs = try e.joiner.run(withInputs: [
                "encoder_outputs": ParakeetEngine.tensor(v, .float, [1, d, 1]),
                "decoder_outputs": ParakeetEngine.tensor(decOut, .float, [1, ParakeetEngine.predDim, 1]),
            ], outputNames: ["outputs"], runOptions: nil)
            guard let o = outs["outputs"] else { throw ParakeetError("joiner gave no output") }
            let logits = try ParakeetEngine.floats(o)
            guard logits.count >= e.tokens.count + ParakeetMath.durations.count else {
                throw ParakeetError("the joiner gave \(logits.count) scores for \(e.tokens.count) tokens")
            }
            return logits
        }

        func run() throws -> [(token: Int, frame: Int)] {
            var failure: Error?
            // ParakeetMath.greedyTdt takes plain closures: keep the first error and stop scoring after it.
            let hits = ParakeetMath.greedyTdt(frames: frames, vocab: e.tokens.count, blank: e.blank, joint: { t in
                guard failure == nil else { return [] }
                do { return try self.joint(t) } catch { failure = error; return [] }
            }, emit: { token in
                guard failure == nil else { return }
                do { try self.predict(token) } catch { failure = error }
            })
            if let failure { throw failure }
            return hits
        }
    }
}


/// The parts of Parakeet that need no model: log-mel features exactly like NeMo's preprocessor,
/// TDT greedy decoding, and turning tokens into timed lines. Mirrors ParakeetMath in Parakeet.cs.
enum ParakeetMath {
    static let sampleRate = 16000, mels = 128, nFft = 512, hop = 160, win = 400
    /// The encoder keeps one frame in 8, so each decoded step is 80 ms.
    static let frameSeconds = 0.08
    /// Durations the TDT joiner predicts after the vocabulary scores.
    static let durations = [0, 1, 2, 3, 4]
    static let maxTokensPerFrame = 10
    /// Samples per encoder step (80 ms): windows start on these, so frame numbers just add up.
    static let stepSamples = hop * 8
    /// Longest audio given to the encoder at once.
    static let maxWindowSamples = 30 * sampleRate
    /// A window is cut at the quietest 80 ms in its last few seconds, so words are rarely split.
    static let cutSearchSamples = 4 * sampleRate

    /// Pieces of at most 30 s that cover `samples` end to end. Each starts on an encoder step; cuts go
    /// where it is quietest near the end of the window. Same as ParakeetMath.Windows in Parakeet.cs.
    static func windows(_ samples: [Float]) -> [(start: Int, length: Int)] {
        var list: [(start: Int, length: Int)] = []
        let n = samples.count
        var start = 0
        while n - start > maxWindowSamples {
            let end = start + maxWindowSamples
            // Each candidate is scored on the 80 ms around it, which must be inside the audio: with
            // less than 40 ms after the 30 s mark (a 30.01 s clip), the first candidate is a step earlier.
            var top = end
            while top + stepSamples / 2 > n { top -= stepSamples }
            var best = top, quietest = Double.greatestFiniteMagnitude
            var cut = top
            while cut >= end - cutSearchSamples {
                var energy = 0.0
                for i in (cut - stepSamples / 2)..<(cut + stepSamples / 2) { energy += Double(samples[i]) * Double(samples[i]) }
                if energy < quietest { quietest = energy; best = cut }
                cut -= stepSamples
            }
            list.append((start, best - start))
            start = best
        }
        if n > start { list.append((start, n - start)) }
        return list
    }

    /// Checks windows() on clip lengths around 30 s and the 80 ms each cut is scored on, the same cases
    /// as the Windows tests (a 30.01 s clip used to read past the end and crash). Needs no model; run by
    /// the Mac CI as `openrewind parakeet-check`. Returns the problems found.
    static func selfTest() -> [String] {
        var problems: [String] = []
        let sec = sampleRate, half = stepSamples / 2
        func check(_ samples: [Float], _ name: String) -> [(start: Int, length: Int)] {
            let list = windows(samples)
            var next = 0
            for (k, w) in list.enumerated() {
                if w.start != next { problems.append("\(name): window \(k) starts at \(w.start), not \(next)") }
                if w.start % stepSamples != 0 { problems.append("\(name): window \(k) does not start on an encoder step") }
                if w.length < 1 || w.length > maxWindowSamples { problems.append("\(name): window \(k) is \(w.length) samples") }
                if k < list.count - 1 && w.length < maxWindowSamples - cutSearchSamples { problems.append("\(name): cut \(k) is before the last 4 s") }
                next = w.start + w.length
            }
            if next != samples.count { problems.append("\(name): windows cover \(next) of \(samples.count) samples") }
            if samples.count <= maxWindowSamples && list.count != (samples.isEmpty ? 0 : 1) { problems.append("\(name): \(list.count) windows for a short clip") }
            return list
        }
        let lengths = [0, 1, sec * 29 + sec / 2, sec * 30 - 1, sec * 30, sec * 30 + 1, 480160, sec * 30 + 320, sec * 30 + 639,
                       sec * 30 + 640, sec * 30 + 641, sec * 30 + 1280, sec * 34, sec * 60, sec * 60 + 1, 960160, 1440160]
        var seed: UInt64 = 1
        for n in lengths {
            for kind in ["silence", "tone", "noise"] {
                var samples = [Float](repeating: 0, count: n)
                for i in 0..<n {
                    if kind == "tone" { samples[i] = Float(0.3 * sin(Double(i) * 0.05)) }
                    else if kind == "noise" {
                        seed = seed &* 6364136223846793005 &+ 1442695040888963407
                        samples[i] = Float(seed >> 40) / Float(1 << 24) - 0.5
                    }
                }
                _ = check(samples, "\(n) samples of \(kind)")
            }
        }
        func toneWithQuiet(_ n: Int, at: Int) -> [Float] {
            (0..<n).map { i in i >= at - half && i < at + half ? 0 : Float(0.3 * sin(Double(i) * 0.05)) }
        }
        // Quiet at the far edge of the 4 s search (26 s): the cut goes there.
        if let w = check(toneWithQuiet(31 * sec, at: 26 * sec), "quiet at 26 s").first, w.length != 26 * sec {
            problems.append("quiet at 26 s: cut at \(w.length), not \(26 * sec)")
        }
        // Quiet at 30 s with just enough audio after it to score it: cut at 30 s.
        let atEnd = check(toneWithQuiet(30 * sec + half, at: 30 * sec), "quiet at 30 s")
        if atEnd.count != 2 || atEnd[0].length != 30 * sec { problems.append("quiet at 30 s: windows \(atEnd)") }
        // One sample short: 30 s cannot be scored, so the cut is a step earlier, not past the end.
        let short = check(toneWithQuiet(30 * sec + half - 1, at: 30 * sec), "quiet at 30 s, one sample short")
        if let w = short.first, w.length > 30 * sec - stepSamples { problems.append("quiet at 30 s, one sample short: cut at \(w.length)") }
        // Quiet just before 26 s, out of reach: the cut still lands in the last 4 s.
        if let w = check(toneWithQuiet(40 * sec, at: 26 * sec - stepSamples), "quiet before 26 s").first,
           w.length < 26 * sec || w.length > 30 * sec {
            problems.append("quiet before 26 s: cut at \(w.length)")
        }
        return problems
    }

    /// A symmetric 400-point Hann window in the middle of the 512-point frame (as torch.stft pads it).
    static let window: [Double] = {
        var w = [Double](repeating: 0, count: nFft)
        let off = (nFft - win) / 2
        for i in 0..<win { w[off + i] = 0.5 - 0.5 * cos(2 * Double.pi * Double(i) / Double(win - 1)) }
        return w
    }()

    /// Mel filters, and for each the range of FFT bins where it is not zero.
    static let mel: (bank: [Double], from: [Int], to: [Int]) = {
        let bank = melFilters(), nb = nFft / 2 + 1
        var from = [Int](repeating: 0, count: mels), to = [Int](repeating: 0, count: mels)
        for m in 0..<mels {
            var a = 0, b = nb
            while a < nb && bank[m * nb + a] == 0 { a += 1 }
            while b > a && bank[m * nb + b - 1] == 0 { b -= 1 }
            from[m] = a; to[m] = b
        }
        return (bank, from, to)
    }()

    /// Log-mel features, [mels, frames] row by row, normalized per mel band (NeMo's "per_feature").
    /// Pre-emphasis 0.97, 25 ms Hann window in a 512-point FFT every 10 ms, centered with zero padding,
    /// power spectrum, Slaney mel filters to 8 kHz, log(x + 2^-24).
    static func features(_ samples: [Float]) -> (feats: [Float], frames: Int) {
        let n = samples.count, frames = 1 + n / hop, bins = nFft / 2 + 1, pad = nFft / 2
        var x = [Double](repeating: 0, count: n + nFft)
        for i in 0..<n { x[pad + i] = i == 0 ? Double(samples[0]) : Double(samples[i]) - 0.97 * Double(samples[i - 1]) }
        let (bank, from, to) = mel
        var logMel = [Double](repeating: 0, count: mels * frames)
        var re = [Double](repeating: 0, count: nFft), im = [Double](repeating: 0, count: nFft)
        var power = [Double](repeating: 0, count: bins)
        for f in 0..<frames {
            let o = f * hop
            for i in 0..<nFft { re[i] = x[o + i] * window[i]; im[i] = 0 }
            fft(&re, &im)
            for k in 0..<bins { power[k] = re[k] * re[k] + im[k] * im[k] }
            for m in 0..<mels {
                var s = 0.0
                let row = m * bins
                for k in from[m]..<to[m] { s += bank[row + k] * power[k] }
                logMel[m * frames + f] = log(s + 5.960464477539063e-08)
            }
        }
        var feats = [Float](repeating: 0, count: mels * frames)
        for m in 0..<mels {
            var mean = 0.0
            for f in 0..<frames { mean += logMel[m * frames + f] }
            mean /= Double(frames)
            var v = 0.0
            for f in 0..<frames { let d = logMel[m * frames + f] - mean; v += d * d }
            let std = (v / Double(max(1, frames - 1))).squareRoot() + 1e-5
            for f in 0..<frames { feats[m * frames + f] = Float((logMel[m * frames + f] - mean) / std) }
        }
        return (feats, frames)
    }

    private static func hzToMel(_ f: Double) -> Double {
        let fSp = 200.0 / 3, minLogHz = 1000.0, minLogMel = minLogHz / fSp, logStep = log(6.4) / 27
        return f >= minLogHz ? minLogMel + log(f / minLogHz) / logStep : f / fSp
    }

    private static func melToHz(_ m: Double) -> Double {
        let fSp = 200.0 / 3, minLogHz = 1000.0, minLogMel = minLogHz / fSp, logStep = log(6.4) / 27
        return m >= minLogMel ? minLogHz * exp(logStep * (m - minLogMel)) : fSp * m
    }

    /// librosa.filters.mel(sr=16000, n_fft=512, n_mels=128, fmin=0, fmax=8000, norm="slaney"), [mels, bins].
    static func melFilters() -> [Double] {
        let bins = nFft / 2 + 1
        let lo = hzToMel(0), hi = hzToMel(Double(sampleRate) / 2)
        let pts = (0..<(mels + 2)).map { melToHz(lo + (hi - lo) * Double($0) / Double(mels + 1)) }
        var w = [Double](repeating: 0, count: mels * bins)
        for m in 0..<mels {
            let l = pts[m], c = pts[m + 1], r = pts[m + 2], norm = 2.0 / (r - l)
            for k in 0..<bins {
                let hz = Double(k) * Double(sampleRate) / Double(nFft)
                let up = (hz - l) / (c - l), down = (r - hz) / (r - c)
                // Rounded through Float like the Windows app, so both sum the same filters.
                w[m * bins + k] = Double(Float(max(0, min(up, down)) * norm))
            }
        }
        return w
    }

    /// In-place radix-2 FFT; the length must be a power of two.
    static func fft(_ re: inout [Double], _ im: inout [Double]) {
        let n = re.count
        var j = 0
        for i in 1..<n {
            var bit = n >> 1
            while j & bit != 0 { j ^= bit; bit >>= 1 }
            j ^= bit
            if i < j { re.swapAt(i, j); im.swapAt(i, j) }
        }
        var len = 2
        while len <= n {
            let ang = -2 * Double.pi / Double(len), wr = cos(ang), wi = sin(ang)
            var i = 0
            while i < n {
                var cr = 1.0, ci = 0.0
                for k in 0..<(len / 2) {
                    let a = i + k, b = a + len / 2
                    let tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr
                    re[b] = re[a] - tr; im[b] = im[a] - ti
                    re[a] += tr; im[a] += ti
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr)
                }
                i += len
            }
            len <<= 1
        }
    }

    /// TDT greedy search. `joint(t)` scores encoder frame t against the current prediction-network
    /// state: vocabulary (with blank) first, then one score per duration. `emit(token)` feeds a
    /// non-blank token back into the prediction network. Returns each token with its frame.
    static func greedyTdt(frames: Int, vocab: Int, blank: Int, joint: (Int) -> [Float], emit: (Int) -> Void) -> [(token: Int, frame: Int)] {
        var hits: [(token: Int, frame: Int)] = []
        var t = 0, sameFrame = 0
        while t < frames {
            let logits = joint(t)
            guard logits.count >= vocab + durations.count else { break }
            let token = argMax(logits, 0, vocab)
            let skip = durations[argMax(logits, vocab, durations.count)]
            if token != blank {
                hits.append((token, t))
                emit(token)
                sameFrame += 1
            }
            if skip > 0 { t += skip; sameFrame = 0 }
            else if token == blank || sameFrame >= maxTokensPerFrame { t += 1; sameFrame = 0 }
        }
        return hits
    }

    private static func argMax(_ v: [Float], _ start: Int, _ count: Int) -> Int {
        var best = start
        for i in (start + 1)..<(start + count) where v[i] > v[best] { best = i }
        return best - start
    }

    /// tokens.txt: "<piece> <id>" per line, in id order.
    static func readTokens(_ lines: [String]) -> [String] {
        var map: [Int: String] = [:]
        for line in lines {
            guard let sp = line.lastIndex(of: " "), sp > line.startIndex,
                  let id = Int(line[line.index(after: sp)...].trimmingCharacters(in: .whitespacesAndNewlines)), id >= 0 else { continue }
            map[id] = String(line[..<sp])
        }
        guard let top = map.keys.max() else { return [] }
        return (0...top).map { map[$0] ?? "" }
    }

    /// A pause this long between words starts a new line.
    static let pauseSeconds = 0.8
    /// Lines longer than this are cut at the next word, like Whisper's segments.
    static let maxLineSeconds = 12.0
    /// A word piece is said over about this long after the step it was decoded at.
    static let tokenTail = 0.3

    /// Joins SentencePiece pieces ("▁" starts a word) into lines that end at sentence ends, pauses,
    /// or after about 12 s, each with start and end times in seconds.
    static func segments(_ hits: [(token: Int, frame: Int)], tokens: [String], audioSeconds: Double) -> [(start: Double, end: Double, text: String)] {
        var lines: [(start: Double, end: Double, text: String)] = []
        var text = "", start = 0.0, last = 0.0, endsSentence = false
        func round2(_ s: Double) -> Double { (s * 100).rounded() / 100 }
        func flush(_ end: Double) {
            let s = text.trimmingCharacters(in: .whitespaces)
            if !s.isEmpty, s.contains(where: { $0.isLetter || $0.isNumber }) { lines.append((round2(start), round2(max(end, start)), s)) }
            text = ""
        }
        for (token, frame) in hits {
            guard token >= 0, token < tokens.count else { continue }
            let piece = tokens[token]
            if piece.isEmpty || (piece.hasPrefix("<") && piece.hasSuffix(">")) { continue }
            let at = Double(frame) * frameSeconds
            let newWord = piece.hasPrefix("\u{2581}")
            if !text.isEmpty && newWord && (endsSentence || at - last >= pauseSeconds || at - start >= maxLineSeconds) {
                flush(min(last + tokenTail, at))
            }
            if text.isEmpty { start = at }
            text += piece.replacingOccurrences(of: "\u{2581}", with: " ")
            last = at
            let trimmed = piece.trimmingCharacters(in: .whitespaces)
            endsSentence = trimmed.last.map { ".?!…。？！".contains($0) } ?? false
        }
        flush(min(last + tokenTail, max(audioSeconds, last)))
        return lines
    }
}
