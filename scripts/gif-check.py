#!/usr/bin/env python3
"""Check a GIF written by `OpenRewind gif-check <file>` (the Mac's GIF encoder, GifWriter.swift).

Decodes it with a small GIF decoder written here (no dependencies), plays it the way a browser does
(each picture drawn over the last, transparent pixels keep what was there) and compares every picture
and delay with the test pattern below, which GifWriter.writeCheck draws the same way.
Usage: python3 scripts/gif-check.py file.gif
"""
import sys

W, H = 320, 200
DURATIONS = [0.5, 0.25, 0.25, 1.0, 0.3]
# Frame 2 is the same as frame 1, so it only makes frame 1 last longer.
WANT_DELAYS = [50, 50, 100, 30]
CHECKER = [(255, 0, 0), (0, 128, 255), (20, 20, 20)]
NOISE = [(0, 0, 0), (255, 255, 255), (0, 200, 0), (200, 0, 200)]


def pattern(frame):
    """The test picture `frame` as a list of (r, g, b) rows."""
    rows = []
    s = 1
    for y in range(H):
        row = []
        for x in range(W):
            if frame <= 2:
                if frame >= 1 and 10 <= x < 40 and 10 <= y < 30:
                    row.append((255, 255, 255))
                else:
                    row.append((x * 255 // 319, y * 255 // 199, (x + y) * 255 // 518))
            elif frame == 3:
                row.append(CHECKER[(x // 8 + y // 8) % 3])
            else:
                s = (s * 1103515245 + 12345) & 0x7FFFFFFF
                row.append(NOISE[(s >> 16) & 3])
        rows.append(row)
    return rows


def lzw_decode(data, min_size, count):
    clear, end = 1 << min_size, (1 << min_size) + 1
    size = min_size + 1
    table = [[i] for i in range(clear)] + [[], []]
    out, prev = [], None
    bits = nbits = 0
    for byte in data:
        bits |= byte << nbits
        nbits += 8
        while nbits >= size:
            code = bits & ((1 << size) - 1)
            bits >>= size
            nbits -= size
            if code == clear:
                table = table[: clear + 2]
                size, prev = min_size + 1, None
                continue
            if code == end:
                assert len(out) == count, f"{len(out)} pixels, want {count}"
                return out
            if prev is None:
                entry = table[code]
            else:
                if code < len(table):
                    entry = table[code]
                    table.append(prev + entry[:1])
                else:
                    assert code == len(table), f"bad code {code}"
                    entry = prev + prev[:1]
                    table.append(entry)
                if len(table) == (1 << size) and size < 12:
                    size += 1
            out.extend(entry)
            prev = entry
    raise AssertionError("no end code")


def decode(b):
    assert b[:6] == b"GIF89a", "not a GIF89a"
    w, h = b[6] | b[7] << 8, b[8] | b[9] << 8
    assert b[10] & 0x80 == 0, "unexpected global palette"
    i = 13
    canvas = [[(0, 0, 0)] * w for _ in range(h)]
    frames, loop = [], False
    delay = transparent = None
    while True:
        tag = b[i]
        if tag == 0x3B:
            break
        if tag == 0x21:
            label = b[i + 1]
            i += 2
            blocks = []
            while b[i]:
                blocks.append(b[i + 1 : i + 1 + b[i]])
                i += 1 + b[i]
            i += 1
            if label == 0xF9:
                g = blocks[0]
                assert (g[0] >> 2) & 7 == 1, "disposal should be 1 (keep)"
                delay = g[1] | g[2] << 8
                transparent = g[3] if g[0] & 1 else None
            elif label == 0xFF and blocks[0] == b"NETSCAPE2.0":
                loop = blocks[1][1:3] == b"\x00\x00"
            continue
        assert tag == 0x2C, f"unexpected block {tag:#x} at {i}"
        x0, y0 = b[i + 1] | b[i + 2] << 8, b[i + 3] | b[i + 4] << 8
        rw, rh = b[i + 5] | b[i + 6] << 8, b[i + 7] | b[i + 8] << 8
        flags = b[i + 9]
        assert flags & 0x80, "every picture should have its own palette"
        assert not flags & 0x40, "not interlaced"
        n = 2 << (flags & 7)
        assert n <= 256 and transparent is not None and transparent < n
        i += 10
        palette = [tuple(b[i + 3 * k : i + 3 * k + 3]) for k in range(n)]
        i += 3 * n
        min_size = b[i]
        i += 1
        data = bytearray()
        while b[i]:
            assert b[i] <= 255
            data += b[i + 1 : i + 1 + b[i]]
            i += 1 + b[i]
        i += 1
        assert 0 <= x0 and x0 + rw <= w and 0 <= y0 and y0 + rh <= h, "picture outside the GIF"
        pixels = lzw_decode(data, min_size, rw * rh)
        for k, p in enumerate(pixels):
            if p != transparent:
                canvas[y0 + k // rw][x0 + k % rw] = palette[p]
        frames.append(([row[:] for row in canvas], delay))
        delay = transparent = None
    return w, h, frames, loop


def main(path):
    w, h, frames, loop = decode(open(path, "rb").read())
    assert (w, h) == (W, H), f"size {w}x{h}"
    assert loop, "should loop forever"
    delays = [d for _, d in frames]
    assert delays == WANT_DELAYS, f"delays {delays}, want {WANT_DELAYS}"
    # Shown pictures: frames 0, 1 (with 2), 3 and 4. The first two have thousands of colors, so they are
    # approximated; the others have only a few and must be exact.
    for got, want_frame, tolerance in zip([f for f, _ in frames], [0, 1, 3, 4], [40, 40, 0, 0]):
        want = pattern(want_frame)
        for y in range(H):
            for x in range(W):
                g, e = got[y][x], want[y][x]
                if any(abs(a - c) > tolerance for a, c in zip(g, e)):
                    sys.exit(f"frame {want_frame} pixel {x},{y}: {g}, want {e}")
        # The white box is exact even in the approximated frame (it covers much of what changed).
        if want_frame == 1:
            assert all(got[y][x] == (255, 255, 255) for y in range(10, 30) for x in range(10, 40)), "white box"
    print(f"GIF ok: {len(frames)} pictures, delays {delays}")


if __name__ == "__main__":
    main(sys.argv[1])
