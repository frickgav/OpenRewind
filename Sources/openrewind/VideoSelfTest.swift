import CoreGraphics
import Foundation
import UniformTypeIdentifiers

/// `openrewind video-check`: runs Compress Older Recordings for real on made-up screenshots in a temp
/// folder (encode, check, commit, delete stills), reads every frame back in a jumbled order, deletes part
/// of the video and checks the rewrite, deletes a row behind the app's back (the backstop rewrites it),
/// runs doctor's check, compresses a capture wider than encoders take, and turns a video that can't be
/// written again back into pictures. CI runs it; it is also a quick check on a new Mac.
/// `encrypted`: the same with Encrypt Recordings on (a throwaway key): stills, videos and the database
/// are encrypted, every step still works, and no plain picture or video is left in the data folder.
/// Windows has the same check in VideoSelfTest.cs, and unit tests for these steps in VideoTests.cs.
enum VideoSelfTest {
    static func run(encrypted: Bool = false) -> Bool {
        let codec = AVCodec()
        guard let name = codec.usable else { print("no video encoder: \(codec.probeError ?? "unknown")"); return false }
        print("codec: \(name)\(encrypted ? ", encrypted" : "")")
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("openrewind-video-check-\(UUID().uuidString.prefix(8))")
        let before = (key: RecordingFile.key, encryptNew: RecordingFile.encryptNew)
        let key: Data? = encrypted ? RecordingFile.newKey() : nil
        RecordingFile.key = key
        RecordingFile.encryptNew = encrypted
        defer {
            try? FileManager.default.removeItem(at: home)
            RecordingFile.key = before.key
            RecordingFile.encryptNew = before.encryptNew
        }
        do {
            let config = Config.load(arguments: ["--home", home.path])
            let db = try Database(path: config.dbPath, key: key, createEncrypted: encrypted)
            if encrypted != db.isEncrypted { print("the database is \(db.isEncrypted ? "" : "not ")encrypted"); return false }
            db.releasingVideos = { paths in paths.forEach(FrameImages.shared.release) }
            let now = Date()
            // Odd size on purpose: videos are cut to even sizes.
            let w = 965, h = 543, n = 45
            let start = Compaction.windowStart(now.addingTimeInterval(-7200))
            var thumbs = try addStills(db, config, start: start, count: n, w: w, h: h, display: "test")
            let compactor = VideoCompactor(db: db, codec: codec, dataDir: config.dataDir)
            var clock = Date()
            let r = compactor.runOnce(compress: true, now: now)
            print("compressed \(r.frames) frames into \(r.videos) video(s) in \(Int(Date().timeIntervalSince(clock) * 1000)) ms, \(r.failed) failed")
            guard r.frames == n, r.failed == 0 else { return false }
            let stats = db.videoStats()
            print("video: \(stats.bytes / 1024) KB, saves \(stats.saved / 1024) KB "
                  + String(format: "(%.1fx smaller)", Double(stats.bytes + stats.saved) / Double(max(1, stats.bytes))))
            let jpgs = (FileManager.default.enumerator(atPath: config.dataDir.path)?.allObjects as? [String] ?? []).filter { $0.hasSuffix(".jpg") }
            guard jpgs.isEmpty else { print("stills were not deleted"); return false }

            guard checkAll(db, thumbs, w, h) else { return false }
            guard allEncrypted(config, encrypted) else { return false }

            // Delete a stretch in the middle: the video is written again without it.
            let deleted = db.deleteFrames(from: start.addingTimeInterval(20), to: start.addingTimeInterval(40))
            for id in thumbs.keys where db.frame(id: id) == nil { thumbs.removeValue(forKey: id) }
            clock = Date()
            let r2 = compactor.runOnce(compress: false, now: now)
            print("deleted \(deleted) frames, rewrote \(r2.rewritten) video(s) in \(Int(Date().timeIntervalSince(clock) * 1000)) ms, \(r2.failed) failed")
            guard r2.rewritten == 1, r2.failed == 0, thumbs.count == n - deleted else { return false }
            guard checkAll(db, thumbs, w, h) else { return false }

            // A row deleted behind the app's back (another process, a delete cut short): the video still
            // holds its picture until the backstop marks it and it is written again.
            let other = try Database(path: config.dbPath, key: key)
            guard let gone = thumbs.keys.sorted().first else { return false }
            other.run("DELETE FROM frames WHERE id = ?", [.int(gone)])
            thumbs.removeValue(forKey: gone)
            let r3 = compactor.runOnce(compress: false, now: now)
            print("deleted a row from another connection, rewrote \(r3.rewritten) video(s), \(r3.failed) failed")
            guard r3.rewritten == 1, r3.failed == 0, db.segments().first?.frameCount == thumbs.count else { return false }
            guard checkAll(db, thumbs, w, h) else { return false }

            let doctor = AVCodec.checkVideos(db)
            print("doctor's check: \(doctor.checked) video(s), \(doctor.bad) do not open")
            guard doctor.checked == 1, doctor.bad == 0 else { return false }

            // A capture wider than every encoder takes is scaled down to fit.
            let ww = 5000, wh = 1000
            let wide = try addStills(db, config, start: Compaction.windowStart(now.addingTimeInterval(-3 * 3600)), count: 3, w: ww, h: wh, display: "wide")
            // A new compactor, as after a restart: this one already went past these older times.
            let r4 = VideoCompactor(db: db, codec: codec, dataDir: config.dataDir).runOnce(compress: true, now: now)
            let fit = Compaction.fit(ww, wh)
            print("wide capture: \(r4.frames) frames of \(ww)x\(wh) into \(r4.videos) video(s) of \(fit.width)x\(fit.height), \(r4.failed) failed")
            guard r4.frames == 3, r4.failed == 0, checkAll(db, wide, fit.width, fit.height) else { return false }

            // A video that can't be written again (say, the encoder is gone) becomes pictures again, so the
            // deleted frame is in no file.
            let before = db.segments().count
            guard let drop = thumbs.keys.sorted().last, let f = db.frame(id: drop), let t = Time.iso.date(from: f.timestamp) else { return false }
            guard db.deleteFrames(from: t, to: t.addingTimeInterval(1)) == 1 else {
                print("could not delete one frame"); return false
            }
            thumbs.removeValue(forKey: drop)
            // The pictures must come out as the video showed them (each lossy step was checked against the
            // screenshot already; a JPEG of a video frame adds its own small loss on top).
            var shown: [Int64: [UInt8]] = [:]
            for id in thumbs.keys {
                guard let src = db.frame(id: id)?.source, let pic = FrameImages.shared.image(src, cache: false) else { print("frame \(id) did not load"); return false }
                shown[id] = FrameImages.comparable(pic)
            }
            let broken = VideoCompactor(db: db, codec: BrokenEncoder(codec), dataDir: config.dataDir)
            let r5 = broken.runOnce(compress: false, now: now)
            print("without an encoder: \(r5.unpacked) video(s) turned back into pictures, \(r5.rewritten) rewritten, \(r5.failed) failed")
            guard r5.unpacked == 1, r5.failed == 0, db.segments().count == before - 1, db.stuckRewrites() == 0 else { return false }
            guard thumbs.keys.allSatisfy({ db.frame(id: $0)?.videoPath.isEmpty == true }) else { print("frames still point at the video"); return false }
            guard checkAll(db, shown, w, h) else { return false }
            guard allEncrypted(config, encrypted) else { return false }

            // Delete the rest: the videos and stills go too.
            db.deleteFrames(from: nil, to: now)
            let left = (FileManager.default.enumerator(atPath: config.dataDir.path)?.allObjects as? [String] ?? [])
                .filter { $0.hasSuffix(".mp4") || $0.hasSuffix(".jpg") }
            print("after deleting everything: \(left.count) files")
            return left.isEmpty
        } catch {
            print("failed: \(error.localizedDescription)")
            return false
        }
    }

    /// Stills 2 s apart from `start`, saved like the recorder does; returns their comparable thumbnails by id.
    private static func addStills(_ db: Database, _ config: Config, start: Date, count: Int, w: Int, h: Int, display: String) throws -> [Int64: [UInt8]] {
        var thumbs: [Int64: [UInt8]] = [:]
        for i in 0..<count {
            guard let pic = screen(i, w, h) else { throw AVCodec.error("cannot draw test pictures") }
            let ts = Time.iso.string(from: start.addingTimeInterval(Double(i * 2)))
            let dir = config.dataDir.appendingPathComponent(String(ts.prefix(10)))
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let file = dir.appendingPathComponent(ts.replacingOccurrences(of: ":", with: "-") + ".jpg")
            guard let jpg = ImageTools.jpegData(pic, quality: 0.8) else { throw AVCodec.error("cannot save a test picture") }
            try RecordingFile.write(jpg, to: file)  // encrypted when the check runs with encryption
            let id = try db.insert(Frame(id: 0, timestamp: ts, appName: "Test", windowName: "Window \(i)", focused: true,
                                         snapshotPath: file.path, text: "frame \(i)", textSource: "ocr", hash: 0, display: display))
            guard let still = FrameImages.shared.image(FrameSource(stillPath: file.path)) else { throw AVCodec.error("cannot read a test picture") }
            thumbs[id] = FrameImages.comparable(still)
        }
        return thumbs
    }

    /// With encryption, every picture and video under data/ is encrypted (none was left plain on the way).
    private static func allEncrypted(_ config: Config, _ encrypted: Bool) -> Bool {
        guard encrypted else { return true }
        let files = (FileManager.default.enumerator(atPath: config.dataDir.path)?.allObjects as? [String] ?? [])
            .filter { $0.hasSuffix(".mp4") || $0.hasSuffix(".jpg") }
        let plain = files.filter { !RecordingFile.isEncrypted(path: config.dataDir.appendingPathComponent($0).path) }
        print("encrypted: \(files.count - plain.count) of \(files.count) pictures and videos")
        if !plain.isEmpty { print("plain files: \(plain.prefix(5).joined(separator: ", "))") }
        return plain.isEmpty && !files.isEmpty
    }

    /// Every frame reads back like its screenshot, in a jumbled order (scrubbing back and forth).
    private static func checkAll(_ db: Database, _ thumbs: [Int64: [UInt8]], _ w: Int, _ h: Int) -> Bool {
        let ids = thumbs.keys.sorted { ($0 * 7919) % 31 < ($1 * 7919) % 31 }
        let clock = Date()
        for id in ids {
            guard let f = db.frame(id: id), let pic = FrameImages.shared.image(f.source) else { print("frame \(id) did not load"); return false }
            guard pic.width == Compaction.even(w), pic.height == Compaction.even(h) else { print("frame \(id) is \(pic.width)x\(pic.height)"); return false }
            let got = FrameImages.comparable(pic)
            guard let want = thumbs[id], Compaction.similar(got, want) else {
                let off = zip(got, thumbs[id] ?? []).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) } / max(1, got.count)
                print("frame \(id) (\(f.source.isVideo ? "video" : "still")) looks different from its screenshot: off by \(off) gray levels on average"); return false
            }
        }
        print(String(format: "read %d frames back, %.1f ms each", ids.count, Date().timeIntervalSince(clock) * 1000 / Double(max(1, ids.count))))
        return true
    }

    /// Something like a screen: a title bar, lines of "text", and a box that moves.
    private static func screen(_ i: Int, _ w: Int, _ h: Int) -> CGImage? {
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else { return nil }
        ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        ctx.setFillColor(CGColor(red: 0.41, green: 0.41, blue: 0.41, alpha: 1))
        ctx.fill(CGRect(x: 0, y: h - 30, width: w, height: 30))
        ctx.setFillColor(CGColor(red: 0, green: 0, blue: 0, alpha: 1))
        for line in 0..<20 {
            var x = 12
            for word in 0..<9 {
                let len = 18 + ((line * 31 + word * 17 + (line == i % 20 ? 7 : 0)) % 40)
                ctx.fill(CGRect(x: x, y: h - 50 - line * 24, width: len, height: 9))
                x += len + 8
            }
        }
        let green = i % 2 == 0
        ctx.setFillColor(CGColor(red: green ? 0.18 : 0.86, green: green ? 0.55 : 0.08, blue: green ? 0.34 : 0.24, alpha: 1))
        ctx.fill(CGRect(x: 600 + i * 7 % 300, y: 100 + i * 13 % 300, width: 120, height: 90))
        return ctx.makeImage()
    }
}

/// The real codec, except that it can't write videos any more (stills and reading still work).
private final class BrokenEncoder: VideoCodec {
    let real: AVCodec
    init(_ real: AVCodec) { self.real = real }
    func measure(_ frame: FrameSource) -> (width: Int, height: Int)? { real.measure(frame) }
    func write(_ frames: [FrameSource], width: Int, height: Int, to path: String) throws -> String { throw AVCodec.error("no encoder (test)") }
    func verify(_ path: String, _ frames: [FrameSource], width: Int, height: Int) -> Bool { false }
    func writeStill(_ frame: FrameSource, to path: String) throws { try real.writeStill(frame, to: path) }
    func release(_ path: String) { real.release(path) }
}
