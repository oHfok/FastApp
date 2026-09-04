using System;
using System.Collections.Generic;
using System.Linq;

namespace FastApp.Services.Analytics
{
    /// <summary>
    /// Phase two: the detectors that look for shape rather than change.
    ///
    /// Where the first set asks "is this week different", these ask "what is
    /// this person actually like" -- what pulls them away from something, what
    /// they open first, which applications are really one activity wearing two
    /// process names, and what each part of the day is for.
    ///
    /// One detector was prototyped and then deliberately not written: a general
    /// anomalous-day finder using median absolute deviation. On 51 real days the
    /// MAD is 4.4 hours against a median of 8.2, so the distribution is far too
    /// wide for it to find anything striking, and its strongest hits were simply
    /// days off. It would have produced noise wearing the clothes of insight,
    /// which is worse than saying nothing.
    /// </summary>
    public static partial class Detectors
    {
        public static List<Insight> Patterns(
            IReadOnlyList<Visit> recent,
            IReadOnlyList<Visit> everything,
            IReadOnlyList<DayProfile> recentDays,
            Baseline baseline)
        {
            var found = new List<Insight>();
            void Add(Insight i) { if (i != null) found.Add(i); }

            // Said once and passed down, because the engine loads a fixed
            // window rather than everything ever recorded, and a page built on
            // checkable evidence cannot carry "your whole history" as its one
            // unchecked claim.
            int span = everything.Count == 0
                ? 0
                : everything.Select(v => v.Day).Distinct().Count();
            string period = $"the last {span} days";

            Add(WhatInterrupts(everything, period));
            Add(WhatYouOpenFirst(everything, period));
            Add(TravellingCompanions(everything, period));
            Add(ShapeOfTheDay(everything, period));
            Add(WeekendDiffers(everything, period));

            return found;
        }

        // ------------------------------------------------------------------
        // What pulls you out of something you were settled into.
        //
        // The most useful thing this engine has found. Not "you get distracted"
        // -- which is a judgement, and one this program is in no position to
        // make -- but which application is on the other side of the move, how
        // often, and out of how many.
        // ------------------------------------------------------------------
        private static Insight WhatInterrupts(IReadOnlyList<Visit> visits, string period)
        {
            const int SettledMinutes = 10;

            var deliberate = visits.Deliberate().OrderBy(v => v.Start).ToList();
            var pulls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;

            for (int i = 0; i < deliberate.Count - 1; i++)
            {
                var settled = deliberate[i];
                var next = deliberate[i + 1];

                if (settled.Length < TimeSpan.FromMinutes(SettledMinutes)) continue;
                if (string.Equals(settled.App, next.App, StringComparison.OrdinalIgnoreCase)) continue;

                // Only a move, not a return after a break. A minute's gap means
                // the stretch ended on its own and the next thing is a fresh
                // start rather than the thing that ended it.
                if (next.Start - settled.End > TimeSpan.FromMinutes(1)) continue;

                pulls.TryGetValue(next.App, out int count);
                pulls[next.App] = count + 1;
                total++;
            }

            if (total < 25) return null;

            var top = pulls.OrderByDescending(p => p.Value).First();
            double share = (double)top.Value / total;

            // Below a fifth there is no single culprit, just ordinary movement.
            if (share < 0.20) return null;

            var insight = new Insight
            {
                Kind = "pattern",
                Title = $"{Pretty(top.Key)} is what usually pulls you away",
                Explanation =
                    $"When you have been settled in something for more than {SettledMinutes} minutes and then move, "
                    + $"{Pretty(top.Key)} is where you go {share * 100:0}% of the time.",
                Recommendation = share >= 0.35
                    ? $"If you want longer runs at something, {Pretty(top.Key)} is the one worth quietening first."
                    : null,
                Period = period,
                Confidence = Math.Min(0.92, 0.55 + total / 500.0),
                Importance = 0.9,
                Novelty = 0.75,
                Evidence =
                {
                    $"{top.Value} of {total} interruptions to a settled stretch",
                    $"counted only where the next application opened within a minute",
                    pulls.Count > 1
                        ? $"next most common: {Pretty(pulls.OrderByDescending(p => p.Value).Skip(1).First().Key)}"
                        : "nothing else came close"
                }
            };
            insight.Apps.Add(Pretty(top.Key));
            return insight;
        }

        // ------------------------------------------------------------------
        // The first thing you reach for.
        // ------------------------------------------------------------------
        private static Insight WhatYouOpenFirst(IReadOnlyList<Visit> visits, string period)
        {
            var firsts = visits.Deliberate()
                .GroupBy(v => v.Day)
                .Select(day => day.OrderBy(v => v.Start).First().App)
                .ToList();

            if (firsts.Count < 10) return null;

            var counts = firsts.GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var top = counts.OrderByDescending(kv => kv.Value).First();
            double share = (double)top.Value / firsts.Count;

            // A quarter of days is a habit; less is just the commonest of many.
            if (share < 0.25) return null;

            var insight = new Insight
            {
                Kind = "routine",
                Title = $"Your day usually starts with {Pretty(top.Key)}",
                Explanation =
                    $"It was the first thing you opened on {top.Value} of your last {firsts.Count} days.",
                Period = period,
                Confidence = Math.Min(0.9, 0.5 + firsts.Count / 100.0),
                Importance = 0.55,
                Novelty = 0.6,
                Evidence =
                {
                    $"first on {top.Value} of {firsts.Count} days ({share * 100:0}%)",
                    $"{counts.Count} different applications have started a day",
                    counts.Count > 1
                        ? $"next most often: {Pretty(counts.OrderByDescending(kv => kv.Value).Skip(1).First().Key)}"
                        : "always the same one"
                }
            };
            insight.Apps.Add(Pretty(top.Key));
            return insight;
        }

        // ------------------------------------------------------------------
        // Applications that are really one activity.
        //
        // Measured against the rarer of the pair, not against the total. Two
        // applications that are always used together matter whether that is
        // forty hours or four; dividing by the whole history would bury every
        // pair that is not one of the biggest.
        // ------------------------------------------------------------------
        private static Insight TravellingCompanions(IReadOnlyList<Visit> visits, string period)
        {
            var hours = new Dictionary<(DateTime Day, int Hour), HashSet<string>>();

            foreach (var visit in visits.Deliberate())
            {
                var key = (visit.Day, visit.Start.Hour);
                if (!hours.TryGetValue(key, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    hours[key] = set;
                }
                set.Add(visit.App);
            }

            var alone = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var together = new Dictionary<(string, string), int>();

            foreach (var apps in hours.Values)
            {
                foreach (var app in apps)
                {
                    alone.TryGetValue(app, out int seen);
                    alone[app] = seen + 1;
                }

                var sorted = apps.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    for (int j = i + 1; j < sorted.Count; j++)
                    {
                        var pair = (sorted[i], sorted[j]);
                        together.TryGetValue(pair, out int seen);
                        together[pair] = seen + 1;
                    }
                }
            }

            (string A, string B, int Count, double Tie)? best = null;
            foreach (var ((a, b), count) in together)
            {
                if (count < 15) continue;                    // too rare to be a habit
                int rarer = Math.Min(alone[a], alone[b]);
                if (rarer < 15) continue;
                double tie = (double)count / rarer;
                if (tie < 0.7) continue;                     // not really a pair
                if (best == null || tie > best.Value.Tie) best = (a, b, count, tie);
            }

            if (best == null) return null;
            var (first, second, hoursTogether, strength) = best.Value;

            var insight = new Insight
            {
                Kind = "routine",
                Title = $"{Pretty(first)} and {Pretty(second)} go together",
                Explanation =
                    $"In {strength * 100:0}% of the hours you used the less frequent of the two, "
                    + "the other was open in the same hour. They look like one activity rather than two.",
                Period = period,
                Confidence = Math.Min(0.9, 0.5 + hoursTogether / 100.0),
                Importance = 0.5,
                Novelty = 0.7,
                Evidence =
                {
                    $"{hoursTogether} hours contained both",
                    $"that is {strength * 100:0}% of the hours the rarer one appeared in at all",
                    "measured by the hour, so it means the same stretch of time rather than the same instant"
                }
            };
            insight.Apps.Add(Pretty(first));
            insight.Apps.Add(Pretty(second));
            return insight;
        }

        // ------------------------------------------------------------------
        // What each part of the day is for.
        // ------------------------------------------------------------------
        private static readonly (string Name, int From, int To)[] Parts =
        {
            ("morning", 6, 12), ("afternoon", 12, 18), ("evening", 18, 24), ("night", 0, 6)
        };

        private static Insight ShapeOfTheDay(IReadOnlyList<Visit> visits, string period)
        {
            var perPart = new Dictionary<string, Dictionary<string, double>>();
            var partTotal = new Dictionary<string, double>();

            foreach (var (name, from, to) in Parts)
            {
                perPart[name] = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                partTotal[name] = 0;
            }

            foreach (var visit in visits)
            {
                var part = Parts.FirstOrDefault(p => visit.Start.Hour >= p.From && visit.Start.Hour < p.To);
                if (part.Name == null) continue;

                perPart[part.Name].TryGetValue(visit.App, out double so_far);
                perPart[part.Name][visit.App] = so_far + visit.Length.TotalHours;
                partTotal[part.Name] += visit.Length.TotalHours;
            }

            double all = partTotal.Values.Sum();
            if (all < 20) return null;

            var biggest = partTotal.OrderByDescending(kv => kv.Value).First();
            double share = biggest.Value / all;

            var top = perPart[biggest.Key].OrderByDescending(kv => kv.Value).Take(3).ToList();
            if (top.Count == 0) return null;

            var insight = new Insight
            {
                Kind = "pattern",
                Title = $"Most of your computer time is in the {biggest.Key}",
                Explanation =
                    $"{share * 100:0}% of it, and it is mostly "
                    + string.Join(", ", top.Select(t => Pretty(t.Key))) + ".",
                Period = period,
                Confidence = 0.85,
                Importance = 0.5,
                Novelty = 0.4,
                Evidence = { $"{Describe(TimeSpan.FromHours(biggest.Value))} in the {biggest.Key}" }
            };

            foreach (var (name, _, _) in Parts)
            {
                if (partTotal[name] <= 0) continue;
                insight.Evidence.Add($"{name}: {Describe(TimeSpan.FromHours(partTotal[name]))}");
            }
            foreach (var app in top) insight.Apps.Add(Pretty(app.Key));
            return insight;
        }

        // ------------------------------------------------------------------
        // Weekends, but only when they are actually different.
        // ------------------------------------------------------------------
        private static Insight WeekendDiffers(IReadOnlyList<Visit> visits, string period)
        {
            var byDay = visits.GroupBy(v => v.Day)
                              .ToDictionary(g => g.Key, g => g.Sum(v => v.Length.TotalHours));

            var week = byDay.Where(kv => kv.Key.DayOfWeek != DayOfWeek.Saturday
                                      && kv.Key.DayOfWeek != DayOfWeek.Sunday)
                            .Select(kv => kv.Value).ToList();
            var end = byDay.Where(kv => kv.Key.DayOfWeek == DayOfWeek.Saturday
                                     || kv.Key.DayOfWeek == DayOfWeek.Sunday)
                           .Select(kv => kv.Value).ToList();

            if (week.Count < 8 || end.Count < 4) return null;

            double weekday = Baseline.Median(week), weekend = Baseline.Median(end);
            if (weekday <= 0) return null;

            double change = (weekend - weekday) / weekday;

            // On the history this was written against the two differ by about a
            // tenth, which is not a difference worth a card. Staying quiet when
            // there is nothing to say is a feature, not a gap.
            if (Math.Abs(change) < 0.25) return null;

            bool more = change > 0;
            return new Insight
            {
                Kind = "pattern",
                Title = more ? "Your weekends are busier than your weekdays"
                             : "Your weekends are quieter than your weekdays",
                Explanation =
                    $"A typical weekend day is {Describe(TimeSpan.FromHours(weekend))} against "
                    + $"{Describe(TimeSpan.FromHours(weekday))} on a weekday.",
                Period = period,
                Confidence = 0.8,
                Importance = 0.45,
                Novelty = 0.5,
                Evidence =
                {
                    $"weekday median {Describe(TimeSpan.FromHours(weekday))} over {week.Count} days",
                    $"weekend median {Describe(TimeSpan.FromHours(weekend))} over {end.Count} days",
                    $"{Math.Abs(change) * 100:0}% {(more ? "more" : "less")}"
                }
            };
        }
    }
}
