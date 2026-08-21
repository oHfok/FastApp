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
    // Sparkline, categories, overview, leaderboard, insights, timeline,
    // week heatmap, recent sessions and the full app list.
    //
    // Split out of DashboardServerService.StartAsync, which had grown to 2,261
    // lines with every endpoint's query, aggregation and shaping logic inline.
    // Same class (partial), same registration, same behaviour -- this only
    // changes which file each group lives in, so the one method every change had
    // to be made inside is no longer the whole server.
    public static partial class DashboardServerService
    {
        private static void MapStatsEndpoints(WebApplication app)
        {
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

        // ---- /api/week-heatmap?date= ---------------------------------------------
        // The hour-by-day grid behind Overview's Week scope, in one response.
        // The frontend used to build this by calling /api/timeline once per day
        // of the week -- seven requests, each opening its own AppDbContext
        // against the file the tracker is writing to, and all seven repeating
        // on every 12-second poll for as long as the tab stayed open.
        //
        // Aggregating here also means sending 168 numbers instead of every
        // session row for seven days, when the client was only ever bucketing
        // them by hour anyway.
        app.MapGet("/api/week-heatmap", async (string date, HttpContext context) =>
        {
            try
            {
                using var db = new AppDbContext();
                DateTime targetDate = string.IsNullOrEmpty(date) ? DateTime.Today : DateTime.Parse(date).Date;
                DateTime monday = GetMondayStartOfWeek(targetDate);
                DateTime rangeEnd = monday.AddDays(7);
                var hiddenApps = GetHiddenApps(db);

                var sessions = await db.SessionLogs
                    .Where(s => s.StartTime >= monday && s.StartTime < rangeEnd
                                && s.AppName != "SYSTEM_PC" && !hiddenApps.Contains(s.AppName))
                    .Select(s => new { s.StartTime, s.EndTime })
                    .ToListAsync();

                // [day 0..6 = Mon..Sun][hour 0..23] of focused minutes, matching
                // the Monday-first ordering every other heatmap in the UI uses.
                var grid = new double[7][];
                for (int d = 0; d < 7; d++) grid[d] = new double[24];

                foreach (var s in sessions)
                {
                    int dayIdx = (int)(s.StartTime.Date - monday).TotalDays;
                    if (dayIdx < 0 || dayIdx > 6) continue;
                    int hour = s.StartTime.Hour;
                    if (hour < 0 || hour > 23) continue;
                    grid[dayIdx][hour] += (s.EndTime - s.StartTime).TotalMinutes;
                }

                for (int d = 0; d < 7; d++)
                    for (int h = 0; h < 24; h++)
                        grid[d][h] = Math.Round(grid[d][h], 1);

                await context.Response.WriteAsJsonAsync(new
                {
                    WeekStart = monday.ToString("yyyy-MM-dd"),
                    // Days after the selected date have no data yet and are
                    // rendered as "not happened" rather than "zero".
                    ElapsedDays = Math.Min(7, (int)(targetDate.Date - monday).TotalDays + 1),
                    Grid = grid
                });
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
        }
    }
}
