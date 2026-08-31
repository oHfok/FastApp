using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Rx = System.Text.RegularExpressions.Regex;
using System.Threading;
using System.Threading.Tasks;

namespace FastApp.Services
{
    /// <param name="Version">Plain version, no leading "v" — Velopack tags by raw version.</param>
    /// <param name="IsInstallable">Whether Velopack actually has a package for it, i.e. whether it can be rolled back to.</param>
    public record ReleaseNote(
        string Version,
        DateTime PublishedAt,
        string NotesMarkdown,
        bool IsInstallable);

    /// <summary>
    /// The list of published FastApp versions and what changed in each.
    ///
    /// Notes come from the GitHub Releases API rather than from Velopack's own
    /// feed, which reports them as empty: scripts/release.ps1 packs first and
    /// sets the notes with `gh release edit` afterwards, so nothing is embedded
    /// in the .nupkg. Reading GitHub directly also means the whole back
    /// catalogue has notes, not just releases published after this was written.
    ///
    /// Velopack's feed is still consulted, for a different question: which
    /// versions have a package that can actually be installed. A GitHub release
    /// whose assets were removed shows in the history but is not offered as a
    /// rollback target.
    /// </summary>
    public static class ReleaseFeedService
    {
        private const string RepoOwner = "oHfok";
        private const string RepoName = "FastApp";
        private const string RepoUrl = "https://github.com/" + RepoOwner + "/" + RepoName;

        // Unauthenticated GitHub calls are rate-limited per hour, and both the
        // desktop Settings tab and the dashboard read this, so the result is held
        // rather than re-fetched on every glance.
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static List<ReleaseNote> _cache;
        private static DateTime _cachedAtUtc;

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // GitHub rejects API requests that don't identify themselves.
            client.DefaultRequestHeaders.Add("User-Agent", "FastApp");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return client;
        }

        public static async Task<List<ReleaseNote>> GetReleasesAsync(bool forceRefresh = false)
        {
            await Gate.WaitAsync();
            try
            {
                if (!forceRefresh && _cache != null && DateTime.UtcNow - _cachedAtUtc < CacheLifetime)
                    return _cache;

                var releases = await FetchGitHubReleasesAsync();
                if (releases == null)
                {
                    // Offline, rate-limited, or GitHub is down. A stale list beats
                    // an empty one; an empty one beats an exception reaching the UI.
                    return _cache ?? new List<ReleaseNote>();
                }

                var installable = await FetchInstallableVersionsAsync();
                _cache = releases
                    .Select(r => r with { IsInstallable = installable.Contains(r.Version) })
                    .OrderByDescending(r => ParseVersion(r.Version))
                    .ToList();
                _cachedAtUtc = DateTime.UtcNow;
                return _cache;
            }
            catch
            {
                return _cache ?? new List<ReleaseNote>();
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>Notes for one version, or null when that version has none recorded.</summary>
        public static async Task<ReleaseNote> GetReleaseAsync(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            string wanted = version.TrimStart('v', 'V');
            var all = await GetReleasesAsync();
            return all.FirstOrDefault(r => r.Version.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<List<ReleaseNote>> FetchGitHubReleasesAsync()
        {
            try
            {
                string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=100";
                using var response = await Http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

                var results = new List<ReleaseNote>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (Read(element, "draft")?.GetBoolean() == true) continue;

                    string tag = Read(element, "tag_name")?.GetString();
                    if (string.IsNullOrWhiteSpace(tag)) continue;

                    DateTime published = DateTime.MinValue;
                    if (Read(element, "published_at")?.GetString() is string publishedText &&
                        DateTime.TryParse(publishedText, System.Globalization.CultureInfo.InvariantCulture,
                                          System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    {
                        published = parsed;
                    }

                    results.Add(new ReleaseNote(
                        Version: tag.TrimStart('v', 'V'),
                        PublishedAt: published,
                        NotesMarkdown: Read(element, "body")?.GetString() ?? string.Empty,
                        IsInstallable: false));
                }
                return results;
            }
            catch
            {
                return null;
            }
        }

        private static JsonElement? Read(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

        private static async Task<HashSet<string>> FetchInstallableVersionsAsync()
        {
            var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var source = new Velopack.Sources.GithubSource(RepoUrl, null, false);
                var feed = await source.GetReleaseFeed(null, RepoName, "win", null, null);
                foreach (var asset in feed.Assets ?? Array.Empty<Velopack.VelopackAsset>())
                {
                    if (asset?.Version != null) versions.Add(asset.Version.ToString());
                }
            }
            catch
            {
                // Leaves the set empty, which marks everything as not installable —
                // history still renders, rollback simply isn't offered.
            }
            return versions;
        }

        /// <summary>
        /// Markdown flattened for the desktop Settings card, which is a plain
        /// TextBlock rather than a renderer. The dashboard shows the same notes
        /// formatted properly; this only has to stay readable, so the markers
        /// are removed rather than interpreted, and tables (which cannot survive
        /// as plain text in a narrow card) are dropped entirely.
        /// </summary>
        public static string ToPlainText(string markdown, int maxLines = 0)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

            var lines = new List<string>();
            foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(string.Empty);
                    continue;
                }
                if (line.StartsWith("|")) continue;                        // table row
                if (Rx.IsMatch(line, @"^(-{3,}|\*{3,}|_{3,})$")) continue;  // horizontal rule

                bool isBullet = Rx.IsMatch(line, @"^[-*+]\s+");
                line = Rx.Replace(line, @"^#{1,6}\s*", string.Empty);
                line = Rx.Replace(line, @"^[-*+]\s+", string.Empty);
                line = Rx.Replace(line, @"\*\*([^*]+)\*\*", "$1");
                line = Rx.Replace(line, @"(?<![\w*])\*([^*]+)\*(?![\w*])", "$1");
                line = Rx.Replace(line, "`([^`]+)`", "$1");
                line = Rx.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");

                lines.Add(isBullet ? "  \u2022 " + line : line);
            }

            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (maxLines > 0 && lines.Count > maxLines)
            {
                lines = lines.Take(maxLines).ToList();
                lines.Add("\u2026");
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static Version ParseVersion(string text) =>
            Version.TryParse(text, out var version) ? version : new Version(0, 0, 0);
    }
}
