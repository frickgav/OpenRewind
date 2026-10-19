import CoreGraphics
import Foundation
import UniformTypeIdentifiers

/// `openrewind video-check`: runs Compress Older Recordings for real on made-up screenshots in a temp
/// folder (encode, check, commit, delete stills), reads every frame back in a jumbled order, deletes part
/// of the video and checks the rewrite. CI runs it; it is also a quick check on a new Mac.
/// Windows has the same check in VideoSelfTest.cs.
enum VideoSelfTest {
    static func run() -> Bool {
        let codec = AVCodec()
        guard let name = codec.usable else { print("no video encoder: \(codec.probeError ?? "unknown")"); return false }
        print("codec: \(name)")
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("openrewind-video-check-\(UUID().uuidString.prefix(8))")
        defer { try? FileManager.default.removeItem(at: home) }
        do {
            let config = Config.load(arguments: ["--home", home.path])
            let db = try Database(path: config.dbPath)
            db.releasingVideos = { paths in paths.forEach(FrameImages.shared.release) }
            let now = Date()
            // Odd size on purpose: videos are cut to even sizes.
            let w = 965, h = 543, n = 45
            let start = Compaction.windowStart(now.addingTimeInterval(-7200))
            var thumbs: [Int64: [UInt8]] = [:]
            for i in 0..<n {
                guard let pic = screen(i, w, h) else { print("cannot draw test pictures"); return false }
                let ts = Time.iso.string(from: start.addingTimeInterval(Double(i * 2)))
                let dir = config.dataDir.appendingPathComponent(String(ts.prefix(10)))
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                let file = dir.appendingPathComponent(ts.replacingOccurrences(of: ":", with: "-") + ".jpg")
                try ImageTools.write(pic, to: file, type: .jpeg, quality: 0.8)
                let id = try db.insert(Frame(id: 0, timestamp: ts, appName: "Test", windowName: "Window \(i)", focused: true,
                                             snapshotPath: file.path, text: "frame \(i)", textSource: "ocr", hash: 0, display: "test"))
                guard let still = FrameImages.shared.image(FrameSource(stillPath: file.path)) else { print("cannot read a test picture"); return false }
                thumbs[id] = FrameImages.comparable(still)
            }
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

            // Delete a stretch in the middle: the video is written again without it.
            let deleted = db.deleteFrames(from: start.addingTimeInterval(20), to: start.addingTimeInterval(40))
            for id in thumbs.keys where db.frame(id: id) == nil { thumbs.removeValue(forKey: id) }
            clock = Date()
            let r2 = compactor.runOnce(compress: false, now: now)
            print("deleted \(deleted) frames, rewrote \(r2.rewritten) video(s) in \(Int(Date().timeIntervalSince(clock) * 1000)) ms, \(r2.failed) failed")
            guard r2.rewritten == 1, r2.failed == 0, thumbs.count == n - deleted else { return false }
            guard checkAll(db, thumbs, w, h) else { return false }

            // Delete the rest: the video goes too.
            db.deleteFrames(from: nil, to: now)
            let left = (FileManager.default.enumerator(atPath: compactor.videoDir)?.allObjects as? [String] ?? []).filter { $0.hasSuffix(".mp4") }
            print("after deleting everything: \(left.count) video files")
            return left.isEmpty
        } catch {
            print("failed: \(error.localizedDescription)")
            return false
        }
    }

    /// Every frame reads back like its screenshot, in a jumbled order (scrubbing back and forth).
    private static func checkAll(_ db: Database, _ thumbs: [Int64: [UInt8]], _ w: Int, _ h: Int) -> Bool {
        let ids = thumbs.keys.sorted { ($0 * 7919) % 31 < ($1 * 7919) % 31 }
        let clock = Date()
        for id in ids {
            guard let f = db.frame(id: id), let pic = FrameImages.shared.image(f.source) else { print("frame \(id) did not load"); return false }
            guard pic.width == Compaction.even(w), pic.height == Compaction.even(h) else { print("frame \(id) is \(pic.width)x\(pic.height)"); return false }
            guard let want = thumbs[id], Compaction.similar(FrameImages.comparable(pic), want) else {
                print("frame \(id) looks different from its screenshot"); return false
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
