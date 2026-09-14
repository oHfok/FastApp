using System;
using System.Collections.Generic;

namespace FastApp.Services
{
    /// <summary>
    /// When the tracker considered the user AFK, as start/end timestamps rather
    /// than just the running DailyUsageLog.AfkTimeSpent total -- so the Timeline
    /// ribbon can show WHEN someone was away, not just how much time it added up
    /// to. A sibling loose table on its own short-lived context, same shape as
    /// MusicListeningDaily/AppResourceDaily: a derived view of what the tracker
    /// saw, not domain data -- losing it costs nothing a day of ordinary use
    /// does not refill.
    /// </summary>
    public static class AfkIntervalStore
    {
        private const string Table = "AfkIntervals";

        public static string CreateTableSql => IntervalStore.CreateTableSql(Table);

        /// <summary>
        /// Records one AFK stretch. Called wherever the tracker closes one out --
        /// the isAfk true-&gt;false transition, and (mirroring SessionLog's own
        /// close-out points) on pause, day rollover, and shutdown while still AFK.
        /// </summary>
        public static void RecordInterval(DateTime start, DateTime end) =>
            IntervalStore.RecordInterval(Table, start, end);

        /// <summary>
        /// AFK intervals overlapping the given calendar day, clipped to that
        /// day's own boundaries so a stretch spanning midnight doesn't paint
        /// past either edge of the ribbon it's drawn on.
        /// </summary>
        public static List<(DateTime Start, DateTime End)> GetForDay(DateTime day) =>
            IntervalStore.GetForDay(Table, day);
    }
}
