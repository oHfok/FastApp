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
    // The App Detail drawer: per-app stats, its period breakdown and usage
    // trend, plus the category / daily-limit / extension writes it performs.
    //
    // Split out of DashboardServerService.StartAsync, which had grown to 2,261
    // lines with every endpoint's query, aggregation and shaping logic inline.
    // Same class (partial), same registration, same behaviour -- this only
    // changes which file each group lives in, so the one method every change had
    // to be made inside is no longer the whole server.
    public static partial class DashboardServerService
    {
        private static void MapAppDetailEndpoints(WebApplication app)
        {
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

        }
    }
}
