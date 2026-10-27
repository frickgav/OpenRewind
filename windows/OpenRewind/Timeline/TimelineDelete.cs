using System.Globalization;

namespace OpenRewind;

/// Delete on the timeline: the shown screenshot, the range marked with [ and ], or every screenshot that has the
/// searched words (a password or key that was on screen). Database.DeleteFrames takes the screen text, the search
/// index rows, Search by Meaning moments, stars, meeting titles and the day's recap with the pictures, and has
/// videos written again without them; FlushDeleted then makes sure the deleted text is not left in the database
/// file either. Mac: TimelineDelete.swift.
sealed partial class TimelineModel
{
    /// Asks before deleting: title, text and the delete buttons; returns the index of the one chosen, or -1.
    public delegate int ConfirmDelete(string title, string text, string[] buttons);

    /// A delete is running; another waits for it to finish.
    public bool Deleting { get; private set; }

    /// Delete / Backspace: the marked range when both ends are marked, otherwise the shown screenshot.
    public void DeletePressed(ConfirmDelete confirm)
    {
        if (ClipStart != null && ClipEnd != null) DeleteMarkedRange(confirm); else DeleteShownScreenshot(confirm);
    }

    public void DeleteShownScreenshot(ConfirmDelete confirm)
    {
        if (Current is not TimelineFrame f) { ShowNotice("Nothing to delete here"); return; }
        var when = f.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        if (confirm("Delete this screenshot?",
                    $"The screenshot from {when} and its screen text are deleted, with what search and Ask AI learned from it, "
                    + "and the journal recap of that day. This cannot be undone.",
                    new[] { "Delete Screenshot" }) != 0) return;
        var id = f.Id;
        RunDelete(f.Time, db => db.DeleteFrame(id) > 0 ? "Screenshot deleted" : "That screenshot was already gone");
    }

    /// The marked range, as the timeline shows it: with the app filter on, only that app's screenshots go, and
    /// apps hidden from the timeline are never touched. Without either, every screenshot in the range goes.
    public void DeleteMarkedRange(ConfirmDelete confirm)
    {
        if (MarkedRange is not (DateTime from, DateTime to)) { ShowNotice("Mark where to start and end with [ and ] first"); return; }
        var app = AppFilterName;
        var hidden = HiddenAppNames.ToList();
        var filtered = app != null || HiddenApps.Keys(hidden).Count > 0;
        var ids = filtered ? AppFilter.ShownIds(Db, from, to, app, hidden) : new List<long>();
        var n = filtered ? ids.Count : Db.CountFrames(from, to);
        if (n == 0) { ShowNotice("No screenshots shown in that stretch"); return; }
        var a = from.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var b = to.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        var what = $"{n} screenshot{(n == 1 ? "" : "s")}" + (app != null ? $" of {app}" : "");
        var kept = app != null ? " Other apps in that time are kept, and so is the audio."
            : filtered ? " Apps hidden from the timeline are kept." : "";
        var choice = confirm($"Delete {what} from {a} to {b}?",
                             $"{what} and their screen text are deleted, with what search and Ask AI learned from them, and the journal "
                             + $"recap of that day.{kept}"
                             + (app == null ? " Audio is only deleted when you choose it (each audio clip that starts in the range)." : "")
                             + " This cannot be undone.",
                             app == null ? new[] { "Delete Screenshots", "Delete Screenshots and Audio" } : new[] { $"Delete {what}" });
        if (choice < 0) return;
        var audio = choice == 1;
        ClearMarks();
        RunDelete(from, db =>
        {
            var f = filtered ? ids.Sum(id => db.DeleteFrame(id)) : db.DeleteFrames(from, to);
            var s = audio ? db.DeleteAudio(from, to) : 0;
            return $"Deleted {f} screenshot{(f == 1 ? "" : "s")}" + (app != null ? $" of {app}" : "")
                + (audio ? $" and {s} audio clip{(s == 1 ? "" : "s")}" : "");
        });
    }

    /// Every screenshot that has the words in the search box, in every app and at any time.
    public void DeleteMatching(ConfirmDelete confirm)
    {
        var q = Query.Trim();
        if (q.Length == 0) { ShowNotice("Search for the words first"); return; }
        var ids = Db.FrameIdsMatching(q);
        if (ids.Count == 0) { ShowNotice($"No screenshots have “{q}”"); return; }
        var s = ids.Count == 1 ? "" : "s";
        if (confirm($"Delete every screenshot with “{q}”?",
                    $"{ids.Count} screenshot{s} in every app, from any day, have these words on screen or in the window title. "
                    + "They and their screen text are deleted, with what search and Ask AI learned from them, and the journal recaps of those days. "
                    + "This cannot be undone.",
                    new[] { $"Delete {ids.Count} Screenshot{s}" }) != 0) return;
        RunDelete(Playhead, db =>
        {
            var n = ids.Sum(id => db.DeleteFrame(id));
            return $"Deleted {n} screenshot{(n == 1 ? "" : "s")} with “{q}”";
        });
    }

    /// The next drag across the bar selects a stretch instead of moving (the trash button's Select a Stretch).
    public bool SelectingRange { get; set; }

    public void StartSelecting()
    {
        SelectingRange = true;
        StopAudio();
        ShowNotice("Drag across the bar at the bottom to select a stretch. Shift-drag does it any time.");
    }

    /// A drag across the bar (Select a Stretch, or Shift-drag): marks it like [ and ] do.
    public void SelectRange(DateTime a, DateTime b)
    {
        ClipStart = a < b ? a : b;
        ClipEnd = a < b ? b : a;
        Notify();
    }

    public bool CanDeleteMatching => Query.Trim().Length > 0 && !SearchingByMeaning && !StarredOnly;

    /// The marked range from the screenshot shown at its start through the one at its end, or null without both marks.
    public (DateTime From, DateTime To)? MarkedRange
    {
        get
        {
            if (ClipStart is not DateTime s || ClipEnd is not DateTime e || Frames.Count == 0) return null;
            var (a, b) = s <= e ? (s, e) : (e, s);
            // The screenshot on screen at the start mark was taken a little before it.
            var shown = Frames[FrameIndex(a)].Time;
            return (shown < a ? shown : a, b.AddMilliseconds(10));
        }
    }

    /// Runs the delete off the UI thread, then shows the timeline again at `date` and tells what was done.
    void RunDelete(DateTime date, Func<Database, string> work)
    {
        if (Deleting) { ShowNotice("Still deleting…"); return; }
        Deleting = true;
        StopAudio();
        ShowNotice("Deleting…");
        var ui = SynchronizationContext.Current;
        var db = Db;
        Task.Run(() =>
        {
            string done;
            try { done = work(db); db.FlushDeleted(); }
            catch (Exception e) { Log.Write($"timeline delete failed: {e.Message}"); done = "Could not delete. See the log."; }
            Log.Write($"timeline delete: {done}");
            return done;
        }).ContinueWith(t =>
        {
            void Done()
            {
                Deleting = false;
                ReloadAfterDelete(date);
                if (Query.Trim().Length > 0) RunSearch();
                ShowNotice(t.Result);
            }
            if (ui != null) ui.Post(_ => Done(), null); else Done();
        });
    }

    /// Read the history again as far back as it was loaded, keep the app filter and go back to `date`.
    void ReloadAfterDelete(DateTime date)
    {
        var keep = AppFilterName;
        var since = LoadedSince;
        highlightCache.Clear();
        highlightOrder.Clear();
        ReloadStars();
        Apply(DateTime.UtcNow, since, Db.TimelineFrames(since), Db.TimelineAudio(since));
        if (keep != null && allFrames.Any(f => f.AppName == keep) && !HiddenApps.Keys(HiddenAppNames).Contains(keep)) { AppFilterName = keep; ApplyFilter(); }
        Jump(date);
    }
}
