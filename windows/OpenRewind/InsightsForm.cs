using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Insights: where the time went in a day or a week, worked out on this PC from the saved frames
/// (no AI). Blocked apps, windows and sites are left out. Same window as InsightsView on the Mac.
sealed class InsightsForm : Form
{
    static InsightsForm? open;
    readonly InsightsView view;
    readonly Panel scroller;

    /// One window at a time. `openAt` shows the timeline at a moment, only showing an app when given.
    public static void ShowFor(Settings settings, Database db, Action<DateTime, string?> openAt)
    {
        if (open is { IsDisposed: false }) { open.view.Reload(); open.Activate(); return; }
        open = new InsightsForm(settings, db, openAt);
        open.Show();
        open.Activate();
    }

    InsightsForm(Settings settings, Database db, Action<DateTime, string?> openAt)
    {
        var k = DeviceDpi / 96f;
        var dark = settings.TimelineLook switch { "light" => false, "dark" => true, _ => OverlayForm.SystemUsesDark() };
        Text = "Insights";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size((int)(520 * k), (int)(420 * k));
        ClientSize = new Size((int)(660 * k), (int)(820 * k));
        StartPosition = FormStartPosition.CenterScreen;
        view = new InsightsView(db, openAt, dark, k);
        BackColor = view.Bg;
        scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = view.Bg };
        scroller.Controls.Add(view);
        Controls.Add(scroller);
        scroller.Resize += (_, _) => FitWidth();
        view.HeightChanged += FitWidth;
        HandleCreated += (_, _) => { int on = dark ? 1 : 0; DwmSetWindowAttribute(Handle, 20, ref on, 4); };
        FitWidth();
    }

    /// The view is as wide as the window and as tall as what it shows; the panel scrolls it.
    void FitWidth()
    {
        var w = scroller.ClientSize.Width;
        if (view.Width != w) view.Width = w;
    }

    /// ← → previous / next, D / W day or week, Esc closes.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Left: view.Step(-1); return true;
            case Keys.Right: view.Step(1); return true;
            case Keys.D: view.SetRange(InsightsRange.Day); return true;
            case Keys.W: view.SetRange(InsightsRange.Week); return true;
            case Keys.Escape: Close(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { open = null; base.OnFormClosed(e); }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// Everything in the Insights window, drawn with GDI+. Laid out while painting; a click or a
/// hover is matched against the shapes drawn last time.
sealed class InsightsView : Control
{
    readonly Database db;
    readonly Action<DateTime, string?> openAt;
    readonly bool dark;
    readonly float k;
    InsightsRange range = InsightsRange.Day;
    DateTime day = DateTime.Today;
    InsightsReport? report;
    string? error;
    bool loading;
    int loadId;
    readonly List<(RectangleF R, Action Act)> hits = new();
    readonly List<(RectangleF R, string Tip)> tips = new();
    readonly ToolTip tooltip = new() { InitialDelay = 300, ReshowDelay = 100 };
    string? shownTip;
    PointF mouse = new(-1, -1);

    public event Action? HeightChanged;

    public Color Bg => dark ? Color.FromArgb(24, 24, 27) : Color.FromArgb(246, 246, 248);
    Color CardColor => dark ? Color.FromArgb(36, 36, 40) : Color.White;
    Color Hover => dark ? Color.FromArgb(48, 48, 54) : Color.FromArgb(238, 238, 242);
    Color TextColor => dark ? Color.FromArgb(240, 240, 242) : Color.FromArgb(26, 26, 28);
    Color Dim => dark ? Color.FromArgb(165, 165, 172) : Color.FromArgb(96, 96, 104);
    Color Faint => dark ? Color.FromArgb(110, 110, 118) : Color.FromArgb(160, 160, 168);
    Color Accent => dark ? Color.FromArgb(64, 156, 255) : Color.FromArgb(0, 113, 227);
    Color SiteColor => dark ? Color.FromArgb(48, 209, 160) : Color.FromArgb(0, 150, 110);
    Color EmptyCell => dark ? Color.FromArgb(46, 46, 52) : Color.FromArgb(232, 232, 237);
    Color Line => dark ? Color.FromArgb(52, 52, 58) : Color.FromArgb(222, 222, 228);

    public InsightsView(Database db, Action<DateTime, string?> openAt, bool dark, float k)
    {
        this.db = db;
        this.openAt = openAt;
        this.dark = dark;
        this.k = k;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Bg;
        Height = (int)(800 * k);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Reload(); }

    // MARK: data

    public void SetRange(InsightsRange r)
    {
        if (r == range) return;
        range = r;
        Reload();
    }

    /// Previous (-1) or next (+1) day or week; never past the current one.
    public void Step(int n)
    {
        if (n > 0 && IsCurrent(day)) return;
        day = Insights.Step(range, day, n);
        Reload();
    }

    bool IsCurrent(DateTime d)
    {
        var (s, e) = Insights.LocalPeriod(range, d, FirstDay);
        return DateTime.Today >= s && DateTime.Today < e;
    }

    static DayOfWeek FirstDay => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    /// Works out the report off the UI thread; the last one asked for wins.
    public void Reload()
    {
        if (!IsHandleCreated) return;
        var id = ++loadId;
        loading = true;
        Invalidate();
        var (r, d) = (range, day);
        // Same rule as Ask AI: app name, private-window title, blocked site, or a blocked word in the title or address.
        var list = Blocklist.Current;
        Func<string, string, string, bool> hides = (a, w, u) => list.BlocksWindow(a, a, w) || list.BlocksUrl(u) || list.HasKeyword(u);
        Task.Run(() =>
        {
            InsightsReport? rep = null;
            string? err = null;
            try { rep = Insights.Load(db, r, d, DateTime.UtcNow, hides); }
            catch (Exception e) { err = e.Message; Log.Write($"insights failed: {e.Message}"); }
            try
            {
                BeginInvoke(() =>
                {
                    if (id != loadId) return;
                    loading = false;
                    report = rep;
                    error = err;
                    Invalidate();
                });
            }
            catch (InvalidOperationException) { }  // the window was closed meanwhile
        });
    }

    // MARK: text

    string Title()
    {
        var today = DateTime.Today;
        if (range == InsightsRange.Day)
            return day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : day.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
        if (IsCurrent(day)) return "This week";
        return IsCurrent(day.AddDays(7)) ? "Last week" : "Week of " + Insights.LocalPeriod(range, day, FirstDay).Start.ToString("MMMM d", CultureInfo.CurrentCulture);
    }

    string Subtitle()
    {
        var (s, e) = Insights.LocalPeriod(range, day, FirstDay);
        var c = CultureInfo.CurrentCulture;
        return range == InsightsRange.Day ? s.ToString("D", c) : $"{s.ToString("MMM d", c)} – {e.AddDays(-1).ToString("MMM d, yyyy", c)}";
    }

    /// "vs yesterday", "vs last week at this time"…
    string Versus()
    {
        var current = IsCurrent(day);
        var what = range == InsightsRange.Day ? (current ? "yesterday" : "the day before") : (current ? "last week" : "the week before");
        return report?.SoFar == true ? $"vs {what} by this time" : $"vs {what}";
    }

    static string Clock(DateTime utc) => utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    static string HourLabel(int h)
    {
        var c = CultureInfo.CurrentCulture;
        return c.DateTimeFormat.ShortTimePattern.Contains('t')
            ? DateTime.Today.AddHours(h).ToString("h tt", c).ToLowerInvariant()
            : h.ToString("00", c);
    }

    // MARK: painting

    Font F(float px, bool bold = false) => Ui.F(px * k, bold);
    float S(float v) => v * k;

    void Write(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment align = StringAlignment.Near)
    {
        using var b = new SolidBrush(c);
        using var fmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = align, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
        };
        g.DrawString(s, f, b, r, fmt);
    }

    void Wrap(Graphics g, string s, Font f, Color c, RectangleF r)
    {
        using var b = new SolidBrush(c);
        g.DrawString(s, f, b, r);
    }

    float WrapHeight(Graphics g, string s, Font f, float width) => g.MeasureString(s, f, (int)width).Height;

    /// A clickable area: highlighted under the mouse.
    bool Hot(RectangleF r, Action act)
    {
        hits.Add((r, act));
        return r.Contains(mouse);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        g.Clear(Bg);
        hits.Clear();
        tips.Clear();
        float pad = S(20), x = pad, w = Width - pad * 2, y = S(16);

        y = Header(g, x, y, w);
        if (report == null)
        {
            Write(g, error != null ? "Could not read your recordings: " + error : "Working it out…", F(13), Dim, new RectangleF(x, y + S(10), w, S(24)));
            Finish(y + S(60));
            return;
        }
        var r = report;
        if (r.IsEmpty)
        {
            var what = range == InsightsRange.Day ? "on this day" : "in this week";
            Write(g, $"Nothing was recorded {what}, or only in blocked apps and sites.", F(13), Dim,
                 new RectangleF(x, y + S(10), w, S(24)));
            Finish(y + S(60));
            return;
        }

        y = Stats(g, r, x, y, w) + S(22);
        y = Section(g, "Apps", x, y, w);
        y = Bars(g, r.Apps, x, y, w, Accent, app: true) + S(22);
        if (r.Sites.Count > 0)
        {
            y = Section(g, "Websites", x, y, w);
            y = Bars(g, r.Sites, x, y, w, SiteColor, app: false) + S(22);
        }
        y = Section(g, range == InsightsRange.Day ? "When you were active" : "When you were active, by day", x, y, w);
        y = Heat(g, r, x, y, w) + S(22);
        y = Section(g, "Focus sessions", x, y, w);
        y = FocusList(g, r, x, y, w) + S(18);

        var note = "Worked out on this PC from your recordings, without AI. Blocked apps and sites are left out. " +
                   "Time counts from one screenshot to the next. Breaks over 2 minutes (locked, asleep, paused) don't count, and after 5 minutes with nothing changing on screen and no audio recorded, the rest counts as away. " +
                   "Click an app, site or focus session to see it in the timeline.";
        var nh = WrapHeight(g, note, F(11), w);
        Wrap(g, note, F(11), Faint, new RectangleF(x, y, w, nh + S(4)));
        Finish(y + nh + S(20));
    }

    /// Grow or shrink to what was drawn, so the window scrolls the whole page.
    void Finish(float bottom)
    {
        var h = (int)Math.Ceiling(bottom);
        if (Height != h) { Height = h; HeightChanged?.Invoke(); }
        UpdateTip();
    }

    float Header(Graphics g, float x, float y, float w)
    {
        // Day | Week switch.
        var seg = new RectangleF(x, y, S(132), S(28));
        Draw.Fill(g, seg, S(8), CardColor);
        Draw.Stroke(g, seg, S(8), Line);
        foreach (var (rg, label, i) in new[] { (InsightsRange.Day, "Day", 0), (InsightsRange.Week, "Week", 1) })
        {
            var cell = new RectangleF(seg.X + S(3) + i * (seg.Width - S(6)) / 2, seg.Y + S(3), (seg.Width - S(6)) / 2, seg.Height - S(6));
            var on = range == rg;
            var hot = Hot(cell, () => SetRange(rg));
            if (on) Draw.Fill(g, cell, S(6), Accent);
            else if (hot) Draw.Fill(g, cell, S(6), Hover);
            Write(g, label, F(12.5f, on), on ? Color.White : TextColor, cell, StringAlignment.Center);
        }

        // ‹ › and back to now.
        var current = IsCurrent(day);
        var nextR = new RectangleF(x + w - S(30), y, S(30), S(28));
        var prevR = new RectangleF(nextR.X - S(34), y, S(30), S(28));
        NavButton(g, prevR, "‹", true, () => Step(-1));
        NavButton(g, nextR, "›", !current, () => Step(1));
        if (!current)
        {
            var label = range == InsightsRange.Day ? "Today" : "This week";
            var nowR = new RectangleF(prevR.X - S(86), y, S(80), S(28));
            NavButton(g, nowR, label, true, () => { day = DateTime.Today; Reload(); });
        }
        y += S(42);
        Write(g, Title(), F(22, true), TextColor, new RectangleF(x, y, w, S(30)));
        y += S(30);
        Write(g, Subtitle() + (loading && report != null ? "   ·   updating…" : ""), F(12.5f), Dim, new RectangleF(x, y, w, S(20)));
        return y + S(32);
    }

    void NavButton(Graphics g, RectangleF r, string label, bool enabled, Action act)
    {
        var hot = enabled && Hot(r, act);
        Draw.Fill(g, r, S(7), hot ? Hover : CardColor);
        Draw.Stroke(g, r, S(7), Line);
        Write(g, label, F(label.Length == 1 ? 17 : 12.5f), enabled ? TextColor : Faint, new RectangleF(r.X, r.Y - (label.Length == 1 ? S(2) : 0), r.Width, r.Height), StringAlignment.Center);
    }

    float Section(Graphics g, string title, float x, float y, float w)
    {
        Write(g, title, F(15, true), TextColor, new RectangleF(x, y, w, S(22)));
        return y + S(30);
    }

    float Stats(Graphics g, InsightsReport r, float x, float y, float w)
    {
        var longest = r.Focus.FirstOrDefault();
        var cards = new (string Label, string Value, string Sub)[]
        {
            ("Active time", Insights.Duration(r.TotalSeconds), $"{Insights.Change(r.TotalSeconds, r.PreviousTotalSeconds)} {Versus()}"),
            ("App switches", r.Switches.ToString("N0", CultureInfo.CurrentCulture), $"{Insights.Change(r.Switches, r.PreviousSwitches)} {Versus()}"),
            ("Longest focus", longest == null ? "—" : Insights.Duration(longest.Seconds), longest == null ? "No 10-minute stretch yet" : longest.App),
        };
        float gap = S(10), cw = (w - gap * 2) / 3, h = S(86);
        for (int i = 0; i < cards.Length; i++)
        {
            var c = new RectangleF(x + i * (cw + gap), y, cw, h);
            Draw.Fill(g, c, S(10), CardColor);
            Draw.Stroke(g, c, S(10), Line);
            var inner = RectangleF.Inflate(c, -S(12), 0);
            Write(g, cards[i].Label, F(11.5f), Dim, new RectangleF(inner.X, c.Y + S(10), inner.Width, S(16)));
            Write(g, cards[i].Value, F(22, true), TextColor, new RectangleF(inner.X, c.Y + S(28), inner.Width, S(30)));
            Write(g, cards[i].Sub, F(11.5f), Dim, new RectangleF(inner.X, c.Y + S(60), inner.Width, S(16)));
        }
        return y + h;
    }

    float Bars(Graphics g, List<InsightsItem> items, float x, float y, float w, Color color, bool app)
    {
        if (items.Count == 0) return y;
        var max = items.Max(i => i.Seconds);
        float rowH = S(30), nameW = Math.Min(S(190), w * 0.32f), timeW = S(64), changeW = S(70);
        float barX = x + nameW + S(10), barW = w - nameW - timeW - changeW - S(20);
        foreach (var it in items)
        {
            var row = new RectangleF(x - S(8), y, w + S(16), rowH);
            var tip = $"{(it.IsOther ? $"{it.Others} more" : it.Name)}: {Insights.Duration(it.Seconds)} ({Insights.Change(it.Seconds, it.PreviousSeconds)} {Versus()})";
            tips.Add((row, tip));
            if (!it.IsOther)
            {
                var first = it.First;
                var name = it.Name;
                if (Hot(row, () => openAt(first, app ? name : null))) Draw.Fill(g, row, S(6), Hover);
            }
            float nx = x;
            if (app && !it.IsOther && AppIcons.Get(it.Name) is Image icon)
            {
                g.DrawImage(icon, new RectangleF(nx, y + (rowH - S(16)) / 2, S(16), S(16)));
                nx += S(22);
            }
            var label = it.IsOther ? $"Other ({it.Others})" : it.Name.Length == 0 ? "Unknown app" : it.Name;
            Write(g, label, F(12.5f), it.IsOther ? Dim : TextColor, new RectangleF(nx, y, nameW - (nx - x), rowH));
            var bw = Math.Max(S(3), (float)(barW * it.Seconds / Math.Max(1, max)));
            Draw.Fill(g, new RectangleF(barX, y + S(9), barW, rowH - S(18)), S(4), EmptyCell);
            Draw.Fill(g, new RectangleF(barX, y + S(9), bw, rowH - S(18)), S(4), it.IsOther ? Faint : color);
            Write(g, Insights.Duration(it.Seconds), F(12.5f), TextColor, new RectangleF(barX + barW + S(8), y, timeW, rowH), StringAlignment.Far);
            Write(g, it.IsOther ? "" : Insights.Change(it.Seconds, it.PreviousSeconds), F(11.5f), Dim,
                 new RectangleF(barX + barW + S(8) + timeW, y, changeW, rowH), StringAlignment.Far);
            y += rowH;
        }
        return y;
    }

    float Heat(Graphics g, InsightsReport r, float x, float y, float w)
    {
        var rows = range == InsightsRange.Day ? new[] { r.Hours } : r.DayHours;
        float labelW = range == InsightsRange.Day ? 0 : S(56);
        float cellGap = S(2), cellW = (w - labelW - cellGap * 23) / 24, cellH = range == InsightsRange.Day ? S(34) : S(20);
        // Full color at 45 minutes or more in an hour.
        var (start, _) = Insights.LocalPeriod(range, day, FirstDay);
        for (int d = 0; d < rows.Length; d++)
        {
            if (labelW > 0)
                Write(g, start.AddDays(d).ToString("ddd d", CultureInfo.CurrentCulture), F(11.5f), Dim, new RectangleF(x, y, labelW, cellH));
            for (int h = 0; h < 24; h++)
            {
                var secs = rows[d][h];
                var cell = new RectangleF(x + labelW + h * (cellW + cellGap), y, cellW, cellH);
                var level = Math.Min(1, secs / 2700);
                Draw.Fill(g, cell, S(3), secs < 1 ? EmptyCell : Blend(EmptyCell, Accent, 0.25 + 0.75 * level));
                var when = range == InsightsRange.Day ? HourLabel(h) : $"{start.AddDays(d).ToString("ddd", CultureInfo.CurrentCulture)} {HourLabel(h)}";
                tips.Add((cell, $"{when}: {(secs < 1 ? "nothing" : Insights.Duration(secs))}"));
            }
            y += cellH + cellGap;
        }
        // Hour labels under the grid.
        y += S(2);
        for (int h = 0; h < 24; h += 6)
            Write(g, HourLabel(h), F(11), Faint, new RectangleF(x + labelW + h * (cellW + cellGap), y, S(60), S(16)));
        return y + S(16);
    }

    static Color Blend(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    float FocusList(Graphics g, InsightsReport r, float x, float y, float w)
    {
        if (r.Focus.Count == 0)
        {
            Write(g, "No stretch of 10 minutes or more mostly in one app.", F(12.5f), Dim, new RectangleF(x, y, w, S(24)));
            return y + S(24);
        }
        float rowH = S(30);
        foreach (var f in r.Focus)
        {
            var row = new RectangleF(x - S(8), y, w + S(16), rowH);
            var (start, app) = (f.Start, f.App);
            if (Hot(row, () => openAt(start, app))) Draw.Fill(g, row, S(6), Hover);
            float nx = x;
            if (AppIcons.Get(f.App) is Image icon)
            {
                g.DrawImage(icon, new RectangleF(nx, y + (rowH - S(16)) / 2, S(16), S(16)));
                nx += S(22);
            }
            var weekday = range == InsightsRange.Week ? f.Start.ToLocalTime().ToString("ddd", CultureInfo.CurrentCulture) + " " : "";
            Write(g, f.App.Length == 0 ? "Unknown app" : f.App, F(12.5f), TextColor, new RectangleF(nx, y, w * 0.45f - (nx - x), rowH));
            Write(g, $"{weekday}{Clock(f.Start)} – {Clock(f.End)}", F(12.5f), Dim, new RectangleF(x + w * 0.45f, y, w * 0.35f, rowH));
            Write(g, Insights.Duration(f.Seconds), F(12.5f, true), TextColor, new RectangleF(x + w * 0.8f, y, w * 0.2f, rowH), StringAlignment.Far);
            y += rowH;
        }
        return y;
    }

    // MARK: mouse

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var was = hits.FindIndex(h => h.R.Contains(mouse));
        mouse = e.Location;
        var now = hits.FindIndex(h => h.R.Contains(mouse));
        Cursor = now >= 0 ? Cursors.Hand : Cursors.Default;
        if (was != now) Invalidate();
        UpdateTip();
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        mouse = new PointF(-1, -1);
        Invalidate();
        UpdateTip();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            foreach (var (r, act) in hits.ToList())
                if (r.Contains(e.Location)) { act(); break; }
        base.OnMouseClick(e);
    }

    void UpdateTip()
    {
        var tip = tips.LastOrDefault(t => t.R.Contains(mouse)).Tip;
        if (tip == shownTip) return;
        shownTip = tip;
        tooltip.SetToolTip(this, tip ?? "");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tooltip.Dispose();
        base.Dispose(disposing);
    }
}
