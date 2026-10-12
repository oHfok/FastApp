using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// One verified copy of the database per day, the last seven kept.
    ///
    /// Until now the only copies FastApp ever made were the ones before a migration or
    /// a restore. The database has corrupted twice, and both times what saved the data
    /// was a copy that happened to exist. This makes sure one always does.
    ///
    /// Kept in a folder NEXT TO FastAppData rather than inside it, so deleting or
    /// replacing the data folder does not take the backups with it. Each copy is made
    /// with VACUUM INTO (a consistent snapshot even while the tracker is writing) and
    /// checked before it counts: a copy of an already-damaged database is discarded,
    /// and old good copies are only pruned once a new good one exists.
    /// </summary>
    public static class AutoBackup
    {
        public const int Keep = 7;
        private const string Prefix = "appmanager-";

        private static readonly object Gate = new();
        private static System.Threading.Timer _timer;

        /// <summary>%LOCALAPPDATA%\FastAppDataBackups for a normal install.</summary>
        public static string Folder =>
            AppDbContext.GetDbFolder().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "Backups";

        /// <summary>A few minutes after launch, so it never competes with startup, then four times a day.</summary>
        public static void Start() =>
            _timer = new System.Threading.Timer(_ => RunIfDue(), null, TimeSpan.FromMinutes(3), TimeSpan.FromHours(6));

        /// <summary>Makes today's backup unless it already exists. Returns its path, or null if none was made.</summary>
        public static string RunIfDue(DateTime? today = null)
        {
            lock (Gate)
            {
                long started = PerfLog.Stamp();
                string final = Path.Combine(Folder, $"{Prefix}{today ?? DateTime.Today:yyyy-MM-dd}.db");
                string temp = final + ".tmp";
                try
                {
                    if (File.Exists(final)) return null;
                    Directory.CreateDirectory(Folder);
                    if (File.Exists(temp)) File.Delete(temp);

                    using (var db = new AppDbContext())
                    {
                        var connection = db.Database.GetDbConnection();
                        db.Database.OpenConnection();
                        using var command = connection.CreateCommand();
                        command.CommandText = $"VACUUM INTO '{temp.Replace("'", "''")}';";
                        command.ExecuteNonQuery();
                    }

                    if (!LooksHealthy(temp))
                    {
                        File.Delete(temp);
                        CrashLog.Log("Automatic backup discarded",
                            new InvalidDataException("The copy failed its integrity check, so the database itself may be damaged."));
                        return null;
                    }

                    File.Move(temp, final);
                    foreach (var old in Existing().Skip(Keep))
                        try { File.Delete(old.FullName); } catch { }

                    PerfLog.Done("backup.auto", started, 5000);
                    return final;
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    CrashLog.Log("Automatic backup failed", ex);
                    return null;
                }
            }
        }

        /// <summary>Backups on disk, newest first.</summary>
        public static List<FileInfo> Existing()
        {
            try
            {
                return Directory.Exists(Folder)
                    ? new DirectoryInfo(Folder).GetFiles(Prefix + "*.db").OrderByDescending(f => f.Name).ToList()
                    : new List<FileInfo>();
            }
            catch { return new List<FileInfo>(); }
        }

        private static bool LooksHealthy(string path)
        {
            // Pooling off, so the file handle is released and the copy can be renamed.
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            return string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase);
        }
    }
}
