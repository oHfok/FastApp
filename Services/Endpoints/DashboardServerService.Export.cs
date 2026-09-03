using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// Getting the data out.
    ///
    /// Until now the only download the product offered was /api/backup, which
    /// hands over the SQLite file itself. That is a backup, not an export: two
    /// months of tracking could not leave the application in any form a person
    /// could open without installing a database browser.
    ///
    /// Two shapes, because they answer different questions. The summary is one
    /// row per application over a period -- what you would bill from, or paste
    /// into a spreadsheet. Sessions are the raw record, one row per stretch of
    /// focus, for anyone who wants to do their own arithmetic.
    /// </summary>
    public static partial class DashboardServerService
    {
        private static void MapExportEndpoints(WebApplication app)
        {
            app.MapGet("/api/export", async (string what, string format, string period, string date, HttpContext context) =>
            {
                try
                {
                    string kind = (what ?? "summary").ToLowerInvariant();
                    string type = (format ?? "csv").ToLowerInvariant();
                    if (type != "csv" && type != "json")
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = "format must be csv or json" });
                        return;
                    }

                    DateTime target = string.IsNullOrEmpty(date) ? DateTime.Today : DateTime.Parse(date).Date;
                    var (from, to, label) = Window(period, target);

                    using var db = new AppDbContext();
                    var hidden = GetHiddenApps(db);
                    var categories = await GetAppCategoriesSafely(db);

                    IReadOnlyList<IReadOnlyList<string>> rows;
                    string[] headers;
                    object json;

                    if (kind == "sessions")
                    {
                        headers = new[] { "appName", "category", "start", "end", "minutes", "windowTitle" };

                        // The whole of the last day is included, so a session
                        // that began at 23:50 is not cut off by a date-only
                        // comparison against midnight.
                        DateTime end = to.AddDays(1);
                        var sessions = await db.SessionLogs.AsNoTracking()
                            .Where(s => s.StartTime >= from && s.StartTime < end && s.AppName != "SYSTEM_PC")
                            .OrderBy(s => s.StartTime)
                            .ToListAsync();

                        var kept = sessions.Where(s => !hidden.Contains(s.AppName)).ToList();

                        rows = kept.Select(s => (IReadOnlyList<string>)new[]
                        {
                            s.AppName,
                            CategoryMap.For(categories, s.AppName),
                            s.StartTime.ToString("s", CultureInfo.InvariantCulture),
                            s.EndTime.ToString("s", CultureInfo.InvariantCulture),
                            Round((s.EndTime - s.StartTime).TotalMinutes),
                            s.WindowTitle ?? string.Empty
                        }).ToList();

                        json = kept.Select(s => new
                        {
                            appName = s.AppName,
                            category = CategoryMap.For(categories, s.AppName),
                            start = s.StartTime,
                            end = s.EndTime,
                            minutes = Math.Round((s.EndTime - s.StartTime).TotalMinutes, 2),
                            windowTitle = s.WindowTitle
                        }).ToList();
                    }
                    else
                    {
                        headers = new[] { "appName", "category", "focusMinutes", "activeMinutes", "totalMinutes", "afkMinutes", "days" };

                        var logs = await db.DailyLogs.AsNoTracking()
                            .Where(l => l.Date >= from && l.Date <= to && l.AppName != "SYSTEM_PC")
                            .ToListAsync();

                        var grouped = logs
                            .Where(l => !hidden.Contains(l.AppName))
                            .GroupBy(l => l.AppName)
                            .Select(g => new
                            {
                                appName = g.Key,
                                category = CategoryMap.For(categories, g.Key),
                                focusMinutes = Math.Round(g.Sum(x => x.TimeFocused.TotalMinutes), 2),
                                activeMinutes = Math.Round(Math.Max(0, g.Sum(x => x.ActiveRunningTime.TotalMinutes)), 2),
                                totalMinutes = Math.Round(g.Sum(x => x.TimeSpent.TotalMinutes), 2),
                                afkMinutes = Math.Round(g.Sum(x => x.AfkTimeSpent.TotalMinutes), 2),
                                days = g.Select(x => x.Date).Distinct().Count()
                            })
                            .OrderByDescending(r => r.focusMinutes)
                            .ToList();

                        rows = grouped.Select(r => (IReadOnlyList<string>)new[]
                        {
                            r.appName, r.category,
                            Round(r.focusMinutes), Round(r.activeMinutes),
                            Round(r.totalMinutes), Round(r.afkMinutes),
                            r.days.ToString(CultureInfo.InvariantCulture)
                        }).ToList();

                        json = grouped;
                    }

                    string stem = $"FastApp-{kind}-{label}";

                    if (type == "json")
                    {
                        // Wrapped rather than a bare array: a file that says
                        // which period it covers is still readable in six
                        // months, and the filename is not part of the data.
                        var payload = new
                        {
                            exported = DateTime.Now,
                            what = kind,
                            period = label,
                            from = from.ToString("yyyy-MM-dd"),
                            to = to.ToString("yyyy-MM-dd"),
                            rows = json
                        };

                        Attach(context, $"{stem}.json", "application/json; charset=utf-8");
                        await context.Response.WriteAsync(JsonSerializer.Serialize(payload,
                            new JsonSerializerOptions { WriteIndented = true }));
                        return;
                    }

                    Attach(context, $"{stem}.csv", "text/csv; charset=utf-8");

                    var csv = new StringBuilder();
                    csv.Append(string.Join(",", headers)).Append("\r\n");
                    foreach (var row in rows)
                    {
                        csv.Append(string.Join(",", row.Select(Escape))).Append("\r\n");
                    }

                    // A BOM, because the one thing anybody actually does with a
                    // CSV on Windows is double-click it, and Excel reads a
                    // BOM-less UTF-8 file as the system codepage -- which turns
                    // every window title with an accent in it into mojibake.
                    await context.Response.WriteAsync("﻿" + csv.ToString(), new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });
        }

        private static void Attach(HttpContext context, string filename, string contentType)
        {
            context.Response.ContentType = contentType;
            context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{filename}\"");
        }

        /// <summary>
        /// The period, matching the windows the dashboard's own scopes use so an
        /// export of "this week" covers the same days the page was showing.
        /// </summary>
        private static (DateTime From, DateTime To, string Label) Window(string period, DateTime target) =>
            (period ?? "month").ToLowerInvariant() switch
            {
                "day" => (target, target, target.ToString("yyyy-MM-dd")),
                "week" => (GetMondayStartOfWeek(target), target, $"week-{target:yyyy-MM-dd}"),
                "year" => (target.AddDays(-364), target, $"year-to-{target:yyyy-MM-dd}"),
                "all" => (DateTime.MinValue.Date, target, "all-time"),
                _ => (target.AddDays(-29), target, $"30-days-to-{target:yyyy-MM-dd}")
            };

        private static string Round(double minutes) =>
            Math.Round(minutes, 2).ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// RFC 4180 quoting. Window titles contain commas, quotes and newlines
        /// as a matter of course, and an unquoted one silently shifts every
        /// column after it.
        /// </summary>
        private static string Escape(string value)
        {
            value ??= string.Empty;
            bool needsQuotes = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needsQuotes) return value;
            return '"' + value.Replace("\"", "\"\"") + '"';
        }
    }
}
