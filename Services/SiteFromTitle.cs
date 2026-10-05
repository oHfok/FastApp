using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FastApp.Services
{
    /// <summary>
    /// Works out which website a browser window title is about.
    ///
    /// FastApp does not read URLs -- only the window title, and only when the
    /// user has switched title capture on -- so this is a heuristic, and an
    /// honest one: "A video - YouTube - Zen Browser" is YouTube; a page whose
    /// title names no site comes back as null and is shown as "Other pages"
    /// rather than guessed at. Nothing here leaves the machine.
    ///
    /// Private windows are recognised so they can be left out entirely.
    /// </summary>
    public static class SiteFromTitle
    {
        /// <summary>Process names (any case) of the browsers whose titles are worth splitting.</summary>
        public static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "zen", "firefox", "chrome", "msedge", "brave", "opera", "opera_gx", "operagx", "vivaldi",
            "arc", "helium", "chromium", "floorp", "librewolf", "waterfox", "tor", "browser"
        };

        // " - Google Chrome", " — Zen Browser", " | Microsoft Edge" at the very end.
        private static readonly Regex BrowserSuffix = new(
            @"\s+[-–—|]\s+(?:(?:Mozilla\s+)?Firefox(?:\s+(?:Developer\s+Edition|Nightly))?|Zen(?:\s+Browser)?|Google\s+Chrome|Chrome|Microsoft\s+Edge|Edge|Brave|Opera(?:\s+GX)?|Vivaldi|Arc|Helium|Chromium|Floorp|LibreWolf|Waterfox|Tor\s+Browser)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Unread-count prefixes some sites add: "(37) ", "[2] ", "• ".
        private static readonly Regex LeadingCount = new(@"^(?:\(\d+\+?\)|\[\d+\+?\]|[•●])\s*", RegexOptions.Compiled);

        private static readonly Regex PrivateWindow = new(
            @"InPrivate|Private\s+Browsing|Incognito|Private\s+window", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Separators = new(@"\s+[-–—|·/]\s+", RegexOptions.Compiled);

        private static readonly Regex Hostname = new(
            @"^(?:www\.)?([a-z0-9][a-z0-9-]*(?:\.[a-z0-9-]+)*\.[a-z]{2,})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> Tlds = new(StringComparer.OrdinalIgnoreCase)
        {
            "com", "org", "net", "io", "dev", "ai", "co", "app", "pl", "uk", "de", "fr", "es", "it", "nl", "ru", "tv",
            "gg", "me", "us", "eu", "info", "edu", "gov", "to", "ly", "fm", "so", "xyz", "tech", "news", "wiki", "gl"
        };

        private static readonly Regex FileName = new(
            @"\.(?:pdf|docx?|xlsx?|pptx?|txt|md|png|jpe?g|gif|webp|mp[34]|zip|rar|csv|json)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NewTab = new(@"^(?:New\s+Tab|Nowa\s+karta|about:blank|Untitled)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Sites that name themselves in a title, so "Page - Site" and "Site - Page" both resolve.
        private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            ["youtube"] = "YouTube", ["youtube music"] = "YouTube Music", ["github"] = "GitHub", ["reddit"] = "Reddit",
            ["twitch"] = "Twitch", ["netflix"] = "Netflix", ["prime video"] = "Prime Video", ["disney+"] = "Disney+",
            ["gmail"] = "Gmail", ["google"] = "Google", ["google search"] = "Google", ["google docs"] = "Google Docs",
            ["google sheets"] = "Google Sheets", ["google slides"] = "Google Slides", ["google drive"] = "Google Drive",
            ["google calendar"] = "Google Calendar", ["google meet"] = "Google Meet", ["google maps"] = "Google Maps",
            ["google translate"] = "Google Translate", ["wikipedia"] = "Wikipedia", ["stack overflow"] = "Stack Overflow",
            ["stackoverflow"] = "Stack Overflow", ["twitter"] = "X", ["x"] = "X", ["facebook"] = "Facebook",
            ["instagram"] = "Instagram", ["tiktok"] = "TikTok", ["linkedin"] = "LinkedIn", ["chatgpt"] = "ChatGPT",
            ["claude"] = "Claude", ["claude.ai"] = "Claude", ["discord"] = "Discord", ["whatsapp"] = "WhatsApp",
            ["messenger"] = "Messenger", ["notion"] = "Notion", ["figma"] = "Figma", ["spotify"] = "Spotify",
            ["bing"] = "Bing", ["duckduckgo"] = "DuckDuckGo", ["microsoft teams"] = "Teams", ["outlook"] = "Outlook",
            ["amazon"] = "Amazon", ["amazon.com"] = "Amazon", ["allegro"] = "Allegro", ["olx"] = "OLX",
        };

        /// <summary>
        /// The page title as shown in a list: without the trailing browser name
        /// ("- Zen Browser") or an unread-count prefix ("(37) ").
        /// </summary>
        public static string Clean(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;
            string t = BrowserSuffix.Replace(title.Trim(), string.Empty);
            return LeadingCount.Replace(t, string.Empty).Trim();
        }

        /// <summary>True for titles of private/incognito windows, which are never listed.</summary>
        public static bool IsPrivate(string title) =>
            !string.IsNullOrEmpty(title) && PrivateWindow.IsMatch(title);

        /// <summary>
        /// The site a title is about, or null when it names none (shown as
        /// "Other pages"). Blank titles also return null.
        /// </summary>
        public static string Extract(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;

            string t = Clean(title);
            if (t.Length == 0) return null;
            if (NewTab.IsMatch(t)) return "New tab";

            var parts = Separators.Split(t).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0) return null;

            // A well-known site named at either end: "Page - YouTube" or "GitHub - owner/repo".
            foreach (var part in new[] { parts[^1], parts[0] })
            {
                if (Known.TryGetValue(part, out var site)) return site;
            }

            // A segment that is itself a hostname beats a guess. Only real top-level
            // domains count, so file names in a title ("main.py", "notes.md") do not.
            foreach (var part in parts.Reverse())
            {
                var host = Hostname.Match(part);
                if (!host.Success) continue;
                string name = host.Groups[1].Value.ToLowerInvariant();
                if (Tlds.Contains(name[(name.LastIndexOf('.') + 1)..])) return Known.TryGetValue(name, out var named) ? named : name;
            }

            // Otherwise the usual "Page - Site" convention: the last segment, unless it
            // looks like a tagline or a file name ("Site - Play the Online Game Now!"),
            // in which case the first. A name is short and has no sentence punctuation.
            if (parts.Length < 2) return null;
            foreach (var part in new[] { parts[^1], parts[0] })
            {
                if (part.Length <= 28 && part.IndexOfAny(new[] { '!', '?' }) < 0 && !FileName.IsMatch(part))
                    return part.TrimEnd('.', ':', ',');
            }
            return null;
        }
    }
}
