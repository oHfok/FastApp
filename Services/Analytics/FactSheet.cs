using System;
using System.Collections.Generic;
using System.Linq;

namespace FastApp.Services.Analytics
{
    /// <summary>
    /// Everything known about a person's computer use, reduced to facts.
    ///
    /// One place where every measured thing about a person's use is collected,
    /// so that answering a question is a matter of finding the right field
    /// rather than going back to the events and working something out again.
    /// Two answers derived separately from ten thousand rows will eventually
    /// disagree with each other; two answers reading the same field cannot.
    ///
    /// Every number here arrives with what it was measured against and how many
    /// days stand behind it. That is what lets an answer be wrong in the way a
    /// measurement is wrong, which is a kind of wrong the reader can check.
    /// </summary>
    public sealed class FactSheet
    {
        /// <summary>
        /// The history behind these numbers is incomplete or unreadable. Every
        /// answer has to lead with that: a fact sheet that quietly reports zero
        /// hours because the database would not open is the same confident
        /// falsehood the report used to print, only phrased as an answer to a
        /// question somebody asked.
        /// </summary>
        public bool CouldNotRead { get; init; }
        public string Problem { get; init; }

        public int DaysOfHistory { get; init; }
        public int BaselineDays { get; init; }
        public int RecentDays { get; init; }
        public bool HasBaseline { get; init; }
        public double BaselineConfidence { get; init; }

        // --- the recent period ---
        public double RecentHours { get; init; }
        public double RecentHoursPerDay { get; init; }
        public double SwitchesPerHour { get; init; }
        public double LongestStretchMinutes { get; init; }

        // --- what is usual ---
        public double BaselineHoursPerDay { get; init; }
        public double BaselineSwitchesPerHour { get; init; }
        public double BaselineLongestStretchMinutes { get; init; }
        public TimeSpan BaselineFirstUse { get; init; }

        // --- the shape of things ---
        public List<(string App, double Hours, double ChangePercent)> TopApps { get; init; } = new();
        public List<(string Part, double Hours)> DayParts { get; init; } = new();

        /// <summary>Hours per category over the recent period, richest first.</summary>
        public List<(string Category, double Hours)> CategorySplit { get; init; } = new();

        /// <summary>Share of recorded time carrying a curated category, 0-1.</summary>
        public double CategoryCoverage { get; init; }

        /// <summary>
        /// When the longest unbroken stretches start. Named for what it
        /// measures rather than for what somebody might have been doing: the
        /// database records that one window held the foreground, not that
        /// anybody was concentrating on it.
        /// </summary>
        public string ContinuityWindow { get; init; }
        public string Interrupter { get; init; }
        public double InterrupterShare { get; init; }
        public string StartsDayWith { get; init; }

        /// <summary>
        /// The application you keep returning to, how reliably, and how long
        /// you were away. Null when nothing was left often enough to say.
        /// </summary>
        public (string App, double Rate, TimeSpan Glance)? Anchor { get; init; }

        /// <summary>The strongest one-leads-to-another relationship, as its sentence.</summary>
        public string Successor { get; init; }
        public string BusiestDay { get; init; }

        /// <summary>Everything the detectors found, ranked. The evidence lives here.</summary>
        public List<Insight> Insights { get; init; } = new();

        public double Change(double now, double was) =>
            was <= 0 ? 0 : (now - was) / was * 100.0;
    }
}
