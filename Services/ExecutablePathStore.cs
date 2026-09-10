using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// Where on disk each process FastApp has seen running actually lives.
    ///
    /// FastApp tracks every application that takes the foreground, but the logs
    /// record only a bare process name -- "Discord", "Javaw" -- and adding an
    /// app to the manager needs a real path. TrackedApps.ResolvePath could get
    /// one only from a process that happened to be running at the moment you
    /// searched, or from a Start-menu / Store scan that misses anything
    /// installed some other way. So an app you had closed, or a portable exe,
    /// resolved to nothing and you had to point the file picker at it yourself.
    ///
    /// This closes that gap. The tracker already enumerates every process every
    /// five seconds; it now also reads MainModule.FileName for names it has not
    /// captured yet and writes them here, so by the time you go looking for an
    /// app in search its path is already known whether it is open or not.
    ///
    /// It matters for enforcement too. Discord's stored ExecutablePath was
    /// ...\Discord\Update.exe -- the Squirrel stub, which exits seconds after
    /// launch -- so a path captured from the live Discord.exe process is not
    /// just more complete, it is the one the daily-limit force-close can
    /// actually match against.
    ///
    /// A rebuildable cache, not domain data: raw SQL over its own table on its
    /// own short-lived context, the same shape as AppSettingsStore, rather than
    /// an EF-migrated entity. Losing it costs nothing a day of ordinary use
    /// does not refill.
    /// </summary>
    public static class ExecutablePathStore
    {
        public const string CreateTableSql =
            "CREATE TABLE IF NOT EXISTS KnownExecutables (" +
            "ProcessName TEXT PRIMARY KEY COLLATE NOCASE, " +
            "FullPath TEXT NOT NULL, " +
            "LastSeenUtc TEXT NOT NULL);";

        /// <summary>The recorded path for a process name, or null if none is known.</summary>
        public static string Get(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return null;

            try
            {
                using var db = new AppDbContext();
                using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT FullPath FROM KnownExecutables WHERE ProcessName = $name";
                var p = cmd.CreateParameter();
                p.ParameterName = "$name";
                p.Value = processName;
                cmd.Parameters.Add(p);

                db.Database.OpenConnection();
                using var reader = cmd.ExecuteReader();
                return reader.Read() && !reader.IsDBNull(0) ? reader.GetString(0) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Every recorded name to path, lowercased-insensitive keys.</summary>
        public static Dictionary<string, string> All()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var db = new AppDbContext();
                using var cmd = db.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT ProcessName, FullPath FROM KnownExecutables";
                db.Database.OpenConnection();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                        map[reader.GetString(0)] = reader.GetString(1);
                }
            }
            catch { /* an unreadable cache is an empty one */ }
            return map;
        }

        /// <summary>
        /// Record a batch of name/path pairs in one transaction. Called from
        /// the tracker with only the names it has newly seen this session, so
        /// this is a handful of rows on a cold start and nothing at all after.
        /// </summary>
        public static void RecordMany(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            if (pairs == null) return;

            try
            {
                using var db = new AppDbContext();
                var conn = db.Database.GetDbConnection();
                db.Database.OpenConnection();

                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO KnownExecutables (ProcessName, FullPath, LastSeenUtc) " +
                    "VALUES ($name, $path, $seen) " +
                    "ON CONFLICT(ProcessName) DO UPDATE SET FullPath = $path, LastSeenUtc = $seen;";

                var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
                var pPath = cmd.CreateParameter(); pPath.ParameterName = "$path"; cmd.Parameters.Add(pPath);
                var pSeen = cmd.CreateParameter(); pSeen.ParameterName = "$seen"; cmd.Parameters.Add(pSeen);

                string now = DateTime.UtcNow.ToString("o");
                int written = 0;
                foreach (var pair in pairs)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                    pName.Value = pair.Key;
                    pPath.Value = pair.Value;
                    pSeen.Value = now;
                    cmd.ExecuteNonQuery();
                    written++;
                }

                if (written > 0) tx.Commit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExecutablePathStore.RecordMany failed: {ex.Message}");
            }
        }
    }
}
