using System;
using System.Collections.Generic;
using System.Linq;

namespace FastApp.Services.Analytics
{
    /// <summary>
    /// The detectors that look at what kind of thing was being done rather than
    /// at which process was in front.
    ///
    /// Everything above this file operates on process names, which say where
    /// somebody was and not what they were doing. "Chrome" is a location. The
    /// mapping that turns it into "Browsing" already existed -- curated in the
    /// dashboard, resolved by CategoryMap -- and the analytics simply never read
    /// it.
    ///
    /// These stay quiet unless the mapping actually covers the time being
    /// described. A category breakdown over half the history is not a
    /// breakdown, and a detector firing on one would be reporting how much of a
    /// list somebody had filled in.
    /// </summary>
    public static partial class Detectors
    {
        public static List<Insight> ByCategory(
            IReadOnlyList<Visit> everything,
            IReadOnlyList<DayProfile> recentDays,
            Baseline baseline,
            Categories categories,
            double coverage)
        {
            var found = new List<Insight>();
            if (categories == null || coverage < Categories.MinimumCoverage) return found;

            void Add(Insight i) { if (i != null) found.Add(i); }

            Add(CategoryAgainstBaseline(recentDays, baseline, coverage));
            Add(ContinuityByCategory(everything, categories, coverage));

            return found;
        }

        // ------------------------------------------------------------------
        // A kind of activity that has grown or shrunk against what is usual.
        //
        // The application-level version of this already exists, and it is
        // noisier than it looks: somebody who moves from one browser to another
        // has changed nothing about their week, but two app-level detectors
        // will report an arrival and a departure between them. At the category
        // level that is correctly silent, because Browsing did not move.
        // ------------------------------------------------------------------
        private static Insight CategoryAgainstBaseline(
            IReadOnlyList<DayProfile> recentDays, Baseline baseline, double coverage)
        {
            if (!baseline.IsUsable || recentDays.Count < 3) return null;

            var recentMinutes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var day in recentDays)
            {
                foreach (var (category, span) in day.PerCategory)
                {
                    recentMinutes.TryGetValue(category, out double so_far);
                    recentMinutes[category] = so_far + span.TotalMinutes;
                }
            }
            if (recentMinutes.Count == 0) return null;

            (string Category, double Now, double Was, double Change)? best = null;

            foreach (var (category, total) in recentMinutes)
            {
                if (!baseline.MedianCategoryMinutes.TryGetValue(category, out double was)) continue;

                // A norm worth comparing to: a quarter of an hour a day, on at
                // least half the days behind the baseline. Below that a change
                // is a Tuesday rather than a change.
                if (was < 15) continue;
                if (!baseline.CategoryDays.TryGetValue(category, out int days)
                    || days < baseline.DayCount / 2) continue;

                double now = total / recentDays.Count;
                double change = (now - was) / was;
                if (Math.Abs(change) < 0.30) continue;

                if (best == null || Math.Abs(change) > Math.Abs(best.Value.Change))
                    best = (category, now, was, change);
            }

            if (best == null) return null;
            var (name, nowMinutes, wasMinutes, shift) = best.Value;
            bool up = shift > 0;

            return new Insight
            {
                Topic = "category-shift",
                Kind = "change",
                Title = up
                    ? $"You are spending more time on {name} than usual"
                    : $"You are spending less time on {name} than usual",
                Explanation =
                    $"About {Describe(TimeSpan.FromMinutes(nowMinutes))} a day, against your usual "
                    + $"{Describe(TimeSpan.FromMinutes(wasMinutes))}.",
                Trend = up ? "up" : "down",
                Period = $"the last {recentDays.Count} days",
                Confidence = baseline.Confidence * CoveragePenalty(coverage),
                Importance = 0.75,
                Novelty = Math.Min(1.0, Math.Abs(shift)),
                Evidence =
                {
                    $"{Describe(TimeSpan.FromMinutes(nowMinutes))} a day over the last {recentDays.Count} days",
                    $"{Describe(TimeSpan.FromMinutes(wasMinutes))} a day across the {baseline.DayCount} days before",
                    $"{Math.Abs(shift) * 100:0}% {(up ? "more" : "less")}",
                    $"counted over the {coverage * 100:0}% of your time that carries a category"
                }
            };
        }

        // ------------------------------------------------------------------
        // Which kinds of activity you settle into, and which you dip in and
        // out of.
        //
        // This is the insight the category mapping was worth wiring in for. It
        // cannot be expressed at the application level at all: it is a
        // statement about kinds of work, and the interesting version of it
        // needs both sides of the comparison to be categories.
        //
        // The statistic took three attempts and the first two were measured and
        // thrown away, which is worth recording because both looked right.
        //
        //   median visit length  -- Gaming 1.7 min against Utilities 0.4 min.
        //       True, and useless: it produced a card reading "a typical
        //       unbroken stretch of Gaming runs 1m, against 24s".
        //
        //   median run length, merging consecutive visits in one category --
        //       Gaming 2.8 min. Barely different, because the problem was never
        //       the merging. Run lengths are enormously skewed: Gaming's 207
        //       hours arrive in 1,036 runs whose median is under three minutes,
        //       so the median describes the many short runs while nearly all
        //       the time sits in the few long ones. A median is the wrong tool
        //       on a distribution shaped like that.
        //
        //   share of TIME spent in long runs -- Gaming 76%, Development 23%.
        //       This is the question anybody actually meant: not what a typical
        //       visit looks like, but where the hours go.
        //
        // Note what it does not say. Settling into something for longer is not
        // better, and a category dipped in and out of is not a distraction: a
        // person who checks messages in short bursts and reads for an hour is
        // doing neither of them wrong. The card reports where the time falls
        // and stops there.
        // ------------------------------------------------------------------

        /// <summary>A run this long or longer is time settled into something.</summary>
        private static readonly TimeSpan Settled = TimeSpan.FromMinutes(20);

        /// <summary>
        /// Consecutive visits within one category, further apart than this, are
        /// two runs rather than one. Two minutes covers stepping away to
        /// something uncategorised and back.
        /// </summary>
        private static readonly TimeSpan RunGap = TimeSpan.FromMinutes(2);

        /// <summary>The spread below which the categories are the same shape.</summary>
        private const double MinimumSpread = 0.25;

        private static Insight ContinuityByCategory(
            IReadOnlyList<Visit> visits, Categories categories, double coverage)
        {
            var runs = CategoryRuns(visits, categories);

            // Enough runs for the share to mean anything, and enough time for
            // the category to be part of the person's weeks rather than one
            // afternoon.
            var solid = runs
                .Where(kv => kv.Value.Count >= 15 && kv.Value.Sum(r => r.TotalMinutes) >= 120)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            if (solid.Count < 2) return null;

            var shares = solid.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Where(r => r >= Settled).Sum(r => r.TotalMinutes)
                      / kv.Value.Sum(r => r.TotalMinutes),
                StringComparer.OrdinalIgnoreCase);

            var ranked = shares.OrderByDescending(kv => kv.Value).ToList();
            var most = ranked[0];
            var least = ranked[ranked.Count - 1];

            if (most.Value - least.Value < MinimumSpread) return null;

            return new Insight
            {
                Topic = "continuity-by-category",
                Kind = "continuity",
                Title = $"{most.Key} arrives in long runs; {least.Key} does not",
                Explanation =
                    $"{most.Value * 100:0}% of your {most.Key} time comes in unbroken runs of twenty "
                    + $"minutes or more, against {least.Value * 100:0}% of your {least.Key} time. "
                    + "That is where the hours fall, not how much of each you do.",
                Period = $"the last {Span(visits)} days",
                Confidence = Math.Min(0.9, 0.5 + solid.Count / 20.0) * CoveragePenalty(coverage),
                Importance = 0.7,
                Novelty = 0.8,
                Evidence =
                {
                    $"{most.Key}: {most.Value * 100:0}% of {Describe(Total(solid[most.Key]))} "
                        + $"in runs of twenty minutes or more, across {solid[most.Key].Count} runs",
                    $"{least.Key}: {least.Value * 100:0}% of {Describe(Total(solid[least.Key]))}, "
                        + $"across {solid[least.Key].Count} runs",
                    $"half your {most.Key} time is in runs of "
                        + $"{Describe(HalfTimeAt(solid[most.Key]))} or longer",
                    $"measured by where the time falls rather than by a typical run, because run "
                        + "lengths are far too skewed for a middle to describe them",
                    $"counted over the {coverage * 100:0}% of your time that carries a category"
                }
            };
        }

        /// <summary>
        /// Consecutive deliberate visits belonging to one category, merged.
        /// Moving between two development tools is one stretch of development,
        /// not eight; an uncategorised application in between ends the run,
        /// because what happened during it is not known.
        /// </summary>
        private static Dictionary<string, List<TimeSpan>> CategoryRuns(
            IReadOnlyList<Visit> visits, Categories categories)
        {
            var runs = new Dictionary<string, List<TimeSpan>>(StringComparer.OrdinalIgnoreCase);

            string current = null;
            DateTime start = default, end = default;

            void Close()
            {
                if (current == null) return;
                if (!runs.TryGetValue(current, out var list))
                {
                    list = new List<TimeSpan>();
                    runs[current] = list;
                }
                list.Add(end - start);
                current = null;
            }

            foreach (var visit in visits.Deliberate().OrderBy(v => v.Start))
            {
                if (!categories.IsKnown(visit.App)) { Close(); continue; }

                string category = categories.For(visit.App);
                if (current != null
                    && string.Equals(current, category, StringComparison.OrdinalIgnoreCase)
                    && visit.Start - end <= RunGap)
                {
                    if (visit.End > end) end = visit.End;
                    continue;
                }

                Close();
                current = category;
                start = visit.Start;
                end = visit.End;
            }
            Close();

            return runs;
        }

        private static TimeSpan Total(List<TimeSpan> runs) =>
            TimeSpan.FromTicks(runs.Sum(r => r.Ticks));

        /// <summary>
        /// The run length at which half a category's time has accumulated,
        /// counting from the longest run down. A time-weighted median, and the
        /// honest answer to "how long are you usually in this for" on a
        /// distribution where most runs are short and most time is not.
        /// </summary>
        private static TimeSpan HalfTimeAt(List<TimeSpan> runs)
        {
            long half = runs.Sum(r => r.Ticks) / 2;
            long accumulated = 0;
            foreach (var run in runs.OrderByDescending(r => r.Ticks))
            {
                accumulated += run.Ticks;
                if (accumulated >= half) return run;
            }
            return TimeSpan.Zero;
        }

        /// <summary>
        /// Confidence is scaled by how much of the time actually carried a
        /// category, so a finding drawn from 62% coverage says so in its number
        /// rather than presenting itself as though it had seen everything.
        /// </summary>
        private static double CoveragePenalty(double coverage) =>
            Math.Max(0.5, Math.Min(1.0, coverage));

        private static int Span(IReadOnlyList<Visit> visits) =>
            visits.Count == 0 ? 0 : visits.Select(v => v.Day).Distinct().Count();
    }
}
