using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services.Analytics
{
    /// <summary>One uninterrupted stretch with a single application in front.</summary>
    public sealed class Visit
    {
        public string App { get; init; }
        public DateTime Start { get; init; }
        public DateTime End { get; init; }

        public TimeSpan Length => End - Start;
        public DateTime Day => Start.Date;

        /// <summary>
        /// Too short to have been a decision. See <see cref="FlickerCeiling"/>.
        /// </summary>
        public bool IsFlicker => Length < ActivityStream.FlickerCeiling;
    }

    /// <summary>
    /// The result of trying to read the history: what was found, and whether
    /// the finding can be trusted to be the whole of it.
    ///
    /// This exists because the previous signature could not tell the truth. It
    /// returned a list, swallowed every exception, and handed back an empty one
    /// either way -- so a locked, corrupt or missing database rendered as
    /// "Nothing has been recorded yet. This page fills in as you use your
    /// computer." A confident false statement, on the one page in this
    /// application whose entire premise is that every sentence can be checked.
    ///
    /// Three outcomes, and they are not the same thing:
    ///   read it, and there is nothing there    -> Visits empty, CouldNotRead false
    ///   could not read it at all               -> Visits empty, CouldNotRead true
    ///   read some of it and then failed        -> Visits partial, CouldNotRead true
    ///
    /// The third is the reason this is a flag beside the data rather than an
    /// exception: a failure part-way through a fortnight's rows leaves a real
    /// but incomplete stream, and drawing conclusions from it without saying so
    /// would be the same lie in a quieter voice.
    /// </summary>
    public sealed class ActivityHistory
    {
        public List<Visit> Visits { get; init; } = new();

        /// <summary>The stream is not known to be complete. Say so; conclude nothing.</summary>
        public bool CouldNotRead { get; init; }

        /// <summary>What went wrong, for the reader rather than for a log.</summary>
        public string Problem { get; init; }

        /// <summary>Read successfully, and there was genuinely nothing there.</summary>
        public bool IsGenuinelyEmpty => !CouldNotRead && Visits.Count == 0;

        public static ActivityHistory Of(List<Visit> visits) => new() { Visits = visits };

        public static ActivityHistory Unreadable(List<Visit> partial, string problem) =>
            new() { Visits = partial ?? new List<Visit>(), CouldNotRead = true, Problem = problem };
    }

    /// <summary>
    /// The cleaned stream of application visits that every other piece of the
    /// analytics reads. Nothing above this layer touches SessionLogs directly.
    ///
    /// SessionLogs is not a list of visits. Two things stand between it and one,
    /// and both were measured against 10,919 real rows covering 51 days rather
    /// than assumed:
    ///
    /// One visit can be several rows. The tracker closes and reopens a session
    /// as the window title changes, so a single stretch in one application
    /// arrives split. 10,919 rows are 9,317 visits; counting rows overstates
    /// activity by about 15% and invents switches that never happened.
    ///
    /// A third of what is left is flicker. 3,369 of those 9,317 visits are
    /// under ten seconds -- 66 a day -- and they are led by the Windows search
    /// box, Explorer and FastApp's own window. They are focus stealing itself
    /// for a moment, not a person changing their mind. Counted as switches they
    /// would dominate the figure and make it meaningless: 214 a day of noise
    /// against 117 a day of choices.
    ///
    /// So flicker is kept in the stream but marked, because it is real elapsed
    /// time and belongs in a total, and it is not a switch and does not belong
    /// in one. Each measurement says which it wants.
    /// </summary>
    public static class ActivityStream
    {
        /// <summary>
        /// Under this, a visit is focus moving rather than a person moving.
        /// Ten seconds sits in the trough of the real distribution: sub-ten is
        /// mostly the search box and notification steals, and above it the
        /// counts fall away smoothly into deliberate use.
        /// </summary>
        public static readonly TimeSpan FlickerCeiling = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Rows this close together are the same visit split by a title change,
        /// not two visits. The tracker writes them end-to-start, so this only
        /// has to tolerate rounding.
        /// </summary>
        private static readonly TimeSpan StitchGap = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Never analysed. FastApp is in the foreground exactly when someone is
        /// looking at their own statistics, and counting that would make
        /// reading the analytics change the analytics.
        /// </summary>
        public static readonly HashSet<string> Ignored =
            new(StringComparer.OrdinalIgnoreCase) { "Fastapp", "FastApp", "SYSTEM_PC" };

        /// <summary>
        /// Visits between two dates, oldest first. Its own short-lived context:
        /// the tracker's is written to every sixty seconds and a dashboard
        /// request has no business queueing behind it.
        /// </summary>
        public static ActivityHistory Read(DateTime fromInclusive, DateTime toExclusive)
        {
            var visits = new List<Visit>();

            try
            {
                using var db = new AppDbContext();

                var rows = db.SessionLogs.AsNoTracking()
                    .Where(s => s.StartTime >= fromInclusive && s.StartTime < toExclusive)
                    .OrderBy(s => s.StartTime)
                    .Select(s => new { s.AppName, s.StartTime, s.EndTime })
                    .ToList();

                string app = null;
                DateTime start = default, end = default;

                foreach (var row in rows)
                {
                    if (string.IsNullOrWhiteSpace(row.AppName)) continue;
                    if (Ignored.Contains(row.AppName)) continue;

                    // Same application, and close enough to be the same visit.
                    if (app != null
                        && string.Equals(app, row.AppName, StringComparison.OrdinalIgnoreCase)
                        && row.StartTime - end <= StitchGap)
                    {
                        if (row.EndTime > end) end = row.EndTime;
                        continue;
                    }

                    if (app != null) visits.Add(new Visit { App = app, Start = start, End = end });

                    app = row.AppName;
                    start = row.StartTime;
                    end = row.EndTime;
                }

                if (app != null) visits.Add(new Visit { App = app, Start = start, End = end });
            }
            catch (Exception ex)
            {
                // Reported, never disguised. Whatever was stitched before the
                // failure is handed back too -- it is real, and the flag beside
                // it is what stops anything drawing a conclusion from a
                // fragment.
                return ActivityHistory.Unreadable(Clean(visits), Describe(ex));
            }

            return ActivityHistory.Of(Clean(visits));
        }

        /// <summary>A row whose end precedes its start is not worth reasoning about.</summary>
        private static List<Visit> Clean(List<Visit> visits) =>
            visits.Where(v => v.End > v.Start).ToList();

        /// <summary>
        /// The failure in the reader's terms. The exception's own message is
        /// kept -- it is the only thing that distinguishes a locked file from a
        /// corrupt one, and somebody trying to get their history back needs it.
        /// </summary>
        private static string Describe(Exception ex)
        {
            string message = ex.Message?.Trim();
            if (string.IsNullOrEmpty(message)) message = ex.GetType().Name;
            return message.Length > 200 ? message[..200] + "..." : message;
        }

        /// <summary>The visits a person chose to make: flicker removed.</summary>
        public static IEnumerable<Visit> Deliberate(this IEnumerable<Visit> visits) =>
            visits.Where(v => !v.IsFlicker);

        /// <summary>
        /// Moves from one application to another. Built from deliberate visits
        /// only, so returning to what you were doing after the search box stole
        /// focus for two seconds is not counted as two switches.
        /// </summary>
        public static IEnumerable<(Visit From, Visit To)> Switches(this IEnumerable<Visit> visits)
        {
            Visit previous = null;
            foreach (var visit in visits.Deliberate())
            {
                if (previous != null
                    && !string.Equals(previous.App, visit.App, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (previous, visit);
                }
                previous = visit;
            }
        }
    }
}
