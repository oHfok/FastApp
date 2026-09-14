using System;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// A history of daily-limit enforcement -- warned, hit, killed -- and of
    /// "Extend time" grants, both of which the tracker only ever kept as
    /// today-only flags/counters (HasWarnedToday, HasNotifiedToday,
    /// TodayBonusMinutes) that reset at midnight and leave no trace behind.
    /// That is fine for the enforcement logic itself, which only ever needs
    /// to know about today, but it means "how often did Discord actually hit
    /// its limit this month" or "how many times did I reach for Extend time"
    /// was unanswerable after the fact. A sibling loose table, same shape as
    /// AfkIntervalStore/MusicIntervalStore: a derived log of what the tracker
    /// did, not domain data.
    ///
    /// Write-only for now -- no read/query method is added until something
    /// actually calls one. An unused GetForRange written on spec is exactly
    /// the kind of dead code a later cleanup pass would flag.
    /// </summary>
    public static class LimitEventStore
    {
        public const string CreateTableSql =
            "CREATE TABLE IF NOT EXISTS LimitEvents (" +
            "Date TEXT NOT NULL, " +
            "AppName TEXT NOT NULL, " +
            "EventType TEXT NOT NULL, " + // 'Warned' | 'LimitReached' | 'Killed' | 'Extended'
            "Minutes INTEGER, " +         // extra minutes granted -- only set for 'Extended'
            "Timestamp TEXT NOT NULL);";

        public static void RecordEvent(string appName, string eventType, DateTime when, int? minutes = null)
        {
            if (string.IsNullOrWhiteSpace(appName)) return;
            try
            {
                using var db = new AppDbContext();
                db.Database.ExecuteSqlRaw(
                    "INSERT INTO LimitEvents (Date, AppName, EventType, Minutes, Timestamp) VALUES ({0}, {1}, {2}, {3}, {4});",
                    when.ToString("yyyy-MM-dd"), appName, eventType, (object)minutes ?? DBNull.Value, when.ToString("o"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LimitEventStore.RecordEvent failed: {ex.Message}");
            }
        }
    }
}
