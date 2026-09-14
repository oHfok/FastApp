using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// Shared plumbing behind AfkIntervalStore and MusicIntervalStore: both are
    /// "when was X true" start/end logs on their own loose table, differing only
    /// in the table name. Kept internal -- callers go through the named wrapper
    /// (AfkIntervalStore.RecordInterval, not IntervalStore.RecordInterval("Afk...")))
    /// so call sites read the same way the rest of the *Store classes do.
    /// </summary>
    internal static class IntervalStore
    {
        public static string CreateTableSql(string table) =>
            $"CREATE TABLE IF NOT EXISTS {table} (StartTime TEXT NOT NULL, EndTime TEXT NOT NULL);";

        public static void RecordInterval(string table, DateTime start, DateTime end)
        {
            if (end <= start) return;
            try
            {
                using var db = new AppDbContext();
                db.Database.ExecuteSqlRaw(
                    $"INSERT INTO {table} (StartTime, EndTime) VALUES ({{0}}, {{1}});",
                    start.ToString("o"), end.ToString("o"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IntervalStore.RecordInterval({table}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Intervals overlapping the given day, clipped to its boundaries.
        /// <paramref name="openStart"/> is the caller's own CurrentOpenStart --
        /// a stretch that began but hasn't closed (and so has no row yet) --
        /// added as one more interval running through to now. Only applied
        /// when the requested day is today: an interval still open always
        /// started (or was normalized to, at midnight) today, so it has
        /// nothing to add to a query for any other day.
        /// </summary>
        public static List<(DateTime Start, DateTime End)> GetForDay(string table, DateTime day, DateTime? openStart = null)
        {
            var result = new List<(DateTime, DateTime)>();
            DateTime dayStart = day.Date;
            DateTime dayEnd = dayStart.AddDays(1);
            try
            {
                using var db = new AppDbContext();
                using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText =
                    $"SELECT StartTime, EndTime FROM {table} " +
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
                System.Diagnostics.Debug.WriteLine($"IntervalStore.GetForDay({table}) failed: {ex.Message}");
            }

            if (openStart.HasValue && dayStart == DateTime.Today)
            {
                DateTime s = openStart.Value < dayStart ? dayStart : openStart.Value;
                DateTime e = DateTime.Now > dayEnd ? dayEnd : DateTime.Now;
                if (e > s) result.Add((s, e));
            }

            return result;
        }
    }
}
