using FastApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.IO;

namespace FastApp.Tests;

// The access keys live in the data folder and DashboardAccess caches them in statics.
[Collection("Db")]
public class DashboardAccessTests
{
    private sealed record Result(int Status, bool Served, string Location, string SetCookie, string Body);

    // from/on: the caller's address and the address of this PC it reached (null = on this PC, as before the phone app).
    private static async Task<Result> Send(string method, string pathAndQuery, string cookie = null, string bearer = null,
        string from = null, string on = null, string body = null)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        if (from != null) context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(from);
        if (on != null) context.Connection.LocalIpAddress = System.Net.IPAddress.Parse(on);
        if (body != null) context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        int q = pathAndQuery.IndexOf('?');
        context.Request.Method = method;
        context.Request.Path = q < 0 ? pathAndQuery : pathAndQuery[..q];
        if (q >= 0) context.Request.QueryString = new QueryString(pathAndQuery[q..]);
        if (cookie != null) context.Request.Headers.Cookie = $"{DashboardAccess.CookieName}={cookie}";
        if (bearer != null) context.Request.Headers.Authorization = "Bearer " + bearer;
        context.Response.Body = new MemoryStream();

        bool served = false;
        await DashboardAccess.Guard(context, () => { served = true; return Task.CompletedTask; });

        context.Response.Body.Position = 0;
        return new Result(context.Response.StatusCode, served, context.Response.Headers.Location.ToString(),
            context.Response.Headers.SetCookie.ToString(), new StreamReader(context.Response.Body).ReadToEnd());
    }

    private static string PathOf(string launchUrl) => new Uri(launchUrl).PathAndQuery;

    [Fact]
    public async Task NothingIsServedWithoutASessionOrToken()
    {
        var page = await Send("GET", "/dashboard.html");
        Assert.Equal(401, page.Status);
        Assert.False(page.Served);
        Assert.Contains("The dashboard is locked", page.Body);

        var api = await Send("GET", "/api/overview");
        Assert.Equal(401, api.Status);
        Assert.False(api.Served);

        Assert.False((await Send("GET", "/debug.html")).Served);
        Assert.False((await Send("GET", "/api/backup")).Served);
    }

    [Fact]
    public async Task OpeningFromFastAppUnlocksThatBrowserAndTheLinkExpires()
    {
        string launch = PathOf(DashboardAccess.LaunchUrl("?app=Discord"));

        var unlock = await Send("GET", launch);
        Assert.Equal(302, unlock.Status);
        Assert.Equal("/dashboard.html?app=Discord", unlock.Location);
        Assert.Contains(DashboardAccess.CookieName + "=" + DashboardAccess.SessionSecret, unlock.SetCookie);
        Assert.Contains("httponly", unlock.SetCookie, StringComparison.OrdinalIgnoreCase);

        // The browser now carries the cookie: everything works, writes included.
        Assert.True((await Send("GET", "/dashboard.html", cookie: DashboardAccess.SessionSecret)).Served);
        Assert.True((await Send("POST", "/api/hide", cookie: DashboardAccess.SessionSecret)).Served);

        // A browser or security tool that requests the address twice still gets in...
        Assert.Equal(302, (await Send("GET", launch)).Status);

        // ...but a minute later the same link, in a browser with no cookie, is dead.
        DashboardAccess.ExpireLaunchTokensForTests();
        var late = await Send("GET", launch);
        Assert.Equal(401, late.Status);
        Assert.DoesNotContain(DashboardAccess.CookieName, late.SetCookie);
        // A browser that is already unlocked just carries on.
        Assert.Equal(302, (await Send("GET", launch, cookie: DashboardAccess.SessionSecret)).Status);
    }

    [Fact]
    public async Task AMadeUpUnlockTokenOrCookieGetsNothing()
    {
        Assert.Equal(401, (await Send("GET", "/unlock?t=" + new string('A', 64))).Status);
        Assert.Equal(401, (await Send("GET", "/unlock")).Status);
        Assert.False((await Send("GET", "/api/overview", cookie: new string('B', 64))).Served);
    }

    [Fact]
    public async Task TheApiTokenCanReadDataAndNothingElse()
    {
        DashboardAccess.DisableApiToken();
        try
        {
            string token = DashboardAccess.EnableApiToken();
            Assert.Equal(token, DashboardAccess.EnableApiToken());   // switching on again keeps it

            Assert.True((await Send("GET", "/api/overview?date=2026-10-12", bearer: token)).Served);

            // Never a write or delete...
            var write = await Send("POST", "/api/hide", bearer: token);
            Assert.Equal(403, write.Status);
            Assert.False(write.Served);
            Assert.False((await Send("DELETE", "/api/anything", bearer: token)).Served);
            Assert.False((await Send("POST", "/api/restore", bearer: token)).Served);

            // ...nor the whole database, the diagnostics, its own settings, or the pages.
            Assert.False((await Send("GET", "/api/backup", bearer: token)).Served);
            Assert.False((await Send("GET", "/api/backup/auto", bearer: token)).Served);
            Assert.False((await Send("GET", "/api/diagnostics", bearer: token)).Served);
            Assert.False((await Send("GET", "/api/settings/api-access", bearer: token)).Served);
            Assert.False((await Send("GET", "/dashboard.html", bearer: token)).Served);

            Assert.Equal(401, (await Send("GET", "/api/overview", bearer: new string('C', 64))).Status);

            // Replacing it cuts the old one off; switching off cuts everything off.
            string replaced = DashboardAccess.EnableApiToken(regenerate: true);
            Assert.NotEqual(token, replaced);
            Assert.False((await Send("GET", "/api/overview", bearer: token)).Served);
            Assert.True((await Send("GET", "/api/overview", bearer: replaced)).Served);

            DashboardAccess.DisableApiToken();
            Assert.False((await Send("GET", "/api/overview", bearer: replaced)).Served);
        }
        finally { DashboardAccess.DisableApiToken(); }
    }

    [Fact]
    public async Task ThePhoneGetsInOverTailscaleWithACodeAndCanOnlyRead()
    {
        const string phone = "100.101.102.103", tailscale = "100.64.0.7", lan = "192.168.1.20";
        DashboardAccess.DisableApiToken();
        try
        {
            // Switched off: nothing from outside this PC is answered, whatever it carries.
            DashboardAccess.PhoneListening = false;
            string token = DashboardAccess.EnableApiToken();
            Assert.Equal(403, (await Send("GET", "/api/overview", bearer: token, from: phone, on: tailscale)).Status);

            DashboardAccess.PhoneListening = true;
            // Switched on: still nothing for the Wi-Fi or the internet, only for Tailscale.
            Assert.Equal(403, (await Send("GET", "/api/overview", bearer: token, from: "192.168.1.50", on: lan)).Status);
            Assert.Equal(403, (await Send("GET", "/api/overview", bearer: token, from: "8.8.8.8", on: "83.12.4.9")).Status);
            Assert.Equal(403, (await Send("POST", "/pair", from: "192.168.1.50", on: lan, body: "000000")).Status);

            // Pairing: the code on the PC's screen buys the token, once.
            string code = DashboardAccess.NewPairingCode();
            var paired = await Send("POST", "/pair", from: phone, on: tailscale, body: code);
            Assert.Equal(200, paired.Status);
            Assert.Contains(token, paired.Body);
            Assert.Equal(401, (await Send("POST", "/pair", from: phone, on: tailscale, body: code)).Status);

            // Five wrong guesses and the code is void, the right one included.
            code = DashboardAccess.NewPairingCode();
            string wrong = code == "111111" ? "222222" : "111111";
            for (int i = 0; i < 5; i++) Assert.Equal(401, (await Send("POST", "/pair", from: phone, on: tailscale, body: wrong)).Status);
            Assert.Equal(401, (await Send("POST", "/pair", from: phone, on: tailscale, body: code)).Status);

            // With the token the phone reads, and that is all.
            Assert.True((await Send("GET", "/api/timeline?date=2026-10-12", bearer: token, from: phone, on: tailscale)).Served);
            Assert.True((await Send("GET", "/api/device-battery", bearer: token, from: phone, on: tailscale)).Served);
            Assert.False((await Send("POST", "/api/hide", bearer: token, from: phone, on: tailscale)).Served);
            Assert.False((await Send("GET", "/api/settings/phone-access", bearer: token, from: phone, on: tailscale)).Served);
            Assert.False((await Send("GET", "/api/overview", from: phone, on: tailscale)).Served);

            // The browser session is for this PC: neither the cookie nor a launch link works from outside.
            Assert.False((await Send("GET", "/dashboard.html", cookie: DashboardAccess.SessionSecret, from: phone, on: tailscale)).Served);
            Assert.False((await Send("POST", "/api/hide", cookie: DashboardAccess.SessionSecret, from: phone, on: tailscale)).Served);
            var unlock = await Send("GET", PathOf(DashboardAccess.LaunchUrl()), from: phone, on: tailscale);
            Assert.Equal(401, unlock.Status);
            Assert.DoesNotContain(DashboardAccess.CookieName, unlock.SetCookie);
        }
        finally { DashboardAccess.PhoneListening = false; DashboardAccess.DisableApiToken(); }
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("::ffff:100.100.1.1", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("127.0.0.1", false)]
    public void OnlyTailscaleAddressesCountAsTailscale(string address, bool expected) =>
        Assert.Equal(expected, DashboardAccess.OverTailscale(System.Net.IPAddress.Parse(address)));

    [Theory]
    [InlineData("?app=Discord", "/dashboard.html?app=Discord")]
    [InlineData(null, "/dashboard.html")]
    [InlineData("//evil.example/x", "/dashboard.html")]
    [InlineData("https://evil.example", "/dashboard.html")]
    [InlineData("?a=1\r\nSet-Cookie: x=y", "/dashboard.html")]
    public void UnlockOnlyEverLandsOnTheDashboard(string to, string expected) =>
        Assert.Equal(expected, DashboardAccess.SafeLanding(to));
}

[Collection("Db")]
public class AutoBackupTests
{
    [Fact]
    public void MakesOneVerifiedCopyADayAndKeepsTheLastSeven()
    {
        foreach (var old in AutoBackup.Existing()) old.Delete();
        using (var db = new AppDbContext()) db.Database.EnsureCreated();

        var day = new DateTime(2026, 1, 1);
        string first = AutoBackup.RunIfDue(day);
        Assert.NotNull(first);
        Assert.True(new FileInfo(first).Length > 0);
        Assert.Null(AutoBackup.RunIfDue(day));                       // already done today

        for (int i = 1; i <= 9; i++) Assert.NotNull(AutoBackup.RunIfDue(day.AddDays(i)));

        var kept = AutoBackup.Existing();
        Assert.Equal(AutoBackup.Keep, kept.Count);
        Assert.Equal("appmanager-2026-01-10.db", kept[0].Name);       // newest first
        Assert.DoesNotContain(kept, f => f.Name == "appmanager-2026-01-01.db");
        Assert.Empty(Directory.GetFiles(AutoBackup.Folder, "*.tmp"));
        Assert.NotEqual(AppDbContext.GetDbFolder(), AutoBackup.Folder);
    }
}
