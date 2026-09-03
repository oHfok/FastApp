using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// Applications FastApp has recorded you using but which you have never
    /// added to it.
    ///
    /// FastApp tracks everything that takes the foreground, so it knows about
    /// far more applications than the handful anyone bothers to add -- on the
    /// machine this was written for, 144 against 8. Those 144 are a much better
    /// source of "what might you want a hotkey for" than scanning the Start
    /// menu, because they are things you demonstrably use rather than things
    /// that happen to be installed.
    /// </summary>
    public static class TrackedApps
    {
        public sealed record Candidate(string Name, int Minutes);

        /// <summary>
        /// Below this a name is a one-off or a background process that briefly
        /// owned the foreground, not something worth offering to add.
        /// </summary>
        private const int MinimumMinutes = 10;

        public static List<Candidate> Unmanaged(IEnumerable<string> managedNames)
        {
            var managed = new HashSet<string>(managedNames ?? Enumerable.Empty<string>(),
                                              StringComparer.OrdinalIgnoreCase);

            try
            {
                using var db = new AppDbContext();

                var hidden = db.HiddenApps.AsNoTracking()
                    .Select(h => h.AppName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return db.DailyLogs.AsNoTracking()
                    .Where(l => l.AppName != "SYSTEM_PC")
                    .GroupBy(l => l.AppName)
                    .Select(g => new { Name = g.Key, Ticks = g.Sum(l => l.TimeFocusedTicks ?? 0L) })
                    .ToList()
                    .Select(row => new Candidate(row.Name, (int)TimeSpan.FromTicks(row.Ticks).TotalMinutes))
                    .Where(c => c.Minutes >= MinimumMinutes
                                && !managed.Contains(c.Name)
                                && !hidden.Contains(c.Name))
                    .OrderByDescending(c => c.Minutes)
                    .ToList();
            }
            catch
            {
                // No history is not an error here; it only means there is
                // nothing to suggest.
                return new List<Candidate>();
            }
        }

        /// <summary>
        /// Find the executable behind a tracked name.
        ///
        /// The logs record only the process name -- the tracker capitalises the
        /// lowercased process name, so "Javaw" is the process "javaw" -- and
        /// adding an app needs a path. Two ways to get one, in order of how much
        /// they can be trusted: ask the process itself if it happens to be
        /// running, then look through what is installed. Neither is guaranteed,
        /// which is why the caller has to handle null.
        /// </summary>
        public static string ResolvePath(string trackedName, IEnumerable<string> installedPaths = null)
        {
            if (string.IsNullOrWhiteSpace(trackedName)) return null;

            Process[] running;
            try { running = Process.GetProcesses(); }
            catch { running = Array.Empty<Process>(); }

            try
            {
                foreach (var process in running)
                {
                    try
                    {
                        if (!string.Equals(process.ProcessName, trackedName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string path = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path)) return path;
                    }
                    catch
                    {
                        // A protected or exiting process answers nothing useful.
                    }
                }
            }
            finally
            {
                foreach (var process in running) process.Dispose();
            }

            if (installedPaths == null) return null;

            foreach (var path in installedPaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (string.Equals(Path.GetFileNameWithoutExtension(path),
                                  trackedName, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            return null;
        }
    }
}
