using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenRewind;

/// A small animated demo of the app for the setup window: open the timeline with the hotkey,
/// scroll back in time, search. Loops every 12 seconds. Drawn by hand, no video file needed.
sealed class DemoView : Control
{
    readonly float k;
    readonly string hotkey;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    readonly DateTime start = DateTime.UtcNow;
    const double Scene = 4.0;

    static readonly Color[] AppColors =
    {
        Color.FromArgb(10, 132, 255), Color.FromArgb(191, 90, 242), Color.FromArgb(48, 209, 88),
        Color.FromArgb(255, 159, 10), Color.FromArgb(255, 69, 58), Color.FromArgb(100, 210, 255),
    };
    static readonly string[] Steps = { "Open your timeline", "Scroll back in time", "Search anything" };

    public DemoView(float k, string hotkey)
    {
        this.k = k;
        this.hotkey = hotkey;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Bg;
        timer.Tick += (_, _) => Invalidate();
        VisibleChanged += (_, _) => timer.Enabled = Visible;
    }

    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        double t = (DateTime.UtcNow - start).TotalSeconds % (Scene * 3);
        int scene = (int)(t / Scene);
        double st = t - scene * Scene;

        // Step tabs along the top.
        float tabW = (Width - 16 * k) / 3f;
        for (int i = 0; i < 3; i++)
        {
            var r = new RectangleF(i * (tabW + 8 * k), 0, tabW, 34 * k);
            bool on = i == scene;
            Ui.Fill(g, r, 17 * k, on ? Ui.Accent : Ui.Card);
            if (on)
            {
                // Progress of this step.
                using var clip = Ui.Round(r, 17 * k);
                var old = g.Clip;
                g.SetClip(clip);
                using (var b = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillRectangle(b, r.X, r.Y, (float)(r.Width * st / Scene), r.Height);
                g.Clip = old;
            }
            TextRenderer.DrawText(g, $"{i + 1}  {Steps[i]}", Ui.F(13 * k, on), Rectangle.Round(r), on ? Color.White : Ui.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        var screen = new RectangleF(0, 48 * k, Width - 1, Height - 49 * k);
        Ui.Fill(g, screen, 14 * k, Color.FromArgb(24, 26, 32), Ui.Stroke);
        using var screenClip = Ui.Round(screen, 14 * k);
        g.SetClip(screenClip);
        switch (scene)
        {
            case 0: DrawOpen(g, screen, st); break;
            case 1: DrawScrub(g, screen, st); break;
            default: DrawSearch(g, screen, st); break;
        }
        g.ResetClip();
    }

    // Scene 1: a normal desktop, the hotkey is pressed, the timeline fades in.
    void DrawOpen(Graphics g, RectangleF s, double t)
    {
        DrawDesktop(g, s, 0);
        float fade = (float)Math.Clamp((t - 1.6) / 0.5, 0, 1);
        if (fade > 0) DrawOverlay(g, s, 0.96, fade, null, 0);

        // Keycaps, pressed at 1.0–1.5 s.
        var keys = hotkey.Split('+');
        bool down = t is > 1.0 and < 1.6;
        float kh = 44 * k, gap = 8 * k;
        var widths = keys.Select(x => Math.Max(kh, TextRenderer.MeasureText(x, Ui.F(16 * k, true)).Width + 26 * k)).ToArray();
        float total = widths.Sum() + gap * (keys.Length - 1);
        float x = s.X + (s.Width - total) / 2, y = s.Y + s.Height / 2 - kh / 2 + (fade > 0 ? s.Height * 0.3f * fade : 0);
        float alpha = 1 - fade;
        if (alpha <= 0) return;
        for (int i = 0; i < keys.Length; i++)
        {
            var r = new RectangleF(x, y + (down ? 3 * k : 0), widths[i], kh);
            if (!down) Ui.Fill(g, new RectangleF(r.X, r.Y + 4 * k, r.Width, r.Height), 9 * k, Color.FromArgb((int)(255 * alpha), 8, 8, 10));
            Ui.Fill(g, r, 9 * k, Color.FromArgb((int)(255 * alpha), down ? Ui.Accent : Color.FromArgb(58, 58, 66)));
            TextRenderer.DrawText(g, keys[i], Ui.F(16 * k, true), Rectangle.Round(r), Color.FromArgb((int)(255 * alpha), 255, 255, 255),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            x += widths[i] + gap;
        }
    }

    // Scene 2: the playhead slides back along the bar and the screenshot changes with it.
    void DrawScrub(Graphics g, RectangleF s, double t)
    {
        double p = 0.96 - 0.8 * Ease(Math.Clamp((t - 0.3) / 3.2, 0, 1));
        DrawOverlay(g, s, p, 1, null, 0);
        // A mouse wheel hint next to the bar.
        float cx = s.Right - 46 * k, cy = s.Bottom - 110 * k;
        Ui.Fill(g, new RectangleF(cx - 11 * k, cy - 17 * k, 22 * k, 34 * k), 11 * k, Color.FromArgb(70, 255, 255, 255));
        float wheel = (float)((t * 3) % 1);
        Ui.Fill(g, new RectangleF(cx - 2 * k, cy - 10 * k + wheel * 8 * k, 4 * k, 8 * k), 2 * k, Color.White);
    }

    // Scene 3: a word is typed, results drop down, the word lights up on the screenshot.
    void DrawSearch(Graphics g, RectangleF s, double t)
    {
        const string word = "invoice";
        int chars = (int)Math.Clamp((t - 0.3) / 0.12, 0, word.Length);
        bool results = t > 1.4 && t < 2.6;
        bool jumped = t >= 2.6;
        DrawOverlay(g, s, jumped ? 0.42 : 0.96, 1, word[..chars], results ? 3 : 0, jumped);
    }

    /// The timeline overlay: search box, screenshot, timeline bar with app blocks and the playhead at `p` (0–1).
    void DrawOverlay(Graphics g, RectangleF s, double p, float alpha, string? query, int resultRows, bool highlight = false)
    {
        int a = (int)(255 * alpha);
        using (var dim = new SolidBrush(Color.FromArgb((int)(235 * alpha), 14, 14, 18))) g.FillRectangle(dim, s);

        // Search pill.
        var pill = new RectangleF(s.X + 18 * k, s.Y + 14 * k, Math.Min(260 * k, s.Width * 0.45f), 28 * k);
        Ui.Fill(g, pill, 14 * k, Color.FromArgb((int)(40 * alpha), 255, 255, 255), query != null ? Color.FromArgb(a, Ui.Accent) : null);
        var text = string.IsNullOrEmpty(query) ? "Search what you saw or heard" : query + (DateTime.UtcNow.Millisecond < 500 ? "|" : "");
        TextRenderer.DrawText(g, text, Ui.F(12.5f * k), Rectangle.Round(new RectangleF(pill.X + 12 * k, pill.Y, pill.Width - 14 * k, pill.Height)),
            Color.FromArgb(a, string.IsNullOrEmpty(query) ? Ui.Dim : Color.White), TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        // Screenshot at the playhead: a fake window whose color follows the app block under the playhead.
        var shot = new RectangleF(s.X + 18 * k, pill.Bottom + 12 * k, s.Width - 36 * k, s.Height - 150 * k);
        int app = AppAt(p);
        DrawFakeWindow(g, shot, app, alpha, highlight);

        // Results list under the search box.
        if (resultRows > 0)
        {
            var list = new RectangleF(pill.X, pill.Bottom + 6 * k, pill.Width + 60 * k, resultRows * 30 * k + 8 * k);
            Ui.Fill(g, list, 10 * k, Color.FromArgb(a, 38, 38, 46), Ui.Stroke);
            string[] lines = { "Invoice #1042 · Mail · 2:14 PM", "Invoices 2026 · Excel · 11:03 AM", "\"send the invoice\" · Meeting · Mon" };
            for (int i = 0; i < resultRows; i++)
            {
                var r = new RectangleF(list.X + 4 * k, list.Y + 4 * k + i * 30 * k, list.Width - 8 * k, 28 * k);
                if (i == 0) Ui.Fill(g, r, 7 * k, Color.FromArgb(a, Ui.Accent));
                TextRenderer.DrawText(g, lines[i], Ui.F(12 * k), Rectangle.Round(new RectangleF(r.X + 10 * k, r.Y, r.Width - 12 * k, r.Height)),
                    Color.FromArgb(a, 255, 255, 255), TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }

        // Timeline bar: blocks per app, time ticks, playhead.
        var bar = new RectangleF(s.X + 18 * k, s.Bottom - 70 * k, s.Width - 36 * k, 22 * k);
        for (int i = 0; i < Blocks.Length; i++)
        {
            float x0 = bar.X + (float)Blocks[i].From * bar.Width, x1 = bar.X + (float)Blocks[i].To * bar.Width;
            using var b = new SolidBrush(Color.FromArgb(a, AppColors[Blocks[i].App]));
            g.FillRectangle(b, x0, bar.Y, Math.Max(1, x1 - x0 - 2 * k), bar.Height);
        }
        using (var tick = new SolidBrush(Color.FromArgb((int)(140 * alpha), 255, 255, 255)))
            for (int i = 0; i <= 6; i++)
            {
                float x = bar.X + bar.Width * i / 6f;
                g.FillRectangle(tick, x, bar.Bottom + 8 * k, 1, 6 * k);
                TextRenderer.DrawText(g, $"{9 + i}:00", Ui.F(11 * k), new Point((int)(x - 14 * k), (int)(bar.Bottom + 16 * k)), Color.FromArgb((int)(150 * alpha), 255, 255, 255));
            }
        float px = bar.X + (float)p * bar.Width;
        using (var pen = new Pen(Color.FromArgb(a, 255, 255, 255), 2 * k)) g.DrawLine(pen, px, bar.Y - 10 * k, px, bar.Bottom + 4 * k);
        Ui.Fill(g, new RectangleF(px - 6 * k, bar.Y - 16 * k, 12 * k, 12 * k), 6 * k, Color.FromArgb(a, 255, 255, 255));
    }

    void DrawFakeWindow(Graphics g, RectangleF r, int app, float alpha, bool highlight)
    {
        int a = (int)(255 * alpha);
        Ui.Fill(g, r, 10 * k, Color.FromArgb(a, 236, 238, 243));
        var head = new RectangleF(r.X, r.Y, r.Width, 26 * k);
        using (var path = Ui.Round(head, 10 * k)) using (var b = new SolidBrush(Color.FromArgb(a, AppColors[app])))
        {
            g.FillPath(b, path);
            g.FillRectangle(b, head.X, head.Bottom - 10 * k, head.Width, 10 * k);
        }
        // Lines of "text", different per app so scrubbing visibly changes the picture.
        var rnd = new Random(app * 7919);
        float y = head.Bottom + 14 * k;
        for (int line = 0; y < r.Bottom - 16 * k; line++)
        {
            float w = (float)(0.35 + rnd.NextDouble() * 0.55) * (r.Width - 40 * k);
            var lr = new RectangleF(r.X + 20 * k, y, w, 9 * k);
            Ui.Fill(g, lr, 4 * k, Color.FromArgb(a, line == 0 ? 120 : 196, line == 0 ? 124 : 200, line == 0 ? 134 : 210));
            if (highlight && line == 2)
            {
                var hl = new RectangleF(lr.X - 4 * k, lr.Y - 5 * k, Math.Min(90 * k, lr.Width) + 8 * k, lr.Height + 10 * k);
                Ui.Fill(g, hl, 4 * k, Color.FromArgb((int)(110 * alpha), 255, 214, 10), Color.FromArgb(a, 255, 214, 10));
            }
            y += (line == 0 ? 26 : 18) * k;
        }
    }

    static readonly (double From, double To, int App)[] Blocks =
    {
        (0.00, 0.14, 0), (0.14, 0.30, 1), (0.30, 0.38, 2), (0.38, 0.55, 3), (0.55, 0.62, 0), (0.62, 0.80, 4), (0.80, 1.00, 5),
    };

    static int AppAt(double p) => Blocks.FirstOrDefault(b => p >= b.From && p < b.To, Blocks[^1]).App;

    static double Ease(double x) => x < 0.5 ? 2 * x * x : 1 - Math.Pow(-2 * x + 2, 2) / 2;

    /// The desktop before the timeline opens: two windows side by side.
    void DrawDesktop(Graphics g, RectangleF s, int app)
    {
        using (var bg = new LinearGradientBrush(s, Color.FromArgb(28, 44, 92), Color.FromArgb(18, 20, 30), 60f)) g.FillRectangle(bg, s);
        DrawFakeWindow(g, new RectangleF(s.X + 26 * k, s.Y + 24 * k, s.Width * 0.52f, s.Height * 0.62f), 5, 1, false);
        DrawFakeWindow(g, new RectangleF(s.X + s.Width * 0.44f, s.Y + 54 * k, s.Width * 0.5f, s.Height * 0.58f), app, 1, false);
    }
}
