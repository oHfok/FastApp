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
    // Wrapped: which recaps are available, and one recap's slides.
    //
    // Split out of DashboardServerService.StartAsync, which had grown to 2,261
    // lines with every endpoint's query, aggregation and shaping logic inline.
    // Same class (partial), same registration, same behaviour -- this only
    // changes which file each group lives in, so the one method every change had
    // to be made inside is no longer the whole server.
    public static partial class DashboardServerService
    {
        private static void MapWrappedEndpoints(WebApplication app)
        {
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
        }
    }
}
