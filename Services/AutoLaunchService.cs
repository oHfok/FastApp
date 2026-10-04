using FastApp.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FastApp.Services
{
    /// <summary>What happened to one app during an auto-launch pass.</summary>
    internal enum LaunchOutcome { Started, AlreadyRunning, NotFound, Failed }

    /// <summary>
    /// Opens the user's startup apps, in order, with their per-app delays, and
    /// reports what actually happened.
    ///
    /// Extracted from MainViewModel, which had grown to ~3,000 lines and owned
    /// this alongside the tracker, updates and the database. Nothing here
    /// needs the view model: it is handed the apps to launch and gives back a
    /// report, so choosing WHICH apps launch (a read of the UI-bound
    /// ManagedApps collection) stays with the caller.
    /// </summary>
    internal static class AutoLaunchService
    {
        /// <summary>
        /// Run one auto-launch pass over <paramref name="appsToLaunch"/>, which
        /// the caller has already filtered and put in startup order.
        /// </summary>
        /// <returns>
        /// A human-readable account of the pass, including which apps failed and
        /// why, or null when there was nothing to launch.
        /// </returns>
        public static async Task<string?> RunAsync(IReadOnlyList<AppItemModel> appsToLaunch, bool showProgress)
        {
            if (appsToLaunch.Count == 0) return null;

            var stopwatch = Stopwatch.StartNew();
            var outcomes = new List<(AppItemModel App, LaunchOutcome Outcome, string? Detail)>();

            // Enumerated once, not once per app. The old loop called
            // Process.GetProcesses() on every iteration -- N full process
            // enumerations, none of them disposed, during boot, which is exactly
            // when the machine can least afford it. Apps started by this pass are
            // added to the set as we go, which is what the re-enumeration was
            // actually for.
            var running = SnapshotProcessNames();

            // The window is told the whole sequence first, so it can show what
            // is coming rather than one name at a time.
            if (showProgress)
            {
                AutoLaunchProgressService.ShowPlan(
                    appsToLaunch.Select(a => a.DisplayNamePrimary).ToList());
            }

            for (int i = 0; i < appsToLaunch.Count; i++)
            {
                var app = appsToLaunch[i];

                if (app.LaunchDelaySeconds > 0)
                {
                    // Counted down rather than stated once: a row that has said
                    // "waiting 30s" for half a minute is indistinguishable from
                    // one that has hung.
                    for (int left = app.LaunchDelaySeconds; left > 0; left--)
                    {
                        if (showProgress)
                        {
                            AutoLaunchProgressService.SetStep(
                                i, LaunchStep.Waiting, $"in {left}s");
                        }
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                }

                if (showProgress)
                {
                    AutoLaunchProgressService.SetStep(i, LaunchStep.Opening);
                }

                string exeName = Path.GetFileNameWithoutExtension(app.ExecutablePath).ToLower();

                if (running.Contains(exeName))
                {
                    outcomes.Add((app, LaunchOutcome.AlreadyRunning, null));
                    if (showProgress)
                        AutoLaunchProgressService.SetStep(i, LaunchStep.AlreadyRunning);
                }
                else
                {
                    if (AppLauncher.TryStart(app, out string error))
                    {
                        running.Add(exeName);
                        outcomes.Add((app, LaunchOutcome.Started, null));
                        if (showProgress)
                            AutoLaunchProgressService.SetStep(i, LaunchStep.Started);
                    }
                    else
                    {
                        outcomes.Add((app, LaunchOutcome.Failed, error));
                        if (showProgress)
                            AutoLaunchProgressService.SetStep(i, LaunchStep.Failed);
                    }
                }

                // Small stagger so launches don't all fire in the same instant
                // (reduces launch-storm/race risk) and so the progress window is
                // actually readable. Skipped after the last one.
                if (i < appsToLaunch.Count - 1)
                {
                    await Task.Delay(350);
                }
            }

            stopwatch.Stop();
            return Report(outcomes, stopwatch.Elapsed, showProgress);
        }

        /// <summary>
        /// Lower-cased names of every running process. The Process objects are
        /// disposed: Process.GetProcesses() hands back one OS handle per process,
        /// and this used to run several times a pass without releasing any.
        /// </summary>
        private static HashSet<string> SnapshotProcessNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = Process.GetProcesses();
            try
            {
                foreach (var proc in all) names.Add(proc.ProcessName.ToLower());
            }
            finally
            {
                foreach (var proc in all) proc.Dispose();
            }
            return names;
        }

        private static string Report(
            List<(AppItemModel App, LaunchOutcome Outcome, string? Detail)> outcomes,
            TimeSpan elapsed,
            bool showProgress)
        {
            int started = outcomes.Count(o => o.Outcome == LaunchOutcome.Started);
            int already = outcomes.Count(o => o.Outcome == LaunchOutcome.AlreadyRunning);
            var problems = outcomes.Where(o => o.Outcome is LaunchOutcome.NotFound or LaunchOutcome.Failed).ToList();

            // One row per app per pass -- the progress window that already
            // shows this closes and forgets it, so "did Slack actually start
            // this morning" was unanswerable an hour later.
            DateTime runAt = DateTime.Now;
            foreach (var (app, outcome, detail) in outcomes)
            {
                AutoLaunchEventStore.RecordEvent(app.Name, outcome.ToString(), detail, runAt);
            }

            // "Opened N apps" used to count apps that were already running, so a
            // pass that opened nothing at all still claimed to have opened
            // everything. Each outcome is now counted as what it was.
            var parts = new List<string>();
            if (started > 0) parts.Add($"Opened {started}");
            if (already > 0) parts.Add($"{already} already running");
            if (problems.Count > 0) parts.Add($"{problems.Count} failed");
            if (parts.Count == 0) parts.Add("Nothing to open");

            string headline = $"{string.Join(", ", parts)} in {elapsed.TotalSeconds:F1}s";

            var report = new StringBuilder(headline);
            foreach (var (app, outcome, detail) in problems)
            {
                report.Append(Environment.NewLine);
                report.Append(outcome == LaunchOutcome.NotFound
                    ? $"{app.Name}: not found at {detail}"
                    : $"{app.Name}: {detail}");
            }

            // Failures were previously Debug.WriteLine only, which is to say
            // invisible in a release build. Until the notification work lands,
            // they at least reach a file and the summary popup.
            if (problems.Count > 0)
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(AppDbContext.GetDbFolder(), "autolaunch.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {report}{Environment.NewLine}");
                }
                catch { /* a log that cannot be written must not break startup */ }
            }

            if (showProgress)
            {
                AutoLaunchProgressService.ShowSummary(
                    problems.Count > 0 ? report.ToString() : headline);
            }

            // The progress popup is opt-in and self-dismisses in 2.5s, which is
            // no way to learn that an app you rely on did not start. A failure
            // is worth a notification whether or not that popup is enabled.
            if (problems.Count > 0)
            {
                NotificationService.Show(
                    problems.Count == 1 ? "A startup app did not open" : $"{problems.Count} startup apps did not open",
                    string.Join(", ", problems.Select(pr => pr.App.Name)),
                    NotificationSeverity.Warning,
                    new[] { new NotificationAction("show-window", "Open FastApp") });
            }

            return report.ToString();
        }
    }
}
