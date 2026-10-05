using FastApp.Services;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Tests;

[Collection("Db")]
public class IntervalStoreTests
{
    private static string NewTable()
    {
        string table = "T" + Guid.NewGuid().ToString("N");
        using var db = new AppDbContext();
        db.Database.ExecuteSqlRaw(IntervalStore.CreateTableSql(table));
        return table;
    }

    private static readonly DateTime Day = new(2026, 3, 10);

    [Fact]
    public void RecordedIntervalComesBackForItsDay()
    {
        var t = NewTable();
        IntervalStore.RecordInterval(t, Day.AddHours(9), Day.AddHours(10));
        var got = IntervalStore.GetForDay(t, Day);
        Assert.Equal((Day.AddHours(9), Day.AddHours(10)), Assert.Single(got));
    }

    [Fact]
    public void EmptyOrBackwardsIntervalIsNotStored()
    {
        var t = NewTable();
        IntervalStore.RecordInterval(t, Day.AddHours(9), Day.AddHours(9));
        IntervalStore.RecordInterval(t, Day.AddHours(10), Day.AddHours(9));
        Assert.Empty(IntervalStore.GetForDay(t, Day));
    }

    [Fact]
    public void IntervalCrossingMidnightIsClippedOnBothDays()
    {
        var t = NewTable();
        IntervalStore.RecordInterval(t, Day.AddHours(23), Day.AddHours(26)); // 23:00 -> 02:00 next day

        Assert.Equal((Day.AddHours(23), Day.AddDays(1)), Assert.Single(IntervalStore.GetForDay(t, Day)));
        Assert.Equal((Day.AddDays(1), Day.AddHours(26)), Assert.Single(IntervalStore.GetForDay(t, Day.AddDays(1))));
        Assert.Empty(IntervalStore.GetForDay(t, Day.AddDays(2)));
        Assert.Empty(IntervalStore.GetForDay(t, Day.AddDays(-1)));
    }

    [Fact]
    public void IntervalEndingExactlyAtMidnightDoesNotLeakIntoNextDay()
    {
        var t = NewTable();
        IntervalStore.RecordInterval(t, Day.AddHours(22), Day.AddDays(1));
        Assert.Empty(IntervalStore.GetForDay(t, Day.AddDays(1)));
    }

    [Fact]
    public void ResultsAreOrderedByStart()
    {
        var t = NewTable();
        IntervalStore.RecordInterval(t, Day.AddHours(15), Day.AddHours(16));
        IntervalStore.RecordInterval(t, Day.AddHours(8), Day.AddHours(9));
        var got = IntervalStore.GetForDay(t, Day);
        Assert.Equal(new[] { Day.AddHours(8), Day.AddHours(15) }, got.Select(g => g.Start));
    }

    [Fact]
    public void OpenStartIsAddedOnlyForToday()
    {
        var t = NewTable();
        DateTime today = DateTime.Today;
        DateTime open = DateTime.Now.AddMinutes(-5);
        if (open < today) return; // five minutes after midnight: nothing meaningful to assert

        var (s, e) = Assert.Single(IntervalStore.GetForDay(t, today, open));
        Assert.Equal(open, s);
        Assert.True(e >= open);

        // a stretch still open has nothing to say about any other day
        Assert.Empty(IntervalStore.GetForDay(t, today.AddDays(-1), open));
    }

    [Fact]
    public void OpenStartFromBeforeMidnightIsClippedToToday()
    {
        var t = NewTable();
        DateTime today = DateTime.Today;
        if (DateTime.Now <= today) return;
        var (s, _) = Assert.Single(IntervalStore.GetForDay(t, today, today.AddHours(-3)));
        Assert.Equal(today, s);
    }

    [Fact]
    public void MissingTableReturnsEmptyInsteadOfThrowing()
    {
        Assert.Empty(IntervalStore.GetForDay("NoSuchTable" + Guid.NewGuid().ToString("N"), Day));
        IntervalStore.RecordInterval("NoSuchTable" + Guid.NewGuid().ToString("N"), Day, Day.AddHours(1)); // must not throw
    }
}
