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
        // A new address (other Wi-Fi, DHCP renewal): listen again on the addresses there are now.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    void OnNetworkChanged(object? sender, EventArgs e)
    {
        try { ui.BeginInvoke(NetworkChanged); } catch (InvalidOperationException) { }
    }

    void NetworkChanged()
    {
        if (!settings.PhoneAccess) return;
        var now = Lan.Addresses().Select(a => $"http://{a}:{PhoneServer.DefaultPort}").ToList();
        if (server != null && server.Urls.SequenceEqual(now)) return;
        if (server == null && now.Count == 0) return;
        Log.Write("phone access: network changed, listening again");
        Stop();
        Start();
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
            Problem = "No Wi-Fi or Ethernet network with a private address, or port 3032 is taken, so phones cannot reach this PC. " +
                      "It starts again by itself when the network changes.";
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

    /// A new pairing code, shown until the phone has finished pairing or the user closes it. The code is a
    /// one-time key: the phone gets its own key in the first answer, and once it has used it the code is
    /// worthless. Codes not used within 15 minutes are forgotten.
    public void Pair()
    {
        if (Urls.Count == 0) return;
        var (id, secret) = PhoneCrypto.NewDevice();
        engine.Db.AddPhoneDevice(id, secret);
        using var f = new PairPhoneForm(Urls.Select(u => (u, PhoneCrypto.PairingUrl(u, id, secret))).ToList(), () => engine.Db.PhoneDevice(id)?.Paired == true);
        f.ShowDialog();
        if (engine.Db.PhoneDevice(id)?.Paired != true) engine.Db.RemovePhoneDevice(id);
    }

    public void Dispose()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        Stop();
        timer.Dispose();
    }
}

/// The pairing code: a QR code of the page address with the one-time key after #. It is kept out of every
/// screenshot (OpenRewind's own and other screen capture), and closes when the phone has paired.
/// With more than one network, a list picks the address the phone can reach.
sealed class PairPhoneForm : Form
{
    readonly PictureBox pic;
    readonly List<(string Base, string Url)> choices;

    public PairPhoneForm(List<(string Base, string Url)> choices, Func<bool> paired)
    {
        this.choices = choices;
        var k = DeviceDpi / 96f;
        Text = "Pair a Phone";
        Icon = Program.AppIcon;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(13 * k);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size((int)(420 * k), (int)(600 * k));
        HandleCreated += (_, _) => Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);

        var title = new Label { Text = "Scan with your phone's camera", Font = Ui.F(17 * k, true), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                                Bounds = new Rectangle(0, (int)(18 * k), ClientSize.Width, (int)(30 * k)) };
        pic = new PictureBox { SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.White,
                               Bounds = new Rectangle((ClientSize.Width - (int)(320 * k)) / 2, (int)(60 * k), (int)(320 * k), (int)(320 * k)) };
        var network = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Ui.Card, ForeColor = Ui.Text,
                                     Font = Ui.F(12.5f * k), Bounds = new Rectangle((int)(60 * k), (int)(390 * k), ClientSize.Width - (int)(120 * k), (int)(28 * k)) };
        foreach (var c in choices) network.Items.Add("This PC at " + c.Base.Replace("http://", ""));
        network.SelectedIndexChanged += (_, _) => ShowCode(network.SelectedIndex);
        network.Visible = choices.Count > 1;
        var note = new Label
        {
            Text = "Your phone must be on the same network as this PC" + (choices.Count > 1 ? " (pick it above if the page does not open)" : "") +
                   ". The code works once, for one phone, within 15 minutes, and closes by itself when the phone is paired. " +
                   "After scanning, add the page to your home screen to open it again.",
            ForeColor = Ui.Dim, Font = Ui.F(12 * k), AutoSize = false,
            Bounds = new Rectangle((int)(24 * k), (int)(428 * k), ClientSize.Width - (int)(48 * k), (int)(86 * k)),
        };
        var status = new Label { Text = "Waiting for the phone…", ForeColor = Ui.Faint, Font = Ui.F(12.5f * k, true), AutoSize = false,
                                 TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, (int)(516 * k), ClientSize.Width, (int)(24 * k)) };
        var done = new PillButton("Done", true) { Font = Ui.F(13 * k, true), Size = new Size((int)(110 * k), (int)(34 * k)) };
        done.Location = new Point((ClientSize.Width - done.Width) / 2, (int)(550 * k));
        done.Click += (_, _) => Close();
        Controls.AddRange(new Control[] { title, pic, network, note, status, done });
        network.SelectedIndex = 0;

        var poll = new System.Windows.Forms.Timer { Interval = 1000 };
        poll.Tick += (_, _) =>
        {
            if (!paired()) return;
            poll.Stop();
            // The code is worthless now, but there is no reason to keep it on screen.
            var old = pic.Image;
            pic.Image = null;
            old?.Dispose();
            pic.Visible = network.Visible = false;
            title.Text = "Phone paired";
            note.Text = "It has its own key now; the code you scanned no longer works. Revoke it in Settings → Security if you lose the phone.";
            status.Text = "";
        };
        poll.Start();
        FormClosed += (_, _) => poll.Dispose();
    }

    void ShowCode(int i)
    {
        if (i < 0 || i >= choices.Count) return;
        var old = pic.Image;
        pic.Image = QrBitmap(choices[i].Url, (int)(300 * DeviceDpi / 96f));
        old?.Dispose();
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
        if (disposing) pic.Image?.Dispose();
        base.Dispose(disposing);
    }
}
