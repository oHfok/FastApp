using System;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// Key/value access to the AppSettings table.
    ///
    /// This app has grown two settings stores: the AppSettings table, which the
    /// web dashboard reads, and loose text files next to the database
    /// (osd_setting.txt, autolaunch_progress_setting.txt), which only the WPF
    /// side knows about. New settings go here, so there is one place to look and
    /// the dashboard can reach them.
    ///
    /// Every call opens its own short-lived context rather than sharing the
    /// view model's: that context is written to from the tracker thread every
    /// 60 seconds, and settings reads have no business queueing behind it. This
    /// is the same pattern the window-title and PIN re-reads already use.
    /// </summary>
    public static class AppSettingsStore
    {
        public static string Get(string key, string fallback = null)
        {
            try
            {
                using var db = new AppDbContext();
                using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT Value FROM AppSettings WHERE Key = $key";
                var p = cmd.CreateParameter();
                p.ParameterName = "$key";
                p.Value = key;
                cmd.Parameters.Add(p);

                db.Database.OpenConnection();
                using var reader = cmd.ExecuteReader();
                return reader.Read() && !reader.IsDBNull(0) ? reader.GetString(0) : fallback;
            }
            catch
            {
                // A settings read that fails must never be worse than an unset
                // setting -- callers all have a sensible default.
                return fallback;
            }
        }

        public static bool GetBool(string key, bool fallback) =>
            Get(key) is string raw ? raw == "true" : fallback;

        public static int? GetInt(string key) =>
            int.TryParse(Get(key), out int value) ? value : null;

        public static void Set(string key, string value)
        {
            try
            {
                using var db = new AppDbContext();
                db.Database.ExecuteSqlRaw(
                    "INSERT OR REPLACE INTO AppSettings (Key, Value) VALUES ({0}, {1})", key, value);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not save setting '{key}': {ex.Message}");
            }
        }

        public static void SetBool(string key, bool value) => Set(key, value ? "true" : "false");
    }
}
