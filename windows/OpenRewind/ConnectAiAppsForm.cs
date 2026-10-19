using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Settings → Connect AI Apps: the switch that lets AI apps read the history (MCP), and the setup
/// to paste into Claude Desktop, Claude Code and Cursor, with this copy's path filled in.
sealed class ConnectAiAppsForm : Form
{
    static ConnectAiAppsForm? open;
    readonly Settings settings;
    readonly CheckBox allow;

    public static void ShowFor(Settings settings, Config config)
    {
        if (open is { IsDisposed: false }) { open.allow.Checked = settings.McpEnabled; open.Activate(); return; }
        open = new ConnectAiAppsForm(settings, config);
        open.Show();
        open.Activate();
    }

    ConnectAiAppsForm(Settings settings, Config config)
    {
        this.settings = settings;
        var k = DeviceDpi / 96f;
        Text = "Connect AI Apps";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ClientSize = new Size((int)(620 * k), (int)(860 * k));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(14 * k);
        HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); };

        int x = (int)(24 * k), w = ClientSize.Width - 2 * x, y = (int)(18 * k);
        Label Add(string text, float size, Color color, bool bold = false, int height = 0)
        {
            var l = new Label { Text = text, Font = Ui.F(size * k, bold), ForeColor = color, Location = new Point(x, y), Size = new Size(w, height > 0 ? height : (int)(size * 1.6f * k)) };
            Controls.Add(l);
            y += l.Height + (int)(4 * k);
            return l;
        }

        Add("Connect AI Apps", 20, Ui.Text, bold: true);
        Add("Let Claude, Cursor and other AI apps that support MCP search your OpenRewind history. They ask OpenRewind on this PC, "
            + "and what they read is sent to that app's AI like anything else you ask it. Blocked apps and sites are left out.",
            12.5f, Ui.Dim, height: (int)(60 * k));
        y += (int)(4 * k);

        allow = new CheckBox
        {
            Text = "Allow AI Apps to Read My History (MCP)", Checked = settings.McpEnabled, Font = Ui.F(13.5f * k, true), ForeColor = Ui.Text,
            Location = new Point(x, y), Size = new Size(w, (int)(26 * k)),
        };
        allow.CheckedChanged += (_, _) => { settings.McpEnabled = allow.Checked; settings.Save(); };
        Controls.Add(allow);
        y += allow.Height + (int)(2 * k);
        Add("Off by default. While off, AI apps that you connected get a message asking you to turn this on. "
            + "Screenshots are shared only if “Let AI look at screenshots” is on in AI Settings.",
            11, Ui.Faint, height: (int)(34 * k));
        y += (int)(10 * k);

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenRewind.exe");
        var home = McpCommand.HomeArg(config);
        var json = McpSetup.JsonConfig(exe, home);
        Snippet("Claude Desktop", $"Paste into {McpSetup.ClaudeDesktopFile(true)} (Settings → Developer → Edit Config), then quit and reopen Claude. "
                + "If the file already has “mcpServers”, add just the openrewind part.", json, 7);
        Snippet("Claude Code", "Run once in a terminal:", McpSetup.ClaudeCode(exe, home, windows: true), 3);
        Snippet("Cursor", $"Paste into {McpSetup.CursorFile(true)} (Cursor Settings → MCP), then restart Cursor.", json, 7);

        void Snippet(string title, string hint, string code, int lines)
        {
            Add(title, 14, Ui.Text, bold: true);
            Add(hint, 11.5f, Ui.Dim, height: (int)(38 * k));
            var bw = (int)(84 * k);
            var box = new TextBox
            {
                Text = code.Replace("\n", "\r\n"), ReadOnly = true, Multiline = true, WordWrap = true, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Ui.Card, ForeColor = Ui.Text, Font = new Font("Consolas", 12 * k, GraphicsUnit.Pixel),
                Location = new Point(x, y), Size = new Size(w - bw - (int)(8 * k), (int)((lines * 16 + 8) * k)),
                ScrollBars = ScrollBars.Vertical,
            };
            Controls.Add(box);
            var copy = new PillButton("Copy") { Font = Ui.F(12.5f * k, true), Bounds = new Rectangle(x + w - bw, y, bw, (int)(34 * k)) };
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(code.Replace("\n", "\r\n")); copy.Text = "Copied"; }
                catch (Exception e) { Log.Write($"cannot copy: {e.Message}"); copy.Text = "Failed"; }
            };
            Controls.Add(copy);
            y += box.Height + (int)(14 * k);
        }

        var close = new PillButton("Done", primary: true) { Font = Ui.F(13.5f * k, true) };
        close.SetBounds(ClientSize.Width - x - (int)(110 * k), ClientSize.Height - (int)(60 * k), (int)(110 * k), (int)(40 * k));
        close.Click += (_, _) => Close();
        Controls.Add(close);
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { open = null; base.OnFormClosed(e); }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
