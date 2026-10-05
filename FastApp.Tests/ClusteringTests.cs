using FastApp.Services.Analytics;

namespace FastApp.Tests;

public class ClusteringTests
{
    private static Insight I(string topic, double importance, params string[] apps)
    {
        var i = new Insight { Topic = topic, Importance = importance, Confidence = 1, Novelty = 1 };
        i.Apps.AddRange(apps);
        return i;
    }

    [Fact]
    public void OneCardPerTopic_StrongestSurvives()
    {
        var weak = I("switching", 0.3);
        var strong = I("switching", 0.9);
        var kept = Clustering.Reduce(new[] { weak, strong }, 7);
        Assert.Same(strong, Assert.Single(kept));
    }

    [Fact]
    public void NoAppMayHeadlineMoreThanMaxCards()
    {
        var all = new[]
        {
            I("a", 0.9, "Discord"), I("b", 0.8, "Discord"), I("c", 0.7, "Discord"), I("d", 0.6, "Chrome"),
        };
        var kept = Clustering.Reduce(all, 7);
        Assert.Equal(Clustering.MaxCardsPerApp, kept.Count(i => i.Apps.Contains("Discord")));
        Assert.Contains(all[3], kept);
        Assert.DoesNotContain(all[2], kept);
    }

    [Fact]
    public void RespectsMaxAndReportsWhatWasSuppressed()
    {
        var all = Enumerable.Range(0, 5).Select(n => I("t" + n, 1.0 - n * 0.1)).ToList();
        var kept = Clustering.Reduce(all, 3);
        Assert.Equal(3, kept.Count);
        Assert.Equal(all.Take(3), kept);                         // ranked by score
        Assert.Equal(all.Skip(3), Clustering.Suppressed(all, kept));
    }

    [Fact]
    public void AppMatchingIsCaseInsensitive()
    {
        var all = new[] { I("a", .9, "chrome"), I("b", .8, "CHROME"), I("c", .7, "Chrome") };
        Assert.Equal(2, Clustering.Reduce(all, 7).Count);
    }
}
