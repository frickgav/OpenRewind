using System.Drawing;
using System.Drawing.Imaging;

namespace OpenRewind;

/// Getting things out of the timeline: text on the shown screenshot, the screenshot itself, and clip marks.
/// Only saved screenshots are used (blocked apps are already blurred in them), and frames the block list
/// covers now are never copied, saved, read or exported.
sealed partial class TimelineModel
{
    /// Clip start and end marked with [ and ] (UTC).
    public DateTime? ClipStart { get; private set; }
    public DateTime? ClipEnd { get; private set; }
    public bool HasMarks => ClipStart != null || ClipEnd != null;

    public void MarkStart()
    {
        ClipStart = Playhead;
        if (ClipEnd is DateTime e && e < Playhead) ClipEnd = null;
        ShowNotice(ClipEnd == null ? "Starts here. Press ] where it ends." : "Marked. Delete deletes it, Ctrl+E exports it.");
    }

    public void MarkEnd()
    {
        ClipEnd = Playhead;
        if (ClipStart is DateTime s && s > Playhead) ClipStart = null;
        ShowNotice(ClipStart == null ? "Ends here. Press [ where it starts." : "Marked. Delete deletes it, Ctrl+E exports it.");
    }

    public void ClearMarks() { ClipStart = ClipEnd = null; Notify(); }

    /// True when the shown screenshot is from an app, window or site the block list covers now.
    public bool CurrentHidden => Current is TimelineFrame f && ClipPlan.Hides(Blocklist.Current, f);

    // MARK: text on the shown screenshot

    /// The words on the shown screenshot, read with Windows OCR once scrolling settles. Null until then.
    public FrameText? Text { get; private set; }
    /// The selected words (indices into Text.Words), or -1.
    public int SelectionAnchor { get; private set; } = -1;
    public int SelectionEnd { get; private set; } = -1;
    public bool HasSelection => Text != null && SelectionAnchor >= 0 && SelectionEnd >= 0;
    public string SelectedText => HasSelection ? Text!.TextOf(SelectionAnchor, SelectionEnd) : "";

    long? textFor;
    System.Windows.Forms.Timer? textTimer;
    readonly Dictionary<long, FrameText> textCache = new();
    readonly List<long> textOrder = new();

    public void Select(int anchor, int end)
    {
        if (anchor == SelectionAnchor && end == SelectionEnd) return;
        (SelectionAnchor, SelectionEnd) = (anchor, end);
        Notify();
    }

    public void ClearSelection() => Select(-1, -1);

    /// Read the words of the shown screenshot shortly, unless they are cached or it is blocked now.
    void RefreshText()
    {
        var id = Current?.Id;
        if (id == textFor) return;
        textFor = id;
        SelectionAnchor = SelectionEnd = -1;
        textTimer?.Stop();
        Text = null;
        if (id is not long fid || Image == null || CurrentHidden || !Ocr.Available) return;
        if (textCache.TryGetValue(fid, out var cached)) { Text = cached; return; }
        if (textTimer == null)
        {
            textTimer = new System.Windows.Forms.Timer { Interval = 350 };
            textTimer.Tick += (_, _) => { textTimer.Stop(); ReadText(); };
        }
        textTimer.Start();
    }

    void ReadText()
    {
        if (Current is not TimelineFrame f || f.Id != textFor) return;
        var ui = SynchronizationContext.Current;
        Task.Run(() =>
        {
            // Its own copy of the picture: GDI+ images can't be shared across threads.
            using var bmp = Picture(f);  // still or video frame
            var text = bmp == null ? null : Ocr.Words(bmp);
            ui?.Post(_ =>
            {
                if (text == null) return;
                textCache[f.Id] = text;
                textOrder.Remove(f.Id);
                textOrder.Add(f.Id);
                if (textOrder.Count > 16) { textCache.Remove(textOrder[0]); textOrder.RemoveAt(0); }
                if (textFor != f.Id) return;
                Text = text;
                Notify();
            }, null);
        });
    }

    // MARK: the screenshot itself

    /// Copy the selected text, or the shown screenshot when nothing is selected.
    public void Copy()
    {
        if (HasSelection)
        {
            // The block list may have changed since the words were read.
            if (CurrentHidden) { ClearSelection(); ShowNotice("This moment is from a blocked app or site"); return; }
            var s = SelectedText;
            if (s.Length > 0) { PrivateClipboard.SetText(s); ShowNotice("Copied the selected text"); }
            return;
        }
        if (FrameForExport() is not Image img) return;
        PrivateClipboard.SetImage(img);
        ShowNotice("Copied the screenshot");
    }

    /// Copy every word read on the shown screenshot.
    public void CopyAllText()
    {
        if (CurrentHidden) { ShowNotice("This moment is from a blocked app or site"); return; }
        if (Text is not FrameText text || text.IsEmpty) { ShowNotice("No text found on this screenshot yet"); return; }
        PrivateClipboard.SetText(text.AllText);
        ShowNotice("Copied the text on this screenshot");
    }

    /// Save the shown screenshot as a PNG. `owner` hosts the save dialog.
    public void SaveFrame(IWin32Window owner)
    {
        if (FrameForExport() is not Image img || Current is not TimelineFrame f) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Save Screenshot",
            Filter = "PNG image|*.png",
            FileName = ClipPlan.FileName(f.Time.ToLocalTime(), "png"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        try
        {
            img.Save(dialog.FileName, ImageFormat.Png);
            ShowNotice($"Saved {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            Log.Write($"cannot save screenshot: {e.Message}");
            ShowNotice("Could not save the screenshot there");
        }
    }

    /// The shown screenshot, or null (with a notice) when there is none or it is blocked now.
    Image? FrameForExport()
    {
        if (Current == null || Image is not Image img) { ShowNotice("No screenshot at this moment"); return null; }
        if (CurrentHidden) { ShowNotice("This moment is from a blocked app or site"); return null; }
        return img;
    }
}
