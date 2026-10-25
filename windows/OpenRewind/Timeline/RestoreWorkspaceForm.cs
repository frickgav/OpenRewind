using System.Drawing;

namespace OpenRewind;

/// "Restore workspace from 2:14 PM?": what was open then, each with a check box (all on), and Open.
sealed class RestoreWorkspaceForm : Form
{
    readonly CheckedListBox list;
    readonly List<WorkspaceItem> items;

    /// The items the user left checked.
    public List<WorkspaceItem> Picked => list.CheckedIndices.Cast<int>().Select(i => items[i]).ToList();

    public RestoreWorkspaceForm(WorkspaceSnapshot snap, List<WorkspaceItem> items)
    {
        this.items = items;
        var k = DeviceDpi / 96f;
        Text = "Restore Workspace";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(13 * k);
        ClientSize = new Size((int)(520 * k), (int)(440 * k));
        int pad = (int)(18 * k), w = ClientSize.Width - pad * 2;

        var title = new Label { Text = $"Open what was open at {snap.Time.ToLocalTime():t}?", Font = Ui.F(15 * k, true), AutoSize = false,
                                Location = new Point(pad, pad), Size = new Size(w, (int)(26 * k)) };
        var note = new Label { Text = "Apps that are running stay as they are, and nothing is closed. Only web pages and files that still exist are opened.",
                               Font = Ui.F(12 * k), ForeColor = Ui.Dim, AutoSize = false, Location = new Point(pad, title.Bottom + (int)(4 * k)),
                               Size = new Size(w, (int)(36 * k)) };
        list = new CheckedListBox
        {
            CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Ui.Card, ForeColor = Ui.Text, Font = Ui.F(13 * k),
            Location = new Point(pad, note.Bottom + (int)(8 * k)), Size = new Size(w, ClientSize.Height - note.Bottom - (int)(78 * k)),
            IntegralHeight = false,
        };
        foreach (var i in items) list.Items.Add(Workspace.Describe(i), true);
        var open = new PillButton("Open", true) { Font = Ui.F(13 * k, true), Size = new Size((int)(110 * k), (int)(34 * k)) };
        var cancel = new PillButton("Cancel", false) { Font = Ui.F(13 * k, true), Size = new Size((int)(110 * k), (int)(34 * k)) };
        open.Location = new Point(ClientSize.Width - pad - open.Width, ClientSize.Height - pad - open.Height);
        cancel.Location = new Point(open.Left - (int)(8 * k) - cancel.Width, open.Top);
        open.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
        };
        Controls.AddRange(new Control[] { title, note, list, cancel, open });
    }
}
