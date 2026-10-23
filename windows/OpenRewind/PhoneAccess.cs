using System.Drawing;
using Net.Codecrete.QrCodeGenerator;

namespace OpenRewind;

/// Phone Access: turns the phone server (PhoneServer, port 3032) on and off, pairs phones, and turns it
/// off by itself after the hours unused picked in Settings. Off by default. Mac: PhoneAccess.swift.
sealed class PhoneAccessController : IDisposable
{
    readonly Engine engine;
    readonly Settings settings;
    readonly Func<bool> locked;
    readonly Control ui;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 60_000 };
    PhoneServer? server;

    /// Hours unused before it turns off; 0 is never.
    public static readonly int[] AutoOffChoices = { 1, 4, 12, 0 };

    /// Why it is not on although it should be, for Settings.
    public string? Problem { get; private set; }
    public bool On => server != null;
    public IReadOnlyList<string> Urls => server?.Urls ?? Array.Empty<string>();

    public PhoneAccessController(Engine engine, Settings settings, Func<bool> locked, Control ui)
    {
        this.engine = engine; this.settings = settings; this.locked = locked; this.ui = ui;
        timer.Tick += (_, _) => CheckIdle();
    }

    /// At start: on again if it was on.
    public void Resume() { if (settings.PhoneAccess) Start(); }

    public void Start()
    {
        if (server != null) return;
        var api = new Api(engine.Db, engine.Config, meaning: () => engine.Meaning, blocked: engine.ServerBlocklist) { Locked = locked };
        var ops = new PhoneOps(engine.Db, api.Handle, Environment.MachineName)
        {
            Ai = () => { var ai = settings.Ai(); return ai.IsReady && settings.AiPrivacyAccepted ? ai : null; },
            Price = settings.AiPriceFor,
            Spent = (ai, used, cost) => ui.BeginInvoke(() => settings.AddAiUsage(ai.Kind, used, cost)),
        };
        var s = new PhoneServer(engine.Db, ops.Run, locked, () => PhonePage.Html);
        if (!s.Start())
        {
            s.Dispose();
            Problem = "No Wi-Fi or Ethernet network with a private address, or port 3032 is taken, so phones cannot reach this PC.";
            return;
        }
        Problem = null;
        server = s;
        timer.Start();
    }

    public void Stop()
    {
        server?.Dispose();
        server = null;
        timer.Stop();
        Problem = null;
    }

    public void Toggle()
    {
        if (!settings.PhoneAccess)
        {
            var text = "Phones you pair can search your screen text and transcripts, ask AI about them and see your promises, from the " +
                       "same Wi-Fi (or a VPN such as Tailscale). Screenshots and audio never leave this PC. Everything is encrypted with a key " +
                       "only the paired phone has; blocked apps stay hidden and nothing is answered while OpenRewind is locked.\n\n" +
                       $"It listens on port {PhoneServer.DefaultPort} of this PC's local network address, and turns off by itself after the hours you pick. " +
                       "Windows may ask whether to allow OpenRewind on private networks: allow it.";
            if (MessageBox.Show(text, "Turn on Phone Access?", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        }
        settings.PhoneAccess = !settings.PhoneAccess;
        settings.Save();
        if (settings.PhoneAccess) Start(); else Stop();
    }

    void CheckIdle()
    {
        if (server == null || settings.PhoneAutoOffHours <= 0 || DateTime.UtcNow - server.LastUsed < TimeSpan.FromHours(settings.PhoneAutoOffHours)) return;
        Log.Write($"phone access: off after {settings.PhoneAutoOffHours} h unused");
        settings.PhoneAccess = false;
        settings.Save();
        Stop();
    }

    /// A new pairing code, shown until the user closes it. Unused codes are forgotten after 15 minutes.
    public void Pair()
    {
        if (Urls.FirstOrDefault() is not string base_) return;
        var (id, secret) = PhoneCrypto.NewDevice();
        engine.Db.AddPhoneDevice(id, secret);
        using var f = new PairPhoneForm(PhoneCrypto.PairingUrl(base_, id, secret), () => engine.Db.PhoneDevice(id)?.Paired == true);
        f.ShowDialog();
        if (engine.Db.PhoneDevice(id)?.Paired != true) engine.Db.RemovePhoneDevice(id);
    }

    public void Dispose()
    {
        Stop();
        timer.Dispose();
    }
}

/// The pairing code: a QR code of the page address with the key after #, and a note that it is secret.
sealed class PairPhoneForm : Form
{
    readonly Bitmap code;

    public PairPhoneForm(string url, Func<bool> paired)
    {
        var k = DeviceDpi / 96f;
        Text = "Pair a Phone";
        Icon = Program.AppIcon;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(13 * k);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size((int)(420 * k), (int)(560 * k));
        code = QrBitmap(url, (int)(300 * k));

        var title = new Label { Text = "Scan with your phone's camera", Font = Ui.F(17 * k, true), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                                Bounds = new Rectangle(0, (int)(18 * k), ClientSize.Width, (int)(30 * k)) };
        var pic = new PictureBox { Image = code, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.White,
                                   Bounds = new Rectangle((ClientSize.Width - (int)(320 * k)) / 2, (int)(60 * k), (int)(320 * k), (int)(320 * k)) };
        var note = new Label
        {
            Text = "Your phone must be on the same Wi-Fi as this PC. The code works for one phone and expires in 15 minutes; it holds the key, " +
                   "so don't share it. After scanning, add the page to your home screen to open it again.",
            ForeColor = Ui.Dim, Font = Ui.F(12 * k), AutoSize = false,
            Bounds = new Rectangle((int)(24 * k), (int)(392 * k), ClientSize.Width - (int)(48 * k), (int)(80 * k)),
        };
        var status = new Label { Text = "Waiting for the phone…", ForeColor = Ui.Faint, Font = Ui.F(12.5f * k, true), AutoSize = false,
                                 TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, (int)(474 * k), ClientSize.Width, (int)(24 * k)) };
        var done = new PillButton("Done", true) { Font = Ui.F(13 * k, true), Size = new Size((int)(110 * k), (int)(34 * k)) };
        done.Location = new Point((ClientSize.Width - done.Width) / 2, (int)(508 * k));
        done.Click += (_, _) => Close();
        Controls.AddRange(new Control[] { title, pic, note, status, done });

        var poll = new System.Windows.Forms.Timer { Interval = 1000 };
        poll.Tick += (_, _) =>
        {
            if (!paired()) return;
            poll.Stop();
            status.Text = "Paired. You can close this.";
            status.ForeColor = Ui.Text;
        };
        poll.Start();
        FormClosed += (_, _) => poll.Dispose();
    }

    /// The QR code of `text`, black on white with a quiet zone, about `size` pixels wide.
    static Bitmap QrBitmap(string text, int size)
    {
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        const int border = 4;
        int n = qr.Size + border * 2, scale = Math.Max(1, size / n);
        var bmp = new Bitmap(n * scale, n * scale);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        for (int y = 0; y < qr.Size; y++)
            for (int x = 0; x < qr.Size; x++)
                if (qr.GetModule(x, y)) g.FillRectangle(Brushes.Black, (x + border) * scale, (y + border) * scale, scale, scale);
        return bmp;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) code.Dispose();
        base.Dispose(disposing);
    }
}
