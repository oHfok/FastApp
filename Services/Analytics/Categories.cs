using System;
using System.Collections.Generic;
using System.Linq;

namespace FastApp.Services.Analytics
{
    /// <summary>
    /// What kind of thing each application is.
    ///
    /// Until now the analytics worked entirely at the process level, which says
    /// where somebody was and not what they were doing. "Chrome, 41 hours" is a
    /// location. "Browsing, 41 hours, against Development's 17" is a shape.
    ///
    /// Nothing here had to be invented: FastApp already keeps a curated mapping
    /// that the dashboard edits, and CategoryMap already resolves the two places
    /// a category can be written down. This is that map, read once per report
    /// and handed down, plus the one thing the analytics needs that the map does
    /// not provide -- an honest measure of how much it actually covers.
    ///
    /// Coverage is the point. A category breakdown over half the time recorded
    /// is not a breakdown, it is a guess with a bar chart on it, so the share of
    /// time carrying a curated category is measured and the section is withheld
    /// below <see cref="MinimumCoverage"/>.
    /// </summary>
    public sealed class Categories
    {
        /// <summary>Where an application nobody has categorised ends up.</summary>
        public const string Fallback = CategoryMap.Fallback;

        /// <summary>
        /// Below this share of time carrying a curated category, the breakdown
        /// says more about what has been filled in than about the person, and
        /// the page does not show it.
        /// </summary>
        public const double MinimumCoverage = 0.60;

        private readonly Dictionary<string, string> _map;

        private Categories(Dictionary<string, string> map)
        {
            _map = map ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Read once, at the top of a report. CategoryMap opens its own
        /// short-lived context and returns an empty map rather than throwing,
        /// which is the right failure here: no categories is a page without a
        /// category section, not a page that fails.
        /// </summary>
        public static Categories Load() => new(CategoryMap.Build());

        public static Categories None() =>
            new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        /// <summary>How many applications have been given a category at all.</summary>
        public int Known => _map.Count;

        public string For(string app) => CategoryMap.For(_map, app);

        /// <summary>Whether an application has been curated, as opposed to falling back.</summary>
        public bool IsKnown(string app) =>
            app != null && _map.TryGetValue(app, out var c) && !string.IsNullOrWhiteSpace(c);

        /// <summary>
        /// The share of elapsed time in a stream that carries a curated
        /// category. Measured on time rather than on application count on
        /// purpose: forty categorised minutes across two applications matter
        /// more than twenty uncategorised ones across ten.
        /// </summary>
        public double Coverage(IEnumerable<Visit> visits)
        {
            double all = 0, known = 0;
            foreach (var visit in visits)
            {
                double ticks = visit.Length.Ticks;
                all += ticks;
                if (IsKnown(visit.App)) known += ticks;
            }
            return all <= 0 ? 0 : known / all;
        }

        /// <summary>Total time per category across a stream.</summary>
        public Dictionary<string, TimeSpan> Split(IEnumerable<Visit> visits)
        {
            var split = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
            foreach (var visit in visits)
            {
                string category = For(visit.App);
                split.TryGetValue(category, out var so_far);
                split[category] = so_far + visit.Length;
            }
            return split;
        }
    }
}
