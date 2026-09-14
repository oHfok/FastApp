using System;
using System.Collections.Generic;

namespace FastApp.Services
{
    /// <summary>
    /// When tracking was paused, as start/end timestamps. PauseTracking/
    /// ResumeTracking only ever kept the pause's deadline (TrackingPausedUntil,
    /// a single settings key overwritten on every pause), so "how often do you
    /// pause, and for how long" was unanswerable after the fact -- the same
    /// gap AfkIntervalStore closed for AFK. Same shape, same lifecycle.
    /// </summary>
    public static class PauseIntervalStore
    {
        private const string Table = "PauseIntervals";

        public static string CreateTableSql => IntervalStore.CreateTableSql(Table);

        public static void RecordInterval(DateTime start, DateTime end) =>
            IntervalStore.RecordInterval(Table, start, end);
    }
}
