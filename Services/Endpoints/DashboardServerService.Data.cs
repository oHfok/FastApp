using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;

namespace FastApp.Services
{
    // Backup download, database size reporting, and restore-from-backup.
    //
    // Split out of DashboardServerService.StartAsync, which had grown to 2,261
    // lines with every endpoint's query, aggregation and shaping logic inline.
    // Same class (partial), same registration, same behaviour -- this only
    // changes which file each group lives in, so the one method every change had
    // to be made inside is no longer the whole server.
    public static partial class DashboardServerService
    {
        private static void MapDataEndpoints(WebApplication app)
        {
        app.MapGet("/api/backup", async (HttpContext context) =>
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"fastapp-backup-{Guid.NewGuid():N}.db");
            try
            {
                using var db = new AppDbContext();
                string escapedPath = tempPath.Replace("'", "''");
                await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{escapedPath}';");

                string downloadName = $"FastApp-Backup-{DateTime.Now:yyyy-MM-dd}.db";
                context.Response.ContentType = "application/octet-stream";
                context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{downloadName}\"");

                using var fileStream = File.OpenRead(tempPath);
                await fileStream.CopyToAsync(context.Response.Body);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort cleanup of the temp snapshot */ }
            }
        });

        // Current database size plus a rough linear-growth projection, for
        // the Settings tab's "how big is this going to get" section. The
        // projection is deliberately simple (total size / days tracked =
        // bytes/day, extrapolated forward) rather than trying to model
        // retention pruning or usage trends -- good enough for "should I
        // be worried", not meant to be precise.
        app.MapGet("/api/db-stats", async (HttpContext context) =>
        {
            try
            {
                string dbPath = AppDbContext.GetDbPath();
                long dbSizeBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
                // The WAL holds committed-but-not-yet-checkpointed data --
                // real disk usage even though it's a separate file.
                string walPath = dbPath + "-wal";
                if (File.Exists(walPath)) dbSizeBytes += new FileInfo(walPath).Length;

                using var db = new AppDbContext();
                DateTime? firstDate = await db.DailyLogs.AsNoTracking()
                    .Where(l => l.AppName == "SYSTEM_PC")
                    .OrderBy(l => l.Date)
                    .Select(l => (DateTime?)l.Date)
                    .FirstOrDefaultAsync();

                int daysTracked = firstDate.HasValue ? Math.Max(1, (int)(DateTime.Today - firstDate.Value).TotalDays + 1) : 1;
                double bytesPerDay = (double)dbSizeBytes / daysTracked;

                await context.Response.WriteAsJsonAsync(new
                {
                    DbSizeBytes = dbSizeBytes,
                    FirstTrackedDate = firstDate?.ToString("yyyy-MM-dd"),
                    DaysTracked = daysTracked,
                    BytesPerDay = Math.Round(bytesPerDay, 1),
                    Projected90Days = (long)(dbSizeBytes + bytesPerDay * 90),
                    Projected365Days = (long)(dbSizeBytes + bytesPerDay * 365)
                });
            }
            catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
        });

        // Restores the tracking database from an uploaded backup file (the
        // counterpart to /api/backup's download). Validates thoroughly
        // BEFORE touching anything live: a bad upload must never reach the
        // point of stopping the tracker or copying over the real database.
        // The actual swap-and-restart happens in MainViewModel (via the
        // same WeakReferenceMessenger pattern already used for every other
        // dashboard-to-WPF action) since it needs to gracefully stop the
        // background tracker first -- see RestoreBackupCommand's handler
        // for why (2026-08-19's database corruption came from skipping
        // exactly that step during an update-triggered restart).
        app.MapPost("/api/restore", async (IFormFile file, HttpContext context) =>
        {
            string stagingPath = Path.Combine(Path.GetTempPath(), $"fastapp-restore-{Guid.NewGuid():N}.db");
            try
            {
                if (file == null || file.Length == 0)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { error = "No file uploaded." });
                    return;
                }
                if (file.Length > 500 * 1024 * 1024)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { error = "File is too large to be a FastApp backup." });
                    return;
                }

                using (var stream = File.Create(stagingPath))
                {
                    await file.CopyToAsync(stream);
                }

                // 1. SQLite file header check ("SQLite format 3\0").
                byte[] header = new byte[16];
                using (var fs = File.OpenRead(stagingPath))
                {
                    int read = await fs.ReadAsync(header, 0, 16);
                    if (read < 16 || System.Text.Encoding.ASCII.GetString(header, 0, 15) != "SQLite format 3")
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = "That doesn't look like a SQLite database file." });
                        return;
                    }
                }

                // 2. Structural integrity + 3. it's actually a FastApp backup,
                // not just any SQLite file someone picked by accident.
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={stagingPath}"))
                {
                    conn.Open();

                    using (var checkCmd = conn.CreateCommand())
                    {
                        checkCmd.CommandText = "PRAGMA integrity_check;";
                        string result = (string)await checkCmd.ExecuteScalarAsync();
                        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsJsonAsync(new { error = "That backup file is corrupted (failed SQLite integrity check)." });
                            return;
                        }
                    }

                    using (var tableCmd = conn.CreateCommand())
                    {
                        tableCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('DailyLogs', 'ManagedApps', 'AppSettings');";
                        long tableCount = (long)await tableCmd.ExecuteScalarAsync();
                        if (tableCount < 3)
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsJsonAsync(new { error = "That's a SQLite file, but not a FastApp backup." });
                            return;
                        }
                    }
                }
                // SqliteConnection is closed by the using block above before the file
                // gets handed off — otherwise the restore's own File.Copy could race
                // against this validation connection still holding it open.

                // Respond success BEFORE triggering the actual restore: the browser
                // needs to receive this while the process is still alive to send it.
                await context.Response.WriteAsJsonAsync(new { success = true, message = "Restoring — FastApp will restart in a few seconds." });
                await context.Response.Body.FlushAsync();

                WeakReferenceMessenger.Default.Send(new FastApp.ViewModels.RestoreBackupCommand(stagingPath));
            }
            catch (Exception ex)
            {
                try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            }
        });

        }
    }
}
