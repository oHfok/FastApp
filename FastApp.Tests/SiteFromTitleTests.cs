using FastApp.Services;

namespace FastApp.Tests;

public class SiteFromTitleTests
{
    [Theory]
    // the page names its site at the end -- the common "Page - Site" shape
    [InlineData("(37) AVI prod. FAVST | RED BULL 64 BARS - YouTube — Zen Browser", "YouTube")]
    [InlineData("Some video - YouTube - Google Chrome", "YouTube")]
    [InlineData("pull request #12 · oHfok/FastApp - GitHub — Mozilla Firefox", "GitHub")]
    // the site first -- "Site - Page"
    [InlineData("GitHub - oHfok/FastApp: tracker", "GitHub")]
    [InlineData("Reddit - Dive into anything - Microsoft Edge", "Reddit")]
    // search results and an X-style title
    [InlineData("best pizza near me - Google Search - Brave", "Google")]
    [InlineData("Home / X — Zen Browser", "X")]
    // unread-count prefixes are ignored
    [InlineData("(3) Inbox - me@mail.com - Gmail - Google Chrome", "Gmail")]
    [InlineData("[2] Messenger", "Messenger")]
    // a hostname in the title beats a guess
    [InlineData("Welcome - example.com", "example.com")]
    [InlineData("hianime.to | Watch anime", "hianime.to")]
    // an unknown site that does name itself last
    [InlineData("Quarterly numbers - Acme Intranet", "Acme Intranet")]
    // the site first, then a tagline: the tagline is rejected, so the first end is used
    [InlineData("Skribbl - Play the Online Game Now!", "Skribbl")]
    // a file name is not a site, so the other end is used
    [InlineData("Quiz - Kartkówka.pdf", "Quiz")]
    // new tabs
    [InlineData("New Tab - Google Chrome", "New tab")]
    public void NamesTheSite(string title, string expected) =>
        Assert.Equal(expected, SiteFromTitle.Extract(title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A page with a long title and no site name at all")]   // nothing to go on: shown as "Other pages", not guessed
    [InlineData("A page title that has a really quite long last segment here - this trailing part is far too long to be a site name")]
    [InlineData("main.py")]                                              // a file name is not a hostname
    [InlineData("notes.md - Zen Browser")]
    public void ReturnsNullWhenNoSiteIsNamed(string title) =>
        Assert.Null(SiteFromTitle.Extract(title));

    [Theory]
    [InlineData("Page - InPrivate - Microsoft Edge", true)]
    [InlineData("Mozilla Firefox (Private Browsing)", true)]
    [InlineData("Page - Google Chrome (Incognito)", true)]
    [InlineData("Page - YouTube - Zen Browser", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RecognisesPrivateWindows(string title, bool expected) =>
        Assert.Equal(expected, SiteFromTitle.IsPrivate(title));

    [Theory]
    [InlineData("Zen")] [InlineData("Firefox")] [InlineData("CHROME")] [InlineData("Msedge")]
    public void KnowsTheBrowsers(string process) =>
        Assert.Contains(process, SiteFromTitle.BrowserProcesses);
}
