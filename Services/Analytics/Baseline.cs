using System;
using System.Collections.Generic;
using System.Linq;

namespace FastApp.Services.Analytics
{
    /// <summary>What one day looked like, reduced to the numbers worth comparing.</summary>
    public sealed class DayProfile
    {
        public DateTime Day { get; init; }
        public TimeSpan Active { get; init; }
        public int Switches { get; init; }
        public int Visits { get; init; }
        public TimeSpan LongestStretch { get; init; }
        public DateTime? FirstUse { get; init; }
        public DateTime? LastUse { get; init; }
        public Dictionary<string, TimeSpan> PerApp { get; init; } = new();

        public double SwitchesPerHour => Active.TotalHours > 0.25 ? Switches / Active.TotalHours : 0;
    }

    /// <summary>
    /// What is normal for this person.
    ///
    /// The whole analytics rests on comparing someone against themselves rather
    /// than against an invented ideal. There is no productive amount of time to
    /// spend in a browser; there is only more or less than you usually do, and
    /// only the second of those is a fact.
    ///
    /// Medians rather than means throughout. A single fourteen-hour day, or one
    /// day off, drags an average far enough to make the next fortnight's
    /// comparisons wrong; the middle of the distribution is what "usual" means
    /// in ordinary speech anyway.
    /// </summary>
    public sealed class Baseline
    {
        /// <summary>How far back "usually" reaches.</summary>
        public const int WindowDays = 28;

        /// <summary>
        /// Below this many days there is no baseline worth quoting, only a
        /// short history mistaken for a habit.
        /// </summary>
        public const int MinimumDays = 7;

        public List<DayProfile> Days { get; } = new();

        public bool IsUsable => Days.Count >= MinimumDays;
        public int DayCount => Days.Count;

        public TimeSpan MedianActive { get; private set; }
        public double MedianSwitchesPerHour { get; private set; }
        public TimeSpan MedianLongestStretch { get; private set; }
        public TimeSpan MedianFirstUse { get; private set; }

        /// <summary>Median minutes a day, per application, across the window.</summary>
        public Dictionary<string, double> MedianAppMinutes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Days the application was used at all, per application. An app used
        /// on two days out of twenty-eight has no daily norm worth comparing to.
        /// </summary>
        public Dictionary<string, int> AppDays { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Build the profile of every day in a stream, newest last.
        /// </summary>
        public static List<DayProfile> Profile(IEnumerable<Visit> visits)
        {
            var byDay = visits.GroupBy(v => v.Day).OrderBy(g => g.Key);
            var profiles = new List<DayProfile>();

            foreach (var day in byDay)
            {
                var ordered = day.OrderBy(v => v.Start).ToList();
                var deliberate = ordered.Deliberate().ToList();

                var perApp = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
                foreach (var visit in ordered)
                {
                    perApp.TryGetValue(visit.App, out var so_far);
                    perApp[visit.App] = so_far + visit.Length;
                }

                profiles.Add(new DayProfile
                {
                    Day = day.Key,
                    // Every visit, flicker included: it is time that passed.
                    Active = TimeSpan.FromTicks(ordered.Sum(v => v.Length.Ticks)),
                    Switches = ordered.Switches().Count(),
                    Visits = deliberate.Count,
                    LongestStretch = deliberate.Count == 0
                        ? TimeSpan.Zero
                        : TimeSpan.FromTicks(deliberate.Max(v => v.Length.Ticks)),
                    FirstUse = ordered.FirstOrDefault()?.Start,
                    LastUse = ordered.LastOrDefault()?.End,
                    PerApp = perApp
                });
            }

            return profiles;
        }

        /// <summary>
        /// The usual shape of a day, from the days before <paramref name="excludeFrom"/>.
        /// Excluded on purpose: a week cannot be unusual compared with a norm it
        /// is itself part of, and including it drags the baseline toward
        /// whatever is being judged.
        /// </summary>
        public static Baseline Build(IEnumerable<DayProfile> days, DateTime excludeFrom)
        {
            var baseline = new Baseline();
            baseline.Days.AddRange(days.Where(d => d.Day < excludeFrom).OrderBy(d => d.Day));

            if (baseline.Days.Count == 0) return baseline;

            baseline.MedianActive = TimeSpan.FromTicks(
                (long)Median(baseline.Days.Select(d => (double)d.Active.Ticks)));
            baseline.MedianLongestStretch = TimeSpan.FromTicks(
                (long)Median(baseline.Days.Select(d => (double)d.LongestStretch.Ticks)));

            var rates = baseline.Days.Where(d => d.SwitchesPerHour > 0)
                                     .Select(d => d.SwitchesPerHour).ToList();
            baseline.MedianSwitchesPerHour = rates.Count > 0 ? Median(rates) : 0;

            var starts = baseline.Days.Where(d => d.FirstUse.HasValue)
                                      .Select(d => d.FirstUse.Value.TimeOfDay.TotalMinutes).ToList();
            baseline.MedianFirstUse = starts.Count > 0
                ? TimeSpan.FromMinutes(Median(starts))
                : TimeSpan.Zero;

            // Per app, the median is taken over the days it was actually used.
            // Counting zeroes for every untouched day would put the median of
            // anything used a few times a week at zero, and then every use of it
            // would read as a dramatic increase.
            var apps = baseline.Days.SelectMany(d => d.PerApp.Keys)
                                    .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var app in apps)
            {
                var minutes = baseline.Days
                    .Where(d => d.PerApp.ContainsKey(app))
                    .Select(d => d.PerApp[app].TotalMinutes)
                    .ToList();

                baseline.AppDays[app] = minutes.Count;
                baseline.MedianAppMinutes[app] = Median(minutes);
            }

            return baseline;
        }

        public static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return 0;
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) / 2.0;
        }

        /// <summary>
        /// How sure a comparison against this baseline can be. Grows with the
        /// number of days behind it and stops at 0.95, because a personal
        /// baseline over four weeks is never certainty.
        /// </summary>
        public double Confidence =>
            !IsUsable ? 0 : Math.Min(0.95, 0.5 + 0.45 * Math.Min(1.0, (DayCount - MinimumDays) / 14.0));
    }
}
