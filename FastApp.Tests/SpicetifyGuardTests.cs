using FastApp.Services;

namespace FastApp.Tests;

public class SpicetifyGuardTests
{
    [Theory]
    [InlineData("1.3.8.620.g39b276d5d7ac", "1.3.9.2055", true)]   // Spotify updated
    [InlineData("1.3.9.2055.gabc", "1.3.9.2055", false)]          // patched this version
    [InlineData("1.3.9.2055", "1.3.9.2055", false)]
    [InlineData("1.3.9.20550.gabc", "1.3.9.2055", true)]          // prefix alone isn't a match
    [InlineData(null, "1.3.9.2055", true)]                        // never backed up
    [InlineData("1.3.8.620.gabc", null, false)]                   // Spotify missing: do nothing
    public void IsStale(string backup, string spotify, bool expected) =>
        Assert.Equal(expected, SpicetifyGuard.IsStale(backup, spotify));

    [Fact]
    public void ReadsVersionFromBackupSectionOnly() =>
        Assert.Equal("1.3.8.620.g39b276d5d7ac", SpicetifyGuard.BackupVersion(new[]
        {
            "[Setting]", "version = wrong",
            "[Backup]", "version = 1.3.8.620.g39b276d5d7ac", "with    = 2.46.4",
        }));
}
