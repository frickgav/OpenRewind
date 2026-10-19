using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Writes pictures into an MP4 with the Media Foundation sink writer: HEVC (H.265) when this PC has an
/// encoder for it, otherwise H.264, which every Windows 10/11 desktop has. Frame i is at i / Compaction.Fps s.
/// The pictures are turned into NV12 here, so the encoder gets the format it wants and no converter is needed.
sealed class Mp4Writer : IDisposable
{
    IMFSinkWriter? writer;
    readonly uint stream;
    readonly int width, height;
    int count;

    Mp4Writer(IMFSinkWriter writer, uint stream, int width, int height)
    {
        this.writer = writer; this.stream = stream; this.width = width; this.height = height;
    }

    /// `width` and `height` must be even.
    public static Mp4Writer Create(string path, int width, int height, bool hevc)
    {
        MF.Start();
        var attrs = MF.Attributes();
        IMFSinkWriter? w = null;
        IMFMediaType? output = null, input = null;
        try
        {
            attrs.SetUINT32(ref MF.READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            attrs.SetUINT32(ref MF.SINK_WRITER_DISABLE_THROTTLING, 1);
            attrs.SetGUID(ref MF.TRANSCODE_CONTAINERTYPE, ref MF.TranscodeContainerType_MPEG4);
            MF.Check(MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out w), "creating the video file");

            output = MF.MediaType();
            output.SetGUID(ref MF.MT_MAJOR_TYPE, ref MF.MediaType_Video);
            var codec = hevc ? MF.VideoFormat_HEVC : MF.VideoFormat_H264;
            output.SetGUID(ref MF.MT_SUBTYPE, ref codec);
            // A ceiling for encoders that ignore the quality setting: about 0.3 bits per pixel per frame.
            output.SetUINT32(ref MF.MT_AVG_BITRATE, (uint)Math.Clamp(width * (long)height * 3 / 10 * (long)Compaction.Fps, 100_000, 20_000_000));
            SetVideo(output, width, height);
            MF.Check(w.AddStream(output, out var stream), "adding the video stream");

            input = MF.MediaType();
            input.SetGUID(ref MF.MT_MAJOR_TYPE, ref MF.MediaType_Video);
            input.SetGUID(ref MF.MT_SUBTYPE, ref MF.VideoFormat_NV12);
            input.SetUINT32(ref MF.MT_DEFAULT_STRIDE, (uint)width);
            SetVideo(input, width, height);

            // Constant quality where the encoder supports it, else variable bit rate, else its defaults.
            // Always a keyframe every KeyframeInterval frames and no B-frames, so a frame decodes quickly.
            int hr = 0;
            foreach (var mode in new uint?[] { MF.RateControl_Quality, MF.RateControl_UnconstrainedVBR, null, uint.MaxValue })
            {
                IMFAttributes? p = null;
                if (mode != uint.MaxValue)
                {
                    p = MF.Attributes();
                    p.SetUINT32(ref MF.AVEncMPVGOPSize, Compaction.KeyframeInterval);
                    p.SetUINT32(ref MF.AVEncMPVDefaultBPictureCount, 0);
                    if (mode is uint m)
                    {
                        p.SetUINT32(ref MF.AVEncCommonRateControlMode, m);
                        if (m == MF.RateControl_Quality) p.SetUINT32(ref MF.AVEncCommonQuality, 70);
                    }
                }
                hr = w.SetInputMediaType(stream, input, p);
                MF.Release(p);
                if (hr >= 0) break;
            }
            MF.Check(hr, hevc ? "starting the HEVC encoder" : "starting the H.264 encoder");
            MF.Check(w.BeginWriting(), "starting to write the video");
            var made = new Mp4Writer(w, stream, width, height);
            w = null;
            return made;
        }
        finally
        {
            MF.Release(attrs); MF.Release(output); MF.Release(input);
            if (w != null) MF.Release(w);
        }
    }

    static void SetVideo(IMFMediaType t, int width, int height)
    {
        t.SetUINT32(ref MF.MT_INTERLACE_MODE, MF.Interlace_Progressive);
        t.SetUINT64(ref MF.MT_FRAME_SIZE, MF.Pack(width, height));
        t.SetUINT64(ref MF.MT_FRAME_RATE, MF.Pack((int)Compaction.Fps, 1));
        t.SetUINT64(ref MF.MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
    }

    public void Add(Bitmap picture)
    {
        var nv12 = Nv12.From(picture, width, height);
        var buffer = MF.Buffer(nv12.Length);
        IMFSample? sample = null;
        try
        {
            MF.Check(buffer.Lock(out var ptr, out _, out _), "locking a buffer");
            Marshal.Copy(nv12, 0, ptr, nv12.Length);
            buffer.Unlock();
            buffer.SetCurrentLength((uint)nv12.Length);
            sample = MF.Sample();
            sample.AddBuffer(buffer);
            long duration = (long)(MF.Second / Compaction.Fps);
            sample.SetSampleTime(count * duration);
            sample.SetSampleDuration(duration);
            MF.Check(writer!.WriteSample(stream, sample), "encoding a frame");
            count++;
        }
        finally
        {
            MF.Release(sample);
            MF.Release(buffer);
        }
    }

    public void Finish()
    {
        MF.Check(writer!.FinalizeWriting(), "finishing the video");
        MF.Release(writer);
        writer = null;
    }

    public void Dispose()
    {
        MF.Release(writer);
        writer = null;
    }
}

/// Reads frames back out of a video with the Media Foundation source reader. Keeps its place, so stepping
/// forward through a video decodes each frame once; going back seeks to the keyframe before (at most
/// Compaction.KeyframeInterval frames to decode). Not thread-safe: FrameImages locks around it.
sealed class Mp4Reader : IDisposable
{
    IMFSourceReader? reader;
    /// Size of the picture (the decoder may pad to a multiple of 16) and of the decoded planes.
    int width, height, codedWidth, codedHeight;
    /// Time of the last frame read, or -1 when the next read must seek.
    long last = -1;

    public int Width => width;
    public int Height => height;

    public static Mp4Reader Open(string path)
    {
        MF.Start();
        MF.Check(MF.MFCreateSourceReaderFromURL(path, null, out var reader), "opening the video");
        var r = new Mp4Reader { reader = reader };
        try
        {
            reader.SetStreamSelection(MF.SOURCE_READER_ALL_STREAMS, false);
            MF.Check(reader.SetStreamSelection(MF.SOURCE_READER_FIRST_VIDEO_STREAM, true), "finding the video stream");
            var want = MF.MediaType();
            try
            {
                want.SetGUID(ref MF.MT_MAJOR_TYPE, ref MF.MediaType_Video);
                want.SetGUID(ref MF.MT_SUBTYPE, ref MF.VideoFormat_NV12);
                MF.Check(reader.SetCurrentMediaType(MF.SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, want), "starting the decoder");
            }
            finally { MF.Release(want); }
            r.ReadType();
            return r;
        }
        catch
        {
            r.Dispose();
            throw;
        }
    }

    /// Picture and plane sizes of the decoder's output.
    void ReadType()
    {
        MF.Check(reader!.GetCurrentMediaType(MF.SOURCE_READER_FIRST_VIDEO_STREAM, out var t), "reading the video format");
        try
        {
            MF.Check(t.GetUINT64(ref MF.MT_FRAME_SIZE, out var size), "reading the frame size");
            codedWidth = (int)(size >> 32); codedHeight = (int)(size & 0xFFFFFFFF);
            (width, height) = (codedWidth, codedHeight);
            // The picture inside the padded planes.
            var area = new byte[16];
            if (t.GetBlob(ref MF.MT_MINIMUM_DISPLAY_APERTURE, area, 16, out var n) >= 0 && n == 16
                || t.GetBlob(ref MF.MT_GEOMETRIC_APERTURE, area, 16, out n) >= 0 && n == 16)
            {
                int cx = BitConverter.ToInt32(area, 8), cy = BitConverter.ToInt32(area, 12);
                if (cx > 0 && cy > 0 && cx <= codedWidth && cy <= codedHeight) (width, height) = (cx, cy);
            }
        }
        finally { MF.Release(t); }
    }

    /// Length of the video in seconds.
    public double Duration
    {
        get
        {
            var key = MF.PD_DURATION;
            return reader!.GetPresentationAttribute(MF.SOURCE_READER_MEDIASOURCE, ref key, out var v) >= 0 ? v.Value / (double)MF.Second : 0;
        }
    }

    /// The frame shown at `seconds`. `keep` gets frames decoded on the way that are close to it, by frame number,
    /// so stepping back and forth through them needs no more decoding.
    public Bitmap? Read(double seconds, Action<int, Bitmap>? keep = null)
    {
        long frame = (long)(MF.Second / Compaction.Fps);
        long target = (long)Math.Round(seconds * MF.Second);
        // Close ahead of where we are: keep reading. Otherwise seek to the keyframe before it.
        if (!(last >= 0 && target > last && target - last <= Compaction.KeyframeInterval * frame))
        {
            var g = Guid.Empty;
            var at = PropVariant.Int64(target);
            MF.Check(reader!.SetCurrentPosition(ref g, ref at), "seeking in the video");
            last = -1;
        }
        for (int i = 0; i < Compaction.KeyframeInterval * 3 + 10; i++)
        {
            MF.Check(reader!.ReadSample(MF.SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out var flags, out var ts, out var sample), "decoding a frame");
            try
            {
                if ((flags & MF.SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0) ReadType();
                if (sample == null)
                {
                    if ((flags & (MF.SOURCE_READERF_ENDOFSTREAM | MF.SOURCE_READERF_ERROR)) != 0) { last = -1; return null; }
                    continue;
                }
                last = ts;
                bool hit = ts >= target - frame / 2;
                // Keep the few frames just before the one asked for: the timeline steps back through them next.
                if (hit || (keep != null && target - ts <= 3 * frame))
                {
                    var bmp = ToBitmap(sample);
                    if (hit) return bmp;
                    if (bmp != null) keep!((int)Math.Round(ts / (double)frame), bmp);
                }
            }
            finally { MF.Release(sample); }
        }
        last = -1;
        return null;
    }

    Bitmap? ToBitmap(IMFSample sample)
    {
        MF.Check(sample.ConvertToContiguousBuffer(out var buffer), "reading a frame");
        try
        {
            MF.Check(buffer.Lock(out var ptr, out _, out var length), "locking a frame");
            byte[] data;
            try
            {
                data = new byte[length];
                Marshal.Copy(ptr, data, 0, (int)length);
            }
            finally { buffer.Unlock(); }
            // NV12: a full-size Y plane, then half-size interleaved U/V. The row stride may be wider than the picture.
            int stride = (int)(length * 2 / (3L * codedHeight));
            if (stride < width || (long)stride * codedHeight * 3 / 2 > length) return null;
            return Nv12.ToBitmap(data, stride, codedHeight, width, height);
        }
        finally { MF.Release(buffer); }
    }

    public void Dispose()
    {
        MF.Release(reader);
        reader = null;
    }
}

/// BGRA pictures to and from NV12 (BT.709, video range), the format the encoders and decoders use.
static class Nv12
{
    /// The picture as NV12 of exactly width x height (even): cut off or padded at the right and bottom.
    public static byte[] From(Bitmap picture, int width, int height)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.DrawImage(picture, new Rectangle(0, 0, picture.Width, picture.Height));  // pixels, whatever the DPI says
        }
        var bits = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        var px = new byte[bits.Stride * height];
        try { Marshal.Copy(bits.Scan0, px, 0, px.Length); }
        finally { bmp.UnlockBits(bits); }
        int stride = bits.Stride;
        var out_ = new byte[width * height * 3 / 2];
        int uv = width * height;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = y * stride + x * 4;
                int b = px[o], gg = px[o + 1], r = px[o + 2];
                out_[y * width + x] = (byte)((47 * r + 157 * gg + 16 * b + 128 >> 8) + 16);
            }
        for (int y = 0; y < height; y += 2)
            for (int x = 0; x < width; x += 2)
            {
                int r = 0, gg = 0, b = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int o = (y + dy) * stride + (x + dx) * 4;
                        b += px[o]; gg += px[o + 1]; r += px[o + 2];
                    }
                r /= 4; gg /= 4; b /= 4;
                int i = uv + y / 2 * width + x;
                out_[i] = Clamp((-26 * r - 87 * gg + 112 * b + 128 >> 8) + 128);
                out_[i + 1] = Clamp((112 * r - 102 * gg - 10 * b + 128 >> 8) + 128);
            }
        return out_;
    }

    /// Decoded NV12 planes (`stride` bytes a row, `rows` rows of Y) to a width x height picture.
    public static Bitmap ToBitmap(byte[] data, int stride, int rows, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        var px = new byte[bits.Stride * height];
        int uv = stride * rows;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int c = 298 * (data[y * stride + x] - 16);
                int i = uv + y / 2 * stride + (x & ~1);
                int d = data[i] - 128, e = data[i + 1] - 128;
                int o = y * bits.Stride + x * 4;
                px[o] = Clamp(c + 541 * d + 128 >> 8);
                px[o + 1] = Clamp(c - 55 * d - 136 * e + 128 >> 8);
                px[o + 2] = Clamp(c + 459 * e + 128 >> 8);
                px[o + 3] = 255;
            }
        Marshal.Copy(px, 0, bits.Scan0, px.Length);
        bmp.UnlockBits(bits);
        return bmp;
    }

    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
