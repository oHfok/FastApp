using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

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
        public const string CreateTableSql =
            "CREATE TABLE IF NOT EXISTS AfkIntervals (" +
            "StartTime TEXT NOT NULL, " +
            "EndTime TEXT NOT NULL);";

        /// <summary>
        /// Records one AFK stretch. Called wherever the tracker closes one out --
        /// the isAfk true-&gt;false transition, and (mirroring SessionLog's own
        /// close-out points) on pause, day rollover, and shutdown while still AFK.
        /// </summary>
        public static void RecordInterval(DateTime start, DateTime end)
        {
            if (end <= start) return;
            try
            {
                using var db = new AppDbContext();
                db.Database.ExecuteSqlRaw(
                    "INSERT INTO AfkIntervals (StartTime, EndTime) VALUES ({0}, {1});",
                    start.ToString("o"), end.ToString("o"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AfkIntervalStore.RecordInterval failed: {ex.Message}");
            }
        }

        /// <summary>
        /// AFK intervals overlapping the given calendar day, clipped to that
        /// day's own boundaries so a stretch spanning midnight doesn't paint
        /// past either edge of the ribbon it's drawn on.
        /// </summary>
        public static List<(DateTime Start, DateTime End)> GetForDay(DateTime day)
        {
            var result = new List<(DateTime, DateTime)>();
            DateTime dayStart = day.Date;
            DateTime dayEnd = dayStart.AddDays(1);
            try
            {
                using var db = new AppDbContext();
                using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText =
                    "SELECT StartTime, EndTime FROM AfkIntervals " +
                    "WHERE StartTime < $dayEnd AND EndTime > $dayStart ORDER BY StartTime";
                var p1 = cmd.CreateParameter(); p1.ParameterName = "$dayEnd"; p1.Value = dayEnd.ToString("o"); cmd.Parameters.Add(p1);
                var p2 = cmd.CreateParameter(); p2.ParameterName = "$dayStart"; p2.Value = dayStart.ToString("o"); cmd.Parameters.Add(p2);

                db.Database.OpenConnection();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                    if (!DateTime.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s)) continue;
                    if (!DateTime.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var e)) continue;

                    if (s < dayStart) s = dayStart;
                    if (e > dayEnd) e = dayEnd;
                    if (e > s) result.Add((s, e));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AfkIntervalStore.GetForDay failed: {ex.Message}");
            }
            return result;
        }
    }
}
