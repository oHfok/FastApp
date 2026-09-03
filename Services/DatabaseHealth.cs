using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace FastApp.Services
{
    /// <summary>
    /// Notices when the app cannot write to its database, and says so.
    ///
    /// Added after a real incident: on 2026-09-02 the database became corrupt at
    /// 23:03 and the tracker's next flush threw. That flush ran inside a
    /// fire-and-forget task, so the exception went into a Task nobody awaited and
    /// the tracking loop simply stopped. FastApp then sat in the tray looking
    /// perfectly healthy for twelve hours, recording nothing. The failure was
    /// eventually noticed on the web dashboard -- which reported "can't reach
    /// FastApp", because a broken table makes every data endpoint return 500 --
    /// rather than from the app that knew.
    ///
    /// So the rule here is that a write failure is never silent and never fatal:
    /// the tracker keeps running and keeps accumulating, and the user is told
    /// within a minute that their time is not being saved.
    /// </summary>
    public static class DatabaseHealth
    {
        /// <summary>False once writes have failed often enough to be believed.</summary>
        public static bool IsWritable { get; private set; } = true;

        public static DateTime? LastSuccessfulWrite { get; private set; }
        public static DateTime? FailingSince { get; private set; }
        public static string? LastError { get; private set; }

        // One dropped write means very little -- a flush can lose a race with a
        // backup or a retention sweep. Two in a row is a fault.
        private const int FailuresBeforeWarning = 2;

        private static readonly object Gate = new();
        private static int _consecutiveFailures;
        private static bool _announced;

        public static void ReportWriteSucceeded()
        {
            bool recovered;
            lock (Gate)
            {
                recovered = _announced;
                _consecutiveFailures = 0;
                _announced = false;
                IsWritable = true;
                FailingSince = null;
                LastError = null;
                LastSuccessfulWrite = DateTime.Now;
            }

            if (!recovered) return;

            Log("writes recovered");
            NotificationService.Show(
                "FastApp is saving again",
                "The database is writable, and tracking has resumed.",
                NotificationSeverity.Success,
                force: true);
        }

        public static void ReportWriteFailed(Exception? ex)
        {
            bool announce;
            lock (Gate)
            {
                _consecutiveFailures++;
                LastError = ex?.Message;
                FailingSince ??= DateTime.Now;
                announce = _consecutiveFailures >= FailuresBeforeWarning && !_announced;
                if (announce)
                {
                    _announced = true;
                    IsWritable = false;
                }
            }

            Log($"write failed ({_consecutiveFailures}): {ex}");
            if (!announce) return;

            // Corruption is worth naming, because it is the one failure the user
            // cannot wait out: every later write will fail the same way until the
            // file is repaired or replaced.
            bool corrupt = ex is SqliteException sqlite
                           && (sqlite.SqliteErrorCode == 11 || sqlite.SqliteErrorCode == 26);

            string since = LastSuccessfulWrite is DateTime t
                ? $"Nothing has been recorded since {t:HH:mm}."
                : "Nothing has been recorded this session.";

            NotificationService.Show(
                corrupt ? "FastApp cannot save your data" : "FastApp is not saving your data",
                corrupt
                    ? $"The tracking database is damaged. {since} Details are in {LogPath}."
                    : $"Writing to the database keeps failing. {since} FastApp will keep trying.",
                NotificationSeverity.Warning,
                force: true);
        }

        /// <summary>
        /// The tracking loop exited on an exception rather than on shutdown. This
        /// is the case that used to be invisible.
        /// </summary>
        public static void ReportTrackerStopped(Exception? ex)
        {
            lock (Gate) { IsWritable = false; LastError = ex?.Message; }

            Log($"tracker stopped unexpectedly: {ex}");
            NotificationService.Show(
                "FastApp has stopped tracking",
                $"Time is no longer being recorded. Restarting FastApp should fix it. Details are in {LogPath}.",
                NotificationSeverity.Warning,
                force: true);
        }

        /// <summary>
        /// Beside the database rather than in the install folder, which Velopack
        /// replaces wholesale on every update.
        /// </summary>
        public static string LogPath =>
            Path.Combine(AppDbContext.GetDbFolder(), "database-errors.log");

        private static void Log(string line)
        {
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
            }
            catch
            {
                // If even the log cannot be written there is nowhere left to say
                // so, and failing here would defeat the point of the class.
            }
        }
    }
}
