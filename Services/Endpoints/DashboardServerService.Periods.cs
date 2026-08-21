using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;

namespace FastApp.Services
{
    // The Periods tab: the list of days/weeks/months/years and one period's
    // detail view.
    //
    // Split out of DashboardServerService.StartAsync, which had grown to 2,261
    // lines with every endpoint's query, aggregation and shaping logic inline.
    // Same class (partial), same registration, same behaviour -- this only
    // changes which file each group lives in, so the one method every change had
    // to be made inside is no longer the whole server.
    public static partial class DashboardServerService
    {
        private static void MapPeriodsEndpoints(WebApplication app)
        {
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
        }
    }
}
