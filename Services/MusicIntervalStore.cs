using System;
using System.Collections.Generic;

namespace FastApp.Services
{
    /// <summary>
    /// When music was playing (the same wall-clock "any music app" moment
    /// MusicStatsStore.WallClockKey sums), as start/end timestamps -- the
    /// Timeline ribbon's music equivalent of AfkIntervalStore. Same shape,
    /// same lifecycle (record on the anyMusicThisTick true-&gt;false edge, and
    /// on pause/day-rollover/shutdown while still playing).
    /// </summary>
    public static class MusicIntervalStore
    {
        private const string Table = "MusicIntervals";

        public static string CreateTableSql => IntervalStore.CreateTableSql(Table);

        /// <summary>Same idea as AfkIntervalStore.CurrentOpenStart, for music.</summary>
        public static DateTime? CurrentOpenStart { get; set; }

        public static void RecordInterval(DateTime start, DateTime end) =>
            IntervalStore.RecordInterval(Table, start, end);

        /// <summary>Includes the currently-open stretch, running through to now, when the day is today.</summary>
        public static List<(DateTime Start, DateTime End)> GetForDay(DateTime day) =>
            IntervalStore.GetForDay(Table, day, CurrentOpenStart);
    }
}
