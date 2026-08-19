using System;
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

        // User-initiated check from the Settings tab. Downloads eagerly so the
        // caller can offer an immediate restart-to-apply once this returns.
        public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
        {
            try
            {
                var mgr = CreateManager();
                if (!mgr.IsInstalled)
                    return new UpdateCheckResult(false, "Not available — this copy wasn't installed via Setup.exe.", null);

                var newVersion = await mgr.CheckForUpdatesAsync();
                if (newVersion == null)
                    return new UpdateCheckResult(true, $"Up to date ({CurrentVersionText}).", null);

                await mgr.DownloadUpdatesAsync(newVersion);
                return new UpdateCheckResult(true, $"Update v{newVersion.TargetFullRelease.Version} ready to install.", newVersion);
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult(false, $"Couldn't check for updates: {ex.Message}", null);
            }
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
