using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Webkit;

namespace FastApp.Phone;

// One screen: a WebView showing Assets/app.html. The page asks for "/api/..." as if it were
// served by FastApp itself; Client answers those from the PC when it can be reached and from
// the copy kept on the phone when it cannot. The WebView never touches the network itself.
[Activity(Label = "FastApp", MainLauncher = true, Theme = "@android:style/Theme.Material.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    WebView web = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var background = Color.ParseColor("#0C0E13");
#pragma warning disable CA1422 // bar colours: still honoured at targetSdk 34 (see AndroidManifest.xml)
        Window!.SetStatusBarColor(background);
        Window.SetNavigationBarColor(background);
#pragma warning restore CA1422
        web = new WebView(this);
        web.Settings.JavaScriptEnabled = true;
        web.SetBackgroundColor(background);
        web.SetWebViewClient(new Client(new Pc(this), Assets!));
        SetContentView(web);
        web.LoadUrl(Client.Origin + "/app.html");
    }

    // The page navigates by #hash, so "back" is the WebView's own history until it runs out.
#pragma warning disable CA1422, CS0672
    public override void OnBackPressed()
    {
        if (web.CanGoBack()) web.GoBack(); else base.OnBackPressed();
    }
#pragma warning restore CA1422, CS0672
}

sealed class Client(Pc pc, Android.Content.Res.AssetManager assets) : WebViewClient
{
    // .invalid never resolves (RFC 2606): even a request this class somehow missed could not leave the phone.
    public const string Origin = "https://fastapp.invalid";

    public override WebResourceResponse? ShouldInterceptRequest(WebView? view, IWebResourceRequest? request)
    {
        var url = request?.Url;
        var path = url?.Path ?? "";
        try
        {
            if (url?.Host != "fastapp.invalid") return Reply(403, "text/plain", []);
            if (path == "/app.html") return new WebResourceResponse("text/html", "utf-8", assets.Open("app.html"));
            if (path == "/local/state") return Json(200, new() { ["address"] = pc.Address, ["paired"] = pc.Paired });
            if (path == "/local/retry") { pc.Retry(); return Json(200, new() { ["ok"] = true }); }
            if (path == "/local/pair") return Json(200, new() { ["error"] = pc.Pair(url.GetQueryParameter("address") ?? "", url.GetQueryParameter("code") ?? "") });
            if (path.StartsWith("/api/"))
            {
                var (status, body, source, at) = pc.Get(path + (url.EncodedQuery is { Length: > 0 } q ? "?" + q : ""));
                return Reply(status, "application/json", body, new() { ["X-Source"] = source, ["X-At"] = at.ToString() });
            }
            return Reply(404, "text/plain", []);
        }
        catch (Exception e)
        {
            Android.Util.Log.Error("FastApp", e.ToString());
            return Json(500, new() { ["error"] = e.Message });
        }
    }

    // JsonObject, not a serialised anonymous type: a Release build is trimmed, and reflection over our own types may not survive it.
    static WebResourceResponse Json(int status, System.Text.Json.Nodes.JsonObject value) => Reply(status, "application/json", Encoding.UTF8.GetBytes(value.ToJsonString()));

    static WebResourceResponse Reply(int status, string mime, byte[] body, Dictionary<string, string>? headers = null) =>
        new(mime, "utf-8", status, status == 200 ? "OK" : "Error", headers ?? [], new MemoryStream(body));
}

// The PC, as seen from the phone: where it is, the token, and a copy of every answer it has given.
sealed class Pc(Context context)
{
    // The managed handler: Android's own would refuse plain http. It is plain http only inside
    // Tailscale's encrypted tunnel, which is the one road FastApp accepts the phone on.
    static readonly HttpClient http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = TimeSpan.FromSeconds(12) };

    // ponytail: the token sits in the app's private preferences, unencrypted. It can only read,
    // and only another app with root could get at it; move it to the Keystore if that changes.
    readonly ISharedPreferences prefs = context.GetSharedPreferences("pc", FileCreationMode.Private)!;

    // FilesDir, not CacheDir: Android empties CacheDir when space runs short, and this is the offline data.
    readonly string kept = System.IO.Path.Combine(context.FilesDir!.AbsolutePath, "kept");

    // After a failed attempt the PC is taken to be off for a while, so that a screen made of
    // five requests does not wait five timeouts before showing what is kept.
    DateTime downUntil;

    public string? Address => prefs.GetString("address", null);
    string? Token => prefs.GetString("token", null);
    public bool Paired => Address is not null && Token is not null;
    public void Retry() => downUntil = default;

    /// <summary>Status, body, where it came from ("live", "kept" = a finished day, complete; "cache" = the PC did not answer) and when (unix).</summary>
    public (int Status, byte[] Body, string Source, long At) Get(string pathAndQuery)
    {
        if (!Paired) return (401, [], "", 0);
        var file = System.IO.Path.Combine(kept, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(pathAndQuery))));
        (int, byte[], string, long) Kept(string source) => File.Exists(file)
            ? (200, File.ReadAllBytes(file), source, new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds())
            : (504, "{}"u8.ToArray(), source, 0);

        if (Finished(pathAndQuery, file)) return Kept("kept");
        if (DateTime.UtcNow < downUntil) return Kept("cache");
        try
        {
            using var ask = new HttpRequestMessage(HttpMethod.Get, $"http://{Address}:5050{pathAndQuery}");
            ask.Headers.Authorization = new("Bearer", Token);
            using var reply = http.Send(ask);
            if ((int)reply.StatusCode == 401) return (401, [], "", 0); // the token was replaced on the PC: pair again
            if (!reply.IsSuccessStatusCode) return Kept("cache");
            var body = reply.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            Directory.CreateDirectory(kept);
            File.WriteAllBytes(file + ".tmp", body);
            File.Move(file + ".tmp", file, true);
            return (200, body, "live", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Net.Sockets.SocketException)
        {
            downUntil = DateTime.UtcNow.AddSeconds(30);
            return Kept("cache");
        }
    }

    // A day that is over cannot change any more: if its answer was saved after that day ended,
    // it is the final one and the PC is not asked again.
    internal static bool Finished(string pathAndQuery, string file)
    {
        var at = pathAndQuery.IndexOf("date=", StringComparison.Ordinal);
        return at >= 0 && pathAndQuery.Length >= at + 15
            && DateTime.TryParseExact(pathAndQuery.AsSpan(at + 5, 10), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var day)
            && File.Exists(file) && File.GetLastWriteTime(file).Date > day;
    }

    /// <summary>Trades the six-digit code shown on the PC for the token. Returns what went wrong, or null.</summary>
    public string? Pair(string address, string code)
    {
        address = address.Trim().Replace("http://", "").TrimEnd('/');
        if (address.EndsWith(":5050")) address = address[..^5];
        if (address.Length == 0 || Uri.CheckHostName(address) == UriHostNameType.Unknown) return "That does not look like an address. Use the one FastApp shows, like 100.101.102.103.";
        try
        {
            using var ask = new HttpRequestMessage(HttpMethod.Post, $"http://{address}:5050/pair") { Content = new StringContent(code.Trim()) };
            using var reply = http.Send(ask);
            var text = reply.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if ((int)reply.StatusCode == 403) return "FastApp answered, but not to the phone: switch on Phone App in its settings and restart it. Both devices must be on Tailscale.";
            using var json = JsonDocument.Parse(text);
            if (!reply.IsSuccessStatusCode || json.RootElement.GetProperty("token").GetString() is not { Length: > 0 } token)
                return json.RootElement.TryGetProperty("error", out var why) ? why.GetString() : $"FastApp answered with an error ({(int)reply.StatusCode}).";
            prefs.Edit()!.PutString("address", address)!.PutString("token", token)!.Apply();
            Retry();
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Net.Sockets.SocketException)
        {
            return "No answer from that address. Is the PC on, FastApp running, and Tailscale connected on both?";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException)
        {
            return "Something answered at that address, but it was not FastApp.";
        }
    }
}
