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
    // Retention, window-title capture, category classification, hidden
    // apps, and the parental PIN.
    //
    // Split out of DashboardServerService.StartAsync, which had grown to 2,261
    // lines with every endpoint's query, aggregation and shaping logic inline.
    // Same class (partial), same registration, same behaviour -- this only
    // changes which file each group lives in, so the one method every change had
    // to be made inside is no longer the whole server.
    public static partial class DashboardServerService
    {
        private static void MapSettingsEndpoints(WebApplication app)
        {
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

                // The list this feeds sat in alphabetical order, which put the
                // categories holding almost no time above the ones that actually
                // move the chart it controls. Ordering needs a weight, so the
                // response carries recorded minutes per category over the same
                // trailing 30 days the rhythm chart itself covers -- no new data,
                // just the totals the sessions table already holds.
                var since = DateTime.Today.AddDays(-30);
                var hiddenApps = GetHiddenApps(db);
                var categoryMap = await GetAppCategoriesSafely(db);
                var recent = await db.SessionLogs
                    .Where(x => x.StartTime >= since && !hiddenApps.Contains(x.AppName))
                    .ToListAsync();
                var minutesByCategory = recent
                    .GroupBy(x => categoryMap.GetValueOrDefault(x.AppName, "Other"))
                    .ToDictionary(g => g.Key, g => g.Sum(x => (x.EndTime - x.StartTime).TotalMinutes),
                                  StringComparer.OrdinalIgnoreCase);

                var result = allCategories.Select(c => new
                {
                    Category = c,
                    Classification = classification.GetValueOrDefault(c, "neutral"),
                    Minutes = Math.Round(minutesByCategory.GetValueOrDefault(c, 0), 1)
                }).OrderByDescending(x => x.Minutes).ThenBy(x => x.Category).ToList();

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
        }
    }
}
