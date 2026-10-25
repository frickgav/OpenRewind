namespace OpenRewind;

/// Starring on the timeline (S, the star button, the Starred filter). The stars themselves: OpenRewind.Core/Stars.cs.
sealed partial class TimelineModel
{
    /// Starred moments, newest first: for the stars on the bar and the star button.
    public List<Star> Stars { get; private set; } = new();
    /// The result list shows only starred moments (the Starred button next to search).
    public bool StarredOnly { get; private set; }

    public void ReloadStars() => Stars = Db.Stars();

    /// The star on the shown screenshot, if any.
    public Star? CurrentStar =>
        Current is TimelineFrame f ? Stars.FirstOrDefault(s => FrameIndex(s.Time) == Index && s.Time >= f.Time.AddSeconds(-60)) : null;

    /// S or the star button: star the shown moment, or take its star off.
    public void ToggleStar()
    {
        if (Current is not TimelineFrame f) { ShowNotice("Nothing to star here"); return; }
        var had = CurrentStar;
        if (had != null) Db.RemoveStar(had.Id); else Db.AddStar(f.Time);
        ReloadStars();
        if (StarredOnly) ShowStarred();
        ShowNotice(had != null ? "Star removed" : "Starred. Find it again with Starred next to search.");
    }

    public void SetStarredOnly(bool on)
    {
        if (StarredOnly == on) return;
        StarredOnly = on;
        if (on) ShowStarred(); else RunSearch();
    }

    /// Fills the result list with starred moments (matching the search words, when there are any).
    public void ShowStarred()
    {
        searchSeq++;
        Searching = false;
        RefreshHighlights();
        Hits = SearchHits.Starred(Db, Query.Trim(), AppFilterName, Blocklist.Current);
        SelectedHit = 0;
        ShowResults = true;
        Notify();
    }
}
