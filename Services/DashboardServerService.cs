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
    // area. This file keeps the server's lifecycle and the shared query helpers.
    public static partial class DashboardServerService
    {

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

        public static async Task StartAsync()
        {
            string exeFolder = AppContext.BaseDirectory;
            string wwwrootPath = Path.Combine(exeFolder, "wwwroot");
            if (!Directory.Exists(wwwrootPath)) Directory.CreateDirectory(wwwrootPath);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = exeFolder, WebRootPath = wwwrootPath });
            // Off the network unless "Phone app" is switched on. Then it listens on every
            // address, because the Tailscale one may not exist yet when FastApp starts --
            // and DashboardAccess.Guard turns away whatever did not arrive over Tailscale.
            // Read once here: a listening address cannot change while the server runs, so
            // the switch takes effect at the next start.
            DashboardAccess.PhoneListening = AppSettingsStore.GetBool("PhoneAccess", false);
            builder.WebHost.UseUrls(DashboardAccess.PhoneListening ? "http://0.0.0.0:5050" : "http://127.0.0.1:5050");

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
                // The phone names this PC by its Tailscale address, so its Host cannot be
                // on the list. Rebinding gains nothing there: the browser cookie is never
                // accepted from outside this PC (DashboardAccess.Guard), only the token.
                string host = context.Request.Host.Value ?? string.Empty;
                if (!DashboardAccess.IsRemote(context) && !allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
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

                // Paths only (never the query string, which can carry app names).
                long perfReq = Services.PerfLog.Stamp();
                await next();
                if (context.Request.Path.StartsWithSegments("/api"))
                    Services.PerfLog.Done($"http {method} {context.Request.Path}", perfReq, 400);
            });

            // ==========================================================
            // ACCESS. Being on this PC is no longer enough to read the data.
            // A request gets through with the session cookie of a browser FastApp
            // opened, or -- for reading only -- the API token the user issued to
            // their other apps. The rule itself lives in DashboardAccess.Decide.
            // Before UseStaticFiles on purpose: the pages are locked too.
            // ==========================================================
            app.Use((context, next) => DashboardAccess.Guard(context, next));

            // no-cache means "keep it, but ask before using it": the browser revalidates
            // each file against its ETag on every load (a 304 on localhost). Without a
            // Cache-Control header browsers guess how long a file stays fresh from its
            // Last-Modified date, so after an update an old cached dashboard.html was
            // paired with new CSS and scripts -- a half-old, half-new, broken-looking
            // page until the next manual refresh.
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
            });

            // A second static-file mount served a "Nova" dashboard from wwwroot2 at
            // /nova. That folder does not exist in the repo and is not packaged, so
            // the only thing the route ever did was create an empty directory inside
            // the install folder on every launch and serve nothing out of it.

            using (var initDb = new AppDbContext())
            {
                await initDb.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS HiddenApps (AppName TEXT PRIMARY KEY);");
                await initDb.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS AppSettings (Key TEXT PRIMARY KEY, Value TEXT);");
                await initDb.Database.ExecuteSqlRawAsync(ExecutablePathStore.CreateTableSql);
                await initDb.Database.ExecuteSqlRawAsync(ResourceStatsStore.CreateTableSql);
                await initDb.Database.ExecuteSqlRawAsync(MusicStatsStore.CreateTableSql);
                await initDb.Database.ExecuteSqlRawAsync(AfkIntervalStore.CreateTableSql);
                await initDb.Database.ExecuteSqlRawAsync(MusicIntervalStore.CreateTableSql);
                await initDb.Database.ExecuteSqlRawAsync(LimitEventStore.CreateTableSql);
                await initDb.Database.ExecuteSqlRawAsync(AutoLaunchEventStore.CreateTableSql);
                await initDb.Database.ExecuteSqlRawAsync(PauseIntervalStore.CreateTableSql);
                await initDb.Database.ExecuteSqlRawAsync(PaletteEventStore.CreateTableSql);
                await initDb.Database.ExecuteSqlRawAsync(SystemLockTracker.CreateTableSql);

                // One-time backfill of the wall-clock music row. Between 5.0.0 and
                // 5.0.1 the headline "Music Playing Time" switched from summing the
                // per-app rows to reading a single SYSTEM_MUSIC row that only
                // exists for days recorded on 5.0.1+, so every earlier day's
                // figure dropped to zero on update. This seeds SYSTEM_MUSIC for
                // those days as MAX(per-app seconds): exact when only one music
                // app ran (the usual case) and a safe under-estimate otherwise --
                // never the old inflated sum. The upsert also tops up a partial
                // SYSTEM_MUSIC row for the current day and never lowers one.
                bool musicBackfilled = PinService.GetSettingValue(initDb, "MusicWallClockBackfilled") == "true";
                if (!musicBackfilled)
                {
                    await initDb.Database.ExecuteSqlRawAsync(
                        "INSERT INTO MusicListeningDaily (Date, AppName, Seconds) " +
                        "SELECT Date, 'SYSTEM_MUSIC', MAX(Seconds) FROM MusicListeningDaily " +
                        "WHERE AppName <> 'SYSTEM_MUSIC' GROUP BY Date " +
                        "ON CONFLICT(Date, AppName) DO UPDATE SET Seconds = MAX(Seconds, excluded.Seconds);");
                    await initDb.Database.ExecuteSqlRawAsync(
                        "INSERT OR REPLACE INTO AppSettings (Key, Value) VALUES ('MusicWallClockBackfilled', 'true');");
                }

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
            MapDataEndpoints(app);
            MapExportEndpoints(app);
            MapAnalyticsEndpoints(app);
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