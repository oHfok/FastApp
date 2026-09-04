using System;
using System.Collections.Generic;

namespace FastApp.Services.Analytics
{
    /// <summary>
    /// One thing worth telling somebody about their own use of their computer.
    ///
    /// A first-class object rather than a formatted string, because an
    /// observation without its evidence is an assertion. "You seem distracted
    /// today" is not something a person can agree or disagree with; "you moved
    /// between applications 47% more often than your usual Tuesday, almost all
    /// of it between 14:00 and 16:00" is. Every detector has to produce the
    /// second kind, and the shape of this class is what makes that the easy
    /// path.
    /// </summary>
    public sealed class Insight
    {
        /// <summary>
        /// Which family this belongs to: pattern, change, routine, discovery,
        /// continuity.
        ///
        /// "continuity" was called "focus" until it was pointed out that the
        /// program cannot see focus. It can see that an application was in front
        /// for two hours without interruption, which is true of a person deep in
        /// a manuscript and equally true of a film. Naming the measurement after
        /// a conclusion it does not support was the one place this engine broke
        /// its own rule about adjectives the data has not earned -- and it broke
        /// it in the label, where every reader sees it.
        /// </summary>
        public string Kind { get; init; }

        /// <summary>
        /// What this insight is *about*, underneath its wording. Two detectors
        /// can measure one behaviour from different sides -- switching more
        /// often and having shorter unbroken runs are the same week described
        /// twice -- and a page that prints both has padded itself rather than
        /// found two things. Insights sharing a topic are collapsed to their
        /// strongest before ranking. See <see cref="Clustering"/>.
        /// </summary>
        public string Topic { get; init; }

        /// <summary>One line, in the reader's terms. Never a metric name.</summary>
        public string Title { get; init; }

        /// <summary>A sentence or two saying what was seen and against what.</summary>
        public string Explanation { get; init; }

        /// <summary>
        /// The measurements behind it, each readable on its own. Shown, not
        /// hidden behind a tooltip: this is the difference between being told
        /// something and being shown it.
        /// </summary>
        public List<string> Evidence { get; } = new();

        /// <summary>Only when the data earns one. Most insights do not.</summary>
        public string Recommendation { get; init; }

        /// <summary>Which applications it concerns, for the reader to recognise.</summary>
        public List<string> Apps { get; } = new();

        /// <summary>"up", "down", "new", "gone", or null where movement is not the point.</summary>
        public string Trend { get; init; }

        /// <summary>What was looked at: "this week", "today", "the last 28 days".</summary>
        public string Period { get; init; }

        /// <summary>
        /// How sure the detector is, 0 to 1. Driven by how much data stood
        /// behind it, not by how large the effect was.
        /// </summary>
        public double Confidence { get; init; } = 1.0;

        /// <summary>How much it would matter if true, 0 to 1.</summary>
        public double Importance { get; init; } = 0.5;

        /// <summary>
        /// How much of a departure it is from what the reader already knows,
        /// 0 to 1. A stable routine is worth saying once and is dull the fourth
        /// time; a change is the opposite.
        /// </summary>
        public double Novelty { get; init; } = 0.5;

        /// <summary>
        /// The ranking. Multiplied rather than added, so a confident but
        /// trivial finding and an important but shaky one both stay down: the
        /// page has room for a handful of insights and a weak one crowds out a
        /// strong one.
        /// </summary>
        public double Score => Importance * Confidence * Novelty;

        public DateTime DetectedAt { get; } = DateTime.Now;
    }
}
