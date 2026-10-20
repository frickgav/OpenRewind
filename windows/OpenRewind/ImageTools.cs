using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OpenRewind;

static class ImageTools
{
    /// 64-bit difference hash (dHash). Close screens give close hashes.
    public static ulong DHash(Bitmap image)
    {
        const int w = 9, h = 8;
        var px = Gray(image, w, h, InterpolationMode.Bilinear);
        ulong hash = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w - 1; x++)
            {
                hash <<= 1;
                if (px[y * w + x] > px[y * w + x + 1]) hash |= 1;
            }
        return hash;
    }

    /// Small grayscale thumbnail for change detection. 9x8 is too coarse to see
    /// a new line of text, so this keeps enough detail (192x120) to notice it.
    public static byte[] Thumbnail(Bitmap image) => Gray(image, 192, 120, InterpolationMode.Low);

    /// Like Thumbnail, but averaged smoothly, so two copies of a picture that differ only by video coding (or
    /// by a pixel cut off an odd edge) give nearly the same thumbnail. Used to check compacted videos.
    public static byte[] SmoothThumbnail(Bitmap image) => Gray(image, 192, 120, InterpolationMode.HighQualityBilinear);

    /// Number of thumbnail pixels that changed by more than `tolerance` gray levels.
    public static int ChangedPixels(byte[] a, byte[] b, int tolerance = 8)
    {
        if (a.Length != b.Length) return int.MaxValue;
        int n = 0;
        for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > tolerance) n++;
        return n;
    }

    static byte[] Gray(Bitmap image, int w, int h, InterpolationMode mode)
    {
        using var small = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = mode;
            g.DrawImage(image, new Rectangle(0, 0, w, h));
        }
        var data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var raw = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            var out_ = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = y * data.Stride + x * 4;
                    out_[y * w + x] = (byte)((raw[o + 2] * 299 + raw[o + 1] * 587 + raw[o] * 114) / 1000);
                }
            return out_;
        }
        finally { small.UnlockBits(data); }
    }

    public static Bitmap Scaled(Bitmap image, double scale)
    {
        if (scale >= 1) return (Bitmap)image.Clone();
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        var out_ = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(out_);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(image, new Rectangle(0, 0, w, h));
        return out_;
    }

    /// A saved screenshot (still or video frame) as a JPEG for the AI: at most 1568 pixels on the long
    /// side, which is what the providers shrink images to anyway. Null if it is gone or unreadable.
    public static byte[]? ForAi(FrameSource f) => FrameImages.Jpeg(f, maxSide: 1568, quality: 75);

    public static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static void WriteJpeg(Bitmap image, string path, long quality = 70)
    {
        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        var tmp = path + ".tmp";
        image.Save(tmp, JpegCodec, ps);
        File.Move(tmp, path, overwrite: true);
    }
}
