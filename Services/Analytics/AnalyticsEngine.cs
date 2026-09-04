using System;
using System.Collections.Generic;
using System.Linq;

namespace FastApp.Services.Analytics
{
    /// <summary>Everything the analytics page is given, in one object.</summary>
    public sealed class AnalyticsReport
    {
        public bool HasEnoughHistory { get; init; }
        public string NotYet { get; init; }

        /// <summary>
        /// The history could not be read, or could not be read in full. Kept
        /// separate from "there is nothing here" because they are different
        /// facts and only one of them is the reader's doing.
        /// </summary>
        public bool CouldNotRead { get; init; }
        public string Problem { get; init; }

        public int DaysOfHistory { get; init; }
        public int BaselineDays { get; init; }
        public string Period { get; init; }

        public string ActiveTotal { get; init; }
        public double ActiveHours { get; init; }
        public double ActiveChangePercent { get; init; }
        public bool HasComparison { get; init; }

        public string Headline { get; init; }
        public List<object> Insights { get; init; } = new();
        public List<object> TopApps { get; init; } = new();
        public List<double> HourShape { get; init; } = new();

        /// <summary>What kind of time it was, when enough of it carries a category.</summary>
        public List<object> Categories { get; init; } = new();
        public double CategoryCoverage { get; init; }
        public bool HasCategories { get; init; }

        /// <summary>
        /// Findings that were made and then not printed, because something
        /// stronger had already said the same thing. Counted rather than hidden.
        /// </summary>
        public int AlsoFound { get; init; }

        /// <summary>What this engine can and cannot see. See <see cref="Coverage"/>.</summary>
        public List<string> Understands { get; init; } = new();
        public List<string> DoesNotUnderstand { get; init; } = new();
    }

    /// <summary>
    /// Turns the visit stream into a report.
    ///
    /// The order is deliberate and is the whole design: read, profile, baseline,
    /// detect, rank, and only then hand anything to a page. Nothing is computed
    /// in the browser, because an insight assembled from three numbers in
    /// JavaScript has no evidence attached to it and no way to say how sure it
    /// is.
    ///
    /// Local throughout. This is a behavioural profile of a person, built from
    /// every application they open; it is read from a database on their machine
    /// and returned to a page served from their machine, and nothing here
    /// reaches the network.
    /// </summary>
    public static class AnalyticsEngine
    {
        /// <summary>How much of the recent past the report is about.</summary>
        public const int RecentDays = 7;

        /// <summary>Insights shown. More are generated; the rest are not worth the room.</summary>
        public const int MaxInsights = 7;

        public static AnalyticsReport Build(DateTime today)
        {
            DateTime recentFrom = today.Date.AddDays(-(RecentDays - 1));
            DateTime historyFrom = today.Date.AddDays(-(Baseline.WindowDays + RecentDays));
            DateTime end = today.Date.AddDays(1);

            var history = ActivityStream.Read(historyFrom, end);
            var all = history.Visits;

            // Said before anything is measured. A page that cannot read the
            // history has nothing to say about it, and the previous version's
            // answer -- "Nothing has been recorded yet" -- was a confident
            // falsehood on the one page whose premise is that every sentence
            // can be checked.
            if (history.CouldNotRead && all.Count == 0)
            {
                return new AnalyticsReport
                {
                    HasEnoughHistory = false,
                    CouldNotRead = true,
                    Problem = history.Problem,
                    Understands = Coverage.Understands,
                    DoesNotUnderstand = Coverage.DoesNot
                };
            }

            var categories = Categories.Load();
            var days = Baseline.Profile(all, categories);

            var recentDays = days.Where(d => d.Day >= recentFrom).ToList();
            var recentVisits = all.Where(v => v.Day >= recentFrom).ToList();
            var baseline = Baseline.Build(days, recentFrom);
            double coverage = categories.Coverage(all);

            if (days.Count == 0)
            {
                return new AnalyticsReport
                {
                    HasEnoughHistory = false,
                    DaysOfHistory = 0,
                    NotYet = "Nothing has been recorded yet. This page fills in as you use your computer.",
                    Understands = Coverage.Understands,
                    DoesNotUnderstand = Coverage.DoesNot
                };
            }

            var report = new List<object>();
            double activeHours = recentDays.Sum(d => d.Active.TotalHours);

            // Everything below the baseline threshold is still worth showing as
            // a description of the week; what it cannot do is call anything
            // unusual, because there is nothing yet to be unusual against.
            bool comparable = baseline.IsUsable;

            double changePercent = 0;
            if (comparable && baseline.MedianActive.TotalHours > 0 && recentDays.Count > 0)
            {
                double perDayNow = activeHours / recentDays.Count;
                changePercent = (perDayNow - baseline.MedianActive.TotalHours)
                                / baseline.MedianActive.TotalHours * 100.0;
            }

            // Two families, deliberately over different spans. What changed is
            // asked of the recent days against the baseline; what someone is
            // like is asked of everything there is, because a habit needs more
            // than a week to be one.
            var candidates = Detectors.All(recentVisits, recentDays, baseline);
            candidates.AddRange(Detectors.Patterns(recentVisits, all, recentDays, baseline));
            candidates.AddRange(Detectors.ByCategory(all, recentDays, baseline, categories, coverage));

            // Collapsed before it is cut. Taking the top seven of a list in
            // which four entries describe the same behaviour spends four slots
            // on one finding and reads, to somebody who does not know how the
            // page was built, like overwhelming evidence rather than repetition.
            var insights = Clustering.Reduce(candidates, MaxInsights);
            int alsoFound = Clustering.Suppressed(candidates, insights).Count;

            foreach (var insight in insights)
            {
                report.Add(new
                {
                    kind = insight.Kind,
                    title = insight.Title,
                    explanation = insight.Explanation,
                    evidence = insight.Evidence,
                    recommendation = insight.Recommendation,
                    apps = insight.Apps,
                    trend = insight.Trend,
                    period = insight.Period,
                    confidence = Math.Round(insight.Confidence, 2),
                    score = Math.Round(insight.Score, 3)
                });
            }

            // The applications the week was actually made of.
            var perApp = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
            foreach (var day in recentDays)
            {
                foreach (var (app, span) in day.PerApp)
                {
                    perApp.TryGetValue(app, out var so_far);
                    perApp[app] = so_far + span;
                }
            }

            var topApps = perApp.OrderByDescending(kv => kv.Value).Take(6).Select(kv => new
            {
                name = Detectors.Pretty(kv.Key),
                time = Detectors.Describe(kv.Value),
                hours = Math.Round(kv.Value.TotalHours, 2),
                share = activeHours > 0 ? Math.Round(kv.Value.TotalHours / activeHours * 100, 0) : 0,
                changePercent = ChangeAgainstBaseline(kv.Key, kv.Value, recentDays.Count, baseline)
            }).Cast<object>().ToList();

            // What kind of time it was, on the same terms as the applications
            // above. Withheld entirely below the coverage floor rather than
            // shown with a caveat: a breakdown of two thirds of somebody's week
            // invites them to read the missing third as free time.
            var categoryRows = new List<object>();
            bool hasCategories = coverage >= Analytics.Categories.MinimumCoverage;
            if (hasCategories)
            {
                var perCategory = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
                foreach (var day in recentDays)
                {
                    foreach (var (category, span) in day.PerCategory)
                    {
                        perCategory.TryGetValue(category, out var so_far);
                        perCategory[category] = so_far + span;
                    }
                }

                double categorised = perCategory.Values.Sum(v => v.TotalHours);
                categoryRows = perCategory.OrderByDescending(kv => kv.Value).Select(kv => (object)new
                {
                    name = kv.Key,
                    time = Detectors.Describe(kv.Value),
                    hours = Math.Round(kv.Value.TotalHours, 2),
                    share = categorised > 0 ? Math.Round(kv.Value.TotalHours / categorised * 100, 0) : 0,
                    changePercent = CategoryChange(kv.Key, kv.Value, recentDays.Count, baseline)
                }).ToList();
            }

            // Minutes per hour of the day across the recent period, for a shape
            // rather than a chart with axes: this says when the days happen.
            var hours = new double[24];
            foreach (var visit in recentVisits) Spread(visit, hours);

            return new AnalyticsReport
            {
                HasEnoughHistory = true,
                DaysOfHistory = days.Count,
                BaselineDays = baseline.DayCount,
                Period = $"the last {recentDays.Count} days",
                ActiveTotal = Detectors.Describe(TimeSpan.FromHours(activeHours)),
                ActiveHours = Math.Round(activeHours, 1),
                ActiveChangePercent = Math.Round(changePercent, 0),
                HasComparison = comparable,
                Headline = Headline(recentDays, insights, comparable),
                Insights = report,
                TopApps = topApps,
                HourShape = hours.Select(h => Math.Round(h, 1)).ToList(),
                Categories = categoryRows,
                CategoryCoverage = Math.Round(coverage * 100, 0),
                HasCategories = hasCategories,
                AlsoFound = alsoFound,
                CouldNotRead = history.CouldNotRead,
                Problem = history.Problem,
                Understands = Coverage.Understands,
                DoesNotUnderstand = Coverage.DoesNot,
                NotYet = comparable
                    ? null
                    : $"Still learning what is normal for you. {baseline.DayCount} of "
                      + $"{Baseline.MinimumDays} days needed before this page can say what is unusual."
            };
        }

        /// <summary>
        /// The same run, kept as facts rather than as a page.
        ///
        /// Built by asking Build for its report and holding on to the working:
        /// a question and the report must never be able to disagree, and the
        /// only way to guarantee that is for both to come out of one pass.
        /// </summary>
        public static FactSheet Facts(DateTime today)
        {
            DateTime recentFrom = today.Date.AddDays(-(RecentDays - 1));
            DateTime historyFrom = today.Date.AddDays(-(Baseline.WindowDays + RecentDays));
            DateTime end = today.Date.AddDays(1);

            var history = ActivityStream.Read(historyFrom, end);
            var all = history.Visits;
            var categories = Categories.Load();
            var days = Baseline.Profile(all, categories);
            var recentDays = days.Where(d => d.Day >= recentFrom).ToList();
            var recentVisits = all.Where(v => v.Day >= recentFrom).ToList();
            var baseline = Baseline.Build(days, recentFrom);
            double coverage = categories.Coverage(all);

            // Every insight, ranked but not collapsed. A question about an
            // application must still be able to reach a finding that lost its
            // slot on the page -- the clustering decides what is printed, not
            // what is known.
            var insights = Detectors.All(recentVisits, recentDays, baseline);
            insights.AddRange(Detectors.Patterns(recentVisits, all, recentDays, baseline));
            insights.AddRange(Detectors.ByCategory(all, recentDays, baseline, categories, coverage));
            insights = insights.OrderByDescending(i => i.Score).ToList();

            double activeHours = recentDays.Sum(d => d.Active.TotalHours);
            var rates = recentDays.Where(d => d.SwitchesPerHour > 0)
                                  .Select(d => d.SwitchesPerHour).ToList();

            var perApp = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
            foreach (var day in recentDays)
            {
                foreach (var (app, span) in day.PerApp)
                {
                    perApp.TryGetValue(app, out var so_far);
                    perApp[app] = so_far + span;
                }
            }

            // Pulled off the insights the detectors already produced rather
            // than recomputed, so an answer cannot contradict the card above it.
            var interrupter = insights.FirstOrDefault(i => i.Title.Contains("pulls you away"));
            var starts = insights.FirstOrDefault(i => i.Title.StartsWith("Your day usually starts"));
            var window = insights.FirstOrDefault(i => i.Title.Contains("stretches start between"));
            var categorySplit = new List<(string, double)>();
            if (coverage >= Analytics.Categories.MinimumCoverage)
            {
                var per = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
                foreach (var day in recentDays)
                {
                    foreach (var (category, span) in day.PerCategory)
                    {
                        per.TryGetValue(category, out var so_far);
                        per[category] = so_far + span;
                    }
                }
                categorySplit = per.OrderByDescending(kv => kv.Value)
                                   .Select(kv => (kv.Key, kv.Value.TotalHours)).ToList();
            }
            var parts = insights.FirstOrDefault(i => i.Title.StartsWith("Most of your computer time"));

            var dayParts = new List<(string, double)>();
            if (parts != null)
            {
                foreach (var line in parts.Evidence)
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) dayParts.Add((line[..colon].Trim(), 0));
                }
            }

            return new FactSheet
            {
                DaysOfHistory = days.Count,
                BaselineDays = baseline.DayCount,
                RecentDays = recentDays.Count,
                HasBaseline = baseline.IsUsable,
                BaselineConfidence = baseline.Confidence,

                RecentHours = activeHours,
                RecentHoursPerDay = recentDays.Count > 0 ? activeHours / recentDays.Count : 0,
                SwitchesPerHour = rates.Count > 0 ? Baseline.Median(rates) : 0,
                LongestStretchMinutes = recentDays.Count > 0
                    ? Baseline.Median(recentDays.Select(d => d.LongestStretch.TotalMinutes))
                    : 0,

                BaselineHoursPerDay = baseline.MedianActive.TotalHours,
                BaselineSwitchesPerHour = baseline.MedianSwitchesPerHour,
                BaselineLongestStretchMinutes = baseline.MedianLongestStretch.TotalMinutes,
                BaselineFirstUse = baseline.MedianFirstUse,

                TopApps = perApp.OrderByDescending(kv => kv.Value).Take(6)
                    .Select(kv => (Detectors.Pretty(kv.Key), kv.Value.TotalHours,
                                   ChangeAgainstBaseline(kv.Key, kv.Value, recentDays.Count, baseline)))
                    .ToList(),
                DayParts = dayParts,
                CouldNotRead = history.CouldNotRead,
                Problem = history.Problem,
                CategoryCoverage = coverage,
                CategorySplit = categorySplit,
                ContinuityWindow = window == null ? null
                    : window.Title.Replace("Your longest stretches start between ", ""),
                Interrupter = interrupter?.Apps.FirstOrDefault(),
                InterrupterShare = InterrupterShare(interrupter),
                StartsDayWith = starts?.Apps.FirstOrDefault(),
                BusiestDay = recentDays.Count == 0 ? null
                    : recentDays.OrderByDescending(d => d.Active).First().Day.ToString("dddd"),
                Insights = insights
            };
        }

        /// <summary>
        /// Read back out of the detector's own sentence rather than worked out
        /// again here. One place decides what the share is.
        /// </summary>
        private static double InterrupterShare(Insight interrupter)
        {
            if (interrupter == null) return 0;
            var evidence = interrupter.Evidence.FirstOrDefault(e => e.Contains(" of ") && e.Contains("interruptions"));
            if (evidence == null) return 0;

            var bits = evidence.Split(' ');
            if (bits.Length < 3) return 0;
            if (!double.TryParse(bits[0], out double top)) return 0;
            if (!double.TryParse(bits[2], out double total) || total <= 0) return 0;
            return top / total;
        }

        private static double CategoryChange(
            string category, TimeSpan recent, int recentDayCount, Baseline baseline)
        {
            if (!baseline.IsUsable || recentDayCount == 0) return 0;
            if (!baseline.MedianCategoryMinutes.TryGetValue(category, out double was) || was <= 1) return 0;

            double now = recent.TotalMinutes / recentDayCount;
            return Math.Round((now - was) / was * 100.0, 0);
        }

        private static double ChangeAgainstBaseline(
            string app, TimeSpan recent, int recentDayCount, Baseline baseline)
        {
            if (!baseline.IsUsable || recentDayCount == 0) return 0;
            if (!baseline.MedianAppMinutes.TryGetValue(app, out double was) || was <= 1) return 0;

            double now = recent.TotalMinutes / recentDayCount;
            return Math.Round((now - was) / was * 100.0, 0);
        }

        /// <summary>
        /// A visit can cross midnight or an hour boundary, so its minutes are
        /// spread across the hours it actually covers rather than dropped whole
        /// into the hour it began in.
        /// </summary>
        private static void Spread(Visit visit, double[] hours)
        {
            DateTime cursor = visit.Start;
            while (cursor < visit.End)
            {
                DateTime nextHour = cursor.Date.AddHours(cursor.Hour + 1);
                DateTime slice = nextHour < visit.End ? nextHour : visit.End;
                hours[cursor.Hour] += (slice - cursor).TotalMinutes;
                cursor = slice;
            }
        }

        /// <summary>
        /// The week in a sentence. Built from what was found rather than from a
        /// template with numbers dropped in, so it never claims more than the
        /// detectors did.
        /// </summary>
        private static string Headline(
            IReadOnlyList<DayProfile> recentDays, IReadOnlyList<Insight> insights, bool comparable)
        {
            if (recentDays.Count == 0) return "No activity recorded in this period.";

            var busiest = recentDays.OrderByDescending(d => d.Active).First();
            string day = busiest.Day.ToString("dddd");
            string longest = Detectors.Describe(recentDays.Max(d => d.LongestStretch));

            string opening =
                $"{day} was your busiest day, and your longest unbroken stretch anywhere in the period was {longest}.";

            if (!comparable) return opening;

            var lead = insights.FirstOrDefault(i => i.Kind == "change" || i.Kind == "continuity");
            return lead == null ? opening : opening + " " + lead.Explanation;
        }
    }
}
