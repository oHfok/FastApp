namespace FastApp.Services
{
    // The single authoritative definition of the per-app milestone ladder.
    //
    // These thresholds previously existed in three separate places -- the
    // app-details endpoint, the Wrapped period scan, and MILESTONE_TIERS in
    // wwwroot/js/utils.js -- kept in step only by a comment asking future editors
    // to remember. Any drift would have been silent and contradictory: the
    // drawer's ladder and Wrapped's "milestones reached" list would disagree
    // about the same app on the same day.
    //
    // The frontend now receives this list from /api/app-details rather than
    // carrying its own copy, so the ladder can only ever be changed here. Colors
    // deliberately stay in CSS/JS: those are presentation, not domain rules.
    public static class MilestoneTiers
    {
        public record Tier(string Name, double Hours);

        public static readonly Tier[] All =
        {
            new("Bronze", 10),
            new("Silver", 50),
            new("Gold", 150),
            new("Platinum", 500)
        };
    }
}
