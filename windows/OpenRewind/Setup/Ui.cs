using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace OpenRewind;

/// Look shared by the setup window, Blocked Apps and the tutorial: dark, rounded, the website's blue.
static class Ui
{
    public static readonly Color Bg = Color.FromArgb(18, 18, 22);
    public static readonly Color Card = Color.FromArgb(30, 30, 36);
    public static readonly Color CardHi = Color.FromArgb(40, 40, 48);
    public static readonly Color Stroke = Color.FromArgb(52, 52, 60);
    public static readonly Color Accent = Color.FromArgb(12, 90, 250);
    public static readonly Color AccentHi = Color.FromArgb(52, 118, 255);
    public static readonly Color Text = Color.White;
    public static readonly Color Dim = Color.FromArgb(170, 170, 182);
    public static readonly Color Faint = Color.FromArgb(120, 120, 132);
    public static readonly Color Good = Color.FromArgb(48, 209, 88);
    public static readonly Color Warn = Color.FromArgb(255, 159, 10);

    static readonly Dictionary<(float, FontStyle), Font> fonts = new();

    /// Segoe UI at `px` logical pixels (scaled by the caller's DPI factor).
    public static Font F(float px, bool bold = false)
    {
        var key = (px, bold ? FontStyle.Bold : FontStyle.Regular);
        if (!fonts.TryGetValue(key, out var f)) fonts[key] = f = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", px, key.Item2, GraphicsUnit.Pixel);
        return f;
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public static void Fill(Graphics g, RectangleF r, float radius, Color c, Color? stroke = null)
    {
        using var path = Round(r, radius);
        using (var b = new SolidBrush(c)) g.FillPath(b, path);
        if (stroke is Color s) using (var pen = new Pen(s)) g.DrawPath(pen, path);
    }

    /// A ✓ in a rounded box, filled blue when on.
    public static void Check(Graphics g, RectangleF r, bool on)
    {
        Fill(g, r, r.Width * 0.28f, on ? Accent : Color.FromArgb(0, 0, 0, 0), on ? Accent : Faint);
        if (!on) return;
        using var pen = new Pen(Color.White, Math.Max(2, r.Width / 9)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(r.X + r.Width * 0.26f, r.Y + r.Height * 0.52f),
            new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.69f),
            new PointF(r.X + r.Width * 0.75f, r.Y + r.Height * 0.33f),
        });
    }

    public static Bitmap? IconBitmap(string? exePath, int size)
    {
        if (exePath == null || !File.Exists(exePath)) return null;
        try
        {
            using var ico = Icon.ExtractAssociatedIcon(exePath);
            if (ico == null) return null;
            using var big = ico.ToBitmap();
            var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(big, 0, 0, size, size);
            return bmp;
        }
        catch { return null; }
    }
}

/// Rounded button: blue for the main action, gray otherwise.
sealed class PillButton : Control
{
    public bool Primary { get; set; }
    bool hover, down;

    public PillButton(string text, bool primary = false)
    {
        Text = text;
        Primary = primary;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        ForeColor = Color.White;
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        var c = Primary ? (hover ? Ui.AccentHi : Ui.Accent) : (hover ? Ui.CardHi : Ui.Card);
        if (down) c = ControlPaint.Dark(c, 0.1f);
        Ui.Fill(g, r, Height / 2f, Enabled ? c : Ui.Card, Primary ? null : Ui.Stroke);
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), Enabled ? ForeColor : Ui.Faint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// One choice in a list: optional icon, a title, a gray line under it, and a check box on the right.
sealed class CheckRow : Control
{
    public string Title { get; set; }
    public string? Detail { get; set; }
    public Image? Icon { get; set; }
    public object? Tag2 { get; set; }
    /// Shows an × instead of a check box; clicking it raises `Removed`.
    public bool Removable { get; set; }
    public event Action<CheckRow>? CheckedChanged;
    public event Action<CheckRow>? Removed;
    bool on, hover;
    readonly float k;

    public CheckRow(string title, string? detail, bool on, float k)
    {
        Title = title;
        Detail = detail;
        this.on = on;
        this.k = k;
        Height = (int)((detail == null ? 44 : 58) * k);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Ui.Bg;
    }

    public bool Checked
    {
        get => on;
        set { if (on == value) return; on = value; Invalidate(); CheckedChanged?.Invoke(this); }
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Removable) Removed?.Invoke(this); else Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 2 * k, Width - 1, Height - 4 * k);
        Ui.Fill(g, r, 10 * k, hover ? Ui.CardHi : Ui.Card);
        float x = 14 * k;
        if (Icon != null)
        {
            float s = 24 * k;
            g.DrawImage(Icon, x, r.Y + (r.Height - s) / 2, s, s);
            x += s + 12 * k;
        }
        float right = Removable ? 40 * k : 48 * k;
        var titleFont = Ui.F(14.5f * k, true);
        var tr = new Rectangle((int)x, (int)(Detail == null ? r.Y : r.Y + 8 * k), (int)(Width - x - right), (int)(Detail == null ? r.Height : 22 * k));
        TextRenderer.DrawText(g, Title, titleFont, tr, Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        if (Detail != null)
        {
            var dr = new Rectangle((int)x, (int)(r.Y + 29 * k), (int)(Width - x - right), (int)(20 * k));
            TextRenderer.DrawText(g, Detail, Ui.F(12.5f * k), dr, Ui.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
        if (Removable)
        {
            float cx = Width - 22 * k, cy = r.Y + r.Height / 2, s = 5 * k;
            using var pen = new Pen(hover ? Ui.Text : Ui.Dim, 2 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
            g.DrawLine(pen, cx - s, cy + s, cx + s, cy - s);
        }
        else
        {
            float s = 20 * k;
            Ui.Check(g, new RectangleF(Width - 16 * k - s, r.Y + (r.Height - s) / 2, s, s), on);
        }
    }
}

/// A vertical list that scrolls, made of rows that fill its width.
sealed class RowList : Panel
{
    readonly float k;

    public RowList(float k)
    {
        this.k = k;
        AutoScroll = true;
        BackColor = Ui.Bg;
        DoubleBuffered = true;
    }

    public void SetRows(IEnumerable<Control> rows)
    {
        SuspendLayout();
        foreach (Control c in Controls.Cast<Control>().ToList()) { Controls.Remove(c); }
        foreach (var r in rows) Controls.Add(r);
        ResumeLayout();
        Relayout();
    }

    public void Relayout()
    {
        int y = -VerticalScroll.Value;
        int w = ClientSize.Width - (int)(4 * k);
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            c.SetBounds(0, y, w, c.Height);
            y += c.Height;
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
}
