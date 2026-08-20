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
    public static class DashboardServerService
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

        private static async Task<Dictionary<string, string>> GetAppCategoriesSafely(AppDbContext db)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var appCats = await db.AppCategories.ToListAsync();
            foreach (var c in appCats)
            {
                if (!string.IsNullOrEmpty(c.AppName)) dict[c.AppName] = c.Category ?? "Other";
            }
            var managedApps = await db.ManagedApps.ToListAsync();
            foreach (var m in managedApps)
            {
                if (!string.IsNullOrEmpty(m.Name) && !dict.ContainsKey(m.Name))
                {
                    dict[m.Name] = m.Category ?? "Other";
                }
            }
            return dict;
        }

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
            var app = builder.Build();

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
            // --- NEW: PHASE 1 TIMELINE SPARKLINE ---
            app.MapGet("/api/sparkline", async (HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    DateTime today = DateTime.Today;
                    DateTime startDate = today.AddDays(-13); // Last 14 days

                    var systemLogs = await db.DailyLogs
                        .Where(l => l.AppName == "SYSTEM_PC" && l.Date >= startDate && l.Date <= today)
                        .ToListAsync();

                    var sparkline = Enumerable.Range(0, 14).Select(i => {
                        var d = startDate.AddDays(i);
                        var log = systemLogs.FirstOrDefault(l => l.Date == d);
                        return new
                        {
                            Date = d.ToString("yyyy-MM-dd"),
                            DisplayDay = d.ToString("ddd"), // "Mon", "Tue"
                            FocusedMinutes = log != null ? Math.Round(log.TimeFocused.TotalMinutes, 1) : 0
                        };
                    }).ToList();

                    await context.Response.WriteAsJsonAsync(sparkline);
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            app.MapGet("/api/categories", async (HttpContext context) =>
            {
                using var db = new AppDbContext();
                await context.Response.WriteAsJsonAsync(await GetAllCategoriesAsync(db));
            });

            // OVERVIEW - UPGRADED WITH HEAD-TO-HEAD MATH
            app.MapGet("/api/overview", async (string date, HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    DateTime targetDate = string.IsNullOrEmpty(date) ? DateTime.Today : DateTime.Parse(date).Date;
                    var hiddenApps = GetHiddenApps(db);

                    // Bounded to the furthest any of the below actually looks back
                    // (prevYear reaches targetDate-730) instead of every SYSTEM_PC row
                    // since install — that grows forever with no ceiling, and this
                    // endpoint is polled every ~12s while the Overview tab is open.
                    // FocusAllTime below still needs the true all-time figure, computed
                    // separately via a cheap SQL-side SUM() instead of pulling every
                    // row ever into memory just for that one number.
                    DateTime historyFloor = targetDate.AddDays(-730);
                    var allSystemLogs = await db.DailyLogs.Where(l => l.AppName == "SYSTEM_PC" && l.Date >= historyFloor && l.Date <= targetDate).ToListAsync();
                    var recentLogs = await db.DailyLogs.Where(l => l.Date >= targetDate.AddDays(-6) && l.Date <= targetDate).ToListAsync();
                    var todaysLogs = await db.DailyLogs.Where(l => l.Date == targetDate && l.AppName != "SYSTEM_PC" && !hiddenApps.Contains(l.AppName)).ToListAsync();

                    var appCategories = await GetAppCategoriesSafely(db);
                    var categoryTotals = todaysLogs.GroupBy(l => appCategories.ContainsKey(l.AppName) ? appCategories[l.AppName] : "Other")
                        .Select(g => new { Category = g.Key, FocusedMinutes = g.Sum(x => x.TimeFocused.TotalMinutes) })
                        .OrderByDescending(x => x.FocusedMinutes).ToList();

                    // --- HEAD-TO-HEAD HISTORICAL MATH ---
                    double focusToday = allSystemLogs.Where(l => l.Date == targetDate).Sum(l => l.TimeFocused.TotalHours);
                    double focusPrevDay = allSystemLogs.Where(l => l.Date == targetDate.AddDays(-1)).Sum(l => l.TimeFocused.TotalHours);

                    // --- NEW: Contextual Baseline Calculation ---
                    // Calculates average focus only on days the PC was actually used over the last 30 days
                    var past30Logs = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-30) && l.Date < targetDate).ToList();
                    int activeDays = past30Logs.Select(l => l.Date).Distinct().Count();
                    double usualDailyFocus = activeDays > 0 ? past30Logs.Sum(l => l.TimeFocused.TotalHours) / activeDays : 0;

                    DateTime startOfWeek = GetMondayStartOfWeek(targetDate);
                    DateTime startOfPrevWeek = startOfWeek.AddDays(-7);

                    // "This Week" is Monday up to today
                    double focusWeek = allSystemLogs.Where(l => l.Date >= startOfWeek && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours);
                    // "Last Week" is the full Mon-Sun of the previous week
                    double focusPrevWeek = allSystemLogs.Where(l => l.Date >= startOfPrevWeek && l.Date < startOfWeek).Sum(l => l.TimeFocused.TotalHours);

                    double focusMonth = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-30) && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours);
                    double focusPrevMonth = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-60) && l.Date <= targetDate.AddDays(-30)).Sum(l => l.TimeFocused.TotalHours);

                    double focusYear = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-365) && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours);
                    double focusPrevYear = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-730) && l.Date <= targetDate.AddDays(-365)).Sum(l => l.TimeFocused.TotalHours);

                    // True all-time — allSystemLogs is now bounded to the last 730
                    // days, so this can't just sum it. SumAsync translates to a SQL
                    // SUM() over the integer Ticks column, never materializing the
                    // individual rows just to add them up in C#.
                    long focusAllTimeTicks = await db.DailyLogs.Where(l => l.AppName == "SYSTEM_PC").SumAsync(l => (long?)l.TimeFocusedTicks) ?? 0;
                    double focusAllTime = TimeSpan.FromTicks(focusAllTimeTicks).TotalHours;

                    double afkWeek = allSystemLogs.Where(l => l.Date >= startOfWeek && l.Date <= targetDate).Sum(l => l.AfkTimeSpent.TotalHours);
                    double afkPrevWeek = allSystemLogs.Where(l => l.Date >= startOfPrevWeek && l.Date < startOfWeek).Sum(l => l.AfkTimeSpent.TotalHours);

                    double afkMonth = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-30) && l.Date <= targetDate).Sum(l => l.AfkTimeSpent.TotalHours);
                    double afkPrevMonth = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-60) && l.Date <= targetDate.AddDays(-30)).Sum(l => l.AfkTimeSpent.TotalHours);

                    double afkYear = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-365) && l.Date <= targetDate).Sum(l => l.AfkTimeSpent.TotalHours);
                    double afkPrevYear = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-730) && l.Date <= targetDate.AddDays(-365)).Sum(l => l.AfkTimeSpent.TotalHours);

                    // --- Total PC uptime (TimeSpent, not just focused) per scope, so Focus
                    // and AFK have something to be read as a share of. ---
                    double totalToday = allSystemLogs.Where(l => l.Date == targetDate).Sum(l => l.TimeSpent.TotalHours);
                    double totalPrevDay = allSystemLogs.Where(l => l.Date == targetDate.AddDays(-1)).Sum(l => l.TimeSpent.TotalHours);

                    double totalWeek = allSystemLogs.Where(l => l.Date >= startOfWeek && l.Date <= targetDate).Sum(l => l.TimeSpent.TotalHours);
                    double totalPrevWeek = allSystemLogs.Where(l => l.Date >= startOfPrevWeek && l.Date < startOfWeek).Sum(l => l.TimeSpent.TotalHours);

                    double totalMonth = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-30) && l.Date <= targetDate).Sum(l => l.TimeSpent.TotalHours);
                    double totalPrevMonth = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-60) && l.Date <= targetDate.AddDays(-30)).Sum(l => l.TimeSpent.TotalHours);

                    double totalYear = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-365) && l.Date <= targetDate).Sum(l => l.TimeSpent.TotalHours);
                    double totalPrevYear = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-730) && l.Date <= targetDate.AddDays(-365)).Sum(l => l.TimeSpent.TotalHours);

                    // --- NEW: 365-DAY HEATMAP DATA ---
                    var yearlyHeatmap = allSystemLogs
                        .Where(l => l.Date > targetDate.AddDays(-365) && l.Date <= targetDate)
                        .Select(l => new {
                            Date = l.Date.ToString("yyyy-MM-dd"),
                            FocusedMinutes = Math.Round(l.TimeFocused.TotalMinutes, 1)
                        }).ToList();

                    var payload = new
                    {
                        TotalToday = totalToday,
                        PrevTotalToday = totalPrevDay,
                        TotalWeek = totalWeek,
                        PrevTotalWeek = totalPrevWeek,
                        TotalMonth = totalMonth,
                        PrevTotalMonth = totalPrevMonth,
                        TotalYear = totalYear,
                        PrevTotalYear = totalPrevYear,
                        FocusToday = focusToday,
                        UsualDailyFocus = usualDailyFocus,
                        PrevFocusToday = focusPrevDay,
                        FocusWeek = focusWeek,
                        PrevFocusWeek = focusPrevWeek,
                        FocusMonth = focusMonth,
                        PrevFocusMonth = focusPrevMonth,
                        YearlyHeatmap = yearlyHeatmap,
                        FocusYear = focusYear,
                        PrevFocusYear = focusPrevYear,
                        FocusAllTime = focusAllTime,
                        AfkToday = allSystemLogs.Where(l => l.Date == targetDate).Sum(l => l.AfkTimeSpent.TotalHours),
                        AfkWeek = afkWeek,
                        PrevAfkWeek = afkPrevWeek,
                        AfkMonth = afkMonth,
                        PrevAfkMonth = afkPrevMonth,
                        AfkYear = afkYear,
                        PrevAfkYear = afkPrevYear,

                        ContextSwitches = await db.SessionLogs.CountAsync(s => s.StartTime >= targetDate && s.StartTime < targetDate.AddDays(1) && !hiddenApps.Contains(s.AppName)),
                        TopAppsToday = todaysLogs.OrderByDescending(l => l.TimeFocused).Take(5).Select(l => new { AppName = l.AppName, FocusedMinutes = l.TimeFocused.TotalMinutes }).ToList(),
                        WeeklyTrend = recentLogs.Where(l => l.AppName == "SYSTEM_PC").OrderBy(l => l.Date).Select(l => new { Day = l.Date.ToString("ddd"), FocusedHours = l.TimeFocused.TotalHours }).ToList(),
                        Categories = categoryTotals
                    };
                    await context.Response.WriteAsJsonAsync(payload);
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // LEADERBOARD
            app.MapGet("/api/leaderboard", async (string timeframe, string date, HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    DateTime targetDate = string.IsNullOrEmpty(date) ? DateTime.Today : DateTime.Parse(date).Date;
                    DateTime startDate = DateTime.MinValue, prevStartDate = DateTime.MinValue, prevEndDate = DateTime.MinValue;
                    var hiddenApps = GetHiddenApps(db);

                    if (timeframe == "day") { startDate = targetDate; prevStartDate = targetDate.AddDays(-1); prevEndDate = targetDate.AddDays(-1); }
                    else if (timeframe == "week")
                    {
                        startDate = GetMondayStartOfWeek(targetDate);
                        prevStartDate = startDate.AddDays(-7);
                        prevEndDate = startDate.AddDays(-1);
                    }
                    else if (timeframe == "month") { startDate = targetDate.AddDays(-29); prevStartDate = targetDate.AddDays(-59); prevEndDate = targetDate.AddDays(-30); }
                    else if (timeframe == "year") { startDate = targetDate.AddDays(-364); prevStartDate = targetDate.AddDays(-729); prevEndDate = targetDate.AddDays(-365); }

                    var currentLogs = await db.DailyLogs.Where(l => l.Date >= startDate && l.Date <= targetDate && l.AppName != "SYSTEM_PC" && !hiddenApps.Contains(l.AppName)).ToListAsync();
                    var previousLogs = startDate != DateTime.MinValue
                        ? await db.DailyLogs.Where(l => l.Date >= prevStartDate && l.Date <= prevEndDate && l.AppName != "SYSTEM_PC" && !hiddenApps.Contains(l.AppName)).ToListAsync()
                        : new List<ViewModels.DailyUsageLog>();

                    var appCategories = await GetAppCategoriesSafely(db);

                    var leaderboard = currentLogs.GroupBy(l => l.AppName).Select(g => new
                    {
                        AppName = g.Key,
                        Category = appCategories.ContainsKey(g.Key) ? appCategories[g.Key] : "Other",
                        FocusedMinutes = Math.Round(g.Sum(x => x.TimeFocused.TotalMinutes), 1),
                        TotalMinutes = Math.Round(g.Sum(x => x.TimeSpent.TotalMinutes), 1),
                        ActiveMinutes = Math.Max(0, Math.Round(g.Sum(x => x.ActiveRunningTime.TotalMinutes), 1)),
                        PrevFocusedMinutes = Math.Round(previousLogs.Where(p => p.AppName == g.Key).Sum(x => x.TimeFocused.TotalMinutes), 1),
                        PrevActiveMinutes = Math.Round(previousLogs.Where(p => p.AppName == g.Key).Sum(x => x.ActiveRunningTime.TotalMinutes), 1),
                        PrevTotalMinutes = Math.Round(previousLogs.Where(p => p.AppName == g.Key).Sum(x => x.TimeSpent.TotalMinutes), 1)
                    }).ToList();

                    await context.Response.WriteAsJsonAsync(leaderboard);
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            app.MapPost("/api/update-category", async (HttpContext context) =>
            {
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    string body = await reader.ReadToEndAsync();
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body, options);
                    string appName = data != null && data.ContainsKey("appName") ? data["appName"] : data != null && data.ContainsKey("AppName") ? data["AppName"] : null;
                    string category = data != null && data.ContainsKey("category") ? data["category"] : data != null && data.ContainsKey("Category") ? data["Category"] : null;

                    if (!string.IsNullOrEmpty(appName) && !string.IsNullOrEmpty(category))
                    {
                        WeakReferenceMessenger.Default.Send(new FastApp.ViewModels.UpdateCategoryCommand(appName, category));
                        await context.Response.WriteAsJsonAsync(new { success = true });
                    }
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // Daily limit / Strict Focus Mode — editable from the App Detail drawer.
            // Same remote-control pattern as /api/update-category: send a message
            // for the live WPF instance to apply (so its own AppDbContext, not a
            // second one here, is what persists the change).
            app.MapPost("/api/update-limit", async (HttpContext context) =>
            {
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    string body = await reader.ReadToEndAsync();
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var data = System.Text.Json.JsonSerializer.Deserialize<UpdateLimitRequest>(body, options);

                    if (data == null || string.IsNullOrEmpty(data.AppName))
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = "appName is required." });
                        return;
                    }

                    // If a PIN is configured, changing the limit (including
                    // clearing it back to "no limit") requires it — otherwise the
                    // whole PIN/extend system is pointless, since anyone could just
                    // edit the limit directly instead of asking for an extension.
                    // No PIN configured yet = nothing to protect, edit freely (this
                    // is also how initial setup works before a PIN ever exists).
                    using var db = new AppDbContext();
                    var (hasPin, salt, hash) = PinService.GetPinInfo(db);
                    if (hasPin && !PinService.VerifyPin(data.Pin, salt, hash))
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsJsonAsync(new { error = "Incorrect PIN." });
                        return;
                    }

                    WeakReferenceMessenger.Default.Send(new FastApp.ViewModels.UpdateLimitCommand(
                        data.AppName, Math.Max(0, data.DailyLimitMinutes), data.StrictFocusMode));
                    await context.Response.WriteAsJsonAsync(new { success = true });
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // Whether a parental PIN is configured — never returns the PIN or its
            // hash, just a bool the dashboard uses to decide whether to show the
            // "Extend Today" control at all.
            app.MapGet("/api/settings/pin", async (HttpContext context) =>
            {
                using var db = new AppDbContext();
                var (hasPin, _, _) = PinService.GetPinInfo(db);
                await context.Response.WriteAsJsonAsync(new { HasPin = hasPin });
            });

            // Sets or changes the PIN. Deliberately doesn't require the old PIN to
            // change it — dashboard access is already the trust boundary here, this
            // isn't trying to be a hardened security control.
            app.MapPost("/api/settings/pin", async (HttpContext context) =>
            {
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    string body = await reader.ReadToEndAsync();
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var data = System.Text.Json.JsonSerializer.Deserialize<SetPinRequest>(body, options);

                    if (data == null || string.IsNullOrEmpty(data.Pin) || data.Pin.Length < 4)
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = "PIN must be at least 4 characters." });
                        return;
                    }

                    var (salt, hash) = PinService.HashPin(data.Pin);
                    using var db = new AppDbContext();
                    await db.Database.ExecuteSqlRawAsync("INSERT OR REPLACE INTO AppSettings (Key, Value) VALUES ('ParentPinSalt', {0})", salt);
                    await db.Database.ExecuteSqlRawAsync("INSERT OR REPLACE INTO AppSettings (Key, Value) VALUES ('ParentPinHash', {0})", hash);

                    await context.Response.WriteAsJsonAsync(new { success = true });
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // PIN-gated time extension for a blocked (or about-to-be-blocked) app.
            // Verifies the PIN here (pure data lookup — no live app state needed for
            // that part), and only messages the live WPF instance once it's confirmed
            // correct, same remote-control pattern as /api/update-limit.
            app.MapPost("/api/extend-limit", async (HttpContext context) =>
            {
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    string body = await reader.ReadToEndAsync();
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var data = System.Text.Json.JsonSerializer.Deserialize<ExtendLimitRequest>(body, options);

                    if (data == null || string.IsNullOrEmpty(data.AppName) || data.ExtraMinutes <= 0)
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = "appName and a positive extraMinutes are required." });
                        return;
                    }

                    using var db = new AppDbContext();
                    var (hasPin, salt, hash) = PinService.GetPinInfo(db);
                    if (!hasPin)
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = "No PIN is set yet — set one in Settings first." });
                        return;
                    }

                    if (!PinService.VerifyPin(data.Pin, salt, hash))
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsJsonAsync(new { error = "Incorrect PIN." });
                        return;
                    }

                    WeakReferenceMessenger.Default.Send(new FastApp.ViewModels.GrantExtensionCommand(data.AppName, data.ExtraMinutes));
                    await context.Response.WriteAsJsonAsync(new { success = true });
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            app.MapGet("/api/insights", async (string date, HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    DateTime targetDate = string.IsNullOrEmpty(date) ? DateTime.Today : DateTime.Parse(date).Date;
                    var hiddenApps = GetHiddenApps(db);
                    var recentSessions = await db.SessionLogs.Where(s => s.StartTime >= targetDate.AddDays(-30) && s.StartTime < targetDate.AddDays(1) && !hiddenApps.Contains(s.AppName)).ToListAsync();
                    var targetDaySessions = recentSessions.Where(s => s.StartTime >= targetDate && s.StartTime < targetDate.AddDays(1)).ToList();

                    var longestBlock = targetDaySessions.Any() ? targetDaySessions.Max(s => (s.EndTime - s.StartTime).TotalMinutes) : 0;
                    var avgSpan = targetDaySessions.Any() ? targetDaySessions.Average(s => (s.EndTime - s.StartTime).TotalMinutes) : 0;
                    var heatmap = recentSessions.GroupBy(s => new { s.StartTime.DayOfWeek, s.StartTime.Hour }).Select(g => new { DayIndex = (int)g.Key.DayOfWeek, Hour = g.Key.Hour, TotalMinutes = Math.Round(g.Sum(s => (s.EndTime - s.StartTime).TotalMinutes), 1) }).ToList();

                    // --- PRODUCTIVITY RHYTHM & FATIGUE MATH ---
                    var categoryMap = await GetAppCategoriesSafely(db);
                    // User-editable (see /api/settings/category-classification), not a
                    // fixed guess — reflects whatever categories this user actually has.
                    var classification = GetCategoryClassification(db);
                    string ClassOf(string appName) => classification.GetValueOrDefault(categoryMap.GetValueOrDefault(appName, "Other"), "neutral");

                    // 1. Group the last 30 days of sessions by the Hour of the Day (0-23)
                    var rhythm = Enumerable.Range(0, 24).Select(hour => {
                        var hourSessions = recentSessions.Where(s => s.StartTime.Hour == hour).ToList();
                        return new
                        {
                            Hour = hour,
                            Work = Math.Round(hourSessions.Where(s => ClassOf(s.AppName) == "work").Sum(s => (s.EndTime - s.StartTime).TotalMinutes), 1),
                            Play = Math.Round(hourSessions.Where(s => ClassOf(s.AppName) == "play").Sum(s => (s.EndTime - s.StartTime).TotalMinutes), 1)
                        };
                    }).ToList();

                    // 2. Group the last 30 days of sessions by Day of the Week
                    var fatigue = Enumerable.Range(0, 7).Select(d => {
                        var daySessions = recentSessions.Where(s => (int)s.StartTime.DayOfWeek == d).ToList();
                        return new
                        {
                            Day = ((DayOfWeek)d).ToString().Substring(0, 3), // e.g. "Mon"
                            DayIndex = d == 0 ? 7 : d, // Shift Sunday (0) to end of week (7) for correct visual sorting
                            AvgMinutes = daySessions.Any() ? Math.Round(daySessions.Average(s => (s.EndTime - s.StartTime).TotalMinutes), 1) : 0
                        };
                    }).OrderBy(x => x.DayIndex).ToList();

                    await context.Response.WriteAsJsonAsync(new
                    {
                        LongestBlock = longestBlock,
                        AverageSpan = avgSpan,
                        Heatmap = heatmap,
                        Rhythm = rhythm,
                        Fatigue = fatigue
                    });
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // --- NEW: TIMELINE ENDPOINT (24-Hour Format) ---
            app.MapGet("/api/timeline", async (string date, HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    DateTime targetDate = string.IsNullOrEmpty(date) ? DateTime.Today : DateTime.Parse(date).Date;
                    var hiddenApps = GetHiddenApps(db);
                    var categoryMap = await GetAppCategoriesSafely(db);

                    var sessions = await db.SessionLogs
                        .Where(s => s.StartTime >= targetDate && s.StartTime < targetDate.AddDays(1) && s.AppName != "SYSTEM_PC" && !hiddenApps.Contains(s.AppName))
                        .ToListAsync();

                    var payload = sessions.Select(s => new {
                        AppName = s.AppName,
                        Category = categoryMap.GetValueOrDefault(s.AppName, "Other"),
                        // Force 24-Hour Format (HH instead of hh)
                        Start = s.StartTime.ToString("HH:mm"),
                        End = s.EndTime.ToString("HH:mm"),
                        DurationMinutes = (s.EndTime - s.StartTime).TotalMinutes,
                        StartMinutes = s.StartTime.TimeOfDay.TotalMinutes
                    }).OrderBy(s => s.StartMinutes).ToList();

                    await context.Response.WriteAsJsonAsync(payload);
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // ---- /api/recent-sessions?limit=&offset= ----------------------------------
            // Raw chronological app-switch feed (not aggregated by day), paged
            // newest-first, for the Activity tab's scrollable log.
            app.MapGet("/api/recent-sessions", async (int? limit, int? offset, HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    var hiddenApps = GetHiddenApps(db);
                    var categoryMap = await GetAppCategoriesSafely(db);
                    int take = Math.Clamp(limit ?? 50, 1, 200);
                    int skip = Math.Max(offset ?? 0, 0);

                    var query = db.SessionLogs.Where(s => s.AppName != "SYSTEM_PC" && !hiddenApps.Contains(s.AppName));
                    int totalCount = await query.CountAsync();

                    var sessions = await query
                        .OrderByDescending(s => s.StartTime)
                        .Skip(skip)
                        .Take(take)
                        .ToListAsync();

                    var payload = sessions.Select(s => new
                    {
                        AppName = s.AppName,
                        Category = categoryMap.GetValueOrDefault(s.AppName, "Other"),
                        StartTime = s.StartTime.ToString("yyyy-MM-ddTHH:mm:ss"),
                        EndTime = s.EndTime.ToString("yyyy-MM-ddTHH:mm:ss"),
                        DurationMinutes = Math.Round((s.EndTime - s.StartTime).TotalMinutes, 1),
                        WindowTitle = s.WindowTitle // null unless CaptureWindowTitles was on when this session was recorded
                    }).ToList();

                    await context.Response.WriteAsJsonAsync(new
                    {
                        Sessions = payload,
                        TotalCount = totalCount,
                        Offset = skip,
                        Limit = take
                    });
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // --- NEW: ALL APPLICATIONS ENDPOINT (Added Category Support) ---
            // --- NEW: ALL APPLICATIONS ENDPOINT (Shows Hidden Apps) ---
            app.MapGet("/api/all-apps", async (HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    var categoryMap = await GetAppCategoriesSafely(db);

                    var logs = await db.DailyLogs
                        .Where(l => l.AppName != "SYSTEM_PC") // Removed hidden apps filter
                        .GroupBy(l => l.AppName)
                        .Select(g => new {
                            AppName = g.Key,
                            TotalRuntimeTicks = g.Sum(x => (long?)x.TimeSpentTicks) ?? 0,
                            TotalFocusTicks = g.Sum(x => (long?)x.TimeFocusedTicks) ?? 0,
                            TotalAfkTicks = g.Sum(x => (long?)x.AfkTimeSpentTicks) ?? 0
                        }).ToListAsync();

                    var sessionData = await db.SessionLogs
                        .Where(s => s.AppName != "SYSTEM_PC") // Removed hidden apps filter
                        .Select(s => new { s.AppName, s.StartTime, s.EndTime })
                        .ToListAsync();

                    var maxSessions = sessionData.GroupBy(s => s.AppName)
                        .ToDictionary(g => g.Key, g => g.Max(s => (s.EndTime - s.StartTime).TotalMinutes));

                    var result = logs.Select(l => new {
                        AppName = l.AppName,
                        Category = categoryMap.GetValueOrDefault(l.AppName, "Other"),
                        TotalFocus = TimeSpan.FromTicks(l.TotalFocusTicks).TotalMinutes,
                        TotalRuntime = TimeSpan.FromTicks(l.TotalRuntimeTicks).TotalMinutes,
                        TotalAfk = TimeSpan.FromTicks(l.TotalAfkTicks).TotalMinutes,
                        LongestSession = maxSessions.GetValueOrDefault(l.AppName, 0)
                    }).OrderBy(x => x.AppName).ToList();

                    await context.Response.WriteAsJsonAsync(result);
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // APP DETAILS - UPGRADED WITH ALGORITHMS & PATH
            app.MapGet("/api/app-details", async (string appName, HttpContext context) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(appName)) return;
                    using var db = new AppDbContext();
                    DateTime targetDate = DateTime.Today;
                    var allTimeLogs = await db.DailyLogs.Where(l => l.AppName == appName).ToListAsync();
                    var macroCount = await db.MacroEventLogs.CountAsync(m => m.AppName == appName);

                    var last30DaysLogs = allTimeLogs.Where(l => l.Date >= targetDate.AddDays(-30)).ToList();
                    var history = last30DaysLogs.OrderBy(l => l.Date).Select(l => new { Date = l.Date.ToString("MMM dd"), FocusedMinutes = Math.Round(l.TimeFocused.TotalMinutes, 1) }).ToList();
                    var sessions = await db.SessionLogs.Where(s => s.AppName == appName && s.StartTime >= targetDate.AddDays(-30)).ToListAsync();

                    var avgSession = sessions.Any() ? sessions.Average(s => (s.EndTime - s.StartTime).TotalMinutes) : 0;
                    var maxStreak = sessions.Any() ? sessions.Max(s => (s.EndTime - s.StartTime).TotalMinutes) : 0;
                    var peakHourGroup = sessions.GroupBy(s => s.StartTime.Hour).OrderByDescending(g => g.Sum(s => (s.EndTime - s.StartTime).TotalMinutes)).FirstOrDefault();

                    // --- NEW: 30-DAY CONSISTENCY ---
                    int daysActiveInLast30 = last30DaysLogs.Select(l => l.Date).Distinct().Count();
                    double consistencyPct = Math.Round((daysActiveInLast30 / 30.0) * 100, 1);

                    // --- NEW: USAGE PATTERN ALGORITHM ---
                    double weekdayFocus = last30DaysLogs.Where(l => l.Date.DayOfWeek >= DayOfWeek.Monday && l.Date.DayOfWeek <= DayOfWeek.Friday).Sum(l => l.TimeFocused.TotalMinutes);
                    double weekendFocus = last30DaysLogs.Where(l => l.Date.DayOfWeek == DayOfWeek.Saturday || l.Date.DayOfWeek == DayOfWeek.Sunday).Sum(l => l.TimeFocused.TotalMinutes);
                    double totalPatternFocus = weekdayFocus + weekendFocus;

                    string usagePattern = "Insufficient Data";
                    if (totalPatternFocus > 0)
                    {
                        double weekdayPct = weekdayFocus / totalPatternFocus;
                        if (weekdayPct >= 0.85) usagePattern = "Heavy Weekday Bias";
                        else if (weekdayPct >= 0.65) usagePattern = "Weekday Bias";
                        else if (weekdayPct <= 0.20) usagePattern = "Heavy Weekend Bias";
                        else if (weekdayPct <= 0.40) usagePattern = "Weekend Bias";
                        else usagePattern = "Mixed / Balanced";
                    }

                    // --- DAILY LIMIT LOOKUP (Safe Version) ---
                    // ExecutablePath used to be read here and returned in the
                    // response. Nothing in wwwroot/ ever displayed it (the feature
                    // it was added for was dropped), so it was handing full local
                    // filesystem paths to every caller of this endpoint for no
                    // reason. Dropped along with /api/open-folder.
                    int dailyLimitMinutes = 0;
                    bool strictFocusMode = false;
                    int todayBonusMinutes = 0;
                    try
                    {
                        var managedApp = await db.ManagedApps.FirstOrDefaultAsync(m => m.Name == appName);
                        if (managedApp != null)
                        {
                            dailyLimitMinutes = managedApp.DailyLimitMinutes;
                            strictFocusMode = managedApp.StrictFocusMode;
                            // Same self-expiring check the tracker uses: a bonus not
                            // stamped for today is stale, reads as zero.
                            todayBonusMinutes = managedApp.BonusMinutesDate?.Date == DateTime.Today ? managedApp.TodayBonusMinutes : 0;
                        }
                    }
                    catch
                    {
                        // Safely catches the SQL error if these columns don't exist yet
                    }

                    double todayMinutes = allTimeLogs.FirstOrDefault(l => l.Date == targetDate)?.TimeSpent.TotalMinutes ?? 0;

                    // Averages & Personal Records (Unchanged)
                    DateTime startOfWeek = GetMondayStartOfWeek(targetDate);
                    DateTime startOfPrevWeek = startOfWeek.AddDays(-7);
                    double weekAvg = allTimeLogs.Where(l => l.Date >= startOfWeek && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours) / 7.0;
                    double prevWeekAvg = allTimeLogs.Where(l => l.Date >= startOfPrevWeek && l.Date < startOfWeek).Sum(l => l.TimeFocused.TotalHours) / 7.0;
                    double monthAvg = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-30) && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours) / 30.0;
                    double prevMonthAvg = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-60) && l.Date <= targetDate.AddDays(-30)).Sum(l => l.TimeFocused.TotalHours) / 30.0;
                    double yearAvg = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-365) && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours) / 365.0;
                    double prevYearAvg = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-730) && l.Date <= targetDate.AddDays(-365)).Sum(l => l.TimeFocused.TotalHours) / 365.0;

                    var maxFocusDay = allTimeLogs.OrderByDescending(l => l.TimeFocused).FirstOrDefault();
                    var maxRunningDay = allTimeLogs.OrderByDescending(l => l.TimeSpent).FirstOrDefault();
                    string maxFocusDayText = maxFocusDay != null && maxFocusDay.TimeFocused.TotalMinutes > 0 ? $"{maxFocusDay.Date:MMM dd} ({Math.Round(maxFocusDay.TimeFocused.TotalHours, 1)}h)" : "N/A";
                    string maxRunningDayText = maxRunningDay != null && maxRunningDay.TimeSpent.TotalMinutes > 0 ? $"{maxRunningDay.Date:MMM dd} ({Math.Round(maxRunningDay.TimeSpent.TotalHours, 1)}h)" : "N/A";

                    // DailyLogs is never pruned by retention (unlike SessionLogs), so
                    // MIN/MAX Date here reliably covers the app's whole tracked history,
                    // not just whatever's left in the retention window.
                    string firstSeenText = allTimeLogs.Any() ? allTimeLogs.Min(l => l.Date).ToString("MMM dd, yyyy") : "N/A";
                    string lastSeenText = allTimeLogs.Any() ? allTimeLogs.Max(l => l.Date).ToString("MMM dd, yyyy") : "N/A";

                    // --- STREAKS: longest and current consecutive-day usage runs ---
                    // A DailyLogs row only exists for a day where the app actually
                    // accumulated tracked time (see the tracker's flush), so "distinct
                    // dates with a row" is exactly "days this app was used."
                    var usedDates = allTimeLogs.Select(l => l.Date.Date).Distinct().ToHashSet();
                    var sortedUsedDates = usedDates.OrderBy(d => d).ToList();

                    int longestStreak = 0;
                    DateTime longestStreakStart = default, longestStreakEnd = default;
                    if (sortedUsedDates.Count > 0)
                    {
                        int runLength = 1;
                        DateTime runStart = sortedUsedDates[0];
                        longestStreak = 1;
                        longestStreakStart = runStart;
                        longestStreakEnd = sortedUsedDates[0];

                        for (int i = 1; i < sortedUsedDates.Count; i++)
                        {
                            if ((sortedUsedDates[i] - sortedUsedDates[i - 1]).Days == 1)
                            {
                                runLength++;
                            }
                            else
                            {
                                runStart = sortedUsedDates[i];
                                runLength = 1;
                            }
                            if (runLength > longestStreak)
                            {
                                longestStreak = runLength;
                                longestStreakStart = runStart;
                                longestStreakEnd = sortedUsedDates[i];
                            }
                        }
                    }
                    string longestStreakRange = longestStreak > 0
                        ? (longestStreakStart == longestStreakEnd
                            ? longestStreakStart.ToString("MMM d, yyyy")
                            : $"{longestStreakStart:MMM d} – {longestStreakEnd:MMM d, yyyy}")
                        : "N/A";

                    // Current streak anchors on today if used already, otherwise
                    // yesterday — a grace period so not having opened the app yet
                    // today (which isn't over) doesn't look like the streak already
                    // broke. Anything older than yesterday means it's actually broken.
                    int currentStreak = 0;
                    DateTime currentStreakStart = default;
                    DateTime? streakAnchor = usedDates.Contains(targetDate) ? targetDate
                        : usedDates.Contains(targetDate.AddDays(-1)) ? targetDate.AddDays(-1)
                        : (DateTime?)null;
                    if (streakAnchor.HasValue)
                    {
                        currentStreak = 1;
                        DateTime cursor = streakAnchor.Value;
                        currentStreakStart = cursor;
                        while (usedDates.Contains(cursor.AddDays(-1)))
                        {
                            currentStreak++;
                            cursor = cursor.AddDays(-1);
                            currentStreakStart = cursor;
                        }
                    }
                    string currentStreakStartText = currentStreak > 0 ? currentStreakStart.ToString("MMM d, yyyy") : "N/A";
                    // The current run is part of the same data the longest-streak scan
                    // above walked, so it can never exceed it — equal means it IS the
                    // record, not just close to it.
                    bool isCurrentStreakBest = currentStreak > 0 && currentStreak >= longestStreak;

                    // --- PERIOD COMPARISONS: total (not average) focused hours, this
                    // period vs the previous one, at each granularity. Same boundary
                    // math /api/overview uses for its whole-PC comparison, scoped here
                    // to just this app's rows. ---
                    double todayFocusH = allTimeLogs.Where(l => l.Date == targetDate).Sum(l => l.TimeFocused.TotalHours);
                    double yesterdayFocusH = allTimeLogs.Where(l => l.Date == targetDate.AddDays(-1)).Sum(l => l.TimeFocused.TotalHours);
                    double thisWeekFocusH = allTimeLogs.Where(l => l.Date >= startOfWeek && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours);
                    double lastWeekFocusH = allTimeLogs.Where(l => l.Date >= startOfPrevWeek && l.Date < startOfWeek).Sum(l => l.TimeFocused.TotalHours);
                    double thisMonthFocusH = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-30) && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours);
                    double lastMonthFocusH = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-60) && l.Date <= targetDate.AddDays(-30)).Sum(l => l.TimeFocused.TotalHours);
                    double thisYearFocusH = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-365) && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours);
                    double lastYearFocusH = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-730) && l.Date <= targetDate.AddDays(-365)).Sum(l => l.TimeFocused.TotalHours);

                    // --- MILESTONE TIERS: the day cumulative focused hours first crossed
                    // each threshold. Ladder comes from MilestoneTiers.All (the single
                    // definition) and is sent to the frontend alongside these dates, so
                    // the drawer renders whatever the backend actually scored rather
                    // than a hardcoded copy that could disagree with it. Null entries
                    // mean that tier hasn't been reached yet.
                    var milestoneTiers = MilestoneTiers.All;
                    string?[] milestoneDates = new string?[milestoneTiers.Length];
                    {
                        double runningHours = 0;
                        int thresholdIdx = 0;
                        foreach (var log in allTimeLogs.OrderBy(l => l.Date))
                        {
                            runningHours += log.TimeFocused.TotalHours;
                            while (thresholdIdx < milestoneTiers.Length && runningHours >= milestoneTiers[thresholdIdx].Hours)
                            {
                                milestoneDates[thresholdIdx] = log.Date.ToString("MMM d, yyyy");
                                thresholdIdx++;
                            }
                        }
                    }

                    await context.Response.WriteAsJsonAsync(new
                    {
                        AppName = appName,
                        MilestoneTiers = milestoneTiers, // ladder definition, so the frontend keeps no copy of its own
                        Consistency = consistencyPct, // Added
                        UsagePattern = usagePattern, // Added
                        MilestoneDates = milestoneDates,
                        AllTimeFocused = Math.Round(allTimeLogs.Sum(l => l.TimeFocused.TotalHours), 1),
                        AllTimeRunning = Math.Round(allTimeLogs.Sum(l => l.TimeSpent.TotalHours), 1),
                        AllTimeAfk = Math.Round(allTimeLogs.Sum(l => l.AfkTimeSpent.TotalHours), 1),
                        TotalMacros = macroCount,
                        DaysActive = daysActiveInLast30,
                        AvgSession = Math.Round(avgSession, 1),
                        MaxStreak = Math.Round(maxStreak, 1),
                        PeakHour = peakHourGroup != null ? $"{peakHourGroup.Key}:00" : "N/A",
                        WeekAvg = weekAvg,
                        PrevWeekAvg = prevWeekAvg,
                        MonthAvg = monthAvg,
                        PrevMonthAvg = prevMonthAvg,
                        YearAvg = yearAvg,
                        PrevYearAvg = prevYearAvg,
                        MaxFocusDay = maxFocusDayText,
                        MaxRunningDay = maxRunningDayText,
                        FirstSeen = firstSeenText,
                        LastSeen = lastSeenText,
                        LongestStreak = longestStreak,
                        LongestStreakRange = longestStreakRange,
                        CurrentStreak = currentStreak,
                        CurrentStreakStart = currentStreakStartText,
                        IsCurrentStreakBest = isCurrentStreakBest,
                        TodayFocusHours = todayFocusH,
                        YesterdayFocusHours = yesterdayFocusH,
                        ThisWeekFocusHours = thisWeekFocusH,
                        LastWeekFocusHours = lastWeekFocusH,
                        ThisMonthFocusHours = thisMonthFocusH,
                        LastMonthFocusHours = lastMonthFocusH,
                        ThisYearFocusHours = thisYearFocusH,
                        LastYearFocusHours = lastYearFocusH,
                        History = history,
                        TodayMinutes = Math.Round(todayMinutes, 1),
                        DailyLimitMinutes = dailyLimitMinutes,
                        StrictFocusMode = strictFocusMode,
                        TodayBonusMinutes = todayBonusMinutes
                    });
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // Backing data for the Period Comparison rows' expand-to-chart — fetched
            // lazily only when a row is actually opened, not bundled into every
            // /api/app-details call. Granularity matches what's natural for each
            // period (hourly for a single day, daily for a week/month, monthly for
            // a year) and current/previous are aligned by *position within the
            // period* rather than calendar date, so e.g. "day 3 of this month" lines
            // up with "day 3 of last month" even though months differ in length.
            app.MapGet("/api/app-period-breakdown", async (string appName, string period, HttpContext context) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(appName)) { context.Response.StatusCode = 400; return; }
                    using var db = new AppDbContext();
                    DateTime targetDate = DateTime.Today;
                    string periodKind = (period ?? "week").ToLowerInvariant();

                    var labels = new List<string>();
                    var current = new List<double>();
                    var previous = new List<double>();

                    if (periodKind == "today")
                    {
                        // No time-of-day granularity in DailyLogs, so this comes from
                        // SessionLogs instead — bucketed by StartTime.Hour, same
                        // convention the existing PeakHour computation already uses.
                        DateTime yesterday = targetDate.AddDays(-1);
                        var sessions = await db.SessionLogs
                            .Where(s => s.AppName == appName && s.StartTime >= yesterday && s.StartTime < targetDate.AddDays(1))
                            .ToListAsync();

                        var todayByHour = sessions.Where(s => s.StartTime.Date == targetDate)
                            .GroupBy(s => s.StartTime.Hour)
                            .ToDictionary(g => g.Key, g => g.Sum(s => (s.EndTime - s.StartTime).TotalMinutes));
                        var yesterdayByHour = sessions.Where(s => s.StartTime.Date == yesterday)
                            .GroupBy(s => s.StartTime.Hour)
                            .ToDictionary(g => g.Key, g => g.Sum(s => (s.EndTime - s.StartTime).TotalMinutes));

                        for (int h = 0; h < 24; h++)
                        {
                            labels.Add($"{h:D2}:00");
                            current.Add(Math.Round(todayByHour.GetValueOrDefault(h), 1));
                            previous.Add(Math.Round(yesterdayByHour.GetValueOrDefault(h), 1));
                        }
                    }
                    else if (periodKind == "week")
                    {
                        DateTime startOfWeek = GetMondayStartOfWeek(targetDate);
                        DateTime startOfPrevWeek = startOfWeek.AddDays(-7);
                        var logs = await db.DailyLogs.Where(l => l.AppName == appName && l.Date >= startOfPrevWeek && l.Date <= targetDate).ToListAsync();
                        var byDate = logs.ToDictionary(l => l.Date, l => l.TimeFocused.TotalMinutes);

                        string[] dayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
                        for (int i = 0; i < 7; i++)
                        {
                            labels.Add(dayNames[i]);
                            current.Add(Math.Round(byDate.GetValueOrDefault(startOfWeek.AddDays(i)), 1));
                            previous.Add(Math.Round(byDate.GetValueOrDefault(startOfPrevWeek.AddDays(i)), 1));
                        }
                    }
                    else if (periodKind == "month")
                    {
                        DateTime thisMonthStart = targetDate.AddDays(-29);
                        DateTime lastMonthStart = targetDate.AddDays(-59);
                        var logs = await db.DailyLogs.Where(l => l.AppName == appName && l.Date >= lastMonthStart && l.Date <= targetDate).ToListAsync();
                        var byDate = logs.ToDictionary(l => l.Date, l => l.TimeFocused.TotalMinutes);

                        for (int i = 0; i < 30; i++)
                        {
                            labels.Add((i + 1).ToString());
                            current.Add(Math.Round(byDate.GetValueOrDefault(thisMonthStart.AddDays(i)), 1));
                            previous.Add(Math.Round(byDate.GetValueOrDefault(lastMonthStart.AddDays(i)), 1));
                        }
                    }
                    else if (periodKind == "year")
                    {
                        DateTime thisYearStart = targetDate.AddDays(-364);
                        DateTime lastYearStart = targetDate.AddDays(-729);
                        var logs = await db.DailyLogs.Where(l => l.AppName == appName && l.Date >= lastYearStart && l.Date <= targetDate).ToListAsync();

                        for (int i = 0; i < 12; i++)
                        {
                            DateTime curMonthStart = thisYearStart.AddMonths(i);
                            DateTime curMonthEnd = thisYearStart.AddMonths(i + 1);
                            DateTime prevMonthStart = lastYearStart.AddMonths(i);
                            DateTime prevMonthEnd = lastYearStart.AddMonths(i + 1);

                            labels.Add(curMonthStart.ToString("MMM"));
                            // Minutes, same unit as the other three periods (today/
                            // week/month), not hours — kept consistent so the frontend
                            // doesn't need to know which unit a given period uses.
                            current.Add(Math.Round(logs.Where(l => l.Date >= curMonthStart && l.Date < curMonthEnd).Sum(l => l.TimeFocused.TotalMinutes), 1));
                            previous.Add(Math.Round(logs.Where(l => l.Date >= prevMonthStart && l.Date < prevMonthEnd).Sum(l => l.TimeFocused.TotalMinutes), 1));
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = "period must be today, week, month, or year." });
                        return;
                    }

                    await context.Response.WriteAsJsonAsync(new { Labels = labels, Current = current, Previous = previous });
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // Rolling usage trend — last 12 buckets at day/week/month granularity,
            // no comparison, just the plain history. Distinct from the "period vs
            // previous" semantics /api/app-period-breakdown uses: week uses
            // Monday-start weeks and month uses actual calendar months (not rolling
            // 30-day windows like the rest of the app's "month" comparisons), since
            // a "month by month" trend reads far more naturally against real
            // calendar months than arbitrary 30-day chunks.
            app.MapGet("/api/app-usage-trend", async (string appName, string granularity, HttpContext context) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(appName)) { context.Response.StatusCode = 400; return; }
                    using var db = new AppDbContext();
                    DateTime today = DateTime.Today;
                    string gran = (granularity ?? "day").ToLowerInvariant();

                    var labels = new List<string>();
                    var values = new List<double>();

                    // Label formats match the Periods tab exactly (day: "d MMMM yyyy",
                    // week: "Week N", month: "MMMM yyyy") so a bucket here can be found
                    // by the same name over in Periods instead of needing translation.
                    if (gran == "day")
                    {
                        DateTime start = today.AddDays(-11);
                        var logs = await db.DailyLogs.Where(l => l.AppName == appName && l.Date >= start && l.Date <= today).ToListAsync();
                        var byDate = logs.ToDictionary(l => l.Date, l => l.TimeFocused.TotalMinutes);
                        for (int i = 0; i < 12; i++)
                        {
                            DateTime d = start.AddDays(i);
                            labels.Add(d.ToString("d MMMM yyyy"));
                            values.Add(Math.Round(byDate.GetValueOrDefault(d), 1));
                        }
                    }
                    else if (gran == "week")
                    {
                        DateTime thisWeekStart = GetMondayStartOfWeek(today);
                        DateTime rangeStart = thisWeekStart.AddDays(-11 * 7);
                        var logs = await db.DailyLogs.Where(l => l.AppName == appName && l.Date >= rangeStart && l.Date <= today).ToListAsync();
                        for (int i = 0; i < 12; i++)
                        {
                            DateTime weekStart = rangeStart.AddDays(i * 7);
                            DateTime weekEnd = weekStart.AddDays(7);
                            labels.Add($"Week {System.Globalization.ISOWeek.GetWeekOfYear(weekStart)}");
                            values.Add(Math.Round(logs.Where(l => l.Date >= weekStart && l.Date < weekEnd).Sum(l => l.TimeFocused.TotalMinutes), 1));
                        }
                    }
                    else if (gran == "month")
                    {
                        DateTime thisMonthStart = new DateTime(today.Year, today.Month, 1);
                        DateTime rangeStart = thisMonthStart.AddMonths(-11);
                        var logs = await db.DailyLogs.Where(l => l.AppName == appName && l.Date >= rangeStart && l.Date <= today).ToListAsync();
                        for (int i = 0; i < 12; i++)
                        {
                            DateTime monthStart = rangeStart.AddMonths(i);
                            DateTime monthEnd = monthStart.AddMonths(1);
                            labels.Add(monthStart.ToString("MMMM yyyy"));
                            values.Add(Math.Round(logs.Where(l => l.Date >= monthStart && l.Date < monthEnd).Sum(l => l.TimeFocused.TotalMinutes), 1));
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = "granularity must be day, week, or month." });
                        return;
                    }

                    await context.Response.WriteAsJsonAsync(new { Labels = labels, Values = values });
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            app.MapGet("/api/periods", async (string type, HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    var hiddenApps = GetHiddenApps(db);
                    string periodKind = (type ?? "week").ToLowerInvariant();

                    // Same 730-day floor as /api/overview — without it this pulls every
                    // SYSTEM_PC row since install on every open of (and every ~12s poll
                    // on) the Periods tab, growing forever with no ceiling.
                    DateTime historyFloor = DateTime.Today.AddDays(-730);
                    var systemLogs = await db.DailyLogs.Where(l => l.AppName == "SYSTEM_PC" && l.Date >= historyFloor).ToListAsync();
                    if (systemLogs.Count == 0)
                    {
                        await context.Response.WriteAsJsonAsync(Array.Empty<object>());
                        return;
                    }

                    var appLogs = await db.DailyLogs
                        .Where(l => l.AppName != "SYSTEM_PC" && l.Date >= historyFloor && !hiddenApps.Contains(l.AppName))
                        .ToListAsync();

                    // Bucket key -> (start, end) of that day/week/month/year
                    var buckets = new Dictionary<string, (DateTime start, DateTime end, string label)>();
                    foreach (var log in systemLogs)
                    {
                        DateTime start, end; string key, label;
                        switch (periodKind)
                        {
                            case "day":
                                start = log.Date;
                                end = log.Date;
                                key = start.ToString("yyyy-MM-dd");
                                label = start.ToString("d MMMM yyyy");
                                break;
                            case "month":
                                start = new DateTime(log.Date.Year, log.Date.Month, 1);
                                end = start.AddMonths(1).AddDays(-1);
                                key = start.ToString("yyyy-MM");
                                label = start.ToString("MMMM yyyy");
                                break;
                            case "year":
                                start = new DateTime(log.Date.Year, 1, 1);
                                end = start.AddYears(1).AddDays(-1);
                                key = start.Year.ToString();
                                label = start.Year.ToString();
                                break;
                            case "week":
                            default:
                                start = GetMondayStartOfWeek(log.Date);
                                end = start.AddDays(6);
                                key = start.ToString("yyyy-MM-dd");
                                label = $"Week {System.Globalization.ISOWeek.GetWeekOfYear(start)}";
                                break;
                        }
                        buckets[key] = (start, end, label);
                    }

                    var results = buckets.Select(kvp =>
                    {
                        var (start, end, label) = kvp.Value;
                        var inRange = systemLogs.Where(l => l.Date >= start && l.Date <= end).ToList();
                        double totalMins = inRange.Sum(l => l.TimeFocused.TotalMinutes);
                        double afkMins = inRange.Sum(l => l.AfkTimeSpent.TotalMinutes);
                        var mostUsed = appLogs.Where(l => l.Date >= start && l.Date <= end)
                            .GroupBy(l => l.AppName)
                            .Select(g => new { Name = g.Key, Mins = g.Sum(x => x.TimeFocused.TotalMinutes) })
                            .OrderByDescending(x => x.Mins)
                            .FirstOrDefault();

                        return new
                        {
                            Key = kvp.Key,
                            Label = label,
                            StartDate = start.ToString("yyyy-MM-dd"),
                            EndDate = end.ToString("yyyy-MM-dd"),
                            TotalFocusMinutes = Math.Round(totalMins, 1),
                            TotalAfkMinutes = Math.Round(afkMins, 1),
                            MostUsedApp = mostUsed?.Name ?? "—"
                        };
                    })
                    .Where(p => p.TotalFocusMinutes > 0)
                    .OrderByDescending(p => p.TotalFocusMinutes)
                    .ToList();

                    var ranked = results.Select((p, i) => new
                    {
                        p.Label,
                        p.StartDate,
                        p.EndDate,
                        p.TotalFocusMinutes,
                        p.TotalAfkMinutes,
                        p.MostUsedApp,
                        Rank = i + 1,
                        TotalPeriods = results.Count
                    })
                    .OrderByDescending(p => p.StartDate) // display most recent first
                    .ToList();

                    await context.Response.WriteAsJsonAsync(ranked);
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // ---- /api/period-detail?type=week|month&start=yyyy-MM-dd -----------------
            // Detail for one period: compares it (focus AND AFK) to the period
            // immediately before, immediately after, and the current (today's) period,
            // plus top apps and top categories for the chosen range.
            app.MapGet("/api/period-detail", async (string type, string start, HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    var hiddenApps = GetHiddenApps(db);
                    var appCategories = await GetAppCategoriesSafely(db);
                    string periodKind = (type ?? "week").ToLowerInvariant();

                    DateTime NormalizeStart(DateTime d) => periodKind switch
                    {
                        "day" => d,
                        "month" => new DateTime(d.Year, d.Month, 1),
                        "year" => new DateTime(d.Year, 1, 1),
                        _ => GetMondayStartOfWeek(d)
                    };

                    DateTime chosenStart = NormalizeStart(DateTime.Parse(start).Date);

                    (DateTime s, DateTime e) Range(DateTime periodStart) => periodKind switch
                    {
                        "day" => (periodStart, periodStart),
                        "month" => (periodStart, periodStart.AddMonths(1).AddDays(-1)),
                        "year" => (periodStart, periodStart.AddYears(1).AddDays(-1)),
                        _ => (periodStart, periodStart.AddDays(6))
                    };

                    DateTime PrevStart(DateTime periodStart) => periodKind switch
                    {
                        "day" => periodStart.AddDays(-1),
                        "month" => periodStart.AddMonths(-1),
                        "year" => periodStart.AddYears(-1),
                        _ => periodStart.AddDays(-7)
                    };

                    DateTime NextStart(DateTime periodStart) => periodKind switch
                    {
                        "day" => periodStart.AddDays(1),
                        "month" => periodStart.AddMonths(1),
                        "year" => periodStart.AddYears(1),
                        _ => periodStart.AddDays(7)
                    };

                    DateTime todayStart = NormalizeStart(DateTime.Today);

                    // Same 730-day floor as /api/periods (whose list is now bounded the
                    // same way, so ranking within this same window stays consistent with
                    // what that list actually shows) and /api/overview.
                    DateTime historyFloor = DateTime.Today.AddDays(-730);
                    var systemLogs = await db.DailyLogs.Where(l => l.AppName == "SYSTEM_PC" && l.Date >= historyFloor).ToListAsync();
                    var appLogs = await db.DailyLogs
                        .Where(l => l.AppName != "SYSTEM_PC" && l.Date >= historyFloor && !hiddenApps.Contains(l.AppName))
                        .ToListAsync();

                    object BuildSummary(DateTime periodStart, string label)
                    {
                        var (s, e) = Range(periodStart);
                        var inRange = systemLogs.Where(l => l.Date >= s && l.Date <= e).ToList();
                        if (!inRange.Any()) return null;
                        double mins = inRange.Sum(l => l.TimeFocused.TotalMinutes);
                        double afkMins = inRange.Sum(l => l.AfkTimeSpent.TotalMinutes);
                        double uptimeMins = inRange.Sum(l => l.TimeSpent.TotalMinutes);
                        return new
                        {
                            Label = label,
                            StartDate = s.ToString("yyyy-MM-dd"),
                            EndDate = e.ToString("yyyy-MM-dd"),
                            TotalFocusMinutes = Math.Round(mins, 1),
                            TotalAfkMinutes = Math.Round(afkMins, 1),
                            TotalUptimeMinutes = Math.Round(uptimeMins, 1)
                        };
                    }

                    string LabelFor(DateTime periodStart) => periodKind switch
                    {
                        "day" => periodStart.ToString("d MMMM yyyy"),
                        "month" => periodStart.ToString("MMMM yyyy"),
                        "year" => periodStart.Year.ToString(),
                        _ => $"Week {System.Globalization.ISOWeek.GetWeekOfYear(periodStart)}"
                    };

                    var (chosenS, chosenE) = Range(chosenStart);
                    var chosenRange = systemLogs.Where(l => l.Date >= chosenS && l.Date <= chosenE).ToList();
                    double chosenMins = chosenRange.Sum(l => l.TimeFocused.TotalMinutes);
                    double chosenAfkMins = chosenRange.Sum(l => l.AfkTimeSpent.TotalMinutes);
                    double chosenUptimeMins = chosenRange.Sum(l => l.TimeSpent.TotalMinutes);

                    // Rank against every period of this type that has data
                    var allBuckets = new HashSet<string>();
                    foreach (var log in systemLogs)
                    {
                        var bs = NormalizeStart(log.Date);
                        allBuckets.Add(bs.ToString("yyyy-MM-dd"));
                    }
                    var allTotals = allBuckets.Select(k =>
                    {
                        var bs = DateTime.Parse(k);
                        var (s, e) = Range(bs);
                        return systemLogs.Where(l => l.Date >= s && l.Date <= e).Sum(l => l.TimeFocused.TotalMinutes);
                    }).Where(m => m > 0).OrderByDescending(m => m).ToList();
                    int rank = allTotals.FindIndex(m => Math.Abs(m - chosenMins) < 0.01) + 1;
                    if (rank <= 0) rank = allTotals.Count + 1;

                    var topApps = appLogs.Where(l => l.Date >= chosenS && l.Date <= chosenE)
                        .GroupBy(l => l.AppName)
                        .Select(g => new { AppName = g.Key, FocusedMinutes = Math.Round(g.Sum(x => x.TimeFocused.TotalMinutes), 1) })
                        .OrderByDescending(x => x.FocusedMinutes).Take(8).ToList();

                    var topCategories = appLogs.Where(l => l.Date >= chosenS && l.Date <= chosenE)
                        .GroupBy(l => appCategories.ContainsKey(l.AppName) ? appCategories[l.AppName] : "Other")
                        .Select(g => new { Category = g.Key, FocusedMinutes = Math.Round(g.Sum(x => x.TimeFocused.TotalMinutes), 1) })
                        .OrderByDescending(x => x.FocusedMinutes).Take(8).ToList();

                    // Per-day focus breakdown for the chosen period's heatmap. Clipped at
                    // today so an in-progress week/month doesn't pad out with empty future days.
                    var days = new List<object>();
                    for (var d = chosenS; d <= chosenE && d <= DateTime.Today; d = d.AddDays(1))
                    {
                        var dayLog = chosenRange.FirstOrDefault(l => l.Date == d);
                        days.Add(new
                        {
                            Date = d.ToString("yyyy-MM-dd"),
                            FocusedMinutes = dayLog != null ? Math.Round(dayLog.TimeFocused.TotalMinutes, 1) : 0
                        });
                    }

                    // A "day" period's Daily Activity is the Timeline ribbon (individual
                    // app sessions, hour-by-hour), not a day-by-day breakdown — breaking
                    // a single day down into "days" would be circular. Same session shape
                    // /api/timeline already returns, computed here instead of making the
                    // frontend fire a second request for it.
                    List<object> daySessions = new List<object>();
                    if (periodKind == "day")
                    {
                        var sessionsForDay = await db.SessionLogs
                            .Where(s => s.StartTime >= chosenS && s.StartTime < chosenS.AddDays(1) && s.AppName != "SYSTEM_PC" && !hiddenApps.Contains(s.AppName))
                            .OrderBy(s => s.StartTime)
                            .ToListAsync();

                        daySessions = sessionsForDay.Select(s => (object)new
                        {
                            AppName = s.AppName,
                            Category = appCategories.GetValueOrDefault(s.AppName, "Other"),
                            Start = s.StartTime.ToString("HH:mm"),
                            End = s.EndTime.ToString("HH:mm"),
                            DurationMinutes = (s.EndTime - s.StartTime).TotalMinutes,
                            StartMinutes = s.StartTime.TimeOfDay.TotalMinutes,
                            WindowTitle = s.WindowTitle // null unless CaptureWindowTitles was on when this session was recorded
                        }).ToList();
                    }

                    var payload = new
                    {
                        Label = LabelFor(chosenStart),
                        StartDate = chosenS.ToString("yyyy-MM-dd"),
                        EndDate = chosenE.ToString("yyyy-MM-dd"),
                        TotalFocusMinutes = Math.Round(chosenMins, 1),
                        TotalAfkMinutes = Math.Round(chosenAfkMins, 1),
                        TotalUptimeMinutes = Math.Round(chosenUptimeMins, 1),
                        Rank = rank,
                        TotalPeriods = allTotals.Count,
                        Previous = BuildSummary(PrevStart(chosenStart), LabelFor(PrevStart(chosenStart))),
                        Next = BuildSummary(NextStart(chosenStart), LabelFor(NextStart(chosenStart))),
                        Current = chosenStart != todayStart ? BuildSummary(todayStart, LabelFor(todayStart)) : null,
                        TopApps = topApps,
                        TopCategories = topCategories,
                        Days = days,
                        DaySessions = daySessions
                    };

                    await context.Response.WriteAsJsonAsync(payload);
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // ---- /api/wrapped/available and /api/wrapped?type=week|month|year --------
            // A Spotify-Wrapped-style recap of the *current* week/month/year, always live
            // (never a one-time reveal) — an in-progress period is compared to the same
            // elapsed portion of the previous one, not its full total, so checking on a
            // Tuesday doesn't read as a huge decline against a completed prior week.
            // Deliberately no historical browsing here (no `start` param): Wrapped is a
            // small, curated "here's what's ready" moment, not another way to page through
            // old periods — that's what the Periods tab is for.
            app.MapGet("/api/wrapped/available", async (HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    var hiddenApps = GetHiddenApps(db);
                    var appCategories = await GetAppCategoriesSafely(db);

                    var entries = new List<object>();
                    foreach (var kind in new[] { "week", "month", "year" })
                    {
                        var w = await BuildWrappedAsync(db, hiddenApps, appCategories, kind);
                        if (w == null) continue;
                        entries.Add(new { Type = kind, Label = w.Label, Teaser = w.Teaser });
                    }
                    await context.Response.WriteAsJsonAsync(entries);
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            app.MapGet("/api/wrapped", async (string type, HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    var hiddenApps = GetHiddenApps(db);
                    var appCategories = await GetAppCategoriesSafely(db);
                    string periodKind = (type ?? "week").ToLowerInvariant();
                    if (periodKind != "week" && periodKind != "month" && periodKind != "year")
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = "type must be week, month, or year." });
                        return;
                    }

                    var wrapped = await BuildWrappedAsync(db, hiddenApps, appCategories, periodKind);
                    if (wrapped == null)
                    {
                        await context.Response.WriteAsJsonAsync(new { error = "No data yet for this period." });
                        return;
                    }
                    await context.Response.WriteAsJsonAsync(wrapped);
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });


            // /api/open-folder used to live here: it took an arbitrary filesystem
            // path from the request body and handed it to explorer.exe. It was
            // built for a reveal-in-Explorer feature that was never shipped, so
            // nothing in wwwroot/ ever called it -- leaving a process-launching
            // endpoint permanently exposed for no benefit. Removed rather than
            // hardened, since the right amount of attack surface for a feature
            // that does not exist is none.

            // Downloads a complete, self-contained snapshot of the tracking database.
            // Uses SQLite's VACUUM INTO rather than copying the .db file directly —
            // the DB runs in WAL mode, so a raw file copy could miss recent writes
            // still sitting in the -wal file. VACUUM INTO produces one consistent,
            // compacted file in a single atomic step, safe to run alongside the live
            // tracker without stopping anything.
            app.MapGet("/api/backup", async (HttpContext context) =>
            {
                string tempPath = Path.Combine(Path.GetTempPath(), $"fastapp-backup-{Guid.NewGuid():N}.db");
                try
                {
                    using var db = new AppDbContext();
                    string escapedPath = tempPath.Replace("'", "''");
                    await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{escapedPath}';");

                    string downloadName = $"FastApp-Backup-{DateTime.Now:yyyy-MM-dd}.db";
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{downloadName}\"");

                    using var fileStream = File.OpenRead(tempPath);
                    await fileStream.CopyToAsync(context.Response.Body);
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort cleanup of the temp snapshot */ }
                }
            });

            // Current database size plus a rough linear-growth projection, for
            // the Settings tab's "how big is this going to get" section. The
            // projection is deliberately simple (total size / days tracked =
            // bytes/day, extrapolated forward) rather than trying to model
            // retention pruning or usage trends -- good enough for "should I
            // be worried", not meant to be precise.
            app.MapGet("/api/db-stats", async (HttpContext context) =>
            {
                try
                {
                    string dbPath = AppDbContext.GetDbPath();
                    long dbSizeBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
                    // The WAL holds committed-but-not-yet-checkpointed data --
                    // real disk usage even though it's a separate file.
                    string walPath = dbPath + "-wal";
                    if (File.Exists(walPath)) dbSizeBytes += new FileInfo(walPath).Length;

                    using var db = new AppDbContext();
                    DateTime? firstDate = await db.DailyLogs.AsNoTracking()
                        .Where(l => l.AppName == "SYSTEM_PC")
                        .OrderBy(l => l.Date)
                        .Select(l => (DateTime?)l.Date)
                        .FirstOrDefaultAsync();

                    int daysTracked = firstDate.HasValue ? Math.Max(1, (int)(DateTime.Today - firstDate.Value).TotalDays + 1) : 1;
                    double bytesPerDay = (double)dbSizeBytes / daysTracked;

                    await context.Response.WriteAsJsonAsync(new
                    {
                        DbSizeBytes = dbSizeBytes,
                        FirstTrackedDate = firstDate?.ToString("yyyy-MM-dd"),
                        DaysTracked = daysTracked,
                        BytesPerDay = Math.Round(bytesPerDay, 1),
                        Projected90Days = (long)(dbSizeBytes + bytesPerDay * 90),
                        Projected365Days = (long)(dbSizeBytes + bytesPerDay * 365)
                    });
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // Restores the tracking database from an uploaded backup file (the
            // counterpart to /api/backup's download). Validates thoroughly
            // BEFORE touching anything live: a bad upload must never reach the
            // point of stopping the tracker or copying over the real database.
            // The actual swap-and-restart happens in MainViewModel (via the
            // same WeakReferenceMessenger pattern already used for every other
            // dashboard-to-WPF action) since it needs to gracefully stop the
            // background tracker first -- see RestoreBackupCommand's handler
            // for why (2026-08-19's database corruption came from skipping
            // exactly that step during an update-triggered restart).
            app.MapPost("/api/restore", async (IFormFile file, HttpContext context) =>
            {
                string stagingPath = Path.Combine(Path.GetTempPath(), $"fastapp-restore-{Guid.NewGuid():N}.db");
                try
                {
                    if (file == null || file.Length == 0)
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = "No file uploaded." });
                        return;
                    }
                    if (file.Length > 500 * 1024 * 1024)
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = "File is too large to be a FastApp backup." });
                        return;
                    }

                    using (var stream = File.Create(stagingPath))
                    {
                        await file.CopyToAsync(stream);
                    }

                    // 1. SQLite file header check ("SQLite format 3\0").
                    byte[] header = new byte[16];
                    using (var fs = File.OpenRead(stagingPath))
                    {
                        int read = await fs.ReadAsync(header, 0, 16);
                        if (read < 16 || System.Text.Encoding.ASCII.GetString(header, 0, 15) != "SQLite format 3")
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsJsonAsync(new { error = "That doesn't look like a SQLite database file." });
                            return;
                        }
                    }

                    // 2. Structural integrity + 3. it's actually a FastApp backup,
                    // not just any SQLite file someone picked by accident.
                    using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={stagingPath}"))
                    {
                        conn.Open();

                        using (var checkCmd = conn.CreateCommand())
                        {
                            checkCmd.CommandText = "PRAGMA integrity_check;";
                            string result = (string)await checkCmd.ExecuteScalarAsync();
                            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                            {
                                context.Response.StatusCode = 400;
                                await context.Response.WriteAsJsonAsync(new { error = "That backup file is corrupted (failed SQLite integrity check)." });
                                return;
                            }
                        }

                        using (var tableCmd = conn.CreateCommand())
                        {
                            tableCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('DailyLogs', 'ManagedApps', 'AppSettings');";
                            long tableCount = (long)await tableCmd.ExecuteScalarAsync();
                            if (tableCount < 3)
                            {
                                context.Response.StatusCode = 400;
                                await context.Response.WriteAsJsonAsync(new { error = "That's a SQLite file, but not a FastApp backup." });
                                return;
                            }
                        }
                    }
                    // SqliteConnection is closed by the using block above before the file
                    // gets handed off — otherwise the restore's own File.Copy could race
                    // against this validation connection still holding it open.

                    // Respond success BEFORE triggering the actual restore: the browser
                    // needs to receive this while the process is still alive to send it.
                    await context.Response.WriteAsJsonAsync(new { success = true, message = "Restoring — FastApp will restart in a few seconds." });
                    await context.Response.Body.FlushAsync();

                    WeakReferenceMessenger.Default.Send(new FastApp.ViewModels.RestoreBackupCommand(stagingPath));
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
                    }
                }
            });

            app.MapGet("/api/settings", async (HttpContext context) => { using var db = new AppDbContext(); await context.Response.WriteAsJsonAsync(new { RetentionDays = GetRetentionDays(db), CaptureWindowTitles = GetCaptureWindowTitles(db) }); });
            // Validated before storing: this value drives an irreversible DELETE on
            // every app start, so an unparseable or nonsensical entry landing in the
            // DB is not something to discover later. Anything invalid is rejected
            // rather than written, leaving the previous setting untouched.
            app.MapPost("/api/settings/retention", async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body);
                string raw = (await reader.ReadToEndAsync()).Trim();
                if (!int.TryParse(raw, out int days) || days < 1 || days > 99999)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { error = "Retention must be a whole number of days between 1 and 99999." });
                    return;
                }
                using var db = new AppDbContext();
                await db.Database.ExecuteSqlRawAsync("UPDATE AppSettings SET Value = {0} WHERE Key = 'RetentionDays'", days.ToString());
            });
            app.MapPost("/api/settings/window-titles", async (HttpContext context) => { using var reader = new StreamReader(context.Request.Body); string enabled = (await reader.ReadToEndAsync()).Trim().ToLower() == "true" ? "true" : "false"; using var db = new AppDbContext(); await db.Database.ExecuteSqlRawAsync("UPDATE AppSettings SET Value = {0} WHERE Key = 'CaptureWindowTitles'", enabled); });

            // Work/Play classification behind the Insights tab's rhythm chart —
            // GET returns every known category (not just ones the user has already
            // classified) so the UI can render a full editable list.
            app.MapGet("/api/settings/category-classification", async (HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    var allCategories = await GetAllCategoriesAsync(db);
                    var classification = GetCategoryClassification(db);
                    var result = allCategories.ToDictionary(c => c, c => classification.GetValueOrDefault(c, "neutral"), StringComparer.OrdinalIgnoreCase);
                    await context.Response.WriteAsJsonAsync(result);
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            app.MapPost("/api/settings/category-classification", async (HttpContext context) =>
            {
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    string body = await reader.ReadToEndAsync();
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var data = System.Text.Json.JsonSerializer.Deserialize<CategoryClassificationRequest>(body, options);

                    var validValues = new[] { "work", "play", "neutral" };
                    if (data == null || string.IsNullOrEmpty(data.Category) || !validValues.Contains(data.Classification?.ToLower()))
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = "category and a valid classification (work/play/neutral) are required." });
                        return;
                    }

                    using var db = new AppDbContext();
                    var current = GetCategoryClassification(db);
                    current[data.Category] = data.Classification.ToLower();
                    string json = System.Text.Json.JsonSerializer.Serialize(current);
                    await db.Database.ExecuteSqlRawAsync("INSERT OR REPLACE INTO AppSettings (Key, Value) VALUES ('CategoryClassification', {0})", json);

                    await context.Response.WriteAsJsonAsync(new { success = true });
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            app.MapGet("/api/hidden-apps", async (HttpContext context) => { using var db = new AppDbContext(); await context.Response.WriteAsJsonAsync(GetHiddenApps(db)); });
            app.MapPost("/api/hide", async (HttpContext context) => { using var reader = new StreamReader(context.Request.Body); string appName = await reader.ReadToEndAsync(); using var db = new AppDbContext(); await db.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO HiddenApps (AppName) VALUES ({0})", appName); });
            app.MapPost("/api/unhide", async (HttpContext context) => { using var reader = new StreamReader(context.Request.Body); string appName = await reader.ReadToEndAsync(); using var db = new AppDbContext(); await db.Database.ExecuteSqlRawAsync("DELETE FROM HiddenApps WHERE AppName = {0}", appName); });

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