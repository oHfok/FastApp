using System;
using System.Collections.Generic;
using System.Linq;

namespace FastApp.Services.Analytics
{
    /// <summary>
    /// The detectors that read <see cref="Transitions"/>: what holds you, and
    /// which applications genuinely lead to which.
    ///
    /// Both are about relationships between applications, which nothing before
    /// them could express. The existing pair detectors come close and miss:
    /// CommonestTransition is undirected and ranked by frequency, so it reports
    /// whichever two applications are biggest; TravellingCompanions measures
    /// being open in the same hour, which is not the same as one leading to the
    /// other. Neither can see a launcher opening a game, and neither can see
    /// that leaving something is usually a round trip.
    /// </summary>
    public static partial class Detectors
    {
        public static List<Insight> Sequences(Transitions moves, string period)
        {
            var found = new List<Insight>();
            if (moves == null) return found;

            void Add(Insight i) { if (i != null) found.Add(i); }

            Add(WhatHoldsYou(moves, period));
            Add(DistinctiveSuccessor(moves, period));

            return found;
        }

        // ------------------------------------------------------------------
        // The application you keep coming back to.
        //
        // Worded as glancing rather than as leaving, and the data is the reason.
        // The median round trip in the measured history is between 24 seconds
        // and two minutes: somebody checks a message and goes back to what they
        // were doing. "You leave Valorant and return" suggests a decision that
        // was never made.
        //
        // Distinct from WhatInterrupts, which requires a settled stretch of ten
        // minutes and treats the move as the thing that ended it. This is the
        // opposite case -- the move that ends nothing.
        // ------------------------------------------------------------------
        private static Insight WhatHoldsYou(Transitions moves, string period)
        {
            // Measured: return rates ran 29%-85% with a median of 57%, so a
            // threshold at 55% would have fired on more than half the
            // applications and meant nothing. Seventy is the top of the range.
            const double Holds = 0.70;

            // Among those that clear the bar, the one left MOST often rather
            // than the one with the highest rate. Three applications cleared it
            // in the measured history -- 85% over 107 departures, 80% over 123,
            // and 77% over 391 -- and picking on rate alone would report the
            // first, which is both the thinnest evidence and a small corner of
            // the person's weeks. The claim is that something holds you; the
            // one you actually leave and return to hundreds of times is the
            // truer answer to it.
            string best = null;
            int mostDepartures = 0;

            foreach (var app in moves.Apps)
            {
                int departures = moves.Departures(app);
                if (departures < Transitions.MinimumDepartures) continue;
                if (moves.ReturnRate(app) < Holds) continue;
                if (departures > mostDepartures) { mostDepartures = departures; best = app; }
            }

            if (best == null) return null;
            double bestRate = moves.ReturnRate(best);

            var (destination, times) = moves.CommonestDestination(best);
            if (destination == null) return null;

            int left = moves.Departures(best);
            int back = moves.Returns(best);
            var glance = moves.MedianGlance(best);

            var insight = new Insight
            {
                Topic = "anchoring",
                Kind = "continuity",
                Title = $"{Pretty(best)} keeps hold of you",
                Explanation =
                    $"When you move away from it you are back within one step {bestRate * 100:0}% of the time, "
                    + $"typically after {Describe(glance)}. Most often you have gone to {Pretty(destination)} "
                    + "and come straight back.",
                Period = period,
                Confidence = Math.Min(0.85, 0.45 + left / 400.0),
                Importance = 0.65,
                Novelty = 0.75,
                Evidence =
                {
                    $"left {left} times, back again immediately on {back} of them",
                    $"median time away: {Describe(glance)}",
                    $"{Pretty(destination)} was where you went on {times} of those departures",
                    "counted on moves of ten seconds or more, so this is not focus flickering"
                }
            };
            insight.Apps.Add(Pretty(best));
            insight.Apps.Add(Pretty(destination));
            return insight;
        }

        // ------------------------------------------------------------------
        // One application that genuinely leads to another.
        //
        // Ranked by lift rather than by frequency, and that is the whole point.
        // The commonest thing after nearly everything in the measured history
        // is Discord -- because Discord is the commonest thing, full stop. A
        // frequency ranking rediscovers that over and over and never finds
        // Prism Launcher opening Javaw, which happens 23 times and is 8.9 times
        // more often than chance would put them together.
        // ------------------------------------------------------------------
        private static Insight DistinctiveSuccessor(Transitions moves, string period)
        {
            // Measured: 75 directed pairs cleared the sample floors, and
            // exactly three cleared both of these. That is the right number for
            // a page with seven slots.
            const double MinimumLift = 3.0;
            const double MinimumShare = 0.35;
            const int MinimumMoves = 10;

            (string From, string To, int N, int Total, double Share, double Lift)? best = null;

            foreach (var from in moves.Apps)
            {
                var destinations = moves.DestinationsFrom(from);
                int total = destinations.Values.Sum();
                if (total < 15) continue;

                foreach (var (to, n) in destinations)
                {
                    if (n < MinimumMoves) continue;

                    double dominance = (double)n / total;
                    if (dominance < MinimumShare) continue;

                    double lift = moves.Lift(from, to);
                    if (lift < MinimumLift) continue;

                    if (best == null || lift > best.Value.Lift)
                        best = (from, to, n, total, dominance, lift);
                }
            }

            if (best == null) return null;
            var (source, target, count, departures, share, strength) = best.Value;

            var insight = new Insight
            {
                Topic = "successor",
                Kind = "routine",
                Title = $"{Pretty(source)} nearly always leads to {Pretty(target)}",
                Explanation =
                    $"On {share * 100:0}% of the times you left {Pretty(source)}, {Pretty(target)} is what you "
                    + $"opened next — {strength:0.0} times more often than {Pretty(target)} turns up after "
                    + "anything else.",
                Period = period,
                Confidence = Math.Min(0.85, 0.45 + count / 100.0),
                Importance = 0.6,
                Novelty = 0.85,
                Evidence =
                {
                    $"{count} of {departures} departures from {Pretty(source)}",
                    $"{strength:0.0} times more often than chance would put them in that order",
                    "measured by how far out of proportion it is, not by how often it happens, "
                        + "because the commonest thing after everything is simply the commonest thing"
                }
            };
            insight.Apps.Add(Pretty(source));
            insight.Apps.Add(Pretty(target));
            return insight;
        }
    }
}
