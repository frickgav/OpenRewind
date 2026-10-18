using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Guided tour on top of the real timeline: a card that points at the bar, then the search box.
/// Steps move on by themselves when the user does the thing (scrolls, searches), or with Next.
/// A separate small window owned by the overlay, so the overlay keeps the keyboard.
sealed class TutorialCoach : Form
{
    readonly OverlayForm overlay;
    readonly TimelineModel model;
    readonly float k;
    readonly PillButton next, skip;
    int step;
    int startIndex;
    DateTime startPlayhead;

    record Step(string Title, string Text, Anchor At);
    enum Anchor { Bar, Search, Center }

    static Step[] Steps => new Step[]
    {
        new("This is your timeline", "Every colored block is an app you used. The newest moment is on the right, and the big picture shows what was on screen.", Anchor.Bar),
        new("Go back in time", "Scroll with the mouse wheel or trackpad, drag along the bar, or press ← and →. Try it now.", Anchor.Bar),
        new("Search anything", "Press / and type a word you saw or heard, like a name or a file. Pick a result to jump to that moment.", Anchor.Search),
        new("You're all set", $"Press {OnboardingForm.Hotkey} from anywhere to open this. Esc closes it, and ? shows every shortcut.", Anchor.Center),
    };

    public TutorialCoach(OverlayForm overlay, TimelineModel model)
    {
        this.overlay = overlay;
        this.model = model;
        k = overlay.DeviceDpi / 96f;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Card;
        ForeColor = Ui.Text;
        DoubleBuffered = true;
        Size = new Size((int)(420 * k), (int)(206 * k));
        Owner = overlay;

        next = new PillButton("Next", primary: true) { Font = Ui.F(13.5f * k, true) };
        skip = new PillButton("Skip tour") { Font = Ui.F(13.5f * k, true) };
        next.SetBounds(Width - (int)(124 * k), Height - (int)(54 * k), (int)(104 * k), (int)(36 * k));
        skip.SetBounds(Width - (int)(246 * k), Height - (int)(54 * k), (int)(112 * k), (int)(36 * k));
        next.Click += (_, _) => Advance();
        skip.Click += (_, _) => Close();
        Controls.Add(next);
        Controls.Add(skip);

        model.Changed += OnModelChanged;
        overlay.VisibleChanged += OnOverlayVisible;
        HandleCreated += (_, _) =>
        {
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
            int round = 2;  // Windows 11 rounded corners
            DwmSetWindowAttribute(Handle, 33, ref round, 4);
        };
        Go(0);
    }

    /// Open the timeline and start the tour on it.
    public static void Start(OverlayForm overlay, TimelineModel model)
    {
        overlay.ShowOverlay();
        new TutorialCoach(overlay, model).Show(overlay);
        overlay.Activate();
    }

    // Do not take the keyboard from the timeline.
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80;  // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    void Go(int i)
    {
        step = i;
        startIndex = model.Index;
        startPlayhead = model.Playhead;
        next.Text = i == Steps.Length - 1 ? "Done" : "Next";
        skip.Visible = i < Steps.Length - 1;
        var b = overlay.Bounds;
        Location = Steps[i].At switch
        {
            Anchor.Bar => new Point(b.Left + (b.Width - Width) / 2, b.Bottom - Height - (int)(190 * k)),
            Anchor.Search => new Point(b.Left + (int)(28 * k), b.Top + (int)(84 * k)),
            _ => new Point(b.Left + (b.Width - Width) / 2, b.Top + (b.Height - Height) / 2),
        };
        Invalidate();
    }

    void Advance()
    {
        if (step >= Steps.Length - 1) { Close(); return; }
        Go(step + 1);
    }

    void OnModelChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            if (step == 1 && (model.Index != startIndex || model.Playhead != startPlayhead)) Go(2);
            else if (step == 2 && model.Query.Trim().Length >= 2) Go(3);
        });
    }

    void OnOverlayVisible(object? s, EventArgs e) { if (!overlay.Visible) Close(); }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        model.Changed -= OnModelChanged;
        overlay.VisibleChanged -= OnOverlayVisible;
        base.OnFormClosed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        using (var pen = new Pen(Ui.Accent, 2 * k)) g.DrawRectangle(pen, k, k, Width - 2 * k, Height - 2 * k);
        var s = Steps[step];
        float x = 22 * k;
        TextRenderer.DrawText(g, $"STEP {step + 1} OF {Steps.Length}", Ui.F(11.5f * k, true), new Point((int)x, (int)(18 * k)), Ui.AccentHi);
        TextRenderer.DrawText(g, s.Title, Ui.F(19 * k, true), new Point((int)x, (int)(38 * k)), Ui.Text);
        TextRenderer.DrawText(g, s.Text, Ui.F(13.5f * k), new Rectangle((int)x, (int)(72 * k), Width - (int)(44 * k), (int)(70 * k)), Ui.Dim, TextFormatFlags.WordBreak);
        // A little arrow toward what this step is about.
        var arrow = s.At switch { Anchor.Bar => "↓", Anchor.Search => "↑", _ => "" };
        if (arrow.Length > 0) TextRenderer.DrawText(g, arrow, Ui.F(26 * k, true), new Point((int)x, Height - (int)(52 * k)), Ui.AccentHi);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
