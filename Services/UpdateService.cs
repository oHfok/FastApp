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
        public static async Task CheckAndApplyOnStartupAsync()
        {
            try
            {
                var mgr = CreateManager();
                if (!mgr.IsInstalled) return;

                var newVersion = await mgr.CheckForUpdatesAsync();
                if (newVersion == null) return;

                await mgr.DownloadUpdatesAsync(newVersion);

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

        public static void ApplyAndRestart(UpdateInfo updateInfo)
        {
            var mgr = CreateManager();
            var restartArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
            mgr.ApplyUpdatesAndRestart(updateInfo, restartArgs);
        }
    }
}
