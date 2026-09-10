using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// How long each application spent actively playing music, per app, per day.
    ///
    /// A five-second tick counts toward an app here when two things are true at
    /// once: the app carries the "Music" category (as resolved by CategoryMap --
    /// what has actually been curated on the dashboard, not the stale
    /// ManagedApps.Category), and a Windows media session belonging to that app
    /// is reporting Playing. Spotify left paused in the background adds nothing;
    /// a video player that happens to be playing but is not categorised as Music
    /// adds nothing.
    ///
    /// Stored as a running total of whole seconds, folded in on the same 60s
    /// cycle as the daily summaries and the resource stats. A sibling loose
    /// table on its own short-lived context, raw SQL, the same shape as
    /// ResourceStatsStore and ExecutablePathStore -- a derived view of what the
    /// tracker saw, not domain data: losing it costs nothing a day of ordinary
    /// listening does not refill.
    /// </summary>
    public static class MusicStatsStore
    {
        public const string CreateTableSql =
            "CREATE TABLE IF NOT EXISTS MusicListeningDaily (" +
            "Date TEXT NOT NULL, " +
            "AppName TEXT NOT NULL, " +
            "Seconds INTEGER NOT NULL DEFAULT 0, " +
            "PRIMARY KEY (Date, AppName));";

        private static string Key(DateTime date) => date.ToString("yyyy-MM-dd");

        /// <summary>
        /// Add a flush window's seconds to the day's rows. Called repeatedly
        /// through a day; the totals come out the same as a single call with the
        /// whole day's seconds would.
        /// </summary>
        public static void FlushBatch(DateTime date, IReadOnlyDictionary<string, long> perApp)
        {
            if (perApp == null || perApp.Count == 0) return;

            try
            {
                using var db = new AppDbContext();
                var conn = db.Database.GetDbConnection();
                db.Database.OpenConnection();

                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO MusicListeningDaily (Date, AppName, Seconds) " +
                    "VALUES ($date, $name, $seconds) " +
                    "ON CONFLICT(Date, AppName) DO UPDATE SET " +
                    "Seconds = Seconds + excluded.Seconds;";

                var pDate = cmd.CreateParameter(); pDate.ParameterName = "$date"; cmd.Parameters.Add(pDate);
                var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
                var pSeconds = cmd.CreateParameter(); pSeconds.ParameterName = "$seconds"; cmd.Parameters.Add(pSeconds);

                string dateKey = Key(date);
                int written = 0;
                foreach (var (name, seconds) in perApp)
                {
                    if (string.IsNullOrWhiteSpace(name) || seconds <= 0) continue;
                    pDate.Value = dateKey;
                    pName.Value = name;
                    pSeconds.Value = seconds;
                    cmd.ExecuteNonQuery();
                    written++;
                }

                if (written > 0) tx.Commit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MusicStatsStore.FlushBatch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Seconds of music per app over [fromInclusive, toExclusive) by date,
        /// summed across every day in the range.
        /// </summary>
        public static Dictionary<string, long> GetRange(DateTime fromInclusive, DateTime toExclusive)
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var db = new AppDbContext();
                using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText =
                    "SELECT AppName, SUM(Seconds) FROM MusicListeningDaily " +
                    "WHERE Date >= $from AND Date < $to GROUP BY AppName";
                var pf = cmd.CreateParameter(); pf.ParameterName = "$from"; pf.Value = Key(fromInclusive); cmd.Parameters.Add(pf);
                var pt = cmd.CreateParameter(); pt.ParameterName = "$to"; pt.Value = Key(toExclusive); cmd.Parameters.Add(pt);

                db.Database.OpenConnection();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    long seconds = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    if (seconds <= 0) continue;
                    result[reader.GetString(0)] = seconds;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MusicStatsStore.GetRange failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Total music seconds across every app over [fromInclusive, toExclusive).
        /// One SUM() on disk rather than materialising the per-app rows.
        /// </summary>
        public static long GetTotalSeconds(DateTime fromInclusive, DateTime toExclusive)
        {
            try
            {
                using var db = new AppDbContext();
                using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText =
                    "SELECT COALESCE(SUM(Seconds), 0) FROM MusicListeningDaily " +
                    "WHERE Date >= $from AND Date < $to";
                var pf = cmd.CreateParameter(); pf.ParameterName = "$from"; pf.Value = Key(fromInclusive); cmd.Parameters.Add(pf);
                var pt = cmd.CreateParameter(); pt.ParameterName = "$to"; pt.Value = Key(toExclusive); cmd.Parameters.Add(pt);

                db.Database.OpenConnection();
                var result = cmd.ExecuteScalar();
                return result == null || result is DBNull ? 0 : Convert.ToInt64(result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MusicStatsStore.GetTotalSeconds failed: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Total music seconds across every app for a single day.</summary>
        public static long GetTotalForDay(DateTime date) =>
            GetTotalSeconds(date.Date, date.Date.AddDays(1));

        /// <summary>
        /// Music seconds per calendar day (summed across every app) over
        /// [fromInclusive, toExclusive). One query; the caller slices it into
        /// whatever period ranges it needs, the same way the Periods endpoints
        /// slice the SYSTEM_PC daily logs they already hold in memory.
        /// </summary>
        public static Dictionary<DateTime, long> GetDailyTotals(DateTime fromInclusive, DateTime toExclusive)
        {
            var result = new Dictionary<DateTime, long>();
            try
            {
                using var db = new AppDbContext();
                using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText =
                    "SELECT Date, SUM(Seconds) FROM MusicListeningDaily " +
                    "WHERE Date >= $from AND Date < $to GROUP BY Date";
                var pf = cmd.CreateParameter(); pf.ParameterName = "$from"; pf.Value = Key(fromInclusive); cmd.Parameters.Add(pf);
                var pt = cmd.CreateParameter(); pt.ParameterName = "$to"; pt.Value = Key(toExclusive); cmd.Parameters.Add(pt);

                db.Database.OpenConnection();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    if (DateTime.TryParseExact(reader.GetString(0), "yyyy-MM-dd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var d))
                        result[d.Date] = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MusicStatsStore.GetDailyTotals failed: {ex.Message}");
            }
            return result;
        }
    }
}
