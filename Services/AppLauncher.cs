using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

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
                // The app updated itself into a new folder (Discord, GitHub Desktop and
                // every other Squirrel app) or was moved. Find where it lives now and
                // remember it, so this heals once rather than on every launch.
                string current = FindCurrentVersionedPath(app.ExecutablePath)
                    ?? RememberedPath(app.ExecutablePath);
                if (current != null)
                    app.ExecutablePath = current;   // saved through the normal property-changed path
                else
                {
                    error = IsPackagedPath(app.ExecutablePath)
                        ? $"{app.Name} has been updated and moved. Re-run Scan PC For Applications to relink it."
                        : $"Not found at {app.ExecutablePath}";
                    return false;
                }
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
        /// Squirrel-style installers keep each version in "rootpp-version\..." and delete
        /// the old folder on update, so a stored "...\Discordpp-1.0.9260\Discord.exe" stops
        /// existing the day Discord updates. Returns the same file inside the newest surviving
        /// app-* folder; failing that, the launcher stub the installer leaves in the root,
        /// which starts the current version itself. Null when the path is not of that shape
        /// or nothing suitable exists.
        /// </summary>
        internal static string FindCurrentVersionedPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;

                var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                int at = Array.FindIndex(parts, x => ParseVersion(x) != null);
                if (at <= 0 || at >= parts.Length - 1) return null;

                string root = string.Join(Path.DirectorySeparatorChar, parts, 0, at);
                string relative = string.Join(Path.DirectorySeparatorChar, parts, at + 1, parts.Length - at - 1);
                if (!Directory.Exists(root)) return null;

                var best = Directory.EnumerateDirectories(root, "app-*")
                    .Select(d => (dir: d, version: ParseVersion(Path.GetFileName(d))))
                    .Where(x => x.version != null && File.Exists(Path.Combine(x.dir, relative)))
                    .OrderByDescending(x => x.version)
                    .Select(x => Path.Combine(x.dir, relative))
                    .FirstOrDefault();
                if (best != null) return best;

                string stub = Path.Combine(root, Path.GetFileName(path));
                return File.Exists(stub) ? stub : null;
            }
            catch { return null; }
        }

        /// <summary>"app-1.0.9260" gives 1.0.9260; null for any other name.</summary>
        private static Version ParseVersion(string folder)
        {
            if (folder == null || !folder.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) return null;
            string v = folder.Substring(4);
            if (v.Length == 0 || !char.IsDigit(v[0])) return null;
            if (!v.Contains('.')) v += ".0";
            return Version.TryParse(v, out var parsed) ? parsed : null;
        }

        /// <summary>
        /// Where the tracker last saw a process of this name running, if that file is still
        /// there -- covers an app that was simply moved or reinstalled elsewhere.
        /// </summary>
        private static string RememberedPath(string missingPath)
        {
            string known = ExecutablePathStore.Get(Path.GetFileNameWithoutExtension(missingPath));
            return known != null && File.Exists(known) ? known : null;
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
