using System;
using System.Collections.Generic;
using System.Linq;

namespace FastApp.Services.Analytics
{
    public sealed class Answer
    {
        public string Question { get; init; }
        public string Text { get; init; }
        public List<string> Evidence { get; } = new();
        public string BasedOn { get; init; }
        public bool Understood { get; init; }
        public List<string> Suggestions { get; } = new();
    }

    /// <summary>
    /// Answering questions about your own computer use, from the fact sheet.
    ///
    /// No model, no key, no request leaving the machine, and none intended.
    /// The questions worth asking about one's own habits turn out to be a short
    /// and fairly stable list -- when am I most focused, what changed, what
    /// interrupts me, what is my routine, what do I spend time in, how much am
    /// I here -- and every one of them is already a field on the fact sheet.
    /// So the work is matching a question to the fact that answers it, which is
    /// a matching problem rather than a language one.
    ///
    /// It also cannot do the thing that would matter most here. A generated
    /// answer is fluent whether or not it is true, and this page is about
    /// somebody's own life: being told something confident and slightly wrong
    /// about your own weeks is worse than being told nothing. Every sentence
    /// below is assembled from measured values and carries the evidence that
    /// produced it, so a wrong answer is wrong the way a measurement is, and
    /// can be checked.
    ///
    /// The cost is that a question phrased in a way nobody anticipated gets a
    /// list of what can be asked instead of a guess. That is the right failure.
    /// </summary>
    public static class Questions
    {
        private sealed class Intent
        {
            public string Name;
            public string[] Words;
            public Func<FactSheet, Answer> Answer;
        }

        public static readonly string[] Examples =
        {
            "When am I most focused?",
            "What changed this week?",
            "What interrupts me the most?",
            "What is my usual routine?",
            "What do I spend the most time in?",
            "How much am I on my computer?"
        };

        public static Answer Ask(string question, FactSheet facts)
        {
            string q = (question ?? string.Empty).ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(q))
            {
                return NotUnderstood("Ask me something about how you use your computer.");
            }

            var words = Tokenise(q);

            // Scored rather than first-match: "what changed about my focus"
            // mentions both, and the intent with more of its words present is
            // the one being asked about.
            Intent best = null;
            int bestScore = 0;
            foreach (var intent in Intents)
            {
                int score = intent.Words.Count(w => Matches(w, q, words));
                if (score > bestScore) { bestScore = score; best = intent; }
            }

            if (best == null) return NotUnderstood("I did not follow that one.");

            var answer = best.Answer(facts);
            return answer ?? NotUnderstood(
                "I do not have enough recorded yet to answer that properly.");
        }

        /// <summary>Letters only, lowercase, split on everything else.</summary>
        private static List<string> Tokenise(string question)
        {
            var words = new List<string>();
            var word = new System.Text.StringBuilder();
            foreach (char c in question)
            {
                if (char.IsLetter(c)) word.Append(c);
                else if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
            }
            if (word.Length > 0) words.Add(word.ToString());
            return words;
        }

        /// <summary>
        /// Whether a keyword is really present.
        ///
        /// This was a substring test, and substrings are a trap on short words:
        /// "do I like pineapple on pizza" matched the applications intent and
        /// was answered with a list of what the person had been using, because
        /// "pineapple" contains "app". Confidently answering the wrong question
        /// is the worst failure this page has, since the answer looks exactly
        /// like a right one.
        ///
        /// So a keyword with a space in it is a phrase and still matches
        /// anywhere; a single word has to be a whole word. Words of five letters
        /// or more may match a longer one that starts with them, which is what
        /// lets "chang" find "changed" and "focus" find "focused" without
        /// letting "app" find "apple".
        /// </summary>
        private static bool Matches(string keyword, string whole, List<string> words)
        {
            if (keyword.Contains(' ')) return whole.Contains(keyword);

            foreach (var word in words)
            {
                if (word == keyword) return true;
                if (keyword.Length >= 5 && word.StartsWith(keyword, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static Answer NotUnderstood(string text)
        {
            var answer = new Answer { Text = text, Understood = false };
            answer.Suggestions.AddRange(Examples);
            return answer;
        }

        private static readonly Intent[] Intents =
        {
            new Intent
            {
                Name = "focus",
                Words = new[] { "focus", "focused", "concentrat", "uninterrupted", "deep", "best time", "productive" },
                Answer = f =>
                {
                    if (f.FocusWindow == null) return null;
                    var a = new Answer
                    {
                        Text = $"Your longest unbroken stretches start between {f.FocusWindow}. "
                             + $"The longest run in a day lately has been about "
                             + $"{Detectors.Describe(TimeSpan.FromMinutes(f.LongestStretchMinutes))}.",
                        BasedOn = $"{f.DaysOfHistory} days of recorded activity",
                        Understood = true
                    };
                    if (f.HasBaseline)
                    {
                        a.Evidence.Add($"typical longest run: {Detectors.Describe(TimeSpan.FromMinutes(f.LongestStretchMinutes))}, "
                                     + $"against {Detectors.Describe(TimeSpan.FromMinutes(f.BaselineLongestStretchMinutes))} before");
                    }
                    a.Evidence.Add($"{f.SwitchesPerHour:0.0} moves between applications an hour");
                    return a;
                }
            },

            new Intent
            {
                Name = "changed",
                Words = new[] { "chang", "differ", "this week", "lately", "recent", "new", "unusual" },
                Answer = f =>
                {
                    if (!f.HasBaseline)
                    {
                        return new Answer
                        {
                            Text = $"Not enough history yet to say what has changed. "
                                 + $"There are {f.BaselineDays} days behind the comparison and it needs "
                                 + $"{Baseline.MinimumDays}.",
                            BasedOn = $"{f.DaysOfHistory} days recorded",
                            Understood = true
                        };
                    }

                    var changes = f.Insights.Where(i => i.Kind == "change" || i.Kind == "focus"
                                                     || i.Trend == "new" || i.Trend == "gone").ToList();
                    if (changes.Count == 0)
                    {
                        return new Answer
                        {
                            Text = "Nothing much. This period looks like your usual, "
                                 + "on time spent, on how often you move between things, and on which applications.",
                            BasedOn = $"the last {f.RecentDays} days against the {f.BaselineDays} before them",
                            Understood = true
                        };
                    }

                    var a = new Answer
                    {
                        Text = string.Join(" ", changes.Take(2).Select(c => c.Title + ": " + c.Explanation)),
                        BasedOn = $"the last {f.RecentDays} days against the {f.BaselineDays} before them",
                        Understood = true
                    };
                    foreach (var c in changes.Take(2)) a.Evidence.AddRange(c.Evidence);
                    return a;
                }
            },

            new Intent
            {
                Name = "interrupt",
                Words = new[] { "interrupt", "interrupts", "interrupted", "distract", "distracts", "pull", "pulls", "away", "break my", "stop me" },
                Answer = f =>
                {
                    if (f.Interrupter == null) return null;
                    var a = new Answer
                    {
                        Text = $"{f.Interrupter}. When you have been settled in something for more than "
                             + $"ten minutes and then move, that is where you go "
                             + $"{f.InterrupterShare * 100:0}% of the time.",
                        BasedOn = $"{f.DaysOfHistory} days of recorded activity",
                        Understood = true
                    };
                    var insight = f.Insights.FirstOrDefault(i => i.Apps.Contains(f.Interrupter));
                    if (insight != null) a.Evidence.AddRange(insight.Evidence);
                    return a;
                }
            },

            new Intent
            {
                Name = "routine",
                Words = new[] { "routine", "typical", "usual", "normal", "habit", "habits", "morning", "pattern", "start my day" },
                Answer = f =>
                {
                    var parts = new List<string>();
                    if (f.StartsDayWith != null) parts.Add($"Your day usually starts with {f.StartsDayWith}.");
                    if (f.DayParts.Count > 0)
                    {
                        var biggest = f.DayParts.OrderByDescending(p => p.Hours).First();
                        parts.Add($"Most of your computer time is in the {biggest.Part}.");
                    }
                    if (f.BaselineFirstUse > TimeSpan.Zero)
                        parts.Add($"You typically first touch it around {f.BaselineFirstUse:hh\\:mm}.");
                    if (f.FocusWindow != null)
                        parts.Add($"Your longest stretches start between {f.FocusWindow}.");

                    if (parts.Count == 0) return null;
                    var a = new Answer
                    {
                        Text = string.Join(" ", parts),
                        BasedOn = $"{f.DaysOfHistory} days of recorded activity",
                        Understood = true
                    };
                    foreach (var (part, hours) in f.DayParts.OrderByDescending(p => p.Hours))
                        a.Evidence.Add($"{part}: {Detectors.Describe(TimeSpan.FromHours(hours))}");
                    return a;
                }
            },

            new Intent
            {
                Name = "apps",
                Words = new[] { "app", "apps", "program", "programs", "software", "spend the most", "most time", "use most" },
                Answer = f =>
                {
                    if (f.TopApps.Count == 0) return null;
                    var top = f.TopApps.Take(3).ToList();
                    var a = new Answer
                    {
                        Text = "Over the last " + f.RecentDays + " days: "
                             + string.Join(", ", top.Select(t =>
                                 $"{t.App} for {Detectors.Describe(TimeSpan.FromHours(t.Hours))}"))
                             + ".",
                        BasedOn = $"the last {f.RecentDays} days",
                        Understood = true
                    };
                    foreach (var (app, hours, change) in f.TopApps)
                    {
                        a.Evidence.Add(change == 0
                            ? $"{app}: {Detectors.Describe(TimeSpan.FromHours(hours))}"
                            : $"{app}: {Detectors.Describe(TimeSpan.FromHours(hours))} "
                              + $"({(change > 0 ? "+" : "")}{change:0}% against your usual)");
                    }
                    return a;
                }
            },

            new Intent
            {
                Name = "howmuch",
                Words = new[] { "how much", "how long", "total", "hours", "time on", "screen time" },
                Answer = f =>
                {
                    var a = new Answer
                    {
                        Text = $"{Detectors.Describe(TimeSpan.FromHours(f.RecentHours))} over the last "
                             + $"{f.RecentDays} days, which is about "
                             + $"{Detectors.Describe(TimeSpan.FromHours(f.RecentHoursPerDay))} a day"
                             + (f.HasBaseline
                                ? $", against your usual {Detectors.Describe(TimeSpan.FromHours(f.BaselineHoursPerDay))}."
                                : "."),
                        BasedOn = f.HasBaseline
                            ? $"the last {f.RecentDays} days against the {f.BaselineDays} before them"
                            : $"the last {f.RecentDays} days",
                        Understood = true
                    };
                    a.Evidence.Add($"{f.RecentHoursPerDay:0.0} hours a day recently");
                    if (f.HasBaseline)
                    {
                        a.Evidence.Add($"{f.BaselineHoursPerDay:0.0} hours a day usually");
                        a.Evidence.Add($"{f.Change(f.RecentHoursPerDay, f.BaselineHoursPerDay):+0;-0;0}% difference");
                    }
                    if (f.BusiestDay != null) a.Evidence.Add($"busiest day: {f.BusiestDay}");
                    return a;
                }
            },

            new Intent
            {
                Name = "switching",
                Words = new[] { "switch", "switches", "switching", "jump", "jumps", "fragment", "back and forth", "bounce", "multitask" },
                Answer = f =>
                {
                    var a = new Answer
                    {
                        Text = $"About {f.SwitchesPerHour:0.0} moves between applications an hour"
                             + (f.HasBaseline
                                ? $", against your usual {f.BaselineSwitchesPerHour:0.0}."
                                : ".")
                             + (f.Interrupter != null
                                ? $" {f.Interrupter} is where most of them go."
                                : ""),
                        BasedOn = $"the last {f.RecentDays} days",
                        Understood = true
                    };
                    a.Evidence.Add($"{f.SwitchesPerHour:0.0} an hour recently");
                    if (f.HasBaseline) a.Evidence.Add($"{f.BaselineSwitchesPerHour:0.0} an hour usually");
                    a.Evidence.Add("moves shorter than ten seconds are not counted, "
                                 + "because focus twitching is not a decision");
                    return a;
                }
            }
        };
    }
}
