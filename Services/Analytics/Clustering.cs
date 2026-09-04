using System;
using System.Collections.Generic;
using System.Linq;

namespace FastApp.Services.Analytics
{
    /// <summary>
    /// Choosing which findings to print, when several of them are the same
    /// finding.
    ///
    /// The detectors are independent by design -- each asks one question and
    /// knows nothing about the others -- and that independence has a cost
    /// nobody notices until the page is full. A week in which somebody starts
    /// leaving a game open can fire six of them at once:
    ///
    ///     You are moving between applications more than usual
    ///     Your longest daily stretch is getting shorter
    ///     Discord is what usually pulls you away
    ///     You move between Discord and Chrome more than any other pair
    ///     Discord and Chrome go together
    ///     Your day usually starts with Discord
    ///
    /// Every line is true, every line carries its own evidence, and the page is
    /// still bad: it has said one thing six times and spent all seven slots
    /// doing it. Worse, repetition reads as emphasis, so a reader comes away
    /// believing the engine found overwhelming evidence of something rather
    /// than one thing from six angles.
    ///
    /// Two rules, both deliberately blunt enough to explain to somebody who
    /// asks why a card they expected is missing.
    /// </summary>
    public static class Clustering
    {
        /// <summary>
        /// How many kept insights may name the same application before the rest
        /// are held back.
        ///
        /// One would be too strict: a person's main application legitimately
        /// turns up in what interrupts them and in what they open first, and
        /// those are genuinely different facts about it. Three is no limit at
        /// all on a seven-card page.
        /// </summary>
        public const int MaxCardsPerApp = 2;

        /// <summary>
        /// Rank, collapse, and take. Ordering happens first so that the
        /// survivor of any collision is always the strongest of it, rather than
        /// whichever detector happened to run earlier.
        /// </summary>
        public static List<Insight> Reduce(IEnumerable<Insight> candidates, int max)
        {
            var kept = new List<Insight>();
            var topicsUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var appCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var insight in candidates.OrderByDescending(i => i.Score))
            {
                if (kept.Count >= max) break;

                // Rule one: one card per topic. Two detectors sharing a topic
                // measured the same behaviour from different sides, and the
                // stronger of them has already said it.
                if (!string.IsNullOrEmpty(insight.Topic) && !topicsUsed.Add(insight.Topic)) continue;

                // Rule two: no application may headline the page. This is the
                // one that catches the Discord pile-up above, where four
                // detectors with four different topics all had the same subject.
                if (insight.Apps.Any(app => Count(appCount, app) >= MaxCardsPerApp)) continue;

                kept.Add(insight);
                foreach (var app in insight.Apps.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    appCount[app] = Count(appCount, app) + 1;
                }
            }

            return kept;
        }

        /// <summary>
        /// What was found but not printed, and why -- so the page can say "and
        /// four related observations" instead of silently discarding them, and
        /// so a question about an application can still reach a finding that
        /// lost its slot.
        /// </summary>
        public static List<Insight> Suppressed(IEnumerable<Insight> candidates, List<Insight> kept)
        {
            var keptSet = new HashSet<Insight>(kept);
            return candidates.Where(i => !keptSet.Contains(i))
                             .OrderByDescending(i => i.Score)
                             .ToList();
        }

        private static int Count(Dictionary<string, int> counts, string app) =>
            app != null && counts.TryGetValue(app, out int n) ? n : 0;
    }
}
