using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace FastApp.Services
{
    public record UpdateCheckResult(bool Success, string Message, UpdateInfo? UpdateInfo);

    public static class UpdateService
    {
        // Where scripts/release.ps1 uploads Setup.exe/RELEASES/*.nupkg via `vpk upload github`.
        private const string RepoUrl = "https://github.com/oHfok/FastApp";

        private static UpdateManager CreateManager() => new(new GithubSource(RepoUrl, null, false));

        // "Dev build" covers running via `dotnet run`/F5, where there's no Velopack
        // install to report a version for — UpdateManager throws if asked.
        public static string CurrentVersionText
        {
            get
            {
                try
                {
                    var mgr = CreateManager();
                    return mgr.IsInstalled ? $"v{mgr.CurrentVersion}" : "Dev build";
                }
                catch
                {
                    return "Dev build";
                }
            }
        }

        // Silent startup check — downloads and restarts straight into the update
        // if one exists. Any failure (offline, GitHub unreachable, no release yet)
        // is swallowed: this is a background nicety, never allowed to block or
        // interrupt normal app startup.
        //
        // beforeRestart lets the caller (MainViewModel, via RequestShutdownFlushAsync)
        // stop the tracker and flush/checkpoint the database before Velopack hard-kills
        // this process to install the update -- ApplyUpdatesAndRestart has no idea this
        // app even has a background tracker or an open DB connection, so without this
        // the process gets killed while both are still live. This gap is what's
        // suspected to have corrupted the database on 2026-08-19; see the comment on
        // RequestShutdownFlushAsync for the full story.
        public static async Task CheckAndApplyOnStartupAsync(Func<Task> beforeRestart = null)
        {
            try
            {
                var mgr = CreateManager();
                if (!mgr.IsInstalled) return;

                var newVersion = await mgr.CheckForUpdatesAsync();
                if (newVersion == null) return;

                await mgr.DownloadUpdatesAsync(newVersion);

                if (beforeRestart != null) await beforeRestart();

                // Preserve whatever args this process actually launched with (e.g.
                // "--minimized" from the startup task) so an update landing during
                // a silent boot launch doesn't suddenly pop the UI open.
                var restartArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
                mgr.ApplyUpdatesAndRestart(newVersion, restartArgs);
            }
            catch
            {
                // Best-effort — see comment above.
            }
        }

        // How long to wait for GitHub to answer before giving up on the check.
        //
        // Only the question, not the download: a 113 MB package legitimately
        // takes minutes on a slow line, and cutting that off would be worse than
        // waiting for it. This is the part that can hang with nothing to show.
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

        // User-initiated check from Settings. Downloads eagerly so the caller
        // can offer an immediate restart-to-apply once this returns.
        //
        // onStatus is called as the phase changes, because "Checking for
        // updates" used to cover the whole of this method and the slow part of
        // it is not the check. When a release is waiting, this fetches the
        // entire package -- 113 MB at the time of writing -- and said nothing
        // about it, so a perfectly healthy download read as a jammed button.
        // It is called from whatever thread Velopack reports on; the caller
        // marshals.
        public static async Task<UpdateCheckResult> CheckForUpdatesAsync(Action<string> onStatus = null)
        {
            try
            {
                var mgr = CreateManager();
                if (!mgr.IsInstalled)
                    return new UpdateCheckResult(false, "Not available — this copy wasn't installed via Setup.exe.", null);

                onStatus?.Invoke("Asking GitHub for the latest version…");

                // Raced rather than cancelled: Velopack's CheckForUpdatesAsync
                // takes no CancellationToken, so there is no way to call it off.
                // The loser keeps running -- it is a read-only HTTP call and
                // harmless -- and its exception is observed here so a late
                // failure cannot surface as an unobserved task.
                var check = mgr.CheckForUpdatesAsync();
                if (await Task.WhenAny(check, Task.Delay(CheckTimeout)) != check)
                {
                    _ = check.ContinueWith(
                        t => { _ = t.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted);

                    return new UpdateCheckResult(
                        false,
                        $"No answer from GitHub after {CheckTimeout.TotalSeconds:0} seconds. Check your connection and try again.",
                        null);
                }

                var newVersion = await check;
                if (newVersion == null)
                    return new UpdateCheckResult(true, $"Up to date ({CurrentVersionText}).", null);

                string version = newVersion.TargetFullRelease.Version.ToString();

                // Whole percents only. Velopack reports far more often than that,
                // and every report crosses to the UI thread and pushes a settings
                // payload to the page.
                int reported = -1;
                onStatus?.Invoke($"Downloading v{version}…");
                await mgr.DownloadUpdatesAsync(newVersion, percent =>
                {
                    if (percent == reported) return;
                    reported = percent;
                    onStatus?.Invoke($"Downloading v{version} — {percent}%");
                });

                return new UpdateCheckResult(true, $"Update v{version} ready to install.", newVersion);
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult(false, $"Couldn't check for updates: {ex.Message}", null);
            }
        }

        // ==========================================================
        // ROLLBACK
        //
        // Velopack will install an older version, but only if asked explicitly:
        // AllowVersionDowngrade, a hand-built UpdateInfo marked IsDowngrade, and
        // the Full package rather than a delta (deltas only patch forward from a
        // known base, so they are useless going backwards).
        // ==========================================================

        private static UpdateManager CreateDowngradeManager() =>
            new(new GithubSource(RepoUrl, null, false), new UpdateOptions { AllowVersionDowngrade = true });

        /// <summary>
        /// Whether the target predates the current database schema.
        ///
        /// Migration ids are timestamps (20260816120000_AddDailyLogsIndex), so a
        /// migration applied after a release was published is one that release's
        /// code never knew about. Today every migration is additive and EF simply
        /// ignores columns its model does not mention, so this is a warning and
        /// not a block -- but a future migration that drops or renames a column
        /// would make going back lossy, and the backup below is what makes that
        /// recoverable rather than final.
        /// </summary>
        public static string DescribeSchemaRisk(DateTime targetPublishedUtc)
        {
            try
            {
                using var db = new AppDbContext();
                return DescribeSchemaRisk(targetPublishedUtc, db.Database.GetAppliedMigrations());
            }
            catch
            {
                // Reading the migration history is not worth failing a rollback
                // over; the database backup is the real safety net.
                return null;
            }
        }

        /// <summary>
        /// The comparison itself, split from the database read so it can be
        /// exercised without one.
        /// </summary>
        public static string DescribeSchemaRisk(DateTime targetPublishedUtc, IEnumerable<string> appliedMigrationIds)
        {
            if (targetPublishedUtc == DateTime.MinValue || appliedMigrationIds == null) return null;

            DateTime newest = DateTime.MinValue;
            foreach (var id in appliedMigrationIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                string stamp = id.Split('_')[0];
                if (stamp.Length >= 14 && DateTime.TryParseExact(stamp[..14], "yyyyMMddHHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsed) && parsed > newest)
                {
                    newest = parsed;
                }
            }

            if (newest == DateTime.MinValue || newest <= targetPublishedUtc) return null;

            // Formatted invariantly on purpose. The interface is English, and the
            // default culture here is Polish, so an interpolated date renders as
            // "16 sie 2026" in the middle of an English sentence. The dashboard
            // had exactly this bug before the UX pass; the desktop app has no
            // equivalent of the server's invariant-culture setup to prevent it.
            string when = newest.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
            return $"Your data was upgraded on {when}, after this version was released. "
                 + "It should still open, but anything recorded since then may not show correctly until you update again.";
        }

        /// <summary>Timestamped copy of the database, taken before a rollback. Returns its path, or null.</summary>
        public static string BackUpDatabase()
        {
            try
            {
                string source = AppDbContext.GetDbPath();
                if (string.IsNullOrEmpty(source) || !System.IO.File.Exists(source)) return null;

                string folder = AppDbContext.GetDbFolder();
                string target = System.IO.Path.Combine(folder, $"appmanager.before-rollback-{DateTime.Now:yyyyMMdd-HHmmss}.db");
                System.IO.File.Copy(source, target, overwrite: false);
                return target;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Finds the Full package for a version and downloads it, ready to apply.</summary>
        public static async Task<UpdateCheckResult> PrepareRollbackAsync(string version)
        {
            try
            {
                var mgr = CreateDowngradeManager();
                if (!mgr.IsInstalled)
                    return new UpdateCheckResult(false, "Not available — this copy wasn't installed via Setup.exe.", null);

                string wanted = (version ?? string.Empty).TrimStart('v', 'V');
                var source = new GithubSource(RepoUrl, null, false);
                var feed = await source.GetReleaseFeed(null, "FastApp", "win", null, null);

                var target = (feed.Assets ?? Array.Empty<VelopackAsset>())
                    .FirstOrDefault(a => a.Type == VelopackAssetType.Full
                                      && a.Version?.ToString() == wanted);
                if (target == null)
                    return new UpdateCheckResult(false, $"No installable package published for {wanted}.", null);

                var info = new UpdateInfo(target, isDowngrade: true);
                await mgr.DownloadUpdatesAsync(info);
                return new UpdateCheckResult(true, $"Version {wanted} downloaded and ready.", info);
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult(false, $"Couldn't prepare that version: {ex.Message}", null);
            }
        }

        /// <summary>Applies a prepared rollback. Backs the database up first, then restarts.</summary>
        public static async Task RollBackAndRestartAsync(UpdateInfo target, Func<Task> beforeRestart = null)
        {
            if (target == null) return;

            // Order matters: flush and checkpoint the database through the caller
            // first, then copy the settled file. Copying a live WAL mid-write is
            // how you get a backup that is itself corrupt.
            if (beforeRestart != null) await beforeRestart();
            BackUpDatabase();

            var mgr = CreateDowngradeManager();
            var restartArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
            mgr.ApplyUpdatesAndRestart(target, restartArgs);
        }

        public static async Task ApplyAndRestartAsync(UpdateInfo updateInfo, Func<Task> beforeRestart = null)
        {
            if (beforeRestart != null) await beforeRestart();

            var mgr = CreateManager();
            var restartArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
            mgr.ApplyUpdatesAndRestart(updateInfo, restartArgs);
        }
    }
}
