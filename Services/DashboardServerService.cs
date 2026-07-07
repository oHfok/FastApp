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
                if (!string.IsNullOrEmpty(c.AppName)) dict[c.AppName] = c.Category ?? "Uncategorized";
            }
            var managedApps = await db.ManagedApps.ToListAsync();
            foreach (var m in managedApps)
            {
                if (!string.IsNullOrEmpty(m.Name) && !dict.ContainsKey(m.Name))
                {
                    dict[m.Name] = m.Category ?? "Uncategorized";
                }
            }
            return dict;
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

            using (var initDb = new AppDbContext())
            {
                await initDb.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS HiddenApps (AppName TEXT PRIMARY KEY);");
                await initDb.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS AppSettings (Key TEXT PRIMARY KEY, Value TEXT);");
                await initDb.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO AppSettings (Key, Value) VALUES ('RetentionDays', '90');");
            }

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
                    var categoryTotals = todaysLogs.GroupBy(l => appCategories.ContainsKey(l.AppName) ? appCategories[l.AppName] : "Uncategorized")
                        .Select(g => new { Category = g.Key, FocusedMinutes = g.Sum(x => x.TimeFocused.TotalMinutes) })
                        .OrderByDescending(x => x.FocusedMinutes).ToList();

                    // --- HEAD-TO-HEAD HISTORICAL MATH ---
                    double focusToday = allSystemLogs.Where(l => l.Date == targetDate).Sum(l => l.TimeFocused.TotalHours);
                    double focusPrevDay = allSystemLogs.Where(l => l.Date == targetDate.AddDays(-1)).Sum(l => l.TimeFocused.TotalHours);

                    double focusWeek = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-7) && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours);
                    double focusPrevWeek = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-14) && l.Date <= targetDate.AddDays(-7)).Sum(l => l.TimeFocused.TotalHours);

                    double focusMonth = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-30) && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours);
                    double focusPrevMonth = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-60) && l.Date <= targetDate.AddDays(-30)).Sum(l => l.TimeFocused.TotalHours);

                    double focusYear = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-365) && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours);
                    double focusPrevYear = allSystemLogs.Where(l => l.Date > targetDate.AddDays(-730) && l.Date <= targetDate.AddDays(-365)).Sum(l => l.TimeFocused.TotalHours);

                    double focusAllTime = allSystemLogs.Sum(l => l.TimeFocused.TotalHours);

                    var payload = new
                    {
                        TotalToday = allSystemLogs.Where(l => l.Date == targetDate).Sum(l => l.TimeSpent.TotalHours),
                        FocusToday = focusToday,
                        PrevFocusToday = focusPrevDay,
                        FocusWeek = focusWeek,
                        PrevFocusWeek = focusPrevWeek,
                        FocusMonth = focusMonth,
                        PrevFocusMonth = focusPrevMonth,
                        FocusYear = focusYear,
                        PrevFocusYear = focusPrevYear,
                        FocusAllTime = focusAllTime,
                        AfkToday = allSystemLogs.Where(l => l.Date == targetDate).Sum(l => l.AfkTimeSpent.TotalHours),
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
                    else if (timeframe == "week") { startDate = targetDate.AddDays(-6); prevStartDate = targetDate.AddDays(-13); prevEndDate = targetDate.AddDays(-7); }
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
                        Category = appCategories.ContainsKey(g.Key) ? appCategories[g.Key] : "Uncategorized",
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
                    await context.Response.WriteAsJsonAsync(new { LongestBlock = longestBlock, AverageSpan = avgSpan, Heatmap = heatmap });
                }
                catch (Exception ex) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
            });

            // APP DETAILS - UPGRADED WITH AVERAGES AND PERSONAL RECORDS
            app.MapGet("/api/app-details", async (string appName, HttpContext context) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(appName)) return;
                    using var db = new AppDbContext();
                    DateTime targetDate = DateTime.Today;
                    var allTimeLogs = await db.DailyLogs.Where(l => l.AppName == appName).ToListAsync();
                    var macroCount = await db.MacroEventLogs.CountAsync(m => m.AppName == appName);
                    var history = allTimeLogs.Where(l => l.Date >= targetDate.AddDays(-30)).OrderBy(l => l.Date).Select(l => new { Date = l.Date.ToString("MMM dd"), FocusedMinutes = Math.Round(l.TimeFocused.TotalMinutes, 1) }).ToList();
                    var sessions = await db.SessionLogs.Where(s => s.AppName == appName && s.StartTime >= targetDate.AddDays(-30)).ToListAsync();
                    var avgSession = sessions.Any() ? sessions.Average(s => (s.EndTime - s.StartTime).TotalMinutes) : 0;
                    var maxStreak = sessions.Any() ? sessions.Max(s => (s.EndTime - s.StartTime).TotalMinutes) : 0;
                    var peakHourGroup = sessions.GroupBy(s => s.StartTime.Hour).OrderByDescending(g => g.Sum(s => (s.EndTime - s.StartTime).TotalMinutes)).FirstOrDefault();

                    // Averages Math
                    double weekAvg = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-7) && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours) / 7.0;
                    double prevWeekAvg = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-14) && l.Date <= targetDate.AddDays(-7)).Sum(l => l.TimeFocused.TotalHours) / 7.0;

                    double monthAvg = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-30) && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours) / 30.0;
                    double prevMonthAvg = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-60) && l.Date <= targetDate.AddDays(-30)).Sum(l => l.TimeFocused.TotalHours) / 30.0;

                    double yearAvg = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-365) && l.Date <= targetDate).Sum(l => l.TimeFocused.TotalHours) / 365.0;
                    double prevYearAvg = allTimeLogs.Where(l => l.Date > targetDate.AddDays(-730) && l.Date <= targetDate.AddDays(-365)).Sum(l => l.TimeFocused.TotalHours) / 365.0;

                    // Personal Records Math
                    var maxFocusDay = allTimeLogs.OrderByDescending(l => l.TimeFocused).FirstOrDefault();
                    var maxRunningDay = allTimeLogs.OrderByDescending(l => l.TimeSpent).FirstOrDefault();

                    string maxFocusDayText = maxFocusDay != null && maxFocusDay.TimeFocused.TotalMinutes > 0
                        ? $"{maxFocusDay.Date:MMM dd} ({Math.Round(maxFocusDay.TimeFocused.TotalHours, 1)}h)" : "N/A";
                    string maxRunningDayText = maxRunningDay != null && maxRunningDay.TimeSpent.TotalMinutes > 0
                        ? $"{maxRunningDay.Date:MMM dd} ({Math.Round(maxRunningDay.TimeSpent.TotalHours, 1)}h)" : "N/A";

                    await context.Response.WriteAsJsonAsync(new
                    {
                        AppName = appName,
                        AllTimeFocused = Math.Round(allTimeLogs.Sum(l => l.TimeFocused.TotalHours), 1),
                        AllTimeRunning = Math.Round(allTimeLogs.Sum(l => l.TimeSpent.TotalHours), 1),
                        AllTimeAfk = Math.Round(allTimeLogs.Sum(l => l.AfkTimeSpent.TotalHours), 1),
                        TotalMacros = macroCount,
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

            app.MapGet("/api/settings", async (HttpContext context) => { using var db = new AppDbContext(); await context.Response.WriteAsJsonAsync(new { RetentionDays = GetRetentionDays(db) }); });
            app.MapPost("/api/settings/retention", async (HttpContext context) => { using var reader = new StreamReader(context.Request.Body); string days = await reader.ReadToEndAsync(); using var db = new AppDbContext(); await db.Database.ExecuteSqlRawAsync("UPDATE AppSettings SET Value = {0} WHERE Key = 'RetentionDays'", days); });
            app.MapGet("/api/hidden-apps", async (HttpContext context) => { using var db = new AppDbContext(); await context.Response.WriteAsJsonAsync(GetHiddenApps(db)); });
            app.MapPost("/api/hide", async (HttpContext context) => { using var reader = new StreamReader(context.Request.Body); string appName = await reader.ReadToEndAsync(); using var db = new AppDbContext(); await db.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO HiddenApps (AppName) VALUES ({0})", appName); });
            app.MapPost("/api/unhide", async (HttpContext context) => { using var reader = new StreamReader(context.Request.Body); string appName = await reader.ReadToEndAsync(); using var db = new AppDbContext(); await db.Database.ExecuteSqlRawAsync("DELETE FROM HiddenApps WHERE AppName = {0}", appName); });

            await app.RunAsync();
        }
    }
}