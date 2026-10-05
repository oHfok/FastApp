using System.Windows.Input;
using FastApp.Services;

namespace FastApp.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(0, "nothing tracked yet")]
    [InlineData(59, "nothing tracked yet")]   // never an awkward "0m"
    [InlineData(60, "1m today")]
    [InlineData(47 * 60, "47m today")]
    [InlineData(3600, "1h 0m today")]
    [InlineData(2 * 3600 + 14 * 60 + 30, "2h 14m today")]
    [InlineData(30 * 3600, "30h 0m today")]   // hours do not roll into days
    public void TodayUsageDescribe(int seconds, string expected) =>
        Assert.Equal(expected, TodayUsage.Describe(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData("LeftCtrl,LeftShift,K", "Ctrl + Shift + K")]
    [InlineData("K,LeftShift,RightCtrl", "Ctrl + Shift + K")]       // modifiers always in fixed order
    [InlineData("LeftShift,RightShift,A", "Shift + A")]             // both shifts read as one
    [InlineData("LeftAlt,Return", "Alt + Enter")]
    [InlineData("D1", "1")]
    [InlineData("NumPad5", "Num 5")]
    [InlineData("LWin,OemComma", "Win + ,")]
    [InlineData("LeftCtrl,NotAKey", "Ctrl + NotAKey")]              // unreadable token shown, not dropped
    [InlineData("", "None")]
    [InlineData(null, "None")]
    public void HotkeyDescribe(string sequence, string expected) =>
        Assert.Equal(expected, HotkeyText.Describe(sequence));

    [Fact]
    public void HotkeyDescribeKeysOnlyModifiersStillReads() =>
        Assert.Equal("Ctrl", HotkeyText.Describe(new[] { Key.LeftCtrl }));
}
