using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// What each tracked application costs the machine: average and peak CPU
    /// and memory, per app, per day.
    ///
    /// FastApp already knows how long you spend in each application. This is the
    /// other half of the same question -- what that application is asking of the
    /// PC while you do -- and it is a thing FastApp is uniquely placed to answer,
    /// because Task Manager shows you a number for right now and this can show
    /// you the average across a week paired with the time you actually spent
    /// there.
    ///
    /// Stored as running aggregates, not a row per sample. The tracker adds one
    /// sample per app per five-second tick into an in-memory accumulator and
    /// flushes the accumulator on the same sixty-second cycle the daily
    /// summaries use; the average is Sum / Samples on read. A row per sample
    /// would be continuous telemetry -- thousands of rows an hour, a different
    /// shape of data with its own retention and query cost -- and the aggregate
    /// answers every question a resource panel actually asks.
    ///
    /// A sibling table on its own short-lived context, raw SQL, the same shape
    /// as ExecutablePathStore and AppSettingsStore. It is a derived view of
    /// what the tracker saw, not domain data: losing it costs nothing that a
    /// day of ordinary use does not refill.
    /// </summary>
    public static class ResourceStatsStore
    {
        public const string CreateTableSql =
            "CREATE TABLE IF NOT EXISTS AppResourceDaily (" +
            "Date TEXT NOT NULL, " +
            "AppName TEXT NOT NULL, " +
            "CpuPercentSum REAL NOT NULL DEFAULT 0, " +
            "CpuPercentPeak REAL NOT NULL DEFAULT 0, " +
            "RamBytesSum INTEGER NOT NULL DEFAULT 0, " +
            "RamBytesPeak INTEGER NOT NULL DEFAULT 0, " +
            "Samples INTEGER NOT NULL DEFAULT 0, " +
            "PRIMARY KEY (Date, AppName));";

        /// <summary>
        /// One flush window's worth of samples for one app, before it is folded
        /// into whatever is already on disk for that day.
        /// </summary>
        public readonly record struct Accum(
            double CpuPercentSum,
            double CpuPercentPeak,
            long RamBytesSum,
            long RamBytesPeak,
            int Samples);

        /// <summary>What a caller gets back for one app over a queried range.</summary>
        public readonly record struct Summary(
            double AverageCpuPercent,
            double PeakCpuPercent,
            long AverageRamBytes,
            long PeakRamBytes,
            int Samples);

        private static string Key(DateTime date) => date.ToString("yyyy-MM-dd");

        /// <summary>
        /// Fold a flush window's accumulators into the day's rows. Sums add,
        /// peaks take the max, so calling this repeatedly through a day builds
        /// the same totals a single call with every sample would.
        /// </summary>
        public static void FlushBatch(DateTime date, IReadOnlyDictionary<string, Accum> perApp)
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
                    "INSERT INTO AppResourceDaily " +
                    "(Date, AppName, CpuPercentSum, CpuPercentPeak, RamBytesSum, RamBytesPeak, Samples) " +
                    "VALUES ($date, $name, $cpuSum, $cpuPeak, $ramSum, $ramPeak, $samples) " +
                    "ON CONFLICT(Date, AppName) DO UPDATE SET " +
                    "CpuPercentSum  = CpuPercentSum  + excluded.CpuPercentSum, " +
                    "CpuPercentPeak = MAX(CpuPercentPeak, excluded.CpuPercentPeak), " +
                    "RamBytesSum    = RamBytesSum    + excluded.RamBytesSum, " +
                    "RamBytesPeak   = MAX(RamBytesPeak, excluded.RamBytesPeak), " +
                    "Samples        = Samples        + excluded.Samples;";

                var pDate = cmd.CreateParameter(); pDate.ParameterName = "$date"; cmd.Parameters.Add(pDate);
                var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
                var pCpuSum = cmd.CreateParameter(); pCpuSum.ParameterName = "$cpuSum"; cmd.Parameters.Add(pCpuSum);
                var pCpuPeak = cmd.CreateParameter(); pCpuPeak.ParameterName = "$cpuPeak"; cmd.Parameters.Add(pCpuPeak);
                var pRamSum = cmd.CreateParameter(); pRamSum.ParameterName = "$ramSum"; cmd.Parameters.Add(pRamSum);
                var pRamPeak = cmd.CreateParameter(); pRamPeak.ParameterName = "$ramPeak"; cmd.Parameters.Add(pRamPeak);
                var pSamples = cmd.CreateParameter(); pSamples.ParameterName = "$samples"; cmd.Parameters.Add(pSamples);

                string dateKey = Key(date);
                int written = 0;
                foreach (var (name, acc) in perApp)
                {
                    if (string.IsNullOrWhiteSpace(name) || acc.Samples <= 0) continue;
                    pDate.Value = dateKey;
                    pName.Value = name;
                    pCpuSum.Value = acc.CpuPercentSum;
                    pCpuPeak.Value = acc.CpuPercentPeak;
                    pRamSum.Value = acc.RamBytesSum;
                    pRamPeak.Value = acc.RamBytesPeak;
                    pSamples.Value = acc.Samples;
                    cmd.ExecuteNonQuery();
                    written++;
                }

                if (written > 0) tx.Commit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ResourceStatsStore.FlushBatch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Per-app summaries over [fromInclusive, toExclusive) by date. Averages
        /// are Sum / Samples across every day in the range, so a week reads as
        /// one figure rather than seven.
        /// </summary>
        public static Dictionary<string, Summary> GetRange(DateTime fromInclusive, DateTime toExclusive)
        {
            var result = new Dictionary<string, Summary>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var db = new AppDbContext();
                using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText =
                    "SELECT AppName, SUM(CpuPercentSum), MAX(CpuPercentPeak), " +
                    "SUM(RamBytesSum), MAX(RamBytesPeak), SUM(Samples) " +
                    "FROM AppResourceDaily WHERE Date >= $from AND Date < $to GROUP BY AppName";
                var pf = cmd.CreateParameter(); pf.ParameterName = "$from"; pf.Value = Key(fromInclusive); cmd.Parameters.Add(pf);
                var pt = cmd.CreateParameter(); pt.ParameterName = "$to"; pt.Value = Key(toExclusive); cmd.Parameters.Add(pt);

                db.Database.OpenConnection();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    string name = reader.GetString(0);
                    double cpuSum = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
                    double cpuPeak = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
                    long ramSum = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                    long ramPeak = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
                    int samples = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                    if (samples <= 0) continue;

                    result[name] = new Summary(
                        AverageCpuPercent: cpuSum / samples,
                        PeakCpuPercent: cpuPeak,
                        AverageRamBytes: (long)(ramSum / samples),
                        PeakRamBytes: ramPeak,
                        Samples: samples);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ResourceStatsStore.GetRange failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>One app over a single day, or null if nothing was recorded.</summary>
        public static Summary? GetForApp(string appName, DateTime date)
        {
            if (string.IsNullOrWhiteSpace(appName)) return null;
            var range = GetRange(date.Date, date.Date.AddDays(1));
            return range.TryGetValue(appName, out var s) ? s : null;
        }
    }
}
