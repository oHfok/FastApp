using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace FastApp.Services
{
    /// <summary>
    /// Who may talk to the dashboard server.
    ///
    /// The server used to answer anything on this PC that asked: any program, any
    /// browser tab, a typed address. Now there are exactly two ways in:
    ///
    ///   - A browser FastApp opened. Every "open the dashboard" action goes through
    ///     <see cref="LaunchUrl"/>, which carries a token good for one minute.
    ///     Redeeming it sets a session cookie in that browser; nothing has to be typed.
    ///   - Another app holding the read-only API token, if the user has switched that
    ///     on. A token can only GET data. It cannot change or delete anything, and it
    ///     cannot download the whole database.
    ///
    /// The phone app is that second way, reached over Tailscale -- see "the phone app" below.
    ///
    /// Both secrets are stored encrypted for this Windows account (DPAPI), so another
    /// user of the PC, or a copy of the folder, cannot use them. A program running as
    /// this same user still could -- that is a limit of Windows, not something this
    /// class can close.
    /// </summary>
    public static class DashboardAccess
    {
        public const string CookieName = "fastapp_session";
        public static readonly TimeSpan SessionLife = TimeSpan.FromDays(30);
        private static readonly TimeSpan LaunchTokenLife = TimeSpan.FromSeconds(60);

        // What the read-only token is never allowed to fetch, even though they are GETs:
        // the whole database file, the diagnostics, and the token's own settings.
        private static readonly string[] TokenBlocked = { "/api/backup", "/api/diagnostics", "/api/settings/api-access", "/api/settings/phone-access", "/api/settings/recovery-key" };

        private static readonly ConcurrentDictionary<string, DateTime> LaunchTokens = new();
        private static readonly object Gate = new();
        private static string _session, _apiToken;
        private static bool _apiLoaded;

        private static string SessionPath => Path.Combine(AppDbContext.GetDbFolder(), "dashboard-session.key");

        /// <summary>Where the read-only token lives, DPAPI-encrypted, for the user's other apps to read.</summary>
        public static string ApiTokenPath => Path.Combine(AppDbContext.GetDbFolder(), "api-token.key");

        public enum Access { Allowed, Locked, Forbidden }

        // ---------------------------------------------------------------- browser sessions

        /// <summary>
        /// The address to hand the browser: unlocks it, then lands on the dashboard
        /// (with <paramref name="query"/>, e.g. "?app=Discord", if given).
        /// </summary>
        public static string LaunchUrl(string query = null)
        {
            var now = DateTime.UtcNow;
            foreach (var stale in LaunchTokens.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
                LaunchTokens.TryRemove(stale, out _);

            string token = NewSecret();
            LaunchTokens[token] = now + LaunchTokenLife;
            return "http://127.0.0.1:5050/unlock?t=" + token
                + (string.IsNullOrEmpty(query) ? "" : "&to=" + Uri.EscapeDataString(query));
        }

        /// <summary>
        /// True for a link FastApp issued, within its minute. Deliberately not single-use:
        /// some browsers and security tools request an address once before the visible
        /// navigation does, and a spent token would leave the user at a locked page after
        /// doing nothing wrong. A minute is the whole exposure either way.
        /// </summary>
        internal static bool RedeemLaunchToken(string token) =>
            !string.IsNullOrEmpty(token) && LaunchTokens.TryGetValue(token, out var expires) && expires >= DateTime.UtcNow;

        /// <summary>Tests only: makes every outstanding launch link as old as it will ever get.</summary>
        internal static void ExpireLaunchTokensForTests()
        {
            foreach (var key in LaunchTokens.Keys.ToList()) LaunchTokens[key] = DateTime.UtcNow.AddSeconds(-1);
        }

        /// <summary>The value the session cookie must carry. Survives restarts and updates.</summary>
        internal static string SessionSecret
        {
            get
            {
                lock (Gate)
                {
                    if (_session != null) return _session;
                    _session = ReadProtected(SessionPath);
                    if (_session == null)
                    {
                        _session = NewSecret();
                        WriteProtected(SessionPath, _session);
                    }
                    return _session;
                }
            }
        }

        /// <summary>
        /// Where the unlock step may send the browser afterwards: only a query string for
        /// the dashboard page, never an address of the caller's choosing.
        /// </summary>
        internal static string SafeLanding(string to) =>
            "/dashboard.html" + (!string.IsNullOrEmpty(to) && to[0] == '?' && !to.Any(char.IsControl) ? to : "");

        // ---------------------------------------------------------------- read-only API token

        /// <summary>The current token, or null while access for other apps is off.</summary>
        public static string ApiToken
        {
            get
            {
                lock (Gate)
                {
                    if (!_apiLoaded) { _apiToken = ReadProtected(ApiTokenPath); _apiLoaded = true; }
                    return _apiToken;
                }
            }
        }

        /// <summary>Switches access on (keeping an existing token) or issues a fresh one.</summary>
        public static string EnableApiToken(bool regenerate = false)
        {
            lock (Gate)
            {
                string current = ApiToken;
                if (current != null && !regenerate) return current;
                _apiToken = NewSecret(); _apiLoaded = true;
                WriteProtected(ApiTokenPath, _apiToken);
                return _apiToken;
            }
        }

        public static void DisableApiToken()
        {
            lock (Gate)
            {
                _apiToken = null; _apiLoaded = true;
                try { File.Delete(ApiTokenPath); } catch { }
            }
        }

        // ---------------------------------------------------------------- the phone app
        //
        // The one way in from outside this PC, and only while "Phone app" is switched on:
        // over Tailscale, with the read-only token. The phone gets the token by typing a
        // six-digit code the dashboard shows for two minutes, so the token itself is never
        // typed or sent anywhere but down the Tailscale link.

        /// <summary>Set once at server start: whether it listens beyond 127.0.0.1 at all.</summary>
        public static bool PhoneListening { get; set; }

        private static readonly TimeSpan PairingLife = TimeSpan.FromMinutes(2);
        private const int PairingTries = 5;
        private static string _pairCode;
        private static DateTime _pairExpires;
        private static int _pairTriesLeft;

        /// <summary>Tailscale hands every device an address in 100.64.0.0/10; a request that arrived on one came through the user's own tailnet.</summary>
        internal static bool OverTailscale(System.Net.IPAddress local)
        {
            if (local == null) return false;
            if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
            byte[] b = local.GetAddressBytes();
            return b.Length == 4 && b[0] == 100 && (b[1] & 0xC0) == 64;
        }

        /// <summary>A fresh code for the dashboard to show. Replaces any earlier one.</summary>
        public static string NewPairingCode()
        {
            lock (Gate)
            {
                _pairCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                _pairExpires = DateTime.UtcNow + PairingLife;
                _pairTriesLeft = PairingTries;
                return _pairCode;
            }
        }

        /// <summary>
        /// The token for a correct code, once. A million codes against five tries: after the
        /// fifth wrong one the code is void and a new one has to be shown on the PC.
        /// </summary>
        internal static string RedeemPairingCode(string code)
        {
            lock (Gate)
            {
                if (_pairCode == null || DateTime.UtcNow > _pairExpires || _pairTriesLeft-- <= 0) { _pairCode = null; return null; }
                if (!Same(code, _pairCode)) return null;
                _pairCode = null;
                return EnableApiToken();
            }
        }

        // ---------------------------------------------------------------- the decision

        /// <summary>
        /// The whole rule in one place. A valid session cookie may do anything. A valid
        /// token may only read /api data. Everything else is locked out.
        /// </summary>
        internal static Access Decide(string method, string path, string cookie, string bearer)
        {
            if (Same(cookie, SessionSecret)) return Access.Allowed;

            if (!string.IsNullOrEmpty(bearer))
            {
                if (!Same(bearer, ApiToken)) return Access.Locked;
                bool reading = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
                bool data = path != null && path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                         && !TokenBlocked.Any(b => path.StartsWith(b, StringComparison.OrdinalIgnoreCase));
                return reading && data ? Access.Allowed : Access.Forbidden;
            }

            return Access.Locked;
        }

        /// <summary>
        /// The server's gate, run for every request before anything is served -- pages
        /// included. Handles /unlock (where every "open the dashboard" action in FastApp
        /// lands first) and otherwise applies <see cref="Decide"/>.
        /// </summary>
        public static async Task Guard(HttpContext context, Func<Task> next)
        {
            string path = context.Request.Path.Value ?? string.Empty;
            string cookie = context.Request.Cookies[CookieName];

            if (IsRemote(context))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!PhoneListening || !OverTailscale(context.Connection.LocalIpAddress))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { error = "FastApp only answers on this PC, or to the phone app over Tailscale." });
                    return;
                }
                if (path.Equals("/pair", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(context.Request.Method))
                {
                    using var reader = new StreamReader(context.Request.Body);
                    string token = RedeemPairingCode((await reader.ReadToEndAsync()).Trim());
                    if (token == null) context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(token == null
                        ? new { error = "Wrong or expired code. Show a new one in FastApp's settings.", token = (string)null }
                        : new { error = (string)null, token });
                    return;
                }
                // The browser session never works from outside, and neither does the link that grants it.
                cookie = null;
                if (path.Equals("/unlock", StringComparison.OrdinalIgnoreCase)) { await WriteLocked(context); return; }
            }

            if (path.Equals("/unlock", StringComparison.OrdinalIgnoreCase))
            {
                bool fresh = RedeemLaunchToken(context.Request.Query["t"]);
                bool already = Decide("GET", path, cookie, null) == Access.Allowed;
                context.Response.Headers.CacheControl = "no-store";
                if (!fresh && !already) { await WriteLocked(context); return; }

                context.Response.Cookies.Append(CookieName, SessionSecret, new CookieOptions
                {
                    HttpOnly = true,                 // page scripts never see it
                    SameSite = SameSiteMode.Lax,     // sent when the browser is opened at the dashboard, not on other sites' requests
                    MaxAge = SessionLife,
                    Path = "/"
                });
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Redirect(SafeLanding(context.Request.Query["to"]));
                return;
            }

            string authorization = context.Request.Headers.Authorization.ToString();
            string bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization.Substring(7).Trim() : null;

            switch (Decide(context.Request.Method, path, cookie, bearer))
            {
                case Access.Allowed:
                    await next();
                    return;

                case Access.Forbidden:
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { error = "This token can only read data." });
                    return;

                default:
                    context.Response.Headers.CacheControl = "no-store";
                    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsJsonAsync(new { error = "Locked. Open the dashboard from FastApp, or send the API token." });
                    }
                    else await WriteLocked(context);
                    return;
            }
        }

        /// <summary>True for a request that did not start on this PC.</summary>
        internal static bool IsRemote(HttpContext context) =>
            context.Connection.RemoteIpAddress is { } from && !System.Net.IPAddress.IsLoopback(from);

        private static async Task WriteLocked(HttpContext context)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(LockedPage);
        }

        /// <summary>Shown to a browser that was not opened from FastApp. Self-contained: nothing else is served to it.</summary>
        public const string LockedPage =
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<meta name=\"robots\" content=\"noindex\"><title>FastApp is locked</title><style>" +
            ":root{color-scheme:light dark;--bg:#F5F6F8;--card:#fff;--line:#E4E7EC;--text:#101828;--dim:#475467}" +
            "@media(prefers-color-scheme:dark){:root{--bg:#0C0E13;--card:#14171E;--line:rgba(255,255,255,.08);--text:#EEF0F4;--dim:#A8B0BE}}" +
            "body{margin:0;min-height:100vh;display:grid;place-items:center;background:var(--bg);color:var(--text);font:15px/1.5 'Segoe UI',system-ui,sans-serif}" +
            "main{max-width:420px;margin:16px;padding:28px;background:var(--card);border:1px solid var(--line);border-radius:12px}" +
            "h1{font-size:20px;margin:0 0 8px}p{margin:0 0 10px;color:var(--dim)}b{color:var(--text)}</style></head><body><main>" +
            "<h1>The dashboard is locked</h1>" +
            "<p>For your privacy, it only opens in a browser that FastApp itself started.</p>" +
            "<p>Open it from the <b>FastApp tray icon &rarr; Statistics dashboard</b>, or from the FastApp window. This browser will then stay unlocked.</p>" +
            "</main></body></html>";

        // ---------------------------------------------------------------- helpers

        private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        private static bool Same(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

        internal static string ReadProtected(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string value = Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
                return value.Length > 0 ? value : null;
            }
            catch { return null; }   // unreadable (copied from another account, damaged): treated as absent
        }

        internal static void WriteProtected(string path, string value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path,
                    ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { CrashLog.Log("Could not save a dashboard access key", ex); }
        }
    }
}
