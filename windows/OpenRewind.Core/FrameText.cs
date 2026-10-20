using System.Drawing;

namespace OpenRewind;

/// A word read off a screenshot, with where it is as fractions of the picture (0–1, top-left origin).
public record OcrWord(string Text, RectangleF Box, int Line);

/// The words on a screenshot in reading order, for selecting and copying text right on the timeline.
/// A selection runs from one word to another in reading order, like dragging across text in a document.
public sealed class FrameText
{
    /// Line by line from the top, each left to right.
    public IReadOnlyList<OcrWord> Words { get; }

    public FrameText(IEnumerable<IEnumerable<(string Text, RectangleF Box)>> lines)
    {
        var ls = lines.Select(l => l.Where(w => w.Text.Trim().Length > 0).OrderBy(w => w.Box.X).ToList())
                      .Where(l => l.Count > 0).ToList();
        // Top to bottom; lines at about the same height (side-by-side columns) left to right.
        ls.Sort((a, b) =>
        {
            float ay = a[0].Box.Y, by = b[0].Box.Y, h = Math.Min(a[0].Box.Height, b[0].Box.Height);
            return Math.Abs(ay - by) > h / 2 ? ay.CompareTo(by) : a[0].Box.X.CompareTo(b[0].Box.X);
        });
        var words = new List<OcrWord>();
        for (int i = 0; i < ls.Count; i++)
            foreach (var w in ls[i]) words.Add(new OcrWord(w.Text.Trim(), w.Box, i));
        Words = words;
    }

    public bool IsEmpty => Words.Count == 0;

    /// The word under `p` (fractions), or -1. `slop` widens each word a little so gaps between words count.
    public int WordAt(PointF p, float slop = 0.004f)
    {
        for (int i = 0; i < Words.Count; i++)
        {
            var r = Words[i].Box;
            r.Inflate(slop, slop / 2);
            if (r.Contains(p)) return i;
        }
        return -1;
    }

    /// The word a drag at `p` reaches: on the line nearest to `p`, the last word starting left of it
    /// (or the line's first word). -1 when there are no words.
    public int NearestWord(PointF p)
    {
        if (Words.Count == 0) return -1;
        int at = WordAt(p);
        if (at >= 0) return at;
        // The nearest line by vertical distance.
        int bestLine = -1; float bestD = float.MaxValue;
        foreach (var w in Words)
        {
            float d = p.Y < w.Box.Top ? w.Box.Top - p.Y : p.Y > w.Box.Bottom ? p.Y - w.Box.Bottom : 0;
            if (d < bestD) { bestD = d; bestLine = w.Line; }
        }
        int pick = -1;
        for (int i = 0; i < Words.Count; i++)
        {
            if (Words[i].Line != bestLine) continue;
            if (pick < 0 || Words[i].Box.Left <= p.X) pick = i;
        }
        return pick;
    }

    /// The text from word `a` to word `b` (either order): spaces between words, a new line between lines.
    public string TextOf(int a, int b)
    {
        if (Words.Count == 0 || a < 0 || b < 0) return "";
        var (from, to) = Order(a, b);
        var sb = new System.Text.StringBuilder();
        for (int i = from; i <= to; i++)
        {
            if (i > from) sb.Append(Words[i].Line != Words[i - 1].Line ? '\n' : ' ');
            sb.Append(Words[i].Text);
        }
        return sb.ToString();
    }

    /// All the text, line by line.
    public string AllText => Words.Count == 0 ? "" : TextOf(0, Words.Count - 1);

    /// One box per line covering the selected words from `a` to `b`, for drawing the selection.
    public List<RectangleF> Boxes(int a, int b)
    {
        var out_ = new List<RectangleF>();
        if (Words.Count == 0 || a < 0 || b < 0) return out_;
        var (from, to) = Order(a, b);
        RectangleF? cur = null;
        int line = -1;
        for (int i = from; i <= to; i++)
        {
            var w = Words[i];
            if (cur is RectangleF r && w.Line == line) cur = RectangleF.Union(r, w.Box);
            else
            {
                if (cur is RectangleF done) out_.Add(done);
                cur = w.Box;
                line = w.Line;
            }
        }
        if (cur is RectangleF last) out_.Add(last);
        return out_;
    }

    (int, int) Order(int a, int b)
    {
        a = Math.Clamp(a, 0, Words.Count - 1);
        b = Math.Clamp(b, 0, Words.Count - 1);
        return a <= b ? (a, b) : (b, a);
    }
}
