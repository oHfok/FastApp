using FastApp.Services;
using FastApp.ViewModels;

namespace FastApp.Tests;

// NotificationService.Quiet* are statics, so these must not run alongside each other.
[Collection("Db")]
public class QuietHoursTests
{
    private static DateTime At(int h, int m) => new(2026, 1, 1, h, m, 0);

    private static bool InQuiet(int? from, int? to, int h, int m)
    {
        NotificationService.QuietFromMinutes = from;
        NotificationService.QuietToMinutes = to;
        try { return NotificationService.InQuietHours(At(h, m)); }
        finally { NotificationService.QuietFromMinutes = null; NotificationService.QuietToMinutes = null; }
    }

    [Theory]
    [InlineData(9, 0, true)]    // start is inclusive
    [InlineData(12, 0, true)]
    [InlineData(16, 59, true)]
    [InlineData(17, 0, false)]  // end is exclusive
    [InlineData(8, 59, false)]
    public void SameDayWindow(int h, int m, bool expected) =>
        Assert.Equal(expected, InQuiet(9 * 60, 17 * 60, h, m));

    [Theory]
    [InlineData(22, 0, true)]
    [InlineData(23, 59, true)]
    [InlineData(0, 0, true)]    // wraps past midnight
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    [InlineData(21, 59, false)]
    [InlineData(12, 0, false)]
    public void WindowWrappingMidnight(int h, int m, bool expected) =>
        Assert.Equal(expected, InQuiet(22 * 60, 7 * 60, h, m));

    [Fact]
    public void DisabledOrEmptyWindowIsNeverQuiet()
    {
        Assert.False(InQuiet(null, null, 3, 0));
        Assert.False(InQuiet(22 * 60, null, 23, 0));
        Assert.False(InQuiet(600, 600, 10, 0)); // from == to
    }

    [Theory]
    [InlineData("22:00", true, 1320)]
    [InlineData("7:5", true, 425)]          // lenient, per the doc comment
    [InlineData(" 07:30 ", true, 450)]
    [InlineData("00:00", true, 0)]
    [InlineData("23:59", true, 1439)]
    [InlineData("24:00", false, 0)]
    [InlineData("12:60", false, 0)]
    [InlineData("-1:00", false, 0)]
    [InlineData("12", false, 0)]
    [InlineData("12:30:00", false, 0)]
    [InlineData("ab:cd", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    public void TryParseTimeOfDay(string text, bool ok, int minutes)
    {
        Assert.Equal(ok, MainViewModel.TryParseTimeOfDay(text, out int got));
        Assert.Equal(minutes, got);
    }
}
