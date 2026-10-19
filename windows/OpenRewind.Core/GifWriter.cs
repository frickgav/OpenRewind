namespace OpenRewind;

/// A small animated GIF encoder for exported clips, with no dependencies.
/// Each picture gets its own palette of up to 255 colors (the most used ones), and only the part that changed
/// since the last picture is written; unchanged pixels inside it are transparent. Screen recordings change
/// little from one screenshot to the next, so this keeps files small. Pictures that did not change at all
/// only make the one before them last longer. Loops forever.
public sealed class GifWriter
{
    readonly Stream output;
    public int Width { get; }
    public int Height { get; }
    /// The colors on screen after the last picture (0xRRGGBB), and the source pixels it came from.
    readonly int[] shown, source;
    bool first = true, finished;
    /// The last picture, written once we know how long it lasts.
    byte[]? pending;
    double pendingSeconds;
    /// Rounding left over from earlier delays, in 1/100 s, so a long clip does not drift.
    double carry;

    // Per-picture scratch space: a 15-bit color histogram and the palette index for each 15-bit color.
    readonly int[] counts = new int[32768];
    readonly long[] sumR = new long[32768], sumG = new long[32768], sumB = new long[32768];
    readonly int[] lookup = new int[32768];

    public GifWriter(Stream output, int width, int height)
    {
        if (width < 1 || height < 1 || width > 65535 || height > 65535) throw new ArgumentOutOfRangeException(nameof(width));
        this.output = output;
        Width = width;
        Height = height;
        shown = new int[width * height];
        source = new int[width * height];
        var head = new List<byte>();
        head.AddRange("GIF89a"u8.ToArray());
        Le16(head, width); Le16(head, height);
        head.Add(0); head.Add(0); head.Add(0);  // no global palette
        // NETSCAPE2.0 application block: loop forever.
        head.AddRange(new byte[] { 0x21, 0xFF, 0x0B });
        head.AddRange("NETSCAPE2.0"u8.ToArray());
        head.AddRange(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
        output.Write(head.ToArray());
    }

    /// Add a picture of Width×Height pixels in BGRA byte order (`stride` bytes per row) that stays `seconds`.
    public void AddFrame(ReadOnlySpan<byte> bgra, int stride, double seconds)
    {
        if (finished) throw new InvalidOperationException("finished");
        if (stride < Width * 4 || bgra.Length < stride * (Height - 1) + Width * 4) throw new ArgumentException("too few pixels", nameof(bgra));
        // Where this picture differs from the last one.
        int x0 = Width, y0 = Height, x1 = -1, y1 = -1;
        for (int y = 0; y < Height; y++)
        {
            var row = bgra.Slice(y * stride, Width * 4);
            int o = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int c = row[x * 4 + 2] << 16 | row[x * 4 + 1] << 8 | row[x * 4];
                if (!first && source[o + x] == c) continue;
                source[o + x] = c;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                y1 = y;
            }
        }
        if (x1 < 0) { pendingSeconds += seconds; return; }  // nothing changed: the last picture stays longer
        Flush();
        pending = Encode(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        pendingSeconds = seconds;
        first = false;
    }

    /// Write the last picture and the end of the file. The stream stays open.
    public void Finish()
    {
        if (finished) return;
        if (first)
        {
            // No pictures: one black pixel keeps the file valid.
            var black = new byte[Width * Height * 4];
            for (int i = 3; i < black.Length; i += 4) black[i] = 255;
            AddFrame(black, Width * 4, 0.1);
        }
        Flush();
        output.WriteByte(0x3B);
        output.Flush();
        finished = true;
    }

    void Flush()
    {
        if (pending == null) return;
        double cs = carry + pendingSeconds * 100;
        int delay = Math.Clamp((int)Math.Round(cs), 2, 65535);
        // Only rounding carries over: when a picture lasts longer than a GIF can say (655 s), the rest is dropped
        // instead of stretching the pictures after it.
        carry = delay == 65535 ? 0 : cs - delay;
        // Graphic control: keep the picture under the next one (so its transparent pixels show this one), delay, transparency.
        output.Write(new byte[] { 0x21, 0xF9, 0x04, (1 << 2) | 1, (byte)delay, (byte)(delay >> 8), pending[0], 0x00 });
        output.Write(pending, 1, pending.Length - 1);
        pending = null;
    }

    /// Image descriptor, palette and LZW data for one rectangle, after a leading byte with its transparent index.
    byte[] Encode(int rx, int ry, int rw, int rh)
    {
        // The most used colors in the rectangle, counted at 5 bits per channel.
        var used = new List<int>();
        for (int y = ry; y < ry + rh; y++)
            for (int x = rx; x < rx + rw; x++)
            {
                int c = source[y * Width + x];
                int key = (c >> 19 & 31) << 10 | (c >> 11 & 31) << 5 | (c >> 3 & 31);
                if (counts[key]++ == 0) used.Add(key);
                sumR[key] += c >> 16 & 255; sumG[key] += c >> 8 & 255; sumB[key] += c & 255;
            }
        var palette = Palette(used, rw * rh);
        int colors = palette.Length;

        int transparent = colors;
        var indices = new byte[rw * rh];
        int n_ = 0;
        for (int y = ry; y < ry + rh; y++)
            for (int x = rx; x < rx + rw; x++)
            {
                int o = y * Width + x, c = source[o];
                int idx = lookup[(c >> 19 & 31) << 10 | (c >> 11 & 31) << 5 | (c >> 3 & 31)];
                int q = palette[idx];
                if (!first && shown[o] == q) indices[n_++] = (byte)transparent;
                else { indices[n_++] = (byte)idx; shown[o] = q; }
            }
        foreach (var k in used) { counts[k] = 0; sumR[k] = sumG[k] = sumB[k] = 0; }

        int bits = 1;
        while ((1 << bits) < colors + 1) bits++;
        var out_ = new List<byte>(rw * rh / 2 + 1024) { (byte)transparent, 0x2C };
        Le16(out_, rx); Le16(out_, ry); Le16(out_, rw); Le16(out_, rh);
        out_.Add((byte)(0x80 | (bits - 1)));  // local palette of 2^bits colors
        for (int i = 0; i < 1 << bits; i++)
        {
            int p = i < colors ? palette[i] : 0;
            out_.Add((byte)(p >> 16)); out_.Add((byte)(p >> 8)); out_.Add((byte)p);
        }
        int codeSize = Math.Max(2, bits);
        out_.Add((byte)codeSize);
        new Lzw(out_).Compress(indices, codeSize + 1);
        out_.Add(0);  // end of image data
        return out_.ToArray();
    }

    /// Up to 255 colors for the 15-bit colors in `used`, and `lookup` filled with each one's palette index.
    /// Colors that cover much of the picture (backgrounds, text) keep their exact value; the rest are split
    /// into boxes by median cut and each box gets its average color.
    int[] Palette(List<int> used, int pixels)
    {
        const int Max = 255, MaxExact = 64;
        var colors = new List<int>();
        int Average(IEnumerable<int> keys)
        {
            long r = 0, g = 0, b = 0, n = 0;
            foreach (var k in keys) { r += sumR[k]; g += sumG[k]; b += sumB[k]; n += counts[k]; }
            return (int)(r / n) << 16 | (int)(g / n) << 8 | (int)(b / n);
        }
        if (used.Count <= Max)
        {
            foreach (var k in used) { lookup[k] = colors.Count; colors.Add(Average(new[] { k })); }
            return colors.ToArray();
        }
        used.Sort((a, b) => counts[b] != counts[a] ? counts[b].CompareTo(counts[a]) : a.CompareTo(b));
        int exact = 0;
        while (exact < MaxExact && (long)counts[used[exact]] * 512 >= pixels) { lookup[used[exact]] = colors.Count; colors.Add(Average(new[] { used[exact] })); exact++; }

        var boxes = new List<int[]> { used.Skip(exact).ToArray() };
        static int Channel(int key, int c) => key >> (10 - 5 * c) & 31;
        static (int Channel, int Range) Widest(int[] box)
        {
            int best = 0, range = -1;
            for (int c = 0; c < 3; c++)
            {
                int lo = 31, hi = 0;
                foreach (var k in box) { int v = Channel(k, c); if (v < lo) lo = v; if (v > hi) hi = v; }
                if (hi - lo > range) { range = hi - lo; best = c; }
            }
            return (best, range);
        }
        long Score(int[] box)
        {
            if (box.Length < 2) return 0;
            long n = 0;
            foreach (var k in box) n += counts[k];
            return (Widest(box).Range + 1) * (long)Math.Sqrt(n);
        }
        var scores = new List<long> { Score(boxes[0]) };
        while (exact + boxes.Count < Max)
        {
            // Split the box with the widest spread of colors, weighted by how many pixels it holds.
            int pick = -1; long best = 0;
            for (int i = 0; i < boxes.Count; i++) if (scores[i] > best) { best = scores[i]; pick = i; }
            if (pick < 0) break;
            var box = boxes[pick];
            int ch = Widest(box).Channel;
            Array.Sort(box, (a, b) => Channel(a, ch).CompareTo(Channel(b, ch)));
            long total = 0, half = 0;
            foreach (var k in box) total += counts[k];
            int cut = 1;
            for (; cut < box.Length - 1; cut++) { half += counts[box[cut - 1]]; if (half * 2 >= total) break; }
            boxes[pick] = box[..cut];
            boxes.Add(box[cut..]);
            scores[pick] = Score(boxes[pick]);
            scores.Add(Score(boxes[^1]));
        }
        foreach (var box in boxes)
        {
            foreach (var k in box) lookup[k] = colors.Count;
            colors.Add(Average(box));
        }
        return colors.ToArray();
    }

    static void Le16(List<byte> b, int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }

    /// GIF's variable-length LZW, after the well-known compress/GIFEncoder code (12-bit codes, open-addressing hash).
    sealed class Lzw
    {
        const int Bits = 12, MaxMaxCode = 1 << Bits, HSize = 5003;
        readonly List<byte> output;
        readonly int[] htab = new int[HSize], codetab = new int[HSize];
        readonly byte[] packet = new byte[256];
        int packetCount, nBits, maxCode, initBits, clearCode, eofCode, freeEnt, curAccum, curBits;
        bool clearFlag;

        public Lzw(List<byte> output) { this.output = output; }

        static int MaxCode(int bits) => (1 << bits) - 1;

        public void Compress(byte[] pixels, int initBits)
        {
            this.initBits = initBits;
            nBits = initBits;
            maxCode = MaxCode(nBits);
            clearCode = 1 << (initBits - 1);
            eofCode = clearCode + 1;
            freeEnt = clearCode + 2;
            int hshift = 0;
            for (int fc = HSize; fc < 65536; fc *= 2) hshift++;
            hshift = 8 - hshift;
            Array.Fill(htab, -1);
            Output(clearCode);
            int ent = pixels[0];
            for (int p = 1; p < pixels.Length; p++)
            {
                int c = pixels[p];
                int fcode = (c << Bits) + ent;
                int i = (c << hshift) ^ ent;
                if (htab[i] == fcode) { ent = codetab[i]; continue; }
                if (htab[i] >= 0)
                {
                    int disp = i == 0 ? 1 : HSize - i;
                    bool found = false;
                    do
                    {
                        if ((i -= disp) < 0) i += HSize;
                        if (htab[i] == fcode) { ent = codetab[i]; found = true; break; }
                    } while (htab[i] >= 0);
                    if (found) continue;
                }
                Output(ent);
                ent = c;
                if (freeEnt < MaxMaxCode) { codetab[i] = freeEnt++; htab[i] = fcode; }
                else ClearBlock();
            }
            Output(ent);
            Output(eofCode);
        }

        void ClearBlock()
        {
            Array.Fill(htab, -1);
            freeEnt = clearCode + 2;
            clearFlag = true;
            Output(clearCode);
        }

        void Output(int code)
        {
            curAccum &= (1 << curBits) - 1;
            curAccum = curBits > 0 ? curAccum | code << curBits : code;
            curBits += nBits;
            while (curBits >= 8) { Char((byte)curAccum); curAccum >>= 8; curBits -= 8; }
            // Wider codes once the table outgrows them; back to the start after a clear.
            if (freeEnt > maxCode || clearFlag)
            {
                if (clearFlag) { nBits = initBits; maxCode = MaxCode(nBits); clearFlag = false; }
                else { nBits++; maxCode = nBits == Bits ? MaxMaxCode : MaxCode(nBits); }
            }
            if (code == eofCode)
            {
                while (curBits > 0) { Char((byte)curAccum); curAccum >>= 8; curBits -= 8; }
                FlushPacket();
            }
        }

        void Char(byte b)
        {
            packet[packetCount++] = b;
            if (packetCount >= 254) FlushPacket();
        }

        void FlushPacket()
        {
            if (packetCount == 0) return;
            output.Add((byte)packetCount);
            for (int i = 0; i < packetCount; i++) output.Add(packet[i]);
            packetCount = 0;
        }
    }
}
