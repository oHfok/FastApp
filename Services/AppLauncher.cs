using System;
using System.Diagnostics;
using System.IO;

namespace FastApp.Services
{
    /// <summary>
    /// The one place an app gets started. Auto-launch and Launch-App hotkeys used
    /// to have separate copies of this, which had already drifted -- one set a
    /// working directory and passed arguments, the other did neither.
    /// </summary>
    public static class AppLauncher
    {
        /// <summary>
        /// Starts <paramref name="app"/>, preferring its AppUserModelID when it is
        /// a packaged (Store) app. Returns false with a reason rather than
        /// throwing, so callers can say what went wrong.
        /// </summary>
        public static bool TryStart(ViewModels.AppItemModel app, out string error)
        {
            error = null;

            // 1. A packaged app is launched through the app model, never by path.
            //    Its folder is version-stamped and replaced on every update, and
            //    some packages deny read access to the executable outright.
            string aumid = ResolveAumid(app);
            if (!string.IsNullOrWhiteSpace(aumid))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "shell:AppsFolder\\" + aumid,
                        UseShellExecute = true
                    });
                    return true;
                }
                catch (Exception ex)
                {
                    // Fall through to the path below, which may still work.
                    error = ex.Message;
                }
            }

            if (string.IsNullOrEmpty(app.ExecutablePath))
            {
                error = "No executable is set for this entry.";
                return false;
            }

            if (!File.Exists(app.ExecutablePath))
            {
                error = IsPackagedPath(app.ExecutablePath)
                    ? $"{app.Name} has been updated and moved. Re-run Scan PC For Applications to relink it."
                    : $"Not found at {app.ExecutablePath}";
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = app.ExecutablePath,
                    WorkingDirectory = Path.GetDirectoryName(app.ExecutablePath),
                    UseShellExecute = true
                };
                if (!string.IsNullOrWhiteSpace(app.LaunchArguments))
                    psi.Arguments = app.LaunchArguments;

                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// The stored AUMID, or one recovered from a stale WindowsApps path.
        ///
        /// The recovery matters for entries added before the AUMID was stored:
        /// they hold a path into a package folder that has since been replaced,
        /// and without this they stay broken until the user re-runs the scanner.
        /// Recovered values are written back to the entry so the work happens
        /// once.
        /// </summary>
        private static string ResolveAumid(ViewModels.AppItemModel app)
        {
            if (!string.IsNullOrWhiteSpace(app.PackagedAppId)) return app.PackagedAppId;
            if (!IsPackagedPath(app.ExecutablePath)) return null;

            string family = PackageFamilyFromPath(app.ExecutablePath);
            if (family == null) return null;

            string recovered = AppScannerService.TryResolveAumid(family);
            if (recovered == null) return null;

            // Persisted through the normal property-changed save path.
            app.PackagedAppId = recovered;
            return recovered;
        }

        private static bool IsPackagedPath(string path) =>
            !string.IsNullOrEmpty(path) &&
            path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// "…\WindowsApps\Claude_1.40609.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe"
        /// yields "Claude_pzs8sxrjxfjjc" -- the package family name, which is the
        /// one part of that folder name that does not change between versions.
        /// </summary>
        internal static string PackageFamilyFromPath(string path)
        {
            try
            {
                var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (var part in parts)
                {
                    int hashAt = part.IndexOf("__", StringComparison.Ordinal);
                    if (hashAt <= 0) continue;

                    string publisherHash = part.Substring(hashAt + 2);
                    string name = part.Substring(0, hashAt).Split('_')[0];
                    if (name.Length > 0 && publisherHash.Length > 0)
                        return $"{name}_{publisherHash}";
                }
            }
            catch { /* an unparseable path is simply not a packaged one */ }

            return null;
        }
    }
}
