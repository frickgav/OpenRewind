using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace OpenRewind;

/// Full-screen timeline that floats over everything, on the screen with the mouse (Ctrl+Alt+T).
/// The Mac overlay is SwiftUI; this draws the same layout with GDI+.
sealed class OverlayForm : Form
{
    readonly TimelineModel model;
    readonly TextBox search;
    readonly System.Windows.Forms.Timer searchDebounce = new() { Interval = 200 };
    readonly System.Windows.Forms.Timer clock = new() { Interval = 1000 };
    Bitmap? backdrop;
    /// The window that was in front before the timeline opened; it gets the keyboard back on close.
    IntPtr previousWindow;
    float k = 1;  // DPI scale

    // Layout, recomputed on every paint.
    RectangleF searchPill, shot, bar;
    readonly List<(RectangleF Rect, Action Act)> hot = new();
    float? hoverX;
    double? dragStartV;
    int scrollAccum;
    int resultsScroll;

    static readonly Color Mic = Color.FromArgb(255, 159, 10), Sys = Color.FromArgb(48, 209, 88), Accent = Color.FromArgb(10, 132, 255);
    static readonly Color Yellow = Color.FromArgb(255, 214, 10);

    public OverlayForm(TimelineModel model)
    {
        this.model = model;
        Text = "OpenRewind Timeline";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(18, 18, 20);
        ForeColor = Color.White;
        Icon = Program.AppIcon;

        search = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(44, 44, 48),
            ForeColor = Color.White,
            PlaceholderText = "Search what you saw or heard",
            TabStop = false,
        };
        Controls.Add(search);
        search.TextChanged += (_, _) => { searchDebounce.Stop(); searchDebounce.Start(); };
        search.GotFocus += (_, _) => Invalidate();
        search.LostFocus += (_, _) => Invalidate();
        searchDebounce.Tick += (_, _) =>
        {
            searchDebounce.Stop();
            model.Query = search.Text;
            model.RunSearch();
        };
        clock.Tick += (_, _) => Invalidate();
        model.Changed += () => { if (Visible) Invalidate(); };
        HandleCreated += (_, _) => Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    // MARK: show / hide

    public void Toggle() { if (Visible) HideOverlay(); else ShowOverlay(); }

    public void ShowOverlay()
    {
        if (!Visible) previousWindow = Native.GetForegroundWindow();
        var screen = Screen.FromPoint(Cursor.Position);
        backdrop?.Dispose();
        backdrop = BlurredDesktop(screen.Bounds);
        Bounds = screen.Bounds;
        k = DeviceDpi / 96f;
        model.Reload();
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
        // Keys drive the timeline at once; / or Ctrl+F moves into the search box.
        ActiveControl = null;
        Focus();
        clock.Start();
    }

    /// Open the timeline with the keyboard in the search box.
    public void ShowSearch()
    {
        if (!Visible) ShowOverlay();
        FocusSearch();
    }

    public void HideOverlay()
    {
        model.StopAudio();
        clock.Stop();
        Hide();
        if (previousWindow != IntPtr.Zero) Native.SetForegroundWindow(previousWindow);
        previousWindow = IntPtr.Zero;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); k = DeviceDpi / 96f; Invalidate(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the overlay only hides it; the app keeps running in the tray.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideOverlay(); }
        base.OnFormClosing(e);
    }

    /// A soft, dark copy of the desktop behind the overlay (the Mac version blurs the real windows).
    static Bitmap? BlurredDesktop(Rectangle b)
    {
        try
        {
            using var full = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(full)) g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
            using var tiny = new Bitmap(Math.Max(1, b.Width / 24), Math.Max(1, b.Height / 24));
            using (var g = Graphics.FromImage(tiny)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(full, 0, 0, tiny.Width, tiny.Height); }
            var out_ = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(out_))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(tiny, new Rectangle(0, 0, b.Width, b.Height));
            }
            return out_;
        }
        catch { return null; }
    }

    // MARK: fonts

    readonly Dictionary<(float, FontStyle), Font> fonts = new();
    Font F(float px, bool bold = false)
    {
        var key = (px * k, bold ? FontStyle.Bold : FontStyle.Regular);
        if (!fonts.TryGetValue(key, out var f)) fonts[key] = f = new Font("Segoe UI", key.Item1, key.Item2, GraphicsUnit.Pixel);
        return f;
    }
    Font Semibold(float px)
    {
        var key = (px * k, (FontStyle)0x100);
        if (!fonts.TryGetValue(key, out var f))
        {
            try { f = new Font("Segoe UI Semibold", px * k, FontStyle.Regular, GraphicsUnit.Pixel); }
            catch { f = new Font("Segoe UI", px * k, FontStyle.Bold, GraphicsUnit.Pixel); }
            fonts[key] = f;
        }
        return f;
    }

    // MARK: painting

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        hot.Clear();

        if (backdrop != null) g.DrawImage(backdrop, ClientRectangle);
        using (var dim = new SolidBrush(Color.FromArgb(205, 14, 14, 16))) g.FillRectangle(dim, ClientRectangle);

        float padX = 28 * k, top = 22 * k, bottom = 16 * k, gap = 14 * k;
        float w = ClientSize.Width - 2 * padX;
        float y = top;
        DrawTopBar(g, new RectangleF(padX, y, w, 44 * k));
        y += 44 * k + gap;
        if (StaleMinutes() is int mins)
        {
            DrawStaleBanner(g, padX, y, w, mins);
            y += 34 * k + gap;
        }
        float hintsH = 18 * k, barH = 102 * k, controlsH = 36 * k;
        float shotH = ClientSize.Height - bottom - hintsH - gap - barH - gap - controlsH - gap - y;
        shot = new RectangleF(padX, y, w, Math.Max(40 * k, shotH));
        DrawScreenshot(g, shot);
        y = shot.Bottom + gap;
        DrawControls(g, new RectangleF(padX, y, w, controlsH));
        y += controlsH + gap;
        bar = new RectangleF(padX, y, w, barH);
        DrawBar(g, bar);
        y += barH + gap;
        DrawHints(g, padX, y + hintsH / 2);
        if (model.ShowResults) DrawResults(g);
        if (model.ShowHelp) DrawHelp(g);
    }

    // MARK: top bar

    void DrawTopBar(Graphics g, RectangleF r)
    {
        // Search on the left, the playhead time in the middle, help and close on the right.
        float pillW = Math.Min(460 * k, r.Width * 0.36f);
        searchPill = new RectangleF(r.X, r.Y + 3 * k, pillW, r.Height - 6 * k);
        bool focused = search.Focused;
        Draw.Fill(g, searchPill, searchPill.Height / 2, focused ? Color.FromArgb(58, 58, 62) : Color.FromArgb(44, 44, 48));
        Draw.Stroke(g, searchPill, searchPill.Height / 2, Draw.White(focused ? 0.35 : 0.12));
        // Magnifier.
        using (var pen = new Pen(Draw.White(0.6), 1.6f * k))
        {
            float cx = searchPill.X + 20 * k, cy = searchPill.Y + searchPill.Height / 2 - 1 * k, rr = 5.5f * k;
            g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
            g.DrawLine(pen, cx + rr * 0.7f, cy + rr * 0.7f, cx + rr * 1.5f, cy + rr * 1.5f);
        }
        hot.Add((searchPill, FocusSearch));

        // Right side of the pill: "/" hint, or match count and arrows.
        float right = searchPill.Right - 14 * k, mid = searchPill.Y + searchPill.Height / 2;
        if (model.Query.Length == 0 && search.Text.Length == 0)
        {
            if (!focused) right -= Draw.Keycap(g, "/", F(10), right - 18 * k, mid, k);
        }
        else
        {
            right = HotIcon(g, "✕", right, mid, () => { search.Text = ""; model.ClearSearch(); });
            if (model.Hits.Count > 0 && !model.ShowResults)
            {
                right = HotLabel(g, "List", right - 8 * k, mid, () => { model.ShowResults = true; FocusSearch(); });
                right = HotIcon(g, "▼", right - 6 * k, mid, () => model.NextHit(older: true));
                right = HotIcon(g, "▲", right - 2 * k, mid, () => model.NextHit(older: false));
                var count = $"{model.SelectedHit + 1} of {model.Hits.Count}";
                var sz = Draw.Measure(g, count, F(12));
                right -= sz.Width + 6 * k;
                Draw.Text(g, count, F(12), Draw.White(0.6), right, mid - sz.Height / 2);
            }
            else if (model.Hits.Count == 0 && !model.ShowResults && model.Query.Length > 0)
            {
                var sz = Draw.Measure(g, "No matches", F(12));
                right -= sz.Width + 6 * k;
                Draw.Text(g, "No matches", F(12), Draw.White(0.42), right, mid - sz.Height / 2);
            }
        }
        // The real text box sits inside the pill.
        var font = F(15);
        if (search.Font.Size != font.Size) search.Font = font;
        search.BackColor = focused ? Color.FromArgb(58, 58, 62) : Color.FromArgb(44, 44, 48);
        var box = new Rectangle((int)(searchPill.X + 36 * k), (int)(mid - search.PreferredHeight / 2f), (int)Math.Max(40 * k, right - searchPill.X - 44 * k), search.PreferredHeight);
        if (search.Bounds != box) search.Bounds = box;

        // Center: time and how long ago.
        var local = model.Playhead.ToLocalTime();
        var time = local.ToString("T", CultureInfo.CurrentCulture);
        var tf = Semibold(28);
        var ts = Draw.Measure(g, time, tf);
        Draw.Text(g, time, tf, Color.White, r.X + r.Width / 2 - ts.Width / 2, r.Y - 4 * k);
        var rel = Relative(model.Playhead);
        var rs = Draw.Measure(g, rel, F(12));
        Draw.Text(g, rel, F(12), Draw.White(0.6), r.X + r.Width / 2 - rs.Width / 2, r.Y + ts.Height - 6 * k);

        // Right: help and close.
        float bx = r.Right - 30 * k;
        RoundButton(g, new RectangleF(bx, r.Y + 7 * k, 30 * k, 30 * k), "✕", HideOverlay);
        RoundButton(g, new RectangleF(bx - 40 * k, r.Y + 7 * k, 30 * k, 30 * k), "?", () => { model.ShowHelp = !model.ShowHelp; Invalidate(); });
        var web = new RectangleF(bx - 80 * k, r.Y + 7 * k, 30 * k, 30 * k);
        RoundButton(g, web, "", OpenInBrowser);
        DrawOpenIcon(g, web);
    }

    float HotIcon(Graphics g, string glyph, float right, float mid, Action act)
    {
        var f = F(11);
        var sz = Draw.Measure(g, glyph, f);
        var r = new RectangleF(right - sz.Width - 6 * k, mid - 10 * k, sz.Width + 6 * k, 20 * k);
        Draw.Text(g, glyph, f, Draw.White(0.6), r.X + 3 * k, mid - sz.Height / 2);
        hot.Add((r, act));
        return r.X;
    }

    float HotLabel(Graphics g, string label, float right, float mid, Action act)
    {
        var f = Semibold(12);
        var sz = Draw.Measure(g, label, f);
        var r = new RectangleF(right - sz.Width, mid - 10 * k, sz.Width, 20 * k);
        Draw.Text(g, label, f, Color.White, r.X, mid - sz.Height / 2);
        hot.Add((r, act));
        return r.X;
    }

    void RoundButton(Graphics g, RectangleF r, string glyph, Action act)
    {
        bool hover = r.Contains(PointToClient(Cursor.Position));
        using (var b = new SolidBrush(Draw.White(hover ? 0.2 : 0.1))) g.FillEllipse(b, r);
        Draw.TextCentered(g, glyph, Semibold(12), Color.White, r.X + r.Width / 2, r.Y + r.Height / 2);
        hot.Add((r, act));
    }

    /// "Open outside": a box with an arrow leaving its top-right corner, drawn so no font is needed.
    void DrawOpenIcon(Graphics g, RectangleF r)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, h = 5.5f * k;
        using var pen = new Pen(Color.White, 1.6f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - 0.5f * k, cy - h), new PointF(cx - h, cy - h), new PointF(cx - h, cy + h),
                                  new PointF(cx + h, cy + h), new PointF(cx + h, cy + 0.5f * k) });
        g.DrawLine(pen, cx - 0.5f * k, cy + 0.5f * k, cx + h + 1.5f * k, cy - h - 1.5f * k);
        g.DrawLines(pen, new[] { new PointF(cx + 1.5f * k, cy - h - 1.5f * k), new PointF(cx + h + 1.5f * k, cy - h - 1.5f * k),
                                  new PointF(cx + h + 1.5f * k, cy - 1.5f * k) });
    }

    static string Relative(DateTime utc)
    {
        var s = (DateTime.UtcNow - utc).TotalSeconds;
        if (s < 60) return "just now";
        var day = utc.ToLocalTime().ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
        if (s < 3600) return $"{(int)(s / 60)} min ago · {day}";
        return $"{(int)(s / 3600)} h {(int)(s % 3600 / 60)} min ago · {day}";
    }

    // MARK: stale warning

    /// Minutes since the newest screenshot, when that is long enough to look like a gap.
    /// Counted to when the timeline opened, so the banner does not pop in while you look.
    int? StaleMinutes()
    {
        if (model.Frames.Count == 0) return null;
        var mins = (int)((model.LoadedAt - model.Frames[^1].Time).TotalMinutes);
        return mins >= 3 ? mins : null;
    }

    void DrawStaleBanner(Graphics g, float x, float y, float w, int mins)
    {
        var ago = mins >= 60 ? $"{mins / 60} h {mins % 60} min" : $"{mins} min";
        var text = $"⚠  Newest screenshot is from {ago} ago. Screen recording may be off, or the screen has not changed.";
        var sz = Draw.Measure(g, text, F(13));
        var r = new RectangleF(x + w / 2 - (sz.Width + 28 * k) / 2, y, sz.Width + 28 * k, 34 * k);
        Draw.Fill(g, r, 8 * k, Draw.WithAlpha(Yellow, 0.15));
        Draw.Text(g, text, F(13), Color.White, r.X + 14 * k, r.Y + (r.Height - sz.Height) / 2);
    }

    // MARK: screenshot

    /// A yellow box around each place the searched words appear on the screenshot.
    void DrawHighlights(Graphics g, RectangleF ir)
    {
        foreach (var h in model.Highlights)
        {
            var r = new RectangleF(ir.X + h.X * ir.Width, ir.Y + h.Y * ir.Height, h.Width * ir.Width, h.Height * ir.Height);
            r.Inflate(3 * k, 2 * k);
            Draw.Fill(g, r, 4 * k, Color.FromArgb(70, 255, 214, 10));
            Draw.Stroke(g, r, 4 * k, Color.FromArgb(255, 214, 10), 2 * k);
        }
    }

    /// The screenshot, with the spoken words at this moment shown like subtitles.
    void DrawScreenshot(Graphics g, RectangleF r)
    {
        if (model.Image is Image img)
        {
            float s = Math.Min(r.Width / img.Width, r.Height / img.Height);
            float iw = img.Width * s, ih = img.Height * s;
            var ir = new RectangleF(r.X + (r.Width - iw) / 2, r.Y + (r.Height - ih) / 2, iw, ih);
            using (var path = Draw.Round(ir, 12 * k))
            {
                var state = g.Save();
                g.SetClip(path);
                g.DrawImage(img, ir);
                DrawHighlights(g, ir);
                g.Restore(state);
            }
            Draw.Stroke(g, ir, 12 * k, Draw.White(0.12));
        }
        else
        {
            var empty = model.Frames.Count == 0;
            var title = empty ? "Nothing recorded yet" : "Screenshot not found";
            var sub = empty ? "Turn on Record Screen in the tray menu." : "The file for this moment was deleted or moved.";
            var card = new RectangleF(r.X + r.Width / 2 - 200 * k, r.Y + r.Height / 2 - 60 * k, 400 * k, 120 * k);
            Draw.Fill(g, card, 14 * k, Draw.White(0.08));
            Draw.TextCentered(g, empty ? "●" : "▢", F(30), Draw.White(0.42), card.X + card.Width / 2, card.Y + 32 * k);
            Draw.TextCentered(g, title, Semibold(16), Color.White, card.X + card.Width / 2, card.Y + 68 * k);
            Draw.TextCentered(g, sub, F(13), Draw.White(0.6), card.X + card.Width / 2, card.Y + 94 * k);
        }
        if (model.Caption is string c)
        {
            var box = new RectangleF(0, 0, Math.Min(900 * k, r.Width - 80 * k), 0);
            var sz = g.MeasureString(c, Semibold(17), (int)(box.Width - 32 * k));
            sz.Height = Math.Min(sz.Height, Semibold(17).GetHeight(g) * 3);
            var cr = new RectangleF(r.X + r.Width / 2 - (sz.Width + 32 * k) / 2, r.Bottom - 22 * k - sz.Height - 18 * k, sz.Width + 32 * k, sz.Height + 18 * k);
            Draw.Fill(g, cr, 10 * k, Draw.Black(0.78));
            Draw.Wrapped(g, c, Semibold(17), Color.White, new RectangleF(cr.X + 16 * k, cr.Y + 9 * k, sz.Width + 2, sz.Height), center: true);
        }
    }

    // MARK: controls row

    /// App on the left, audio in the middle, live + zoom on the right.
    void DrawControls(Graphics g, RectangleF r)
    {
        float mid = r.Y + r.Height / 2;
        if (model.Current is TimelineFrame f)
        {
            float x = r.X;
            var icon = AppIcons.Get(f.AppName);
            var ir = new RectangleF(x, mid - 14 * k, 28 * k, 28 * k);
            if (icon != null) g.DrawImage(icon, ir);
            else Draw.Fill(g, ir, 7 * k, Draw.White(0.14));
            // Same color as this app's blocks on the bar below.
            var dot = new RectangleF(ir.Right - 7 * k, ir.Bottom - 7 * k, 9 * k, 9 * k);
            using (var b = new SolidBrush(TimelineModel.AppColor(f.AppName))) g.FillEllipse(b, dot);
            using (var p = new Pen(Draw.Black(0.6), 1.5f * k)) g.DrawEllipse(p, dot);
            float tx = ir.Right + 10 * k, maxW = r.Width / 3 - 40 * k;
            var name = f.AppName.Length == 0 ? "Unknown app" : f.AppName;
            if (f.WindowName.Length > 0)
            {
                Draw.Text(g, name, Semibold(13), Color.White, tx, mid - 17 * k, maxW);
                Draw.Text(g, f.WindowName, F(12), Draw.White(0.6), tx, mid + 1 * k, maxW);
            }
            else Draw.Text(g, name, Semibold(13), Color.White, tx, mid - 9 * k, maxW);
        }

        // Audio buttons, centered.
        var here = model.AudioHere;
        if (here.Count == 0)
        {
            Draw.TextCentered(g, "No audio at this moment", F(12), Draw.White(0.42), r.X + r.Width / 2, mid);
        }
        else
        {
            var f13 = Semibold(12);
            var widths = here.Select(a => Draw.Measure(g, (a.IsInput ? "Microphone" : "System audio"), f13).Width + 48 * k).ToList();
            float x = r.X + r.Width / 2 - (widths.Sum() + 8 * k * (here.Count - 1)) / 2;
            for (int i = 0; i < here.Count; i++)
            {
                var a = here[i];
                bool on = model.PlayingChunk == a.Id;
                var br = new RectangleF(x, mid - 15 * k, widths[i], 30 * k);
                Draw.Fill(g, br, br.Height / 2, on ? Accent : Draw.White(0.14));
                Draw.Text(g, on ? "❚❚" : "▶", F(10), Color.White, br.X + 13 * k, mid - 7 * k);
                Draw.Text(g, a.IsInput ? "Microphone" : "System audio", f13, Color.White, br.X + 28 * k, mid - 8 * k);
                // Matches the audio lane color on the bar.
                using (var b = new SolidBrush(a.IsInput ? Mic : Sys)) g.FillEllipse(b, br.Right - 15 * k, mid - 3.5f * k, 7 * k, 7 * k);
                var capture = a;
                hot.Add((br, () => { if (model.PlayingChunk == capture.Id) { model.StopAudio(); Invalidate(); } else model.Play(capture); }));
                x += widths[i] + 8 * k;
            }
        }

        // Zoom picker and Live/Now, right-aligned.
        var steps = new (string Label, double Span)[] { ("5m", 300), ("15m", 900), ("1h", 3600), ("4h", 4 * 3600), ("12h", 12 * 3600) };
        float segW = 38 * k, pw = segW * steps.Length + 4 * k;
        var pr = new RectangleF(r.Right - pw, mid - 14 * k, pw, 28 * k);
        Draw.Fill(g, pr, pr.Height / 2, Draw.White(0.08));
        for (int i = 0; i < steps.Length; i++)
        {
            var s = steps[i];
            bool on = Math.Abs(model.Span - s.Span) < 1;
            var sr = new RectangleF(pr.X + 2 * k + i * segW, pr.Y + 2 * k, segW, pr.Height - 4 * k);
            if (on) Draw.Fill(g, sr, sr.Height / 2, Draw.White(0.22));
            Draw.TextCentered(g, s.Label, on ? Semibold(12) : F(12), on ? Color.White : Draw.White(0.6), sr.X + sr.Width / 2, sr.Y + sr.Height / 2);
            var span = s.Span;
            hot.Add((sr, () => { model.Span = span; Invalidate(); }));
        }
        var live = model.IsLive;
        var label = live ? "Live" : "Now ⏭";
        var lw = Draw.Measure(g, label, Semibold(12)).Width + (live ? 34 : 24) * k;
        var lr = new RectangleF(pr.X - 10 * k - lw, mid - 14 * k, lw, 28 * k);
        Draw.Fill(g, lr, lr.Height / 2, live ? Draw.White(0.08) : Accent);
        if (live)
        {
            using var red = new SolidBrush(Color.FromArgb(255, 69, 58));
            g.FillEllipse(red, lr.X + 12 * k, mid - 3.5f * k, 7 * k, 7 * k);
            Draw.Text(g, label, Semibold(12), Draw.White(0.6), lr.X + 24 * k, mid - 8 * k);
        }
        else
        {
            Draw.Text(g, label, Semibold(12), Color.White, lr.X + 12 * k, mid - 8 * k);
            hot.Add((lr, model.JumpToNow));
        }
    }

    // MARK: timeline bar

    // Offsets inside the bar (Mac points × k). The top 20 hold the playhead time bubble.
    float AppY => 38 * k; float AppH => 22 * k;
    float MicY => 64 * k; float SysY => 71 * k; float LaneH => 4 * k;
    float TickY => 80 * k;

    /// Bar-seconds at the left edge. Keeps the playhead in the middle, but never shows more than a little space after now.
    double V0(TimelineAxis axis) =>
        Math.Min(axis.V(model.Playhead) - model.Span / 2, axis.V(model.LoadedAt) + model.Span * 0.03 - model.Span);

    void DrawBar(Graphics g, RectangleF r)
    {
        var axis = model.Axis;
        var now = model.LoadedAt;
        double v0 = V0(axis), v1 = v0 + model.Span;
        float X(DateTime d) => r.X + (float)((axis.V(d) - v0) / model.Span) * r.Width;
        var start = axis.Date(v0);
        var end = axis.Date(v1);
        float px = X(model.Playhead);

        Draw.Fill(g, new RectangleF(r.X, r.Y + 20 * k, r.Width, r.Height - 20 * k), 10 * k, Draw.White(0.06));
        var state = g.Save();
        g.SetClip(new RectangleF(r.X, r.Y, r.Width, r.Height));

        // Solid blocks: one per run of frames from the same app (a frame lasts until the next, max 60 s).
        var frames = model.Frames;
        if (frames.Count > 0)
        {
            int i = Math.Max(0, model.FrameIndex(start)), last = model.FrameIndex(end);
            while (i <= last)
            {
                var app = frames[i].AppName;
                var runStart = frames[i].Time;
                var runEnd = runStart.AddSeconds(2);
                int j = i;
                while (j <= last)
                {
                    var f = frames[j];
                    if (f.AppName != app) break;
                    var next = j + 1 < frames.Count ? frames[j + 1].Time : f.Time.AddSeconds(2);
                    runEnd = next < f.Time.AddSeconds(60) ? next : f.Time.AddSeconds(60);
                    j++;
                    if ((next - f.Time).TotalSeconds > 60) break;  // idle: end the block here
                }
                float a = X(runStart), b = X(runEnd);
                if (b > r.X && a < r.Right) Draw.Fill(g, new RectangleF(a, r.Y + AppY, Math.Max(2 * k, b - a - 1), AppH), 3 * k, TimelineModel.AppColor(app));
                i = Math.Max(j, i + 1);
            }
        }

        // Audio lanes.
        foreach (var a in model.Audio.Where(a => a.End > start && a.Start < end))
        {
            float ax = Math.Max(r.X, X(a.Start)), bx = Math.Min(r.Right, X(a.End));
            Draw.Fill(g, new RectangleF(ax, r.Y + (a.IsInput ? MicY : SysY), Math.Max(2 * k, bx - ax), LaneH), 2 * k, a.IsInput ? Mic : Sys);
        }

        // Each shrunk break: a dark notch with how long it was.
        foreach (var (from, to) in axis.Gaps)
        {
            float a = X(from), b = X(to);
            if (b < r.X || a > r.Right) continue;
            var nr = new RectangleF(a + 2 * k, r.Y + 26 * k, Math.Max(0, b - a - 4 * k), r.Height - 30 * k);
            Draw.Fill(g, nr, 4 * k, Draw.Black(0.45));
            using (var pen = new Pen(Draw.White(0.25), 1) { DashPattern = new[] { 3f, 3f } })
                g.DrawLine(pen, nr.X + nr.Width / 2, nr.Y + 4 * k, nr.X + nr.Width / 2, nr.Bottom - 16 * k);
            var label = TimelineAxis.BreakLabel(to - from);
            if (Draw.Measure(g, label, F(9)).Width < nr.Width + 30 * k)
                Draw.TextCentered(g, label, F(9), Draw.White(0.6), nr.X + nr.Width / 2, nr.Bottom - 7 * k);
        }

        // Clock labels at round times inside recorded stretches (never inside a break).
        double target = model.Span / Math.Max(4, (int)(r.Width / (110 * k)));
        var stepsList = new double[] { 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200 };
        double step = stepsList.FirstOrDefault(s => s >= target, 43200);
        float lastLabelX = float.NegativeInfinity;
        foreach (var s in axis.Sessions.Where(s => s.End >= start && s.Start <= end))
        {
            var lo = s.Start > start ? s.Start : start;
            var hi = s.End < end ? s.End : end;
            // Round to whole local steps (an hour tick lands on :00 in the user's time zone).
            var lloc = lo.ToLocalTime();
            var dayStart = lloc.Date;
            var t = dayStart.AddSeconds(Math.Ceiling((lloc - dayStart).TotalSeconds / step) * step).ToUniversalTime();
            while (t <= hi)
            {
                float tx = X(t);
                using (var b = new SolidBrush(Draw.White(0.35))) g.FillRectangle(b, tx - 0.5f, r.Y + TickY, 1, 4 * k);
                if (tx - lastLabelX > 60 * k)
                {
                    var fmt = step >= 3600 ? "h tt" : "t";
                    var text = t.ToLocalTime().ToString(fmt, CultureInfo.CurrentCulture);
                    Draw.TextCentered(g, text, F(10), Draw.White(0.6), tx, r.Y + TickY + 11 * k);
                    lastLabelX = tx;
                }
                t = t.AddSeconds(step);
            }
        }

        // Tiny "mic" / "sys" keys at the left end of the audio lanes.
        Draw.Text(g, "mic", F(7, bold: true), Draw.WithAlpha(Mic, 0.9), r.X + 4 * k, r.Y + MicY - 3 * k);
        Draw.Text(g, "sys", F(7, bold: true), Draw.WithAlpha(Sys, 0.9), r.X + 4 * k, r.Y + SysY - 3 * k);

        // Search hits.
        using (var yb = new SolidBrush(Yellow))
            foreach (var h in model.Hits.Where(h => h.Time >= start && h.Time <= end))
                g.FillRectangle(yb, X(h.Time) - 1.5f * k, r.Y + 24 * k, 3 * k, 10 * k);

        // "Now" edge.
        float nx = X(now);
        if (nx < r.Right - 1)
        {
            using var shade = new SolidBrush(Draw.Black(0.3));
            g.FillRectangle(shade, nx, r.Y + 22 * k, r.Right - nx, r.Height - 22 * k);
            using var red = new SolidBrush(Color.FromArgb(178, 255, 59, 48));
            g.FillRectangle(red, nx - 0.5f, r.Y + 22 * k, 1, r.Height - 24 * k);
        }

        // Hover line.
        if (hoverX is float hx && dragStartV == null && Math.Abs(hx - px) > 3 * k)
            using (var b = new SolidBrush(Draw.White(0.45))) g.FillRectangle(b, hx - 0.5f, r.Y + 20 * k, 1, r.Height - 22 * k);

        // Playhead with its time in a small bubble that stays inside the bar.
        using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, px - 1 * k, r.Y + 16 * k, 2 * k, r.Height - 18 * k);
        var pt = model.Playhead.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        var pf = Semibold(10);
        var ps = Draw.Measure(g, pt, pf);
        float bw = ps.Width + 12 * k;
        float bubbleX = Math.Min(Math.Max(r.X, px - bw / 2), r.Right - bw);
        var bubble = new RectangleF(bubbleX, r.Y + 1 * k, bw, 15 * k);
        Draw.Fill(g, bubble, 7.5f * k, Color.White);
        Draw.TextCentered(g, pt, pf, Color.Black, bubble.X + bubble.Width / 2, bubble.Y + bubble.Height / 2);
        g.Restore(state);

        // Hover preview card above the bar.
        if (hoverX is float hx2 && dragStartV == null && Math.Abs(hx2 - px) > 3 * k)
        {
            var d = axis.Date(v0 + (hx2 - r.X) / r.Width * model.Span);
            if (d <= now) DrawPreview(g, d, hx2, r);
        }
    }

    /// Small card above the bar: the screenshot, app and time under the pointer.
    void DrawPreview(Graphics g, DateTime date, float hx, RectangleF barRect)
    {
        float w = 240 * k, h = 178 * k;
        float x = Math.Min(Math.Max(barRect.X, hx - w / 2), barRect.Right - w);
        var card = new RectangleF(x, barRect.Y - h - 10 * k, w, h);
        Draw.Fill(g, card, 10 * k, Color.FromArgb(33, 33, 35));
        Draw.Stroke(g, card, 10 * k, Draw.White(0.15));
        var p = model.Preview(date);
        var ir = new RectangleF(card.X + 8 * k, card.Y + 8 * k, w - 16 * k, 128 * k);
        Draw.Fill(g, ir, 6 * k, Draw.White(0.06));
        if (p?.Thumb is Image img)
        {
            float s = Math.Min(ir.Width / img.Width, ir.Height / img.Height);
            g.DrawImage(img, ir.X + (ir.Width - img.Width * s) / 2, ir.Y + (ir.Height - img.Height * s) / 2, img.Width * s, img.Height * s);
        }
        else Draw.TextCentered(g, p == null ? "Nothing recorded" : "No picture", F(11), Draw.White(0.5), ir.X + ir.Width / 2, ir.Y + ir.Height / 2);
        float ly = ir.Bottom + 12 * k;
        var time = date.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        var ts = Draw.Measure(g, time, Semibold(11));
        Draw.Text(g, time, Semibold(11), Draw.White(0.6), card.Right - 8 * k - ts.Width, ly);
        if (p?.Frame is TimelineFrame f)
        {
            using (var b = new SolidBrush(TimelineModel.AppColor(f.AppName))) g.FillEllipse(b, card.X + 8 * k, ly + 4 * k, 7 * k, 7 * k);
            Draw.Text(g, f.AppName.Length == 0 ? "Unknown app" : f.AppName, Semibold(11), Color.White, card.X + 20 * k, ly, w - 36 * k - ts.Width);
        }
    }

    // MARK: hints, results, help

    void DrawHints(Graphics g, float x, float mid)
    {
        var f = F(11);
        foreach (var (keys, what) in new[] { (new[] { "←", "→" }, "move"), (new[] { "space" }, "play audio"), (new[] { "/" }, "search"),
                                              (new[] { "+", "−" }, "zoom"), (new[] { "O" }, "browser"), (new[] { "?" }, "all keys"), (new[] { "esc" }, "close") })
        {
            foreach (var key in keys) x += Draw.Keycap(g, key, F(10), x, mid, k) + 4 * k;
            var sz = Draw.Measure(g, what, f);
            Draw.Text(g, what, f, Draw.White(0.42), x, mid - sz.Height / 2);
            x += sz.Width + 14 * k;
        }
    }

    void DrawResults(Graphics g)
    {
        float w = Math.Min(640 * k, shot.Width), rowH = 52 * k, headH = 30 * k;
        var hits = model.Hits;
        float listH = hits.Count == 0 ? 52 * k : Math.Min(440 * k, headH + hits.Count * rowH + 12 * k);
        var box = new RectangleF(searchPill.X, shot.Y + 2 * k, w, listH);
        Draw.Fill(g, box, 12 * k, Color.FromArgb(28, 28, 30));
        Draw.Stroke(g, box, 12 * k, Draw.White(0.12));
        hot.Add((box, () => { }));  // clicks on the list do not fall through
        if (hits.Count == 0)
        {
            Draw.Text(g, $"No matches for “{model.Query}”", F(14), Draw.White(0.6), box.X + 16 * k, box.Y + 16 * k);
            return;
        }
        Draw.Text(g, $"{hits.Count} moment{(hits.Count == 1 ? "" : "s")}", F(11), Draw.White(0.42), box.X + 14 * k, box.Y + 9 * k);
        float hx = box.Right - 14 * k;
        foreach (var part in new[] { "go", "⏎", "choose", "↓", "↑" })
        {
            bool key = part.Length == 1;
            if (key) hx -= Draw.Keycap(g, part, F(10), hx - 18 * k, box.Y + headH / 2, k) + 4 * k;
            else { var sz = Draw.Measure(g, part, F(11)); hx -= sz.Width + 6 * k; Draw.Text(g, part, F(11), Draw.White(0.42), hx, box.Y + 9 * k); }
        }
        // Keep the selected row in view.
        int visible = (int)((box.Height - headH - 12 * k) / rowH);
        if (model.SelectedHit < resultsScroll) resultsScroll = model.SelectedHit;
        if (model.SelectedHit >= resultsScroll + visible) resultsScroll = model.SelectedHit - visible + 1;
        resultsScroll = Math.Clamp(resultsScroll, 0, Math.Max(0, hits.Count - visible));
        var words = model.Query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = resultsScroll; i < Math.Min(hits.Count, resultsScroll + visible); i++)
        {
            var h = hits[i];
            var rr = new RectangleF(box.X + 6 * k, box.Y + headH + (i - resultsScroll) * rowH, box.Width - 12 * k, rowH - 2 * k);
            if (i == model.SelectedHit) Draw.Fill(g, rr, 8 * k, Draw.WithAlpha(Accent, 0.35));
            var icon = h.IsAudio ? null : AppIcons.Get(h.Title);
            if (icon != null) g.DrawImage(icon, rr.X + 10 * k, rr.Y + 8 * k, 18 * k, 18 * k);
            else Draw.Text(g, h.IsAudio ? "∿" : "▭", Semibold(14), h.IsAudio ? Sys : Draw.White(0.6), rr.X + 11 * k, rr.Y + 5 * k);
            var when = h.Time.ToLocalTime().ToString("MMM d, T", CultureInfo.CurrentCulture);
            var ws = Draw.Measure(g, when, F(11));
            Draw.Text(g, when, F(11), Draw.White(0.42), rr.Right - 10 * k - ws.Width, rr.Y + 7 * k);
            Draw.Text(g, h.Title, Semibold(13), Color.White, rr.X + 40 * k, rr.Y + 6 * k, rr.Width - 60 * k - ws.Width);
            DrawHighlighted(g, h.Snippet, words, rr.X + 40 * k, rr.Y + 27 * k, rr.Right - 10 * k);
            int idx = i;
            hot.Add((rr, () => { model.SelectedHit = idx; model.OpenSelected(); ActiveControl = null; Focus(); }));
        }
    }

    /// One line of snippet text with the searched words bold and yellow.
    void DrawHighlighted(Graphics g, string text, string[] words, float x, float y, float right)
    {
        var plain = F(12); var bold = F(12, bold: true);
        var lower = text.ToLowerInvariant();
        int pos = 0;
        while (pos < text.Length && x < right)
        {
            int best = -1, len = 0;
            foreach (var wd in words)
            {
                int at = lower.IndexOf(wd, pos, StringComparison.Ordinal);
                if (at >= 0 && (best < 0 || at < best)) { best = at; len = wd.Length; }
            }
            var piece = best < 0 ? text[pos..] : text[pos..best];
            if (piece.Length > 0)
            {
                Draw.Text(g, piece, plain, Draw.White(0.8), x, y, right - x);
                x += Draw.Measure(g, piece, plain).Width;
            }
            if (best < 0 || x >= right) break;
            var match = text.Substring(best, len);
            Draw.Text(g, match, bold, Yellow, x, y, right - x);
            x += Draw.Measure(g, match, bold).Width;
            pos = best + len;
        }
    }

    static readonly (string Group, string[][] Keys, string What)[] Shortcuts =
    {
        ("MOVE", new[] { new[] { "←" }, new[] { "→" } }, "Previous / next screenshot"),
        ("", new[] { new[] { "Shift", "←" }, new[] { "Shift", "→" } }, "Back / forward 1 minute"),
        ("", new[] { new[] { "↑" }, new[] { "↓" } }, "Back / forward 10 minutes"),
        ("", new[] { new[] { "scroll" } }, "Scroll the wheel or touchpad"),
        ("", new[] { new[] { "N" } }, "Go to now"),
        ("AUDIO", new[] { new[] { "space" } }, "Play or pause audio from here"),
        ("SEARCH", new[] { new[] { "/" }, new[] { "Ctrl", "F" } }, "Search screen text and speech"),
        ("", new[] { new[] { "Ctrl", "G" }, new[] { "Shift", "Ctrl", "G" } }, "Older / newer match"),
        ("", new[] { new[] { "O" } }, "Open the search page in the browser"),
        ("VIEW", new[] { new[] { "+" }, new[] { "−" } }, "Zoom the bar in / out"),
        ("", new[] { new[] { "esc" } }, "Close the list, then the overlay"),
    };

    void DrawHelp(Graphics g)
    {
        using (var b = new SolidBrush(Draw.Black(0.45))) g.FillRectangle(b, ClientRectangle);
        hot.Clear();  // only the card is clickable now
        hot.Add((ClientRectangle, () => { model.ShowHelp = false; Invalidate(); }));
        float w = 600 * k, rowH = 28 * k, h = 24 * k * 2 + 30 * k + Shortcuts.Length * rowH + 30 * k;
        var card = new RectangleF(ClientSize.Width / 2f - w / 2, ClientSize.Height / 2f - h / 2, w, h);
        Draw.Fill(g, card, 16 * k, Color.FromArgb(31, 31, 33));
        Draw.Stroke(g, card, 16 * k, Draw.White(0.12));
        hot.Add((card, () => { }));
        Draw.Text(g, "Keyboard shortcuts", Semibold(17), Color.White, card.X + 24 * k, card.Y + 22 * k);
        RoundButton(g, new RectangleF(card.Right - 54 * k, card.Y + 18 * k, 30 * k, 30 * k), "✕", () => { model.ShowHelp = false; Invalidate(); });
        float y = card.Y + 66 * k;
        foreach (var (group, keys, what) in Shortcuts)
        {
            float mid = y + rowH / 2;
            Draw.Text(g, group, Semibold(11), Draw.White(0.42), card.X + 24 * k, mid - 7 * k);
            float x = card.X + 100 * k;
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) { Draw.Text(g, "/", F(12), Draw.White(0.42), x, mid - 8 * k); x += 12 * k; }
                foreach (var key in keys[i]) x += Draw.Keycap(g, key, F(12), x, mid, k) + 3 * k;
                x += 3 * k;
            }
            Draw.Text(g, what, F(13), Draw.White(0.85), card.X + 300 * k, mid - 9 * k);
            y += rowH;
        }
        Draw.Text(g, "Drag or click the bar to move. Hover it to preview.", F(12), Draw.White(0.6), card.X + 24 * k, y + 6 * k);
    }

    // MARK: input

    void FocusSearch()
    {
        model.ShowResults = model.Query.Length > 0;
        search.Focus();
        search.SelectionStart = search.Text.Length;
        Invalidate();
    }

    void LeaveSearch() { ActiveControl = null; Focus(); Invalidate(); }

    /// Hand off to the browser search page (the tray app turns the local server on if needed).
    void OpenInBrowser()
    {
        if (model.OpenInBrowser is not Action open) return;
        HideOverlay();
        open();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0;
        bool typing = search.Focused;

        if (ctrl && key == Keys.F) { FocusSearch(); return true; }
        if (ctrl && key == Keys.G) { model.NextHit(older: !shift); LeaveSearch(); return true; }
        if (typing)
        {
            switch (key)
            {
                case Keys.Escape:
                    // First Esc closes the list / leaves the box, second Esc closes the overlay.
                    if (model.ShowResults) { model.ShowResults = false; Invalidate(); } else LeaveSearch();
                    return true;
                case Keys.Down when model.ShowResults: model.MoveSelection(1); return true;
                case Keys.Up when model.ShowResults: model.MoveSelection(-1); return true;
                case Keys.Enter:
                    // Run a pending search first, so Enter right after typing still works.
                    if (searchDebounce.Enabled) { searchDebounce.Stop(); model.Query = search.Text; model.RunSearch(); }
                    model.OpenSelected();
                    LeaveSearch();
                    return true;
                default: return base.ProcessCmdKey(ref msg, keyData);  // let the text box have it
            }
        }
        if (key == Keys.OemQuestion && shift) { model.ShowHelp = !model.ShowHelp; Invalidate(); return true; }
        if ((key == Keys.OemQuestion || key == Keys.Divide) && !ctrl) { FocusSearch(); return true; }
        switch (key)
        {
            case Keys.Escape:
                if (model.ShowHelp) model.ShowHelp = false;
                else if (model.ShowResults) model.ShowResults = false;
                else { HideOverlay(); return true; }
                Invalidate();
                break;
            case Keys.Left: if (shift) model.Jump(model.Playhead.AddSeconds(-60)); else model.Step(-1); break;
            case Keys.Right: if (shift) model.Jump(model.Playhead.AddSeconds(60)); else model.Step(1); break;
            case Keys.Up: model.Jump(model.Playhead.AddSeconds(-600)); break;
            case Keys.Down: model.Jump(model.Playhead.AddSeconds(600)); break;
            case Keys.Space: model.ToggleAudio(); break;
            case Keys.N: case Keys.End: model.JumpToNow(); break;
            case Keys.O when !ctrl: OpenInBrowser(); break;
            case Keys.Oemplus: case Keys.Add: model.Span = Math.Max(60, model.Span / 2); Invalidate(); break;
            case Keys.OemMinus: case Keys.Subtract: model.Span = Math.Min(12 * 3600, model.Span * 2); Invalidate(); break;
            default: return base.ProcessCmdKey(ref msg, keyData);
        }
        return true;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (model.ShowResults && model.Hits.Count > 0) { model.MoveSelection(e.Delta > 0 ? -1 : 1); return; }
        // Wheel up = back in time. Mouse wheels send 120 per notch (one step); touchpads send many small deltas.
        if (Math.Abs(e.Delta) >= 120 && e.Delta % 120 == 0) { model.Step(-e.Delta / 120); return; }
        scrollAccum += e.Delta;
        int steps = scrollAccum / 40;
        if (steps != 0) { scrollAccum -= steps * 40; model.Step(-steps); }
    }

    bool InBar(Point p) => bar.Contains(p) && p.Y > bar.Y + 16 * k;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (!model.ShowHelp && !(model.ShowResults && hot.Any(h => h.Rect.Contains(e.Location) && h.Rect.Y < bar.Y)) && InBar(e.Location))
        {
            // A click jumps to where you clicked; a drag then moves from there.
            var axis = model.Axis;
            var v = V0(axis) + (e.X - bar.X) / bar.Width * model.Span;
            dragStartV = v;
            dragStartX = e.X;
            model.Jump(axis.Date(v));
            Capture = true;
            return;
        }
        // Last added is on top.
        for (int i = hot.Count - 1; i >= 0; i--)
            if (hot[i].Rect.Contains(e.Location)) { hot[i].Act(); Invalidate(); return; }
        if (search.Focused) LeaveSearch();
    }

    float dragStartX;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragStartV is double sv && e.Button == MouseButtons.Left)
        {
            model.Jump(model.Axis.Date(sv - (e.X - dragStartX) / bar.Width * model.Span));
            return;
        }
        float? hx = InBar(e.Location) && !model.ShowHelp ? e.X : null;
        if (hx != hoverX) { hoverX = hx; Invalidate(); }
        Cursor = hot.Any(h => h.Rect.Contains(e.Location) && h.Rect.Width < ClientSize.Width / 2) || hx != null ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragStartV = null;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverX != null) { hoverX = null; Invalidate(); }
    }
}

/// App icons, from the exe paths the recorder saw.
static class AppIcons
{
    static readonly Dictionary<string, Image?> cache = new(StringComparer.OrdinalIgnoreCase);

    public static Image? Get(string name)
    {
        if (name.Length == 0) return null;
        if (cache.TryGetValue(name, out var hit)) return hit;
        Image? img = null;
        if (Capture.AppPaths.TryGetValue(name, out var path))
            try { img = Icon.ExtractAssociatedIcon(path)?.ToBitmap(); } catch { }
        // Not seen this run: try again later instead of caching "no icon".
        if (img != null) cache[name] = img;
        return img;
    }
}
