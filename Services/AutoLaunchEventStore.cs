using System;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// One row per app per auto-launch pass: what happened -- Started,
    /// AlreadyRunning, Failed, NotFound -- and why, when a launch didn't just
    /// work. AutoLaunchProgressService/-Window already know all of this at
    /// the moment they show it, then the window closes and it's gone; there
    /// was no way to answer "did Slack actually start this morning" after the
    /// fact. A sibling loose table, same shape as LimitEventStore.
    /// </summary>
    public static class AutoLaunchEventStore
    {
        public const string CreateTableSql =
            "CREATE TABLE IF NOT EXISTS AutoLaunchEvents (" +
            "Date TEXT NOT NULL, " +
            "AppName TEXT NOT NULL, " +
            "Outcome TEXT NOT NULL, " + // 'Started' | 'AlreadyRunning' | 'Failed' | 'NotFound'
            "Detail TEXT, " +           // the launch error, when there was one
            "Timestamp TEXT NOT NULL);";

        public static void RecordEvent(string appName, string outcome, string detail, DateTime when)
        {
            if (string.IsNullOrWhiteSpace(appName)) return;
            try
            {
                using var db = new AppDbContext();
                db.Database.ExecuteSqlRaw(
                    "INSERT INTO AutoLaunchEvents (Date, AppName, Outcome, Detail, Timestamp) VALUES ({0}, {1}, {2}, {3}, {4});",
                    when.ToString("yyyy-MM-dd"), appName, outcome, (object)detail ?? DBNull.Value, when.ToString("o"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AutoLaunchEventStore.RecordEvent failed: {ex.Message}");
            }
        }
    }
}
