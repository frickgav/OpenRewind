using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenRewind;

/// Drawing helpers for the overlay: rounded shapes, text, keycaps.
static class Draw
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, RectangleF r, float radius, Color c)
    {
        using var p = Round(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static void Stroke(Graphics g, RectangleF r, float radius, Color c, float width = 1)
    {
        using var p = Round(r, radius);
        using var pen = new Pen(c, width);
        g.DrawPath(pen, p);
    }

    public static Color White(double opacity) => Color.FromArgb((int)(Math.Clamp(opacity, 0, 1) * 255), 255, 255, 255);
    public static Color Black(double opacity) => Color.FromArgb((int)(Math.Clamp(opacity, 0, 1) * 255), 0, 0, 0);
    public static Color WithAlpha(Color c, double opacity) => Color.FromArgb((int)(Math.Clamp(opacity, 0, 1) * 255), c);

    static readonly StringFormat Left = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces, Trimming = StringTrimming.EllipsisCharacter };

    public static SizeF Measure(Graphics g, string s, Font f) => g.MeasureString(s, f, int.MaxValue, Left);

    /// One line of text, cut with "…" when wider than `maxWidth`.
    public static void Text(Graphics g, string s, Font f, Color c, float x, float y, float maxWidth = 0)
    {
        using var b = new SolidBrush(c);
        if (maxWidth > 0) g.DrawString(s, f, b, new RectangleF(x, y, maxWidth, f.GetHeight(g) * 1.3f), Left);
        else g.DrawString(s, f, b, x, y, Left);
    }

    public static void TextCentered(Graphics g, string s, Font f, Color c, float cx, float cy)
    {
        var sz = Measure(g, s, f);
        Text(g, s, f, c, cx - sz.Width / 2, cy - sz.Height / 2);
    }

    /// Wrapped text inside a box.
    public static void Wrapped(Graphics g, string s, Font f, Color c, RectangleF box, bool center = false)
    {
        using var b = new SolidBrush(c);
        using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisWord, Alignment = center ? StringAlignment.Center : StringAlignment.Near };
        g.DrawString(s, f, b, box, fmt);
    }

    /// A key drawn like a keyboard key. Returns its width.
    public static float Keycap(Graphics g, string key, Font f, float x, float midY, float k)
    {
        var sz = Measure(g, key, f);
        float w = Math.Max(18 * k, sz.Width + 10 * k), h = sz.Height + 3 * k;
        var r = new RectangleF(x, midY - h / 2, w, h);
        Fill(g, r, 4 * k, White(0.12));
        Stroke(g, r, 4 * k, White(0.18));
        Text(g, key, f, White(0.85), r.X + (w - sz.Width) / 2, r.Y + (h - sz.Height) / 2);
        return w;
    }
}
