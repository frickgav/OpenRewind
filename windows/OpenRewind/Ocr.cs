using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace OpenRewind;

/// On-device text recognition with the OCR engine built into Windows 10/11 (Windows.Media.Ocr).
/// Uses the user's display languages; each needs its Windows OCR language pack (most have it).
static class Ocr
{
    static readonly Lazy<OcrEngine?> engine = new(() =>
    {
        var e = OcrEngine.TryCreateFromUserProfileLanguages();
        if (e == null) Log.Write("no Windows OCR language installed → screen text needs accessibility. Add one in Settings → Time & language → Language");
        return e;
    });

    public static bool Available => engine.Value != null;

    /// Lines top-to-bottom, left-to-right.
    public static string Recognize(Bitmap image)
    {
        if (engine.Value is not OcrEngine e) return "";
        try
        {
            using var sb = ToSoftwareBitmap(image, (int)OcrEngine.MaxImageDimension);
            var result = e.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();
            var lines = result.Lines.Select(l =>
            {
                var r = l.Words.Count > 0 ? l.Words[0].BoundingRect : default;
                return (Y: r.Y, X: r.X, H: r.Height, Text: l.Text);
            }).ToList();
            lines.Sort((a, b) => Math.Abs(a.Y - b.Y) > Math.Max(4, Math.Min(a.H, b.H) / 2) ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            return string.Join("\n", lines.Select(l => l.Text));
        }
        catch (Exception ex)
        {
            Log.Write($"ocr failed: {ex.Message}");
            return "";
        }
    }

    static SoftwareBitmap ToSoftwareBitmap(Bitmap image, int maxDim)
    {
        Bitmap src = image;
        if (image.Width > maxDim || image.Height > maxDim)
            src = ImageTools.Scaled(image, (double)maxDim / Math.Max(image.Width, image.Height));
        try
        {
            var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[data.Stride * src.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, src.Width, src.Height, BitmapAlphaMode.Premultiplied);
                sb.CopyFromBuffer(bytes.AsBuffer());
                return sb;
            }
            finally { src.UnlockBits(data); }
        }
        finally { if (!ReferenceEquals(src, image)) src.Dispose(); }
    }
}
