using System.Drawing;

namespace OpenRewind;

/// Search box over a list of the apps on this PC, each with a check box.
/// Used by the setup window and by Blocked Apps → Add Apps.
sealed class AppPicker : Panel
{
    readonly float k;
    readonly TextBox search;
    readonly RowList list;
    readonly Label empty;
    readonly HashSet<string> chosen;
    readonly HashSet<string> hide;

    /// `chosen`: ids ticked at the start. `hide`: ids not to list (already blocked elsewhere).
    public AppPicker(float k, IEnumerable<string> chosen, IEnumerable<string>? hide = null)
    {
        this.k = k;
        this.chosen = new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase);
        this.hide = new HashSet<string>(hide ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        BackColor = Ui.Bg;

        var box = new Panel { BackColor = Ui.Card, Height = (int)(38 * k), Dock = DockStyle.Top, Padding = new Padding((int)(12 * k), (int)(9 * k), (int)(12 * k), 0) };
        box.Paint += (_, e) => { Ui.Smooth(e.Graphics); };
        search = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Ui.Card, ForeColor = Ui.Text, Dock = DockStyle.Fill,
            Font = Ui.F(14 * k), PlaceholderText = "Search apps on this PC",
        };
        box.Controls.Add(search);
        list = new RowList(k) { Dock = DockStyle.Fill, Visible = false };
        empty = new Label { Text = "Looking for apps…", ForeColor = Ui.Dim, Font = Ui.F(13 * k), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
        Controls.Add(list);
        Controls.Add(empty);
        Controls.Add(new Panel { Height = (int)(8 * k), Dock = DockStyle.Top, BackColor = Ui.Bg });
        Controls.Add(box);
        search.TextChanged += (_, _) => Filter();
        Load();
    }

    /// Apps found, with their ticked state.
    public IEnumerable<FoundApp> Chosen => list.Controls.OfType<CheckRow>().Where(r => r.Checked).Select(r => (FoundApp)r.Tag2!);
    public IEnumerable<FoundApp> Unchosen => list.Controls.OfType<CheckRow>().Where(r => !r.Checked).Select(r => (FoundApp)r.Tag2!);

    void Load()
    {
        Task.Run(() =>
        {
            var apps = AppDiscovery.Find().Where(a => !hide.Contains(a.Id)).ToList();
            BeginInvokeSafe(() =>
            {
                var rows = apps.Select(a => new CheckRow(a.Name, null, chosen.Contains(a.Id), k) { Tag2 = a, Height = (int)(48 * k) }).ToList();
                list.SetRows(rows);
                list.Visible = rows.Count > 0;
                empty.Visible = rows.Count == 0;
                empty.Text = "No other apps found";
                Filter();
                // Icons last, off the UI thread; extracting a few hundred takes a moment.
                Task.Run(() =>
                {
                    foreach (var r in rows)
                    {
                        var icon = Ui.IconBitmap(((FoundApp)r.Tag2!).Path, (int)(24 * k));
                        if (icon != null) BeginInvokeSafe(() => { r.Icon = icon; r.Invalidate(); });
                    }
                });
            });
        });
    }

    void Filter()
    {
        var q = search.Text.Trim();
        foreach (var r in list.Controls.OfType<CheckRow>())
            r.Visible = q.Length == 0 || r.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || ((FoundApp)r.Tag2!).Id.Contains(q, StringComparison.OrdinalIgnoreCase);
        list.Relayout();
    }

    void BeginInvokeSafe(Action a)
    {
        if (IsDisposed) return;
        try { if (IsHandleCreated) BeginInvoke(a); else HandleCreated += (_, _) => BeginInvoke(a); } catch (ObjectDisposedException) { }
    }
}
