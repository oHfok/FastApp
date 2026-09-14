using System;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// How the palette itself gets used: when it's summoned, and when an app
    /// is launched from it -- and whether that launch came from typing a
    /// search or from the default list. Neither reached the database before;
    /// the palette's search is entirely client-side (palette.js) and told C#
    /// nothing about it.
    ///
    /// Deliberately does NOT store the search text itself -- what someone
    /// typed to find an app is a step more sensitive than the fact that they
    /// searched, and the "did search help you find this" question doesn't
    /// need the query to answer.
    /// </summary>
    public static class PaletteEventStore
    {
        public const string CreateTableSql =
            "CREATE TABLE IF NOT EXISTS PaletteEvents (" +
            "Date TEXT NOT NULL, " +
            "EventType TEXT NOT NULL, " + // 'Opened' | 'Activated'
            "AppName TEXT, " +            // set for 'Activated' only
            "ViaSearch INTEGER, " +       // 0/1, set for 'Activated' only
            "Timestamp TEXT NOT NULL);";

        public static void RecordOpened(DateTime when) => Record("Opened", null, null, when);

        public static void RecordActivated(string appName, bool viaSearch, DateTime when) =>
            Record("Activated", appName, viaSearch, when);

        private static void Record(string eventType, string appName, bool? viaSearch, DateTime when)
        {
            try
            {
                using var db = new AppDbContext();
                db.Database.ExecuteSqlRaw(
                    "INSERT INTO PaletteEvents (Date, EventType, AppName, ViaSearch, Timestamp) VALUES ({0}, {1}, {2}, {3}, {4});",
                    when.ToString("yyyy-MM-dd"), eventType, (object)appName ?? DBNull.Value,
                    viaSearch.HasValue ? (viaSearch.Value ? 1 : 0) : (object)DBNull.Value, when.ToString("o"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PaletteEventStore.Record failed: {ex.Message}");
            }
        }
    }
}
