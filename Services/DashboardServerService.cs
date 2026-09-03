using CommunityToolkit.Mvvm.Messaging;
using FastApp.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FastApp.Services
{
    // Partial: the endpoint registrations live in Services/Endpoints/, grouped by
    // area. This file keeps the server's lifecycle, the shared query helpers, and
    // the Wrapped builder that two of those groups depend on.
    public static partial class DashboardServerService
    {
        public const string DashboardUrl = "http://127.0.0.1:5050/dashboard.html";

        // Live state of the embedded web server, so the UI can report what is
        // actually true instead of a hardcoded "it's running" string. Set to
        // running optimistically at bind time and corrected if RunAsync throws;
        // a bind failure is by far the likeliest cause (port already taken).
        public static bool IsRunning { get; private set; }
        public static string StatusMessage { get; private set; } = "Starting the dashboard server…";

        private record UpdateLimitRequest(string AppName, int DailyLimitMinutes, bool StrictFocusMode, string Pin);

        private record CategoryClassificationRequest(string Category, string Classification);

        private record SetPinRequest(string Pin);

        private record ExtendLimitRequest(string AppName, string Pin, int ExtraMinutes);

        // PIN hashing/verification lives in PinService — shared with the WPF app's
        // own native Extend Time dialog (tray icon menu) so the two never drift.

        private static async Task<List<string>> GetAllCategoriesAsync(AppDbContext db)
        {
            var wpfHardcodedCategories = new List<string>
            {
                "Development", "Gaming", "Productivity", "Browsing", "Communication", "Media Production", "Music", "Fun", "Education", "Utilities", "Other"
            };

            var mappedCategories = await db.AppCategories.Select(c => c.Category).Where(c => !string.IsNullOrEmpty(c)).ToListAsync();
            var managedCategories = await db.ManagedApps.Select(m => m.Category).Where(c => !string.IsNullOrEmpty(c)).ToListAsync();

            return wpfHardcodedCategories
                .Union(mappedCategories)
                .Union(managedCategories)
                .Distinct()
                .OrderBy(c => c)
                .ToList();
        }

        // Which categories count as "work" vs "play" for the Insights tab's rhythm
        // chart — user-editable (see /api/settings/category-classification), so it
        // reflects whatever categories THIS user actually has, not a fixed guess.
        // Defaults below only apply to a category until the user explicitly sets it.
        private static Dictionary<string, string> GetCategoryClassification(AppDbContext db)
        {
            var classification = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Development"] = "work",
                ["Productivity"] = "work",
                ["Education"] = "work",
                ["Utilities"] = "work",
                ["Gaming"] = "play",
                ["Fun"] = "play",
                ["Media Production"] = "play",
                ["Music"] = "play",
                ["Browsing"] = "play"
            };

            try
            {
                using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT Value FROM AppSettings WHERE Key = 'CategoryClassification'";
                db.Database.OpenConnection();
                using var result = command.ExecuteReader();
                if (result.Read())
                {
                    var stored = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(result.GetString(0));
                    if (stored != null)
                    {
                        foreach (var kvp in stored) classification[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch { /* fall back to the defaults above */ }

            return classification;
        }

        private static List<string> GetHiddenApps(AppDbContext db)
        {
            var hidden = new List<string>();
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT AppName FROM HiddenApps";
            db.Database.OpenConnection();
            using var result = command.ExecuteReader();
            while (result.Read()) hidden.Add(result.GetString(0));
            return hidden;
        }

        private static int GetRetentionDays(AppDbContext db)
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT Value FROM AppSettings WHERE Key = 'RetentionDays'";
            db.Database.OpenConnection();
            using var result = command.ExecuteReader();
            if (result.Read() && int.TryParse(result.GetString(0), out int days)) return days;
            return 90; // Default
        }

        private static bool GetCaptureWindowTitles(AppDbContext db)
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT Value FROM AppSettings WHERE Key = 'CaptureWindowTitles'";
            db.Database.OpenConnection();
            using var result = command.ExecuteReader();
            return result.Read() && result.GetString(0) == "true";
        }

        // The rule moved to CategoryMap so the desktop palette resolves
        // categories the same way this does; they used to disagree.
        private static Task<Dictionary<string, string>> GetAppCategoriesSafely(AppDbContext db) =>
            Task.FromResult(CategoryMap.Build(db));

        private static DateTime GetMondayStartOfWeek(DateTime date)
        {
            // C# DayOfWeek: Sunday=0, Monday=1...
            // This formula calculates how many days to subtract to always land on the most recent Monday.
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.Date.AddDays(-1 * diff);
        }

        // Backing type for /api/wrapped and /api/wrapped/available — see BuildWrappedAsync.
        private class WrappedData
        {
            public string Type { get; set; }
            public string Label { get; set; }
            public string Teaser { get; set; }
            public string DateRange { get; set; }
            public bool IsInProgress { get; set; }
            public string ElapsedLabel { get; set; }
            public object TopApp { get; set; }
            public double TotalFocusedHours { get; set; }
            // --- Per-period-shape fields added for the narrative-arc redesign.
            // Which of these the frontend actually uses depends on Type: week
            // gets RhythmBuckets (day-by-day) + a light Archetype; month gets
            // RhythmBuckets (week-by-week) + Milestones + a full Archetype;
            // year gets RhythmBuckets (month-by-month) + Milestones + TopApps
            // (plural) + the full Archetype as the closing slide. ---
            public string RhythmLabel { get; set; }
            public object RhythmBuckets { get; set; }
            public object CategoryBreakdown { get; set; }
            public object Milestones { get; set; }
            public object TopApps { get; set; }
            public object Archetype { get; set; }
        }

        // Builds a Wrapped recap for the *current* week/month/year (never a past one — see
        // the endpoint comments for why). "Live but labeled": an in-progress period is
        // always compared to the same elapsed number of days in the previous period, not
        // the previous period's full total, so a Tuesday check never reads as a decline
        // just because the week isn't over yet. The headline "% of your week" figure is
        // the one exception -- it's elapsed time over the FULL period length (not just the
        // elapsed portion), so it naturally builds toward the final number as the period
        // progresses, matching how the feature was mocked up and approved.
        private static async Task<WrappedData> BuildWrappedAsync(AppDbContext db, List<string> hiddenApps, Dictionary<string, string> appCategories, string periodKind)
        {
            DateTime today = DateTime.Today;

            DateTime periodStart = periodKind switch
            {
                "month" => new DateTime(today.Year, today.Month, 1),
                "year" => new DateTime(today.Year, 1, 1),
                _ => GetMondayStartOfWeek(today)
            };
            DateTime periodEnd = periodKind switch
            {
                "month" => periodStart.AddMonths(1).AddDays(-1),
                "year" => periodStart.AddYears(1).AddDays(-1),
                _ => periodStart.AddDays(6)
            };
            DateTime prevStart = periodKind switch
            {
                "month" => periodStart.AddMonths(-1),
                "year" => periodStart.AddYears(-1),
                _ => periodStart.AddDays(-7)
            };

            // Elapsed day-count so far in the current period (inclusive of today) --
            // naturally equals the full period length on the period's last day, so no
            // separate "is this period actually over" branch is needed anywhere below.
            int elapsedDays = (int)(today - periodStart).TotalDays + 1;
            DateTime prevElapsedEnd = prevStart.AddDays(elapsedDays - 1);

            var currentSystemLogs = await db.DailyLogs.AsNoTracking()
                .Where(l => l.AppName == "SYSTEM_PC" && l.Date >= periodStart && l.Date <= today)
                .ToListAsync();
            if (currentSystemLogs.Count == 0) return null; // nothing to wrap yet

            double currentUptimeHours = currentSystemLogs.Sum(l => l.TimeSpent.TotalHours);
            double currentFocusedHours = currentSystemLogs.Sum(l => l.TimeFocused.TotalHours);

            // Feeds the Archetype's focus flourish below (high/steady/low framing)
            // -- not exposed as its own field. It doesn't belong on the Cover
            // slide: totalFocusedHours there IS already the truly-focused figure,
            // so pairing it with "X% of that was truly focused" would describe a
            // subset-of-a-subset that doesn't exist.
            double pctFocused = currentUptimeHours > 0.01 ? Math.Round(currentFocusedHours / currentUptimeHours * 100, 1) : 0;

            var appLogs = await db.DailyLogs.AsNoTracking()
                .Where(l => l.AppName != "SYSTEM_PC" && l.Date >= prevStart && l.Date <= today && !hiddenApps.Contains(l.AppName))
                .ToListAsync();

            var currentAppTotals = appLogs.Where(l => l.Date >= periodStart && l.Date <= today)
                .GroupBy(l => l.AppName)
                .Select(g => new { AppName = g.Key, Minutes = g.Sum(x => x.TimeFocused.TotalMinutes) })
                .OrderByDescending(x => x.Minutes).ToList();

            var prevAppTotals = appLogs.Where(l => l.Date >= prevStart && l.Date <= prevElapsedEnd)
                .GroupBy(l => l.AppName)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.TimeFocused.TotalMinutes));

            var topAppRow = currentAppTotals.FirstOrDefault();
            object topApp = null;
            if (topAppRow != null)
            {
                double prevTopMinutes = prevAppTotals.GetValueOrDefault(topAppRow.AppName, 0);
                bool isSameAsLastPeriod = prevAppTotals.Count > 0 &&
                    prevAppTotals.OrderByDescending(kv => kv.Value).FirstOrDefault().Key == topAppRow.AppName;

                // Biggest mover: largest absolute-minutes swing (up or down) among apps
                // with at least 10 minutes on one side of the comparison -- filters out
                // noise like a one-off app going from 0.1 to 2 minutes reading as a "mover".
                var moverCandidates = currentAppTotals.Select(a => a.AppName)
                    .Union(prevAppTotals.Keys)
                    .Select(name =>
                    {
                        double cur = currentAppTotals.FirstOrDefault(a => a.AppName == name)?.Minutes ?? 0;
                        double prev = prevAppTotals.GetValueOrDefault(name, 0);
                        return new { AppName = name, Cur = cur, Prev = prev, Delta = cur - prev };
                    })
                    .Where(x => x.Cur >= 10 || x.Prev >= 10)
                    .OrderByDescending(x => Math.Abs(x.Delta))
                    .FirstOrDefault();

                object mover = null;
                if (moverCandidates != null && Math.Abs(moverCandidates.Delta) > 0.5)
                {
                    mover = new
                    {
                        AppName = moverCandidates.AppName,
                        Direction = moverCandidates.Delta > 0 ? "up" : "down",
                        DeltaMinutes = Math.Round(Math.Abs(moverCandidates.Delta), 1),
                        DeltaPct = moverCandidates.Prev > 0.01 ? Math.Round(Math.Abs(moverCandidates.Delta) / moverCandidates.Prev * 100, 0) : (double?)null
                    };
                }

                topApp = new
                {
                    AppName = topAppRow.AppName,
                    Category = appCategories.GetValueOrDefault(topAppRow.AppName, "Other"),
                    Minutes = Math.Round(topAppRow.Minutes, 1),
                    IsSameAsLastPeriod = isSameAsLastPeriod,
                    Mover = mover
                };
            }

            // Peak day/week/month is no longer computed separately here -- the
            // RhythmBuckets built below cover every period type generically, and
            // the frontend derives "which bucket was best" straight from those
            // (max non-future entry) instead of needing a special-cased field.

            // --- CATEGORY BREAKDOWN: where the period's focused time actually went.
            // Categories are tracked per app but Wrapped never touched that data
            // before -- this is real, previously-unused signal, not a rehash of
            // a number already on Overview. ---
            var categoryTotals = currentAppTotals
                .GroupBy(a => appCategories.GetValueOrDefault(a.AppName, "Other"))
                .Select(g => new { Category = g.Key, Minutes = g.Sum(a => a.Minutes) })
                .OrderByDescending(x => x.Minutes)
                .ToList();
            double totalCategoryMinutes = categoryTotals.Sum(c => c.Minutes);
            object categoryBreakdown = categoryTotals.Count == 0 ? null : new
            {
                Top = new
                {
                    Category = categoryTotals[0].Category,
                    Minutes = Math.Round(categoryTotals[0].Minutes, 1),
                    Pct = totalCategoryMinutes > 0.01 ? Math.Round(categoryTotals[0].Minutes / totalCategoryMinutes * 100, 1) : 0
                },
                All = categoryTotals.Take(4).Select(c => new
                {
                    Category = c.Category,
                    Minutes = Math.Round(c.Minutes, 1),
                    Pct = totalCategoryMinutes > 0.01 ? Math.Round(c.Minutes / totalCategoryMinutes * 100, 1) : 0
                }).ToList()
            };

            // --- RHYTHM BUCKETS: the "when" story, at whatever grain actually fits
            // the period -- individual days for a week (only 7, showing all of them
            // works), weeks for a month, months for a year. All built from the
            // SYSTEM_PC rows already fetched above, no extra query needed. ---
            string rhythmLabel;
            List<object> rhythmBuckets;
            if (periodKind == "week")
            {
                rhythmLabel = "Day by day";
                var byDate = currentSystemLogs.ToDictionary(l => l.Date.Date, l => Math.Round(l.TimeFocused.TotalHours, 1));
                rhythmBuckets = new List<object>();
                for (DateTime d = periodStart; d <= periodEnd; d = d.AddDays(1))
                {
                    rhythmBuckets.Add(new { Label = d.ToString("ddd"), Hours = byDate.GetValueOrDefault(d.Date, 0), IsFuture = d > today });
                }
            }
            else if (periodKind == "month")
            {
                rhythmLabel = "Week by week";
                rhythmBuckets = currentSystemLogs
                    .GroupBy(l => GetMondayStartOfWeek(l.Date))
                    .OrderBy(g => g.Key)
                    .Select((g, i) => (object)new { Label = $"Wk {i + 1}", Hours = Math.Round(g.Sum(x => x.TimeFocused.TotalHours), 1), IsFuture = false })
                    .ToList();
            }
            else
            {
                rhythmLabel = "Month by month";
                rhythmBuckets = currentSystemLogs
                    .GroupBy(l => new DateTime(l.Date.Year, l.Date.Month, 1))
                    .OrderBy(g => g.Key)
                    .Select(g => (object)new { Label = g.Key.ToString("MMM"), Hours = Math.Round(g.Sum(x => x.TimeFocused.TotalHours), 1), IsFuture = false })
                    .ToList();
            }

            // --- ARCHETYPE: the closing-slide payoff. Deterministic, not AI --
            // combines dominant category + weekday/weekend bias + focus quality
            // into a short label and a one-line reason, using real numbers from
            // this same response rather than a generic stock phrase. Week gets
            // "light" weight (a vibe, not a crowned identity -- one week isn't
            // enough data to call it your "type"); month/year get "full". ---
            double weekdayFocusPeriod = currentSystemLogs.Where(l => l.Date.DayOfWeek >= DayOfWeek.Monday && l.Date.DayOfWeek <= DayOfWeek.Friday).Sum(l => l.TimeFocused.TotalHours);
            double weekendFocusPeriod = currentSystemLogs.Where(l => l.Date.DayOfWeek == DayOfWeek.Saturday || l.Date.DayOfWeek == DayOfWeek.Sunday).Sum(l => l.TimeFocused.TotalHours);
            double totalRhythmFocus = weekdayFocusPeriod + weekendFocusPeriod;
            // Weekdays are 5/7 of the week, so a perfectly proportional week
            // already puts ~71% of time on weekdays -- thresholds are set around
            // that baseline rather than 50/50, so an even week doesn't misread as
            // "weekday-biased" just because there are more weekdays to fill.
            string rhythmBias = "Everyday";
            if (totalRhythmFocus > 0.01)
            {
                double weekdayShare = weekdayFocusPeriod / totalRhythmFocus;
                if (weekdayShare >= 0.85) rhythmBias = "Weekday";
                else if (weekdayShare <= 0.40) rhythmBias = "Weekend";
            }

            object archetype = null;
            string archetypeLabel = null; // captured separately so the Teaser below can reuse it without unboxing `archetype`
            if (categoryTotals.Count > 0)
            {
                string topCat = categoryTotals[0].Category;
                string catNoun = topCat switch
                {
                    "Development" => "Developer",
                    "Gaming" => "Gamer",
                    "Productivity" => "Organizer",
                    "Browsing" => "Explorer",
                    "Communication" => "Connector",
                    "Media Production" => "Creator",
                    "Music" => "Curator",
                    "Fun" => "Player",
                    "Education" => "Student",
                    "Utilities" => "Tinkerer",
                    _ => "Wanderer"
                };
                double topCatPct = totalCategoryMinutes > 0.01 ? Math.Round(categoryTotals[0].Minutes / totalCategoryMinutes * 100, 0) : 0;
                string focusFlourish = pctFocused >= 60 ? "and when you're in, you're really in"
                    : pctFocused >= 35 ? "steady and consistent"
                    : "more time open than truly locked in";
                string rhythmPhrase = rhythmBias == "Weekday" ? "mostly on weekdays"
                    : rhythmBias == "Weekend" ? "mostly on weekends"
                    : "spread evenly across the week";
                archetypeLabel = $"The {rhythmBias} {catNoun}";
                archetype = new
                {
                    Label = archetypeLabel,
                    Description = $"{topCatPct}% of your focus went to {topCat} this {periodKind}, {rhythmPhrase} — {focusFlourish}.",
                    Weight = periodKind == "week" ? "light" : "full"
                };
            }

            // --- MILESTONES THIS PERIOD (month/year only): any app whose all-time
            // cumulative focused hours crossed a tier threshold within this period's
            // date range. Needs each app's FULL history (not just this period) to
            // compute the running total correctly -- an app can carry 40h in from
            // before the period and cross Silver (50h) three days into it. Shares
            // the one ladder definition with the App Detail drawer, so the two can
            // no longer disagree about the same app on the same day. ---
            object milestonesThisPeriod = null;
            if (periodKind == "month" || periodKind == "year")
            {
                var allAppLogsAllTime = await db.DailyLogs.AsNoTracking()
                    .Where(l => l.AppName != "SYSTEM_PC" && !hiddenApps.Contains(l.AppName))
                    .ToListAsync();

                var tierDefs = MilestoneTiers.All;

                var crossings = new List<(string AppName, string TierName, DateTime SortDate)>();
                foreach (var appGroup in allAppLogsAllTime.GroupBy(l => l.AppName))
                {
                    double running = 0;
                    int tierIdx = 0;
                    foreach (var log in appGroup.OrderBy(l => l.Date))
                    {
                        running += log.TimeFocused.TotalHours;
                        while (tierIdx < tierDefs.Length && running >= tierDefs[tierIdx].Hours)
                        {
                            if (log.Date >= periodStart && log.Date <= today)
                            {
                                crossings.Add((appGroup.Key, tierDefs[tierIdx].Name, log.Date));
                            }
                            tierIdx++;
                        }
                    }
                }

                milestonesThisPeriod = crossings
                    .OrderByDescending(c => c.SortDate)
                    .Select(c => new { c.AppName, c.TierName, Date = c.SortDate.ToString("MMM d") })
                    .ToList();
            }

            // --- TOP APPS (plural, year only): a whole year earns more than one
            // headline app. Week/month keep the single TopApp+Mover slide below. ---
            object topAppsResult = null;
            if (periodKind == "year")
            {
                topAppsResult = currentAppTotals.Take(3).Select(a =>
                {
                    double prevMinutes = prevAppTotals.GetValueOrDefault(a.AppName, 0);
                    double? topDeltaPct = prevMinutes > 0.01 ? Math.Round((a.Minutes - prevMinutes) / prevMinutes * 100, 0) : (double?)null;
                    return new
                    {
                        AppName = a.AppName,
                        Category = appCategories.GetValueOrDefault(a.AppName, "Other"),
                        Minutes = Math.Round(a.Minutes, 1),
                        DeltaPct = topDeltaPct
                    };
                }).ToList();
            }

            string label = periodKind switch
            {
                "month" => periodStart.ToString("MMMM yyyy"),
                "year" => periodStart.Year.ToString(),
                _ => $"Week {System.Globalization.ISOWeek.GetWeekOfYear(periodStart)}"
            };
            string dateRange = periodKind switch
            {
                "month" => periodStart.ToString("MMMM yyyy"),
                "year" => periodStart.Year.ToString(),
                _ => periodStart.Month == periodEnd.Month
                    ? $"{periodStart:MMM d}–{periodEnd:d}"
                    : $"{periodStart:MMM d}–{periodEnd:MMM d}"
            };
            // The archetype label makes a punchier panel-preview teaser than a raw
            // hour count -- falls back to hours if there wasn't enough data to
            // build one (categoryTotals empty).
            string teaser = archetypeLabel != null
                ? archetypeLabel
                : $"{Math.Round(currentFocusedHours, 0)}h focused so far";

            bool isInProgress = today < periodEnd;

            return new WrappedData
            {
                Type = periodKind,
                Label = label,
                Teaser = teaser,
                DateRange = dateRange,
                IsInProgress = isInProgress,
                ElapsedLabel = isInProgress ? $"as of {today:dddd}" : null,
                TopApp = topApp,
                TotalFocusedHours = Math.Round(currentFocusedHours, 1),
                RhythmLabel = rhythmLabel,
                RhythmBuckets = rhythmBuckets,
                CategoryBreakdown = categoryBreakdown,
                Milestones = milestonesThisPeriod,
                TopApps = topAppsResult,
                Archetype = archetype
            };
        }

        public static async Task StartAsync()
        {
            string exeFolder = AppContext.BaseDirectory;
            string wwwrootPath = Path.Combine(exeFolder, "wwwroot");
            if (!Directory.Exists(wwwrootPath)) Directory.CreateDirectory(wwwrootPath);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = exeFolder, WebRootPath = wwwrootPath });
            builder.WebHost.UseUrls("http://127.0.0.1:5050");

            // Pin the JSON contract. camelCase is already what ASP.NET Core's web
            // defaults produce and what every endpoint here has always emitted --
            // stating it explicitly turns an inherited framework default into a
            // guarantee the frontend can rely on.
            //
            // The frontend used to read every single field twice
            // (data.foo ?? data.Foo), 151 times over, defending against a casing
            // that never actually occurred. That is not just noise: a typo in one
            // branch is invisible because the other covers it.
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            });
            var app = builder.Build();

            // ==========================================================
            // INVARIANT FORMATTING
            //
            // Dates and numbers built here are formatted on the server and sent
            // to the page already rendered, so they picked up whatever culture
            // the machine runs in. On a Polish install that produced "lip 11 –
            // lip 29, 2026", "sierpień 2026" and "4,3h" sitting inside an
            // otherwise English interface -- and it is invisible to anyone
            // developing on an English machine.
            //
            // Forced per-request rather than by setting the process culture,
            // because the WPF app around this server is entitled to the user's
            // real locale for its own UI. CurrentCulture flows across awaits, so
            // every endpoint below inherits it, including ones added later --
            // which beats correcting the ~26 individual format calls and hoping
            // the next one remembers.
            // ==========================================================
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            app.UseRequestLocalization(new Microsoft.AspNetCore.Builder.RequestLocalizationOptions
            {
                DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(invariant, invariant),
                SupportedCultures = new[] { invariant },
                SupportedUICultures = new[] { invariant },
                // Nothing may override it -- no Accept-Language, no query string.
                RequestCultureProviders = new List<Microsoft.AspNetCore.Localization.IRequestCultureProvider>()
            });

            // ==========================================================
            // LOCAL-ONLY GUARD
            //
            // Binding to 127.0.0.1 keeps this off the network, but it does NOT
            // make the server private to this app: anything running in a browser
            // on this machine can reach it too. Without the checks below, any
            // website the user happened to have open could POST here.
            //
            // The endpoints read raw string bodies, which makes them "simple
            // requests" under CORS -- no preflight, so the browser just sends
            // them. The attacker cannot read the response (no CORS headers are
            // ever set, which is deliberate), but a blind write is more than
            // enough: retention could be set to its minimum and take the user's
            // history with it on next launch, or window-title capture -- a
            // privacy setting -- could be switched on.
            //
            // Two layers:
            //  * Host  -- checked on EVERY request, including GET. This is what
            //    stops DNS rebinding, where a hostile domain re-resolves to
            //    127.0.0.1 so the browser treats our responses as same-origin
            //    and can finally read them. Such a request still carries the
            //    attacker's hostname in Host, so it fails here.
            //  * Origin/Referer -- checked on state-changing verbs. Browsers
            //    always attach Origin to cross-origin POSTs (form submissions
            //    included), so a mismatch is a reliable CSRF signal. A request
            //    with neither header is not something a page can produce, so it
            //    is allowed through for local scripting/curl.
            // ==========================================================
            string[] allowedHosts = { "127.0.0.1:5050", "localhost:5050", "[::1]:5050" };
            string[] allowedOrigins = { "http://127.0.0.1:5050", "http://localhost:5050", "http://[::1]:5050" };

            app.Use(async (context, next) =>
            {
                string host = context.Request.Host.Value ?? string.Empty;
                if (!allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { error = "Unrecognized Host header." });
                    return;
                }

                string method = context.Request.Method;
                bool isStateChanging = !HttpMethods.IsGet(method)
                                    && !HttpMethods.IsHead(method)
                                    && !HttpMethods.IsOptions(method);

                if (isStateChanging)
                {
                    string origin = context.Request.Headers.Origin.ToString();
                    string referer = context.Request.Headers.Referer.ToString();

                    bool allowed;
                    if (!string.IsNullOrEmpty(origin))
                    {
                        allowed = allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
                    }
                    else if (!string.IsNullOrEmpty(referer))
                    {
                        // Compared against origin + "/" so a lookalike host like
                        // "http://127.0.0.1:5050.example.com/" cannot satisfy a
                        // plain prefix match.
                        allowed = allowedOrigins.Any(o =>
                            referer.Equals(o, StringComparison.OrdinalIgnoreCase) ||
                            referer.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        allowed = true; // no browser context — not reachable from a web page
                    }

                    if (!allowed)
                    {
                        context.Response.StatusCode = 403;
                        await context.Response.WriteAsJsonAsync(new { error = "Cross-origin request rejected." });
                        return;
                    }
                }

                await next();
            });

            app.UseStaticFiles();

            // A second static-file mount served a "Nova" dashboard from wwwroot2 at
            // /nova. That folder does not exist in the repo and is not packaged, so
            // the only thing the route ever did was create an empty directory inside
            // the install folder on every launch and serve nothing out of it.

            using (var initDb = new AppDbContext())
            {
                await initDb.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS HiddenApps (AppName TEXT PRIMARY KEY);");
                await initDb.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS AppSettings (Key TEXT PRIMARY KEY, Value TEXT);");
                // Keep Forever (99999) is the default, matching what the Settings UI
                // has always presented as the default. This used to seed '90', which
                // meant a user who never opened Settings had their SessionLogs and
                // MacroEventLogs permanently deleted at 90 days -- silently, on every
                // launch, without ever having chosen it.
                await initDb.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO AppSettings (Key, Value) VALUES ('RetentionDays', '99999');");

                // One-time repair for installs created while '90' was the seeded
                // default. A stored 90 is far more likely to be that old default than
                // a deliberate choice, and the two are indistinguishable here -- so
                // this errs toward KEEPING data, since guessing wrong in that
                // direction is recoverable and guessing wrong the other way is not.
                // The marker key makes it strictly one-time: anyone who genuinely
                // wants 90 days can set it again and it will stick.
                bool retentionAlreadyRepaired = PinService.GetSettingValue(initDb, "RetentionDefaultRepaired") == "true";
                if (!retentionAlreadyRepaired)
                {
                    await initDb.Database.ExecuteSqlRawAsync("UPDATE AppSettings SET Value = '99999' WHERE Key = 'RetentionDays' AND Value = '90';");
                    await initDb.Database.ExecuteSqlRawAsync("INSERT OR REPLACE INTO AppSettings (Key, Value) VALUES ('RetentionDefaultRepaired', 'true');");
                }

                // Window-title capture is privacy-sensitive, so it defaults OFF — the
                // user has to explicitly opt in from the Settings drawer.
                await initDb.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO AppSettings (Key, Value) VALUES ('CaptureWindowTitles', 'false');");
            }
            // Endpoint registration lives in Services/Endpoints/, grouped by area.
            // Routing in minimal APIs matches on pattern rather than registration
            // order, so these are free to be split and reordered.
            MapStatsEndpoints(app);
            MapAppDetailEndpoints(app);
            MapPeriodsEndpoints(app);
            MapWrappedEndpoints(app);
            MapDataEndpoints(app);
            MapSettingsEndpoints(app);

            // RunAsync is where a failed port bind actually surfaces. This used to
            // be uncaught in a fire-and-forget task, so if anything else on the
            // machine already held 5050 the exception vanished into an unobserved
            // Task and the dashboard simply never worked -- with the Settings card
            // still cheerfully claiming it was "currently running", and the tray
            // button opening a browser tab to a connection error. Failing loudly
            // into a status the UI can read is the whole point here.
            // Split rather than RunAsync() so a failed bind is caught precisely at
            // startup, instead of having to assume that any exception out of a
            // combined start-and-run call meant the server never came up.
            try
            {
                await app.StartAsync();
                IsRunning = true;
                StatusMessage = "Running on http://127.0.0.1:5050";
            }
            catch (Exception ex)
            {
                IsRunning = false;
                StatusMessage = ex is IOException
                    ? "Port 5050 is already in use by another program, so the dashboard could not start. Close whatever is using it, then restart FastApp."
                    : $"The dashboard failed to start: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Dashboard server failed to start: {ex}");
                return;
            }

            await app.WaitForShutdownAsync();

            IsRunning = false;
            StatusMessage = "The dashboard server has stopped.";
        }
    }
}