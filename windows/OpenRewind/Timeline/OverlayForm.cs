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
    /// Where the screenshot is drawn (empty when there is none), for selecting its text.
    RectangleF imageRect;
    /// True while dragging across the screenshot's text; where the drag began (fractions of the picture).
    bool selecting;
    PointF selectFrom;
    readonly List<(RectangleF Rect, Action Act)> hot = new();
    float? hoverX;
    double? dragStartV;
    int scrollAccum;
    int resultsScroll;
    /// Where the bottom dock starts. Under it, the thin scrubber grows into the full bar.
    float dockTop;
    bool dockHover;
    /// Folds the full bar back a moment after the pointer leaves, so passing over the edge does not make it jump.
    readonly System.Windows.Forms.Timer dockLeave = new() { Interval = 500 };
    bool DockExpanded => dockHover || dragStartV != null || HoldFullBar;
    /// Set by the tour while it points at the bar.
    public bool HoldFullBar { get; set; }

    static readonly Color Mic = Color.FromArgb(255, 159, 10), Sys = Color.FromArgb(48, 209, 88), Accent = Color.FromArgb(10, 132, 255);
    /// Shows the "new version" banner; set by the tray app.
    public Updater? Updater { get; set; }
    /// The Ask AI chat, shared with its own window; set by the tray app.
    public Func<AiChatPanel>? Chat { get; set; }
    AiChatPanel? chatPanel;
    bool ChatOpen => chatPanel is { IsDisposed: false } && chatPanel.Parent == this;

    static readonly Color Yellow = Color.FromArgb(255, 214, 10);

    /// Settings → Timeline Look: "system", "light" or "dark". Set by the tray app.
    public string Look { get; set; } = "system";
    /// The look in use, worked out when the timeline opens.
    bool dark = true;
    /// Black or white, whichever stands out on the background, at this opacity.
    Color Ink(double opacity) => dark ? Draw.White(opacity) : Draw.Black(opacity);
    Color TextColor => dark ? Color.White : Color.FromArgb(26, 26, 26);
    Color PillColor(bool focused) => dark ? (focused ? Color.FromArgb(58, 58, 62) : Color.FromArgb(44, 44, 48))
                                          : (focused ? Color.White : Color.FromArgb(228, 228, 232));
    /// Solid cards: results list, help, notices, hover preview.
    Color CardColor => dark ? Color.FromArgb(31, 31, 33) : Color.FromArgb(250, 250, 250);
    /// Shrunk breaks and the area after now on the bar.
    Color Shade(double darkOpacity) => Draw.Black(dark ? darkOpacity : darkOpacity / 5);
    /// Search words in snippets: plain yellow is hard to read on white.
    Color MatchColor => dark ? Yellow : Color.FromArgb(184, 107, 0);

    internal static bool SystemUsesDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch { return true; }
    }

    void ApplyLook()
    {
        dark = Look switch { "light" => false, "dark" => true, _ => SystemUsesDark() };
        BackColor = dark ? Color.FromArgb(18, 18, 20) : Color.FromArgb(242, 242, 245);
        ForeColor = search.ForeColor = TextColor;
    }

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
        dockLeave.Tick += (_, _) => { dockLeave.Stop(); dockHover = false; hoverX = null; Invalidate(); };
        model.Changed += () => { if (Visible) Invalidate(); };
        HandleCreated += (_, _) => Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    // MARK: show / hide

    public void Toggle() { if (Visible) HideOverlay(); else ShowOverlay(); }

    public void ShowOverlay()
    {
        if (!Visible) previousWindow = Native.GetForegroundWindow();
        ApplyLook();
        var screen = Screen.FromPoint(Cursor.Position);
        backdrop?.Dispose();
        backdrop = BlurredDesktop(screen.Bounds);
        Bounds = screen.Bounds;
        k = DeviceDpi / 96f;
        model.Reload();
        Updater?.CheckIfStale();
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

    /// `restoreFocus: false` when another app is about to come to the front (O opens it).
    public void HideOverlay(bool restoreFocus = true)
    {
        model.StopAudio();
        clock.Stop();
        // Marks and a text selection belong to this visit: next time the bar opens on now, where they can't be seen,
        // and Esc would clear them instead of closing.
        model.ClearMarks();
        model.ClearSelection();
        selecting = false;
        dockLeave.Stop();
        dockHover = false;
        Hide();
        // Close the videos it read, so no decrypted copy stays in the temp folder while it is hidden.
        Task.Run(FrameImages.ReleaseAll);
        if (restoreFocus && previousWindow != IntPtr.Zero) Native.SetForegroundWindow(previousWindow);
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
        using (var dim = new SolidBrush(dark ? Color.FromArgb(205, 14, 14, 16) : Color.FromArgb(200, 246, 246, 248))) g.FillRectangle(dim, ClientRectangle);

        // Like Rewind: the old screen fills the whole display, slim glass bars float at the top,
        // and a thin scrubber runs along the bottom that grows into the full bar under the pointer.
        float chatW = ChatOpen ? chatPanel!.Width : 0;
        shot = new RectangleF(0, 0, ClientSize.Width - chatW, ClientSize.Height);
        DrawScreenshot(g, shot);

        float padX = 16 * k, gap = 10 * k;
        float w = ClientSize.Width - 2 * padX - chatW;
        float y = 14 * k;
        DrawTopBar(g, new RectangleF(padX, y, w, 42 * k));
        y += 42 * k + gap;
        if (StaleMinutes() is int mins)
        {
            DrawStaleBanner(g, padX, y, w, mins);
            y += 34 * k + gap;
        }
        if (Updater is { ShowBanner: true } u && u.Available is ReleaseInfo release)
            DrawUpdateBanner(g, padX, y, w, u, release);

        // Bottom dock, from the screen edge up: the scrubber, the controls row, the spoken words.
        bool full = DockExpanded;
        float barH = full ? 102 * k : 34 * k, controlsH = 36 * k;
        bar = full ? new RectangleF(padX + 4 * k, ClientSize.Height - 10 * k - barH, w - 8 * k, barH)
                   : new RectangleF(0, ClientSize.Height - 2 * k - barH, ClientSize.Width - chatW, barH);
        var controls = new RectangleF(padX + 4 * k, bar.Y - gap - controlsH, w - 8 * k, controlsH);
        float captionBottom = controls.Y - gap;
        dockTop = (model.Caption != null ? captionBottom - 70 * k : controls.Y) - 26 * k;
        // Frosted fade behind the dock: clear at the top, glass at the screen edge.
        var fade = new RectangleF(0, dockTop, ClientSize.Width - chatW, ClientSize.Height - dockTop);
        using (var lg = new LinearGradientBrush(new PointF(0, fade.Y - 1), new PointF(0, fade.Bottom), Color.Transparent, Draw.WithAlpha(CardColor, full ? 0.9 : 0.75)))
            g.FillRectangle(lg, fade);
        DrawCaption(g, captionBottom);
        DrawControls(g, controls);
        DrawBar(g, bar);
        if (model.Notice is string notice) DrawNotice(g, notice);
        if (model.ShowResults) DrawResults(g);
        if (model.ShowHelp) DrawHelp(g);
    }

    /// Frosted panel behind a floating control, so it reads over any screenshot.
    void Glass(Graphics g, RectangleF r, float radius)
    {
        Draw.Fill(g, new RectangleF(r.X, r.Y + 2 * k, r.Width, r.Height), radius, Draw.Black(dark ? 0.3 : 0.1));
        Draw.Fill(g, r, radius, Draw.WithAlpha(CardColor, 0.88));
        Draw.Stroke(g, r, radius, Ink(0.12));
    }

    /// Small message under the top bar, for a moment.
    void DrawNotice(Graphics g, string text)
    {
        var f = Semibold(13);
        var sz = Draw.Measure(g, text, f);
        var r = new RectangleF(ClientSize.Width / 2f - (sz.Width + 40 * k) / 2, 74 * k, sz.Width + 40 * k, 34 * k);
        Draw.Fill(g, r, r.Height / 2, CardColor);
        Draw.Stroke(g, r, r.Height / 2, Ink(0.12));
        Draw.Text(g, "ⓘ", f, Ink(0.6), r.X + 14 * k, r.Y + (r.Height - sz.Height) / 2);
        Draw.Text(g, text, f, TextColor, r.X + 32 * k, r.Y + (r.Height - sz.Height) / 2);
    }

    // MARK: top bar

    /// Search and its filters (Apps, Meetings) on the left; the time, Ask AI, share, help and close on the right.
    /// Each group floats on frosted glass over the screenshot.
    void DrawTopBar(Graphics g, RectangleF r)
    {
        // Right group first, so search knows how much room is left.
        const string askLabel = "✦  Ask AI";
        float btn = 30 * k, inner = 6 * k;
        float askW = Chat != null ? Draw.Measure(g, askLabel, Semibold(12.5f)).Width + 28 * k : 0;
        float rightW = inner + (askW > 0 ? askW + 6 * k : 0) + 4 * btn + 3 * 6 * k + inner;
        var rightGroup = new RectangleF(r.Right - rightW, r.Y, rightW, r.Height);
        Glass(g, rightGroup, r.Height / 2);
        float by = r.Y + (r.Height - btn) / 2, bx = rightGroup.Right - inner - btn;
        RoundButton(g, new RectangleF(bx, by, btn, btn), "✕", () => HideOverlay());
        RoundButton(g, new RectangleF(bx - btn - 6 * k, by, btn, btn), "?", () => { model.ShowHelp = !model.ShowHelp; Invalidate(); });
        var share = new RectangleF(bx - 2 * (btn + 6 * k), by, btn, btn);
        RoundButton(g, share, "", () => ShowFrameMenu(new Point((int)share.X, (int)(share.Bottom + 4 * k))));
        DrawShareIcon(g, share.X + share.Width / 2, share.Y + share.Height / 2);
        var star = new RectangleF(share.X - btn - 6 * k, by, btn, btn);
        RoundButton(g, star, "", () => model.ToggleStar());
        var starred = model.CurrentStar != null;
        Draw.TextCentered(g, starred ? "★" : "☆", Semibold(13), starred ? Yellow : TextColor, star.X + star.Width / 2, star.Y + star.Height / 2);
        if (Chat != null) AskAiButton(g, star.X - 6 * k, by);

        // The time and how long ago, in its own pill left of the buttons.
        var time = model.Playhead.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        var rel = Relative(model.Playhead);
        Font tf = Semibold(15), rf = F(11);
        var ts = Draw.Measure(g, time, tf);
        var rs = Draw.Measure(g, rel, rf);
        float timeW = 16 * k + ts.Width + 8 * k + rs.Width + 16 * k;
        var timePill = new RectangleF(rightGroup.X - 10 * k - timeW, r.Y, timeW, r.Height);
        float mid = r.Y + r.Height / 2;
        Glass(g, timePill, r.Height / 2);
        Draw.Text(g, time, tf, TextColor, timePill.X + 16 * k, mid - ts.Height / 2);
        Draw.Text(g, rel, rf, Ink(0.6), timePill.X + 16 * k + ts.Width + 8 * k, mid - rs.Height / 2);

        // Left group: search, Keyword/Meaning, Apps, Meetings, Starred.
        float pillH = r.Height - 2 * inner;
        float modeW = SearchModeWidth(g), appW = AppFilterWidth(g), meetW = MeetingsWidth(g), starredW = StarredFilterWidth(g);
        float chips = (modeW > 0 ? modeW + 8 * k : 0) + appW + 8 * k + meetW + 8 * k + starredW;
        float room = timePill.X - 16 * k - r.X - 2 * inner - 8 * k - chips;
        float pillW = Math.Clamp(room, 160 * k, 320 * k);
        var leftGroup = new RectangleF(r.X, r.Y, inner + pillW + 8 * k + chips + inner, r.Height);
        Glass(g, leftGroup, r.Height / 2);
        searchPill = new RectangleF(r.X + inner, r.Y + inner, pillW, pillH);
        bool focused = search.Focused;
        Draw.Fill(g, searchPill, searchPill.Height / 2, PillColor(focused));
        Draw.Stroke(g, searchPill, searchPill.Height / 2, Ink(focused ? 0.35 : 0.12));
        // Magnifier.
        using (var pen = new Pen(Ink(0.6), 1.6f * k))
        {
            float cx = searchPill.X + 18 * k, cy = searchPill.Y + searchPill.Height / 2 - 1 * k, rr = 5f * k;
            g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
            g.DrawLine(pen, cx + rr * 0.7f, cy + rr * 0.7f, cx + rr * 1.5f, cy + rr * 1.5f);
        }
        hot.Add((searchPill, FocusSearch));

        // Right side of the pill: "/" hint, or match count and arrows.
        float right = searchPill.Right - 12 * k;
        if (model.Query.Length == 0 && search.Text.Length == 0)
        {
            if (!focused) right -= Draw.Keycap(g, Ink(1), "/", F(10), right - 18 * k, mid, k);
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
                Draw.Text(g, count, F(12), Ink(0.6), right, mid - sz.Height / 2);
            }
            else if (model.Hits.Count == 0 && !model.ShowResults && model.Query.Length > 0 && !model.Searching)
            {
                var sz = Draw.Measure(g, "No matches", F(12));
                right -= sz.Width + 6 * k;
                Draw.Text(g, "No matches", F(12), Ink(0.42), right, mid - sz.Height / 2);
            }
        }
        // The real text box sits inside the pill.
        var font = F(14);
        if (search.Font.Size != font.Size) search.Font = font;
        search.BackColor = PillColor(focused);
        var placeholder = model.SearchingByMeaning ? "Describe what you saw or heard" : "Search what you saw or heard";
        if (search.PlaceholderText != placeholder) search.PlaceholderText = placeholder;
        var box = new Rectangle((int)(searchPill.X + 32 * k), (int)(mid - search.PreferredHeight / 2f), (int)Math.Max(40 * k, right - searchPill.X - 40 * k), search.PreferredHeight);
        if (search.Bounds != box) search.Bounds = box;

        float x = searchPill.Right + 8 * k;
        if (modeW > 0) { DrawSearchMode(g, new RectangleF(x, searchPill.Y, modeW, pillH)); x += modeW + 8 * k; }
        DrawAppFilter(g, new RectangleF(x, searchPill.Y, appW, pillH));
        x += appW + 8 * k;
        DrawMeetings(g, new RectangleF(x, searchPill.Y, meetW, pillH));
        x += meetW + 8 * k;
        DrawStarredFilter(g, new RectangleF(x, searchPill.Y, starredW, pillH));
    }

    /// "✦ Ask AI" pill left of help; `right` is its right edge.
    void AskAiButton(Graphics g, float right, float y)
    {
        const string label = "✦  Ask AI";
        var f = Semibold(12.5f);
        var sz = Draw.Measure(g, label, f);
        var r = new RectangleF(right - sz.Width - 28 * k, y, sz.Width + 28 * k, 30 * k);
        bool hover = r.Contains(PointToClient(Cursor.Position));
        Draw.Fill(g, r, r.Height / 2, Ink(ChatOpen ? 0.22 : hover ? 0.2 : 0.1));
        Draw.Text(g, label, f, TextColor, r.X + 14 * k, r.Y + (r.Height - sz.Height) / 2);
        hot.Add((r, ToggleChat));
    }

    public void ToggleChat()
    {
        if (ChatOpen) { chatPanel!.Close(); return; }
        if (Chat == null) return;
        var p = chatPanel = Chat();
        p.MoveTo(this, onClose: () => { Controls.Remove(p); LeaveSearch(); });
        p.FocusInput();
        Invalidate();
    }

    static readonly string[] ModeLabels = { "Keyword", "Meaning" };

    /// Width of the Keyword | Meaning switch; 0 while Search by Meaning is off.
    float SearchModeWidth(Graphics g) =>
        model.MeaningAvailable ? ModeLabels.Sum(l => Draw.Measure(g, l, Semibold(12)).Width + 24 * k) + 6 * k : 0;

    /// "Keyword | Meaning" switch next to search, shown while Search by Meaning is on.
    void DrawSearchMode(Graphics g, RectangleF r)
    {
        var f = Semibold(12);
        float pad = 12 * k, inset = 3 * k;
        Draw.Fill(g, r, r.Height / 2, PillColor(false));
        Draw.Stroke(g, r, r.Height / 2, Ink(0.12));
        float x = r.X + inset;
        for (int i = 0; i < ModeLabels.Length; i++)
        {
            var w = Draw.Measure(g, ModeLabels[i], f).Width + 2 * pad;
            var seg = new RectangleF(x, r.Y + inset, w, r.Height - 2 * inset);
            bool on = (i == 1) == model.ByMeaning;
            if (on) Draw.Fill(g, seg, seg.Height / 2, Draw.WithAlpha(Accent, dark ? 0.45 : 0.25));
            var sz = Draw.Measure(g, ModeLabels[i], f);
            Draw.Text(g, ModeLabels[i], f, on ? TextColor : Ink(0.6), seg.X + pad, seg.Y + (seg.Height - sz.Height) / 2);
            bool meaning = i == 1;
            hot.Add((seg, () => { model.SetByMeaning(meaning); FocusSearch(); }));
            x += w;
        }
    }

    float StarredFilterWidth(Graphics g) =>
        10 * k + Draw.Measure(g, "★", Semibold(13)).Width + 6 * k + Draw.Measure(g, "Starred", Semibold(13)).Width + 10 * k;

    /// Starred chip: only starred moments in the list.
    void DrawStarredFilter(Graphics g, RectangleF r)
    {
        bool on = model.StarredOnly;
        var f = Semibold(13);
        var sz = Draw.Measure(g, "Starred", f);
        var ss = Draw.Measure(g, "★", f);
        Chip(g, r, on);
        float mid = r.Y + r.Height / 2;
        Draw.Text(g, on ? "★" : "☆", f, on ? Yellow : Ink(0.6), r.X + 10 * k, mid - ss.Height / 2);
        Draw.Text(g, "Starred", f, TextColor, r.X + 10 * k + ss.Width + 6 * k, mid - sz.Height / 2);
        hot.Add((r, () => model.SetStarredOnly(!model.StarredOnly)));
    }

    /// A filter chip under search, like Rewind's Apps / Meetings / Starred.
    void Chip(Graphics g, RectangleF r, bool on)
    {
        bool hover = r.Contains(PointToClient(Cursor.Position));
        Draw.Fill(g, r, 8 * k, on ? Draw.WithAlpha(Accent, dark ? 0.35 : 0.2) : Ink(hover ? 0.16 : 0.1));
        Draw.Stroke(g, r, 8 * k, Ink(on ? 0.25 : 0.12));
    }

    float AppFilterWidth(Graphics g)
    {
        var app = model.AppFilterName;
        float textW = Math.Min(140 * k, Draw.Measure(g, app ?? "Apps", Semibold(13)).Width);
        return 10 * k + (app != null ? 18 * k + 6 * k : 16 * k + 6 * k) + textW + 6 * k + Draw.Measure(g, "▾", F(11)).Width
               + (app != null ? 22 * k : 0) + 10 * k;
    }

    /// Apps chip: "Apps", or the app the timeline is filtered to. Click to choose, or to hide an app (H).
    void DrawAppFilter(Graphics g, RectangleF r)
    {
        var app = model.AppFilterName;
        var label = app ?? "Apps";
        var f = Semibold(13);
        var sz = Draw.Measure(g, label, f);
        float mid = r.Y + r.Height / 2;
        Chip(g, r, app != null);
        hot.Add((r, () => ShowAppMenu(r)));
        float x = r.X + 10 * k;
        if (app != null)
        {
            float icon = 18 * k;
            if (AppIcons.Get(app) is Image img) g.DrawImage(img, x, mid - icon / 2, icon, icon);
            else Draw.Fill(g, new RectangleF(x + 3 * k, mid - 6 * k, 12 * k, 12 * k), 3 * k, TimelineModel.AppColor(app));
            x += icon + 6 * k;
        }
        else
        {
            // Four small squares, like an app grid.
            using var b = new SolidBrush(Ink(0.6));
            float c = 5.5f * k, sp = 1.5f * k, top = mid - c - sp / 2;
            foreach (var (dx, dy) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f) })
                g.FillRectangle(b, x + dx * (c + sp), top + dy * (c + sp), c, c);
            x += 16 * k + 6 * k;
        }
        var arrowW = Draw.Measure(g, "▾", F(11)).Width;
        float textRight = r.Right - 10 * k - arrowW - 6 * k - (app != null ? 22 * k : 0);
        Draw.Text(g, label, f, TextColor, x, mid - sz.Height / 2, textRight - x);
        Draw.Text(g, "▾", F(11), Ink(0.6), textRight + 6 * k, mid - Draw.Measure(g, "▾", F(11)).Height / 2);
        if (app != null) HotIcon(g, "✕", r.Right - 8 * k, mid, () => model.SetAppFilter(null));
    }

    float MeetingsWidth(Graphics g) => 10 * k + 16 * k + 6 * k + Draw.Measure(g, "Meetings", Semibold(13)).Width + 10 * k;

    /// Meetings chip: recent meetings, newest first. Picking one goes to when it started.
    void DrawMeetings(Graphics g, RectangleF r)
    {
        float mid = r.Y + r.Height / 2;
        Chip(g, r, false);
        // A small calendar.
        var cal = new RectangleF(r.X + 10 * k, mid - 6.5f * k, 15 * k, 13 * k);
        Draw.Stroke(g, cal, 2.5f * k, Ink(0.6), 1.3f * k);
        using (var b = new SolidBrush(Ink(0.6))) g.FillRectangle(b, cal.X, cal.Y, cal.Width, 3.5f * k);
        var sz = Draw.Measure(g, "Meetings", Semibold(13));
        Draw.Text(g, "Meetings", Semibold(13), TextColor, r.X + 32 * k, mid - sz.Height / 2);
        hot.Add((r, () => ShowMeetingsMenu(r)));
    }

    ContextMenuStrip? meetingsMenu;

    void ShowMeetingsMenu(RectangleF at)
    {
        meetingsMenu?.Dispose();
        meetingsMenu = new ContextMenuStrip { ShowCheckMargin = false, ShowImageMargin = false };
        List<Meeting> list;
        try
        {
            // Calls in blocked apps or sites stay out of the list, as in the Meetings window.
            var blocked = Blocklist.Current;
            list = model.Db.ShownMeetings(40).Where(m => !MeetingDetector.Hides(blocked, m)).Take(15).ToList();
        }
        catch (Exception e) { Log.Write($"meetings menu: {e.Message}"); list = new(); }
        if (list.Count == 0) meetingsMenu.Items.Add(new ToolStripMenuItem("No meetings yet") { Enabled = false });
        foreach (var m in list)
        {
            var name = m.Name;
            var when = m.Start.ToLocalTime().ToString("ddd MMM d, t", CultureInfo.CurrentCulture);
            var start = m.Start;
            var item = new ToolStripMenuItem($"{name}  ·  {when}");
            item.Click += (_, _) => { model.ShowResults = false; model.OpenAt(start, null); Invalidate(); };
            meetingsMenu.Items.Add(item);
        }
        meetingsMenu.Show(this, new Point((int)at.X, (int)(at.Bottom + 4 * k)));
    }

    ContextMenuStrip? appMenu;

    void ShowAppMenu(RectangleF at)
    {
        appMenu?.Dispose();
        appMenu = new ContextMenuStrip { ShowCheckMargin = false, ShowImageMargin = true };
        var all = new ToolStripMenuItem("All apps") { Checked = model.AppFilterName == null };
        all.Click += (_, _) => { model.SetAppFilter(null); Invalidate(); };
        appMenu.Items.Add(all);
        appMenu.Items.Add(new ToolStripSeparator());
        foreach (var app in model.Apps)
        {
            var item = new ToolStripMenuItem(app, AppIcons.Get(app)) { Checked = app == model.AppFilterName };
            item.Click += (_, _) => { model.SetAppFilter(app); Invalidate(); };
            appMenu.Items.Add(item);
        }
        if (model.Current is TimelineFrame f && f.AppName.Length > 0)
        {
            var name = f.AppName;
            appMenu.Items.Add(new ToolStripSeparator());
            var hide = new ToolStripMenuItem($"Hide {name} from timeline (H)");
            hide.Click += (_, _) => { model.Hide(name); Invalidate(); };
            appMenu.Items.Add(hide);
            var block = new ToolStripMenuItem($"Hide {name} and stop recording it");
            block.Click += (_, _) => { model.Hide(name, stopRecording: true); Invalidate(); };
            appMenu.Items.Add(block);
        }
        if (model.HiddenAppNames.Count > 0)
        {
            var hidden = new ToolStripMenuItem("Hidden apps");
            foreach (var name in model.HiddenAppNames.ToList())
            {
                var show = new ToolStripMenuItem($"Show {name} again");
                show.Click += (_, _) => { model.Unhide(name); Invalidate(); };
                hidden.DropDownItems.Add(show);
            }
            appMenu.Items.Add(new ToolStripSeparator());
            appMenu.Items.Add(hidden);
        }
        appMenu.Show(this, new Point((int)at.X, (int)(at.Bottom + 4 * k)));
    }

    float HotIcon(Graphics g, string glyph, float right, float mid, Action act)
    {
        var f = F(11);
        var sz = Draw.Measure(g, glyph, f);
        var r = new RectangleF(right - sz.Width - 6 * k, mid - 10 * k, sz.Width + 6 * k, 20 * k);
        Draw.Text(g, glyph, f, Ink(0.6), r.X + 3 * k, mid - sz.Height / 2);
        hot.Add((r, act));
        return r.X;
    }

    float HotLabel(Graphics g, string label, float right, float mid, Action act)
    {
        var f = Semibold(12);
        var sz = Draw.Measure(g, label, f);
        var r = new RectangleF(right - sz.Width, mid - 10 * k, sz.Width, 20 * k);
        Draw.Text(g, label, f, TextColor, r.X, mid - sz.Height / 2);
        hot.Add((r, act));
        return r.X;
    }

    void RoundButton(Graphics g, RectangleF r, string glyph, Action act)
    {
        bool hover = r.Contains(PointToClient(Cursor.Position));
        using (var b = new SolidBrush(Ink(hover ? 0.2 : 0.1))) g.FillEllipse(b, r);
        Draw.TextCentered(g, glyph, Semibold(12), TextColor, r.X + r.Width / 2, r.Y + r.Height / 2);
        hot.Add((r, act));
    }

    /// "Open outside": a box with an arrow leaving its top-right corner, drawn so no font is needed.
    void DrawOpenIcon(Graphics g, float cx, float cy)
    {
        float h = 5.5f * k;
        using var pen = new Pen(Color.White, 1.6f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - 0.5f * k, cy - h), new PointF(cx - h, cy - h), new PointF(cx - h, cy + h),
                                  new PointF(cx + h, cy + h), new PointF(cx + h, cy + 0.5f * k) });
        g.DrawLine(pen, cx - 0.5f * k, cy + 0.5f * k, cx + h + 1.5f * k, cy - h - 1.5f * k);
        g.DrawLines(pen, new[] { new PointF(cx + 1.5f * k, cy - h - 1.5f * k), new PointF(cx + h + 1.5f * k, cy - h - 1.5f * k),
                                  new PointF(cx + h + 1.5f * k, cy - 1.5f * k) });
    }

    /// "Copy, save or export": a tray with an arrow leaving it, drawn so no font is needed.
    void DrawShareIcon(Graphics g, float cx, float cy)
    {
        float h = 5.5f * k;
        using var pen = new Pen(TextColor, 1.6f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - h * 0.45f, cy - h * 0.3f), new PointF(cx - h, cy - h * 0.3f), new PointF(cx - h, cy + h),
                                  new PointF(cx + h, cy + h), new PointF(cx + h, cy - h * 0.3f), new PointF(cx + h * 0.45f, cy - h * 0.3f) });
        g.DrawLine(pen, cx, cy + h * 0.4f, cx, cy - h - 1.5f * k);
        g.DrawLines(pen, new[] { new PointF(cx - 3 * k, cy - h + 1.5f * k), new PointF(cx, cy - h - 1.5f * k), new PointF(cx + 3 * k, cy - h + 1.5f * k) });
    }

    ContextMenuStrip? frameMenu;

    /// Copy, save and export, from the top bar button or a right-click on the screenshot.
    void ShowFrameMenu(Point at)
    {
        frameMenu?.Dispose();
        var menu = frameMenu = new ContextMenuStrip();
        void Add(string text, string keys, Action act, bool enabled = true)
        {
            var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys, Enabled = enabled };
            item.Click += (_, _) => { act(); Invalidate(); };
            menu.Items.Add(item);
        }
        bool frame = model.Current != null && model.Image != null && !model.CurrentHidden;
        if (model.HasSelection) Add("Copy Text", "Ctrl+C", model.Copy);
        Add("Copy All Text", "Ctrl+Shift+C", model.CopyAllText, frame);
        Add("Copy Screenshot", model.HasSelection ? "" : "Ctrl+C", () => { model.ClearSelection(); model.Copy(); }, frame);
        Add("Save Screenshot…", "Ctrl+S", () => model.SaveFrame(this), frame);
        menu.Items.Add(new ToolStripSeparator());
        Add("Mark Clip Start Here", "[", model.MarkStart, model.Current != null);
        Add("Mark Clip End Here", "]", model.MarkEnd, model.Current != null);
        if (model.HasMarks) Add("Clear Clip Marks", "", model.ClearMarks);
        Add("Export Clip…", "Ctrl+E", ExportClip, model.Frames.Count > 0);
        menu.Show(this, at);
    }

    /// Export Clip…: the marked range, or the 5 minutes up to the shown screenshot.
    void ExportClip()
    {
        if (model.Frames.Count == 0) { model.ShowNotice("Nothing recorded yet"); return; }
        model.StopAudio();
        var (start, end) = ClipPlan.DefaultRange(model.Playhead, model.ClipStart, model.ClipEnd);
        using var form = new ExportClipForm(model, start, end);
        form.ShowDialog(this);
        Activate();
        LeaveSearch();
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
        if (model.Newest is not TimelineFrame newest) return null;
        var mins = (int)((model.LoadedAt - newest.Time).TotalMinutes);
        return mins >= 3 ? mins : null;
    }

    void DrawStaleBanner(Graphics g, float x, float y, float w, int mins)
    {
        var ago = mins >= 60 ? $"{mins / 60} h {mins % 60} min" : $"{mins} min";
        var text = $"⚠  Newest screenshot is from {ago} ago. Screen recording may be off, or the screen has not changed.";
        var sz = Draw.Measure(g, text, F(13));
        var r = new RectangleF(x + w / 2 - (sz.Width + 28 * k) / 2, y, sz.Width + 28 * k, 34 * k);
        Draw.Fill(g, r, 8 * k, Draw.WithAlpha(dark ? Yellow : Color.Orange, dark ? 0.15 : 0.2));
        Draw.Text(g, text, F(13), TextColor, r.X + 14 * k, r.Y + (r.Height - sz.Height) / 2);
    }

    // MARK: update banner

    /// "A new version is out", with buttons. Closing it hides it until the next version.
    void DrawUpdateBanner(Graphics g, float x, float y, float w, Updater u, ReleaseInfo r)
    {
        var text = u.State switch
        {
            Updater.Phase.Downloading => $"Downloading OpenRewind {r.Version}… {(int)(u.Progress * 100)}%",
            Updater.Phase.Ready => $"OpenRewind {r.Version} is downloaded and ready.",
            Updater.Phase.Installing => $"Installing OpenRewind {r.Version}…",
            Updater.Phase.Failed => $"Could not update to {r.Version}: {u.Error}.",
            _ => $"OpenRewind {r.Version} is out. You have {Updater.Current}.",
        };
        var notes = ("What's New", (Action)(() => Updater.Open(r.Page)));
        (string Label, Action Act)[] buttons = u.State switch
        {
            Updater.Phase.Downloading or Updater.Phase.Installing => Array.Empty<(string, Action)>(),
            Updater.Phase.Ready => new[] { ("Restart to Update", (Action)u.Update), notes },
            Updater.Phase.Failed => new[] { ("Try Again", (Action)u.Update), ("Download", () => Updater.Open(r.AssetUrl)) },
            _ => new[] { (Updater.CanInstall ? "Update" : "Download", (Action)u.Update), notes },
        };

        var font = F(13);
        var bf = Semibold(12);
        float pad = 14 * k, space = 10 * k, bpad = 10 * k, close = 20 * k;
        var ts = Draw.Measure(g, text, font);
        var widths = buttons.Select(b => Draw.Measure(g, b.Label, bf).Width + 2 * bpad).ToArray();
        float total = pad + ts.Width + widths.Sum(bw => space + bw) + space + close + pad;
        var r0 = new RectangleF(x + w / 2 - total / 2, y, total, 34 * k);
        Draw.Fill(g, r0, 8 * k, Draw.WithAlpha(Accent, 0.22));
        float mid = r0.Y + r0.Height / 2, cx = r0.X + pad;
        Draw.Text(g, text, font, TextColor, cx, mid - ts.Height / 2);
        cx += ts.Width;
        var mouse = PointToClient(Cursor.Position);
        for (int i = 0; i < buttons.Length; i++)
        {
            cx += space;
            var br = new RectangleF(cx, mid - 11 * k, widths[i], 22 * k);
            Draw.Fill(g, br, br.Height / 2, Ink(br.Contains(mouse) ? 0.26 : 0.16));
            Draw.TextCentered(g, buttons[i].Label, bf, TextColor, br.X + br.Width / 2, mid);
            hot.Add((br, buttons[i].Act));
            cx += widths[i];
        }
        cx += space;
        var xr = new RectangleF(cx, mid - close / 2, close, close);
        Draw.TextCentered(g, "✕", F(11), Ink(xr.Contains(mouse) ? 0.9 : 0.6), xr.X + xr.Width / 2, mid);
        hot.Add((xr, u.Dismiss));
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

    /// The selected words, in the system's selection color.
    void DrawSelection(Graphics g, RectangleF ir)
    {
        if (!model.HasSelection) return;
        foreach (var h in model.Text!.Boxes(model.SelectionAnchor, model.SelectionEnd))
        {
            var r = new RectangleF(ir.X + h.X * ir.Width, ir.Y + h.Y * ir.Height, h.Width * ir.Width, h.Height * ir.Height);
            r.Inflate(2 * k, 1.5f * k);
            Draw.Fill(g, r, 3 * k, Draw.WithAlpha(SystemColors.Highlight, 0.35));
            Draw.Stroke(g, r, 3 * k, Draw.WithAlpha(SystemColors.Highlight, 0.8), 1 * k);
        }
    }

    /// The screenshot over the whole display, as if looking at the screen at that moment.
    void DrawScreenshot(Graphics g, RectangleF r)
    {
        if (model.Image is Image img)
        {
            float s = Math.Min(r.Width / img.Width, r.Height / img.Height);
            float iw = img.Width * s, ih = img.Height * s;
            var ir = new RectangleF(r.X + (r.Width - iw) / 2, r.Y + (r.Height - ih) / 2, iw, ih);
            imageRect = ir;
            g.DrawImage(img, ir);
            DrawHighlights(g, ir);
            DrawSelection(g, ir);
        }
        else
        {
            imageRect = RectangleF.Empty;
            var empty = model.Frames.Count == 0;
            var title = empty ? "Nothing recorded yet" : "Screenshot not found";
            var sub = empty ? "Turn on Record Screen in the tray menu." : "The file for this moment was deleted or moved.";
            var card = new RectangleF(r.X + r.Width / 2 - 200 * k, r.Y + r.Height / 2 - 60 * k, 400 * k, 120 * k);
            Draw.Fill(g, card, 14 * k, Ink(0.08));
            Draw.TextCentered(g, empty ? "●" : "▢", F(30), Ink(0.42), card.X + card.Width / 2, card.Y + 32 * k);
            Draw.TextCentered(g, title, Semibold(16), TextColor, card.X + card.Width / 2, card.Y + 68 * k);
            Draw.TextCentered(g, sub, F(13), Ink(0.6), card.X + card.Width / 2, card.Y + 94 * k);
        }
    }

    /// The spoken words at this moment, like subtitles, ending at `bottom`.
    void DrawCaption(Graphics g, float bottom)
    {
        if (model.Caption is not string c) return;
        float cw = ClientSize.Width - (ChatOpen ? chatPanel!.Width : 0);
        var box = new RectangleF(0, 0, Math.Min(900 * k, cw - 80 * k), 0);
        var sz = g.MeasureString(c, Semibold(17), (int)(box.Width - 32 * k));
        sz.Height = Math.Min(sz.Height, Semibold(17).GetHeight(g) * 3);
        var cr = new RectangleF(cw / 2 - (sz.Width + 32 * k) / 2, bottom - sz.Height - 18 * k, sz.Width + 32 * k, sz.Height + 18 * k);
        Draw.Fill(g, cr, 10 * k, Draw.Black(0.78));
        Draw.Wrapped(g, c, Semibold(17), Color.White, new RectangleF(cr.X + 16 * k, cr.Y + 9 * k, sz.Width + 2, sz.Height), center: true);
    }

    /// Goes back to what was on screen at this moment: the browser tab, or the app window. Same as O.
    /// Drawn in the controls row with its right edge at `right`; returns its left edge.
    float DrawOpenButton(Graphics g, float right, float mid)
    {
        if (model.CurrentTarget is not OpenTarget t || model.Current is not TimelineFrame f) return right;
        var title = t.IsWeb ? "Open this tab in browser" : $"Go to {f.AppName}";
        var detail = t.IsWeb ? WebAddress.Host(t.Url!) : f.WindowName.Length > 0 ? f.WindowName : f.AppName;
        Font tf = Semibold(14), df = F(11);
        float maxText = 260 * k;
        float textW = Math.Min(maxText, Math.Max(Draw.Measure(g, title, tf).Width, Draw.Measure(g, detail, df).Width));
        var keyW = Draw.Measure(g, "O", F(10)).Width + 10 * k;
        float w = 16 * k + 16 * k + 8 * k + textW + 8 * k + Math.Max(18 * k, keyW) + 16 * k, h = 40 * k;
        var r = new RectangleF(right - w, mid - h / 2, w, h);
        bool hover = r.Contains(PointToClient(Cursor.Position));
        using (var shadow = Draw.Round(new RectangleF(r.X, r.Y + 3 * k, r.Width, r.Height), r.Height / 2))
        using (var sb = new SolidBrush(Draw.Black(0.25))) g.FillPath(sb, shadow);
        Draw.Fill(g, r, r.Height / 2, hover ? ControlPaint.Light(Accent, 0.15f) : Accent);
        DrawOpenIcon(g, r.X + 24 * k, mid);
        float tx = r.X + 40 * k;
        Draw.Text(g, title, tf, Color.White, tx, mid - 17 * k, textW + 2);
        Draw.Text(g, detail, df, Draw.White(0.8), tx, mid + 1 * k, textW + 2);
        Draw.Keycap(g, Color.White, "O", F(10), r.Right - 16 * k - Math.Max(18 * k, keyW), mid, k);
        hot.Add((r, OpenCurrent));
        return r.X;
    }

    // MARK: controls row

    /// Above the scrubber: the app on the left, audio in the middle; Open, Live/Now and (with the full bar) zoom on the right.
    void DrawControls(Graphics g, RectangleF r)
    {
        float mid = r.Y + r.Height / 2;
        if (model.Current is TimelineFrame f)
        {
            var name = f.AppName.Length == 0 ? "Unknown app" : f.AppName;
            float maxW = r.Width / 3 - 60 * k;
            float textW = Math.Min(maxW, Math.Max(Draw.Measure(g, name, Semibold(13)).Width, f.WindowName.Length > 0 ? Draw.Measure(g, f.WindowName, F(12)).Width : 0));
            var pill = new RectangleF(r.X, r.Y - 2 * k, 10 * k + 28 * k + 10 * k + textW + 16 * k, r.Height + 4 * k);
            Glass(g, pill, pill.Height / 2);
            var icon = AppIcons.Get(f.AppName);
            var ir = new RectangleF(pill.X + 10 * k, mid - 12 * k, 24 * k, 24 * k);
            if (icon != null) g.DrawImage(icon, ir);
            else Draw.Fill(g, ir, 6 * k, Ink(0.14));
            // Same color as this app's blocks on the bar.
            var dot = new RectangleF(ir.Right - 6 * k, ir.Bottom - 6 * k, 8 * k, 8 * k);
            using (var b = new SolidBrush(TimelineModel.AppColor(f.AppName))) g.FillEllipse(b, dot);
            using (var p = new Pen(dark ? Draw.Black(0.6) : Draw.White(0.9), 1.5f * k)) g.DrawEllipse(p, dot);
            float tx = ir.Right + 10 * k;
            if (f.WindowName.Length > 0)
            {
                Draw.Text(g, name, Semibold(13), TextColor, tx, mid - 17 * k, textW + 2);
                Draw.Text(g, f.WindowName, F(12), Ink(0.6), tx, mid + 1 * k, textW + 2);
            }
            else Draw.Text(g, name, Semibold(13), TextColor, tx, mid - 9 * k, textW + 2);
        }

        // Audio buttons, centered.
        var here = model.AudioHere;
        if (here.Count == 0)
        {
            const string none = "No audio at this moment";
            var sz = Draw.Measure(g, none, F(12));
            var pr0 = new RectangleF(r.X + r.Width / 2 - sz.Width / 2 - 12 * k, mid - 14 * k, sz.Width + 24 * k, 28 * k);
            Glass(g, pr0, pr0.Height / 2);
            Draw.TextCentered(g, none, F(12), Ink(0.6), r.X + r.Width / 2, mid);
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
                Glass(g, br, br.Height / 2);
                if (on) Draw.Fill(g, br, br.Height / 2, Accent);
                Draw.Text(g, on ? "❚❚" : "▶", F(10), on ? Color.White : TextColor, br.X + 13 * k, mid - 7 * k);
                Draw.Text(g, a.IsInput ? "Microphone" : "System audio", f13, on ? Color.White : TextColor, br.X + 28 * k, mid - 8 * k);
                // Matches the audio lane color on the bar.
                using (var b = new SolidBrush(a.IsInput ? Mic : Sys)) g.FillEllipse(b, br.Right - 15 * k, mid - 3.5f * k, 7 * k, 7 * k);
                var capture = a;
                hot.Add((br, () => { if (model.PlayingChunk == capture.Id) { model.StopAudio(); Invalidate(); } else model.Play(capture); }));
                x += widths[i] + 8 * k;
            }
        }

        // Zoom picker (with the full bar only), Live/Now and Open, right-aligned.
        float right = r.Right;
        if (DockExpanded)
        {
            var steps = new (string Label, double Span)[] { ("5m", 300), ("15m", 900), ("1h", 3600), ("4h", 4 * 3600), ("12h", 12 * 3600) };
            float segW = 38 * k, pw = segW * steps.Length + 4 * k;
            var pr = new RectangleF(right - pw, mid - 14 * k, pw, 28 * k);
            Glass(g, pr, pr.Height / 2);
            for (int i = 0; i < steps.Length; i++)
            {
                var st = steps[i];
                bool on = Math.Abs(model.Span - st.Span) < 1;
                var sr = new RectangleF(pr.X + 2 * k + i * segW, pr.Y + 2 * k, segW, pr.Height - 4 * k);
                if (on) Draw.Fill(g, sr, sr.Height / 2, Ink(0.22));
                Draw.TextCentered(g, st.Label, on ? Semibold(12) : F(12), on ? TextColor : Ink(0.6), sr.X + sr.Width / 2, sr.Y + sr.Height / 2);
                var span = st.Span;
                hot.Add((sr, () => { model.Span = span; Invalidate(); }));
            }
            right = pr.X - 10 * k;
        }
        var live = model.IsLive;
        var label = live ? "Live" : "Now ⏭";
        var lw = Draw.Measure(g, label, Semibold(12)).Width + (live ? 34 : 24) * k;
        var lr = new RectangleF(right - lw, mid - 14 * k, lw, 28 * k);
        Glass(g, lr, lr.Height / 2);
        if (live)
        {
            using var red = new SolidBrush(Color.FromArgb(255, 69, 58));
            g.FillEllipse(red, lr.X + 12 * k, mid - 3.5f * k, 7 * k, 7 * k);
            Draw.Text(g, label, Semibold(12), Ink(0.6), lr.X + 24 * k, mid - 8 * k);
        }
        else
        {
            Draw.Fill(g, lr, lr.Height / 2, Accent);
            Draw.Text(g, label, Semibold(12), Color.White, lr.X + 12 * k, mid - 8 * k);  // on the accent color
            hot.Add((lr, model.JumpToNow));
        }
        DrawOpenButton(g, lr.X - 10 * k, mid);
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

        if (!DockExpanded)
        {
            DrawCompactBar(g, r, start, end, X, px);
            return;
        }
        Draw.Fill(g, new RectangleF(r.X, r.Y + 20 * k, r.Width, r.Height - 20 * k), 10 * k, Ink(0.06));
        var state = g.Save();
        g.SetClip(new RectangleF(r.X, r.Y, r.Width, r.Height));

        // Solid blocks: one per run of frames from the same app (a frame lasts until the next, max 60 s).
        // While filtering by app, a frame lasts until the next screenshot of any app (its End).
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
                    var next = f.End ?? (j + 1 < frames.Count ? frames[j + 1].Time : f.Time.AddSeconds(2));
                    runEnd = next < f.Time.AddSeconds(60) ? next : f.Time.AddSeconds(60);
                    j++;
                    if ((next - f.Time).TotalSeconds > 60) break;  // idle: end the block here
                    if (j < frames.Count && frames[j].Time > next) break;  // another app was in front in between
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
            Draw.Fill(g, nr, 4 * k, Shade(0.45));
            using (var pen = new Pen(Ink(0.25), 1) { DashPattern = new[] { 3f, 3f } })
                g.DrawLine(pen, nr.X + nr.Width / 2, nr.Y + 4 * k, nr.X + nr.Width / 2, nr.Bottom - 16 * k);
            var label = TimelineAxis.BreakLabel(to - from);
            if (Draw.Measure(g, label, F(9)).Width < nr.Width + 30 * k)
                Draw.TextCentered(g, label, F(9), Ink(0.6), nr.X + nr.Width / 2, nr.Bottom - 7 * k);
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
                using (var b = new SolidBrush(Ink(0.35))) g.FillRectangle(b, tx - 0.5f, r.Y + TickY, 1, 4 * k);
                if (tx - lastLabelX > 60 * k)
                {
                    var fmt = step >= 3600 ? "h tt" : "t";
                    var text = t.ToLocalTime().ToString(fmt, CultureInfo.CurrentCulture);
                    Draw.TextCentered(g, text, F(10), Ink(0.6), tx, r.Y + TickY + 11 * k);
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

        DrawStars(g, r, start, end, X);

        // The clip marked with [ and ]: a tinted band between two flags.
        float? ma = model.ClipStart is DateTime s1 ? X(s1) : null, mb = model.ClipEnd is DateTime e1 ? X(e1) : null;
        if (ma is float a0 && mb is float b0)
            using (var band = new SolidBrush(Draw.WithAlpha(Accent, 0.22))) g.FillRectangle(band, a0, r.Y + 22 * k, b0 - a0, r.Height - 24 * k);
        using (var flag = new SolidBrush(Accent))
            foreach (var (mx, isStart) in new[] { (ma, true), (mb, false) })
            {
                if (mx is not float fx) continue;
                g.FillRectangle(flag, fx - 1 * k, r.Y + 20 * k, 2 * k, r.Height - 22 * k);
                float dir = isStart ? 7 * k : -7 * k;
                g.FillPolygon(flag, new[] { new PointF(fx, r.Y + 20 * k), new PointF(fx + dir, r.Y + 24 * k), new PointF(fx, r.Y + 28 * k) });
            }

        // "Now" edge.
        float nx = X(now);
        if (nx < r.Right - 1)
        {
            using var shade = new SolidBrush(Shade(0.3));
            g.FillRectangle(shade, nx, r.Y + 22 * k, r.Right - nx, r.Height - 22 * k);
            using var red = new SolidBrush(Color.FromArgb(178, 255, 59, 48));
            g.FillRectangle(red, nx - 0.5f, r.Y + 22 * k, 1, r.Height - 24 * k);
        }

        // Hover line.
        if (hoverX is float hx && dragStartV == null && Math.Abs(hx - px) > 3 * k)
            using (var b = new SolidBrush(Ink(0.45))) g.FillRectangle(b, hx - 0.5f, r.Y + 20 * k, 1, r.Height - 22 * k);

        // Playhead with its time in a small bubble that stays inside the bar.
        using (var b = new SolidBrush(Ink(1))) g.FillRectangle(b, px - 1 * k, r.Y + 16 * k, 2 * k, r.Height - 18 * k);
        var pt = model.Playhead.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        var pf = Semibold(10);
        var ps = Draw.Measure(g, pt, pf);
        float bw = ps.Width + 12 * k;
        float bubbleX = Math.Min(Math.Max(r.X, px - bw / 2), r.Right - bw);
        var bubble = new RectangleF(bubbleX, r.Y + 1 * k, bw, 15 * k);
        Draw.Fill(g, bubble, 7.5f * k, Ink(1));
        Draw.TextCentered(g, pt, pf, dark ? Color.Black : Color.White, bubble.X + bubble.Width / 2, bubble.Y + bubble.Height / 2);
        g.Restore(state);

        // Hover preview card above the bar.
        if (hoverX is float hx2 && dragStartV == null && Math.Abs(hx2 - px) > 3 * k)
        {
            var d = axis.Date(v0 + (hx2 - r.X) / r.Width * model.Span);
            if (d <= now) DrawPreview(g, d, hx2, r);
        }
    }

    /// Starred moments: a small yellow star each.
    void DrawStars(Graphics g, RectangleF r, DateTime start, DateTime end, Func<DateTime, float> X)
    {
        using (var sb = new SolidBrush(Yellow))
        using (var edge = new Pen(Color.FromArgb(90, 0, 0, 0), 0.6f * k))
            foreach (var st in model.Stars.Where(s => s.Time >= start && s.Time <= end))
            {
                float cx = X(st.Time), cy = r.Y + 29 * k, rad = 5.5f * k;
                var pts = Enumerable.Range(0, 10).Select(i =>
                {
                    double ang = i * Math.PI / 5 - Math.PI / 2, rr = i % 2 == 0 ? rad : rad * 0.45;
                    return new PointF(cx + (float)(Math.Cos(ang) * rr), cy + (float)(Math.Sin(ang) * rr));
                }).ToArray();
                g.FillPolygon(sb, pts);
                g.DrawPolygon(edge, pts);
            }
    }

    /// Rewind's thin scrubber: app colors as a faint strip along the bottom edge, search matches and clip marks,
    /// and a white playhead standing up from the edge.
    void DrawCompactBar(Graphics g, RectangleF r, DateTime start, DateTime end, Func<DateTime, float> X, float px)
    {
        float stripY = r.Bottom - 5 * k;
        var frames = model.Frames;
        if (frames.Count > 0)
        {
            int last = model.FrameIndex(end);
            for (int i = Math.Max(0, model.FrameIndex(start)); i <= last; i++)
            {
                var f = frames[i];
                var next = f.End ?? (i + 1 < frames.Count ? frames[i + 1].Time : f.Time.AddSeconds(2));
                if (next > f.Time.AddSeconds(60)) next = f.Time.AddSeconds(60);
                float a = X(f.Time), b = X(next);
                if (b < r.X || a > r.Right) continue;
                using var br = new SolidBrush(Draw.WithAlpha(TimelineModel.AppColor(f.AppName), 0.55));
                g.FillRectangle(br, a, stripY, Math.Max(1, b - a), 3 * k);
            }
        }
        using (var yb = new SolidBrush(Yellow))
            foreach (var h in model.Hits.Where(h => h.Time >= start && h.Time <= end))
                g.FillRectangle(yb, X(h.Time) - 1 * k, stripY - 6 * k, 2 * k, 9 * k);
        using (var ab = new SolidBrush(Accent))
            foreach (var m in new[] { model.ClipStart, model.ClipEnd })
                if (m is DateTime md) g.FillRectangle(ab, X(md) - 1 * k, stripY - 10 * k, 2 * k, 13 * k);
        DrawStars(g, r, start, end, X);
        var head = new RectangleF(px - 2 * k, r.Y + 2 * k, 4 * k, r.Height - 2 * k);
        Draw.Fill(g, new RectangleF(head.X - 1 * k, head.Y + 1 * k, head.Width + 2 * k, head.Height), 3 * k, Draw.Black(0.25));
        Draw.Fill(g, head, 2 * k, Color.White);
    }

    /// Small card above the bar: the screenshot, app and time under the pointer.
    void DrawPreview(Graphics g, DateTime date, float hx, RectangleF barRect)
    {
        float w = 240 * k, h = 178 * k;
        float x = Math.Min(Math.Max(barRect.X, hx - w / 2), barRect.Right - w);
        var card = new RectangleF(x, barRect.Y - h - 10 * k, w, h);
        Draw.Fill(g, card, 10 * k, CardColor);
        Draw.Stroke(g, card, 10 * k, Ink(0.15));
        var p = model.Preview(date);
        var ir = new RectangleF(card.X + 8 * k, card.Y + 8 * k, w - 16 * k, 128 * k);
        Draw.Fill(g, ir, 6 * k, Ink(0.06));
        if (p?.Thumb is Image img)
        {
            float s = Math.Min(ir.Width / img.Width, ir.Height / img.Height);
            g.DrawImage(img, ir.X + (ir.Width - img.Width * s) / 2, ir.Y + (ir.Height - img.Height * s) / 2, img.Width * s, img.Height * s);
        }
        else Draw.TextCentered(g, p == null ? "Nothing recorded" : "No picture", F(11), Ink(0.5), ir.X + ir.Width / 2, ir.Y + ir.Height / 2);
        float ly = ir.Bottom + 12 * k;
        var time = date.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        var ts = Draw.Measure(g, time, Semibold(11));
        Draw.Text(g, time, Semibold(11), Ink(0.6), card.Right - 8 * k - ts.Width, ly);
        if (p?.Frame is TimelineFrame f)
        {
            using (var b = new SolidBrush(TimelineModel.AppColor(f.AppName))) g.FillEllipse(b, card.X + 8 * k, ly + 4 * k, 7 * k, 7 * k);
            Draw.Text(g, f.AppName.Length == 0 ? "Unknown app" : f.AppName, Semibold(11), TextColor, card.X + 20 * k, ly, w - 36 * k - ts.Width);
        }
    }

    // MARK: results, help

    void DrawResults(Graphics g)
    {
        float w = Math.Min(640 * k, shot.Width), rowH = 52 * k, headH = 30 * k;
        var hits = model.Hits;
        float listH = hits.Count == 0 ? 52 * k : Math.Min(440 * k, headH + hits.Count * rowH + 12 * k);
        var box = new RectangleF(searchPill.X, searchPill.Bottom + 14 * k, w, listH);
        Draw.Fill(g, box, 12 * k, CardColor);
        Draw.Stroke(g, box, 12 * k, Ink(0.12));
        hot.Add((box, () => { }));  // clicks on the list do not fall through
        if (hits.Count == 0)
        {
            var empty = model.Searching ? "Searching by meaning…"
                : model.StarredOnly ? (model.Query.Trim().Length == 0 ? $"No starred moments yet. Press S here, or {HotkeyShortcut.Star.Label} anywhere, to star one." : $"No starred moments match “{model.Query}”")
                : $"No matches for “{model.Query}”";
            Draw.Text(g, empty, F(14), Ink(0.6), box.X + 16 * k, box.Y + 16 * k);
            return;
        }
        var head = $"{hits.Count} {(model.StarredOnly ? "starred " : "")}moment{(hits.Count == 1 ? "" : "s")}" + (model.SearchingByMeaning && !model.StarredOnly ? " · closest in meaning first" : "");
        Draw.Text(g, head, F(11), Ink(0.42), box.X + 14 * k, box.Y + 9 * k);
        float hx = box.Right - 14 * k;
        foreach (var part in new[] { "go", "⏎", "choose", "↓", "↑" })
        {
            bool key = part.Length == 1;
            if (key) hx -= Draw.Keycap(g, Ink(1), part, F(10), hx - 18 * k, box.Y + headH / 2, k) + 4 * k;
            else { var sz = Draw.Measure(g, part, F(11)); hx -= sz.Width + 6 * k; Draw.Text(g, part, F(11), Ink(0.42), hx, box.Y + 9 * k); }
        }
        // Keep the selected row in view.
        int visible = (int)((box.Height - headH - 12 * k) / rowH);
        if (model.SelectedHit < resultsScroll) resultsScroll = model.SelectedHit;
        if (model.SelectedHit >= resultsScroll + visible) resultsScroll = model.SelectedHit - visible + 1;
        resultsScroll = Math.Clamp(resultsScroll, 0, Math.Max(0, hits.Count - visible));
        // A meaning match need not contain the typed words, so none are marked.
        var words = model.SearchingByMeaning ? Array.Empty<string>() : model.Query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = resultsScroll; i < Math.Min(hits.Count, resultsScroll + visible); i++)
        {
            var h = hits[i];
            var rr = new RectangleF(box.X + 6 * k, box.Y + headH + (i - resultsScroll) * rowH, box.Width - 12 * k, rowH - 2 * k);
            if (i == model.SelectedHit) Draw.Fill(g, rr, 8 * k, Draw.WithAlpha(Accent, 0.35));
            var icon = h.IsAudio || h.IsStar ? null : AppIcons.Get(h.Title);
            if (icon != null) g.DrawImage(icon, rr.X + 10 * k, rr.Y + 8 * k, 18 * k, 18 * k);
            else Draw.Text(g, h.IsStar ? "★" : h.IsAudio ? "∿" : "▭", Semibold(14), h.IsStar ? Yellow : h.IsAudio ? Sys : Ink(0.6), rr.X + 11 * k, rr.Y + 5 * k);
            var when = h.Time.ToLocalTime().ToString("MMM d, T", CultureInfo.CurrentCulture);
            var ws = Draw.Measure(g, when, F(11));
            Draw.Text(g, when, F(11), Ink(0.42), rr.Right - 10 * k - ws.Width, rr.Y + 7 * k);
            Draw.Text(g, h.Title, Semibold(13), TextColor, rr.X + 40 * k, rr.Y + 6 * k, rr.Width - 60 * k - ws.Width);
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
                Draw.Text(g, piece, plain, Ink(0.8), x, y, right - x);
                x += Draw.Measure(g, piece, plain).Width;
            }
            if (best < 0 || x >= right) break;
            var match = text.Substring(best, len);
            Draw.Text(g, match, bold, MatchColor, x, y, right - x);
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
        ("OPEN", new[] { new[] { "O" } }, "Open the tab or window from this moment"),
        ("STAR", new[] { new[] { "S" } }, "Star this moment / remove its star"),
        ("FILTER", new[] { new[] { "A" } }, "Only this app / all apps again"),
        ("", new[] { new[] { "H" } }, "Hide this app from the timeline"),
        ("AUDIO", new[] { new[] { "space" } }, "Play or pause audio from here"),
        ("COPY", new[] { new[] { "Ctrl", "C" } }, "Copy the selected text, or the screenshot"),
        ("", new[] { new[] { "drag" } }, "Select text on the screenshot"),
        ("", new[] { new[] { "Ctrl", "Shift", "C" } }, "Copy all the text on the screenshot"),
        ("", new[] { new[] { "Ctrl", "S" } }, "Save the screenshot as a PNG"),
        ("CLIP", new[] { new[] { "[" }, new[] { "]" } }, "Mark where a clip starts / ends"),
        ("", new[] { new[] { "Ctrl", "E" } }, "Export a clip as MP4 or GIF"),
        ("SEARCH", new[] { new[] { "/" }, new[] { "Ctrl", "F" } }, "Search screen text and speech"),
        ("", new[] { new[] { "Ctrl", "G" }, new[] { "Shift", "Ctrl", "G" } }, "Older / newer match"),
        ("VIEW", new[] { new[] { "+" }, new[] { "−" } }, "Zoom the bar in / out"),
        ("", new[] { new[] { "esc" } }, "Close the list, then the overlay"),
    };

    void DrawHelp(Graphics g)
    {
        using (var b = new SolidBrush(Draw.Black(dark ? 0.45 : 0.2))) g.FillRectangle(b, ClientRectangle);
        hot.Clear();  // only the card is clickable now
        hot.Add((ClientRectangle, () => { model.ShowHelp = false; Invalidate(); }));
        float w = 600 * k, rowH = 28 * k, h = 24 * k * 2 + 30 * k + Shortcuts.Length * rowH + 30 * k;
        var card = new RectangleF(ClientSize.Width / 2f - w / 2, ClientSize.Height / 2f - h / 2, w, h);
        Draw.Fill(g, card, 16 * k, CardColor);
        Draw.Stroke(g, card, 16 * k, Ink(0.12));
        hot.Add((card, () => { }));
        Draw.Text(g, "Keyboard shortcuts", Semibold(17), TextColor, card.X + 24 * k, card.Y + 22 * k);
        RoundButton(g, new RectangleF(card.Right - 54 * k, card.Y + 18 * k, 30 * k, 30 * k), "✕", () => { model.ShowHelp = false; Invalidate(); });
        float y = card.Y + 66 * k;
        foreach (var (group, keys, what) in Shortcuts)
        {
            float mid = y + rowH / 2;
            Draw.Text(g, group, Semibold(11), Ink(0.42), card.X + 24 * k, mid - 7 * k);
            float x = card.X + 100 * k;
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) { Draw.Text(g, "/", F(12), Ink(0.42), x, mid - 8 * k); x += 12 * k; }
                foreach (var key in keys[i]) x += Draw.Keycap(g, Ink(1), key, F(12), x, mid, k) + 3 * k;
                x += 3 * k;
            }
            Draw.Text(g, what, F(13), Ink(0.85), card.X + 300 * k, mid - 9 * k);
            y += rowH;
        }
        Draw.Text(g, "Ctrl+Shift + scroll anywhere opens the timeline. Hover the bottom for the full bar. Right-click the screenshot for more.", F(12), Ink(0.6), card.X + 24 * k, y + 6 * k);
    }

    // MARK: input

    /// A: show only the app in this screenshot, or every app again.
    void ToggleAppFilter()
    {
        if (model.AppFilterName != null) model.SetAppFilter(null);
        else if (model.Current is TimelineFrame f && f.AppName.Length > 0)
        {
            model.SetAppFilter(f.AppName);
            model.ShowNotice($"Only showing {f.AppName}. Press A for all apps.");
        }
    }

    void FocusSearch()
    {
        model.ShowResults = model.Query.Length > 0;
        search.Focus();
        search.SelectionStart = search.Text.Length;
        Invalidate();
    }

    void LeaveSearch() { ActiveControl = null; Focus(); Invalidate(); }

    /// Go back to this moment: the browser tab, or the app window.
    void OpenCurrent()
    {
        if (model.CurrentTarget is not OpenTarget t || model.Current is not TimelineFrame f)
        {
            model.ShowNotice("Nothing to open for this moment");
            return;
        }
        HideOverlay(restoreFocus: false);
        if (!Reopen.Open(f, t)) Log.Write($"could not open {f.AppName}");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0;
        bool typing = search.Focused;

        // Typing in the chat: Esc leaves it, everything else goes to the chat.
        if (ChatOpen && chatPanel!.ContainsFocus)
        {
            if (key == Keys.Escape) { LeaveSearch(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
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
        if (ctrl && shift && key == Keys.C) { model.CopyAllText(); return true; }
        if (ctrl && key == Keys.C) { model.Copy(); return true; }
        if (ctrl && key == Keys.S) { model.SaveFrame(this); return true; }
        if (ctrl && key == Keys.E) { ExportClip(); return true; }
        if (key == Keys.OemQuestion && shift) { model.ShowHelp = !model.ShowHelp; Invalidate(); return true; }
        if ((key == Keys.OemQuestion || key == Keys.Divide) && !ctrl) { FocusSearch(); return true; }
        switch (key)
        {
            case Keys.Escape:
                if (model.ShowHelp) model.ShowHelp = false;
                else if (model.ShowResults) model.ShowResults = false;
                else if (ChatOpen) chatPanel!.Close();
                else if (model.HasSelection) model.ClearSelection();
                else if (model.HasMarks) model.ClearMarks();
                else { HideOverlay(); return true; }
                Invalidate();
                break;
            case Keys.Left: if (shift) model.Jump(model.Playhead.AddSeconds(-60)); else model.Step(-1); break;
            case Keys.Right: if (shift) model.Jump(model.Playhead.AddSeconds(60)); else model.Step(1); break;
            case Keys.Up: model.Jump(model.Playhead.AddSeconds(-600)); break;
            case Keys.Down: model.Jump(model.Playhead.AddSeconds(600)); break;
            case Keys.Space: model.ToggleAudio(); break;
            case Keys.N: case Keys.End: model.JumpToNow(); break;
            case Keys.O when !ctrl: OpenCurrent(); break;
            case Keys.A when !ctrl: ToggleAppFilter(); break;
            case Keys.S when !ctrl: model.ToggleStar(); break;
            case Keys.H when !ctrl: model.HideCurrentApp(); Invalidate(); break;
            case Keys.OemOpenBrackets when !ctrl: model.MarkStart(); break;
            case Keys.OemCloseBrackets when !ctrl: model.MarkEnd(); break;
            case Keys.Oemplus: case Keys.Add: model.Span = Math.Max(60, model.Span / 2); Invalidate(); break;
            case Keys.OemMinus: case Keys.Subtract: model.Span = Math.Min(12 * 3600, model.Span * 2); Invalidate(); break;
            default: return base.ProcessCmdKey(ref msg, keyData);
        }
        return true;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Over the chat, the wheel scrolls the chat (it arrives here when the chat box has the keyboard).
        if (ChatOpen && chatPanel!.Bounds.Contains(e.Location)) return;
        if (model.ShowResults && model.Hits.Count > 0) { model.MoveSelection(e.Delta > 0 ? -1 : 1); return; }
        ScrollTime(e.Delta);
    }

    /// Wheel up = back in time. Mouse wheels send 120 per notch (one step); touchpads send many small deltas.
    /// Also called with the turn that opened the timeline (Ctrl+Shift + scroll, see ScrollOpener).
    public void ScrollTime(int delta)
    {
        if (Math.Abs(delta) >= 120 && delta % 120 == 0) { model.Step(-delta / 120); return; }
        scrollAccum += delta;
        int steps = scrollAccum / 40;
        if (steps != 0) { scrollAccum -= steps * 40; model.Step(-steps); }
    }

    bool InBar(Point p) => bar.Contains(p) && (!DockExpanded || p.Y > bar.Y + 16 * k);

    /// A point on the screen as fractions of the shown screenshot.
    PointF InImage(Point p) => new((p.X - imageRect.X) / imageRect.Width, (p.Y - imageRect.Y) / imageRect.Height);

    /// True over the bare screenshot: not over a button on it or a card above it.
    bool OnScreenshot(Point p) =>
        !model.ShowHelp && !model.ShowResults && imageRect.Contains(p) && p.Y < dockTop && !hot.Any(h => h.Rect.Contains(p) && h.Rect.Width < ClientSize.Width / 2);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right && OnScreenshot(e.Location)) { ShowFrameMenu(e.Location); return; }
        if (e.Button != MouseButtons.Left) return;
        if (OnScreenshot(e.Location))
        {
            // Drag across words to select them; a click elsewhere clears the selection.
            if (search.Focused || (ChatOpen && chatPanel!.ContainsFocus)) LeaveSearch();
            selectFrom = InImage(e.Location);
            int at = model.Text?.WordAt(selectFrom) ?? -1;
            model.Select(at, at);
            selecting = model.Text is { IsEmpty: false };
            Capture = selecting;
            return;
        }
        if (!model.ShowHelp && !(model.ShowResults && hot.Any(h => h.Rect.Contains(e.Location) && h.Rect.Y < bar.Y)) && InBar(e.Location))
        {
            // A click jumps to where you clicked; a drag then moves from there.
            var axis = model.Axis;
            var v = V0(axis) + (e.X - bar.X) / bar.Width * model.Span;
            dragStartV = v;
            dragStartX = e.X;
            model.Jump(axis.Date(v));
            Capture = true;
            if (ChatOpen && chatPanel!.ContainsFocus) LeaveSearch();
            return;
        }
        // Last added is on top.
        for (int i = hot.Count - 1; i >= 0; i--)
            if (hot[i].Rect.Contains(e.Location)) { hot[i].Act(); Invalidate(); return; }
        if (search.Focused || (ChatOpen && chatPanel!.ContainsFocus)) LeaveSearch();
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
        if (selecting && e.Button == MouseButtons.Left && model.Text is FrameText text)
        {
            int anchor = model.SelectionAnchor >= 0 ? model.SelectionAnchor : text.NearestWord(selectFrom);
            model.Select(anchor, text.NearestWord(InImage(e.Location)));
            return;
        }
        SetDockHover(e.Y >= dockTop && !model.ShowHelp);
        float? hx = InBar(e.Location) && !model.ShowHelp && DockExpanded ? e.X : null;
        if (hx != hoverX) { hoverX = hx; Invalidate(); }
        Cursor = hot.Any(h => h.Rect.Contains(e.Location) && h.Rect.Width < ClientSize.Width / 2) || hx != null ? Cursors.Hand
               : OnScreenshot(e.Location) && model.Text?.WordAt(InImage(e.Location)) >= 0 ? Cursors.IBeam
               : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragStartV = null;
        selecting = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverX != null) { hoverX = null; Invalidate(); }
        SetDockHover(false);
    }

    /// The pointer came near the bottom (full bar now) or left it (thin scrubber again, after a moment).
    void SetDockHover(bool inside)
    {
        if (inside)
        {
            dockLeave.Stop();
            if (!dockHover) { dockHover = true; Invalidate(); }
        }
        else if (dockHover && !dockLeave.Enabled) dockLeave.Start();
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
