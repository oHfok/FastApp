using CommunityToolkit.Mvvm.Messaging;
using FastApp.ViewModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FastApp.Services
{
    public static class DashboardServerService
    {
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

        public static async Task StartAsync()
        {
            string exeFolder = AppContext.BaseDirectory;
            string wwwrootPath = Path.Combine(exeFolder, "wwwroot");
            if (!Directory.Exists(wwwrootPath)) Directory.CreateDirectory(wwwrootPath);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = exeFolder, WebRootPath = wwwrootPath });
            builder.WebHost.UseUrls("http://127.0.0.1:5050");
            var app = builder.Build();          

            app.UseStaticFiles();

            // Serve the standalone Nova dashboard from wwwroot2 at /nova
            string novaRootPath = Path.Combine(exeFolder, "wwwroot2");
            if (!Directory.Exists(novaRootPath)) Directory.CreateDirectory(novaRootPath);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(novaRootPath),
                RequestPath = "/nova"
            });

            using (var initDb = new AppDbContext())
            {
                await initDb.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS HiddenApps (AppName TEXT PRIMARY KEY);");
                await initDb.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS AppSettings (Key TEXT PRIMARY KEY, Value TEXT);");
                await initDb.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO AppSettings (Key, Value) VALUES ('RetentionDays', '90');");
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
                var wpfHardcodedCategories = new List<string>
                {
                    "Development", "Gaming", "Productivity", "Browsing", "Communication", "Media Production", "Music", "Fun", "Education", "Utilities", "Other"
                };

                var mappedCategories = await db.AppCategories.Select(c => c.Category).Where(c => !string.IsNullOrEmpty(c)).ToListAsync();
                var managedCategories = await db.ManagedApps.Select(m => m.Category).Where(c => !string.IsNullOrEmpty(c)).ToListAsync();

                var finalCategories = wpfHardcodedCategories
                    .Union(mappedCategories)
                    .Union(managedCategories)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList();

                await context.Response.WriteAsJsonAsync(finalCategories);
            });

            // OVERVIEW - UPGRADED WITH HEAD-TO-HEAD MATH
            app.MapGet("/api/overview", async (string date, HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    DateTime targetDate = string.IsNullOrEmpty(date) ? DateTime.Today : DateTime.Parse(date).Date;
                    var hiddenApps = GetHiddenApps(db);

                    var allSystemLogs = await db.DailyLogs.Where(l => l.AppName == "SYSTEM_PC" && l.Date <= targetDate).ToListAsync();
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

                    double focusAllTime = allSystemLogs.Sum(l => l.TimeFocused.TotalHours);

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
                        PrevActiveMinutes = Math.Round(previousLogs.Where(p => p.AppName == g.Key).Sum(x => x.ActiveRunningTime.TotalMinutes), 1)
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

                    // --- NEW: PRODUCTIVITY RHYTHM & FATIGUE MATH ---
                    var categoryMap = await GetAppCategoriesSafely(db);
                    var workCats = new[] { "Development", "Productivity", "Education", "Utilities" };
                    var playCats = new[] { "Gaming", "Fun", "Media Production", "Music", "Browsing" };

                    // 1. Group the last 30 days of sessions by the Hour of the Day (0-23)
                    var rhythm = Enumerable.Range(0, 24).Select(hour => {
                        var hourSessions = recentSessions.Where(s => s.StartTime.Hour == hour).ToList();
                        return new
                        {
                            Hour = hour,
                            Work = Math.Round(hourSessions.Where(s => workCats.Contains(categoryMap.GetValueOrDefault(s.AppName, "Other"))).Sum(s => (s.EndTime - s.StartTime).TotalMinutes), 1),
                            Play = Math.Round(hourSessions.Where(s => playCats.Contains(categoryMap.GetValueOrDefault(s.AppName, "Other"))).Sum(s => (s.EndTime - s.StartTime).TotalMinutes), 1)
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

                    // --- PATH LOOKUP ---
                    // Try to find the path from ManagedApps if available. 
                    // (If you don't store Path in DB yet, it will return this placeholder until you track it).
                    // --- PATH LOOKUP (Safe Version) ---
                    string exePath = "Path not recorded in database.";
                    try
                    {
                        var managedApp = await db.ManagedApps.FirstOrDefaultAsync(m => m.Name == appName);
                        if (managedApp != null && !string.IsNullOrEmpty(managedApp.ExecutablePath))
                        {
                            exePath = managedApp.ExecutablePath;
                        }
                    }
                    catch
                    {
                        // Safely catches the SQL error if the ExecutablePath column doesn't exist yet
                    }

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

                    await context.Response.WriteAsJsonAsync(new
                    {
                        AppName = appName,
                        ExecutablePath = exePath, // Added
                        Consistency = consistencyPct, // Added
                        UsagePattern = usagePattern, // Added
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
                        History = history
                    });
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            app.MapGet("/api/periods", async (string type, HttpContext context) =>
            {
                try
                {
                    using var db = new AppDbContext();
                    var hiddenApps = GetHiddenApps(db);
                    bool isMonth = string.Equals(type, "month", StringComparison.OrdinalIgnoreCase);

                    var systemLogs = await db.DailyLogs.Where(l => l.AppName == "SYSTEM_PC").ToListAsync();
                    if (systemLogs.Count == 0)
                    {
                        await context.Response.WriteAsJsonAsync(Array.Empty<object>());
                        return;
                    }

                    var appLogs = await db.DailyLogs
                        .Where(l => l.AppName != "SYSTEM_PC" && !hiddenApps.Contains(l.AppName))
                        .ToListAsync();

                    // Bucket key -> (start, end) of that week/month
                    var buckets = new Dictionary<string, (DateTime start, DateTime end, string label)>();
                    foreach (var log in systemLogs)
                    {
                        string key; DateTime start, end; string label;
                        if (isMonth)
                        {
                            start = new DateTime(log.Date.Year, log.Date.Month, 1);
                            end = start.AddMonths(1).AddDays(-1);
                            key = start.ToString("yyyy-MM");
                            label = start.ToString("MMMM yyyy");
                        }
                        else
                        {
                            start = GetMondayStartOfWeek(log.Date);
                            end = start.AddDays(6);
                            key = start.ToString("yyyy-MM-dd");
                            int weekNo = System.Globalization.ISOWeek.GetWeekOfYear(start);
                            label = $"Week {weekNo}";
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
                    bool isMonth = string.Equals(type, "month", StringComparison.OrdinalIgnoreCase);

                    DateTime chosenStart = DateTime.Parse(start).Date;
                    if (!isMonth) chosenStart = GetMondayStartOfWeek(chosenStart);
                    else chosenStart = new DateTime(chosenStart.Year, chosenStart.Month, 1);

                    (DateTime s, DateTime e) Range(DateTime periodStart) => isMonth
                        ? (periodStart, periodStart.AddMonths(1).AddDays(-1))
                        : (periodStart, periodStart.AddDays(6));

                    DateTime PrevStart(DateTime periodStart) => isMonth ? periodStart.AddMonths(-1) : periodStart.AddDays(-7);
                    DateTime NextStart(DateTime periodStart) => isMonth ? periodStart.AddMonths(1) : periodStart.AddDays(7);

                    DateTime todayStart = isMonth
                        ? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)
                        : GetMondayStartOfWeek(DateTime.Today);

                    var systemLogs = await db.DailyLogs.Where(l => l.AppName == "SYSTEM_PC").ToListAsync();
                    var appLogs = await db.DailyLogs
                        .Where(l => l.AppName != "SYSTEM_PC" && !hiddenApps.Contains(l.AppName))
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

                    string LabelFor(DateTime periodStart) => isMonth
                        ? periodStart.ToString("MMMM yyyy")
                        : $"Week {System.Globalization.ISOWeek.GetWeekOfYear(periodStart)}";

                    var (chosenS, chosenE) = Range(chosenStart);
                    var chosenRange = systemLogs.Where(l => l.Date >= chosenS && l.Date <= chosenE).ToList();
                    double chosenMins = chosenRange.Sum(l => l.TimeFocused.TotalMinutes);
                    double chosenAfkMins = chosenRange.Sum(l => l.AfkTimeSpent.TotalMinutes);
                    double chosenUptimeMins = chosenRange.Sum(l => l.TimeSpent.TotalMinutes);

                    // Rank against every period of this type that has data
                    var allBuckets = new HashSet<string>();
                    foreach (var log in systemLogs)
                    {
                        var bs = isMonth ? new DateTime(log.Date.Year, log.Date.Month, 1) : GetMondayStartOfWeek(log.Date);
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
                        Days = days
                    };

                    await context.Response.WriteAsJsonAsync(payload);
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });


            // --- NEW: OPEN FOLDER SECURE ENDPOINT ---
            app.MapPost("/api/open-folder", async (HttpContext context) =>
            {
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    string path = await reader.ReadToEndAsync();

                    // Basic security & validation check
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        // Tells Windows Explorer to open and highlight the specific file
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                        await context.Response.WriteAsJsonAsync(new { success = true });
                    }
                    else
                    {
                        context.Response.StatusCode = 404;
                        await context.Response.WriteAsJsonAsync(new { error = "Path not found or invalid." });
                    }
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            app.MapGet("/api/settings", async (HttpContext context) => { using var db = new AppDbContext(); await context.Response.WriteAsJsonAsync(new { RetentionDays = GetRetentionDays(db) }); });
            app.MapPost("/api/settings/retention", async (HttpContext context) => { using var reader = new StreamReader(context.Request.Body); string days = await reader.ReadToEndAsync(); using var db = new AppDbContext(); await db.Database.ExecuteSqlRawAsync("UPDATE AppSettings SET Value = {0} WHERE Key = 'RetentionDays'", days); });
            app.MapGet("/api/hidden-apps", async (HttpContext context) => { using var db = new AppDbContext(); await context.Response.WriteAsJsonAsync(GetHiddenApps(db)); });
            app.MapPost("/api/hide", async (HttpContext context) => { using var reader = new StreamReader(context.Request.Body); string appName = await reader.ReadToEndAsync(); using var db = new AppDbContext(); await db.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO HiddenApps (AppName) VALUES ({0})", appName); });
            app.MapPost("/api/unhide", async (HttpContext context) => { using var reader = new StreamReader(context.Request.Body); string appName = await reader.ReadToEndAsync(); using var db = new AppDbContext(); await db.Database.ExecuteSqlRawAsync("DELETE FROM HiddenApps WHERE AppName = {0}", appName); });

            await app.RunAsync();
        }
    }
}