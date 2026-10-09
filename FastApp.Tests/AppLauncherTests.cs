using FastApp.Services;
using System.IO;

namespace FastApp.Tests;

public class AppLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FastApp.Tests", "launch-" + Guid.NewGuid().ToString("N"), "Discord");

    public AppLauncherTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_root)!, true); } catch { } }

    private string Touch(params string[] parts)
    {
        string path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void FollowsAnUpdateToTheNewVersionFolder()
    {
        string stored = Path.Combine(_root, "app-1.0.9260", "Discord.exe");   // deleted by the update
        string now = Touch("app-1.0.9261", "Discord.exe");
        Touch("Discord.exe");                                                // the root stub

        Assert.Equal(now, AppLauncher.FindCurrentVersionedPath(stored));
    }

    [Fact]
    public void ComparesVersionsNumericallyNotAlphabetically()
    {
        Touch("app-1.0.9261", "Discord.exe");
        string newest = Touch("app-1.0.10002", "Discord.exe");

        Assert.Equal(newest, AppLauncher.FindCurrentVersionedPath(Path.Combine(_root, "app-1.0.9260", "Discord.exe")));
    }

    [Fact]
    public void KeepsTheRelativePathInsideTheVersionFolder()
    {
        string now = Touch("app-3.6.6", "resources", "app", "git", "cmd", "git.exe");

        Assert.Equal(now, AppLauncher.FindCurrentVersionedPath(
            Path.Combine(_root, "app-3.6.5", "resources", "app", "git", "cmd", "git.exe")));
    }

    [Fact]
    public void FallsBackToTheRootStubWhenNoVersionFolderHasIt()
    {
        string stub = Touch("Discord.exe");

        Assert.Equal(stub, AppLauncher.FindCurrentVersionedPath(Path.Combine(_root, "app-1.0.9260", "Discord.exe")));
    }

    [Fact]
    public void ReturnsNullForOrdinaryPathsAndWhenNothingSurvives()
    {
        Assert.Null(AppLauncher.FindCurrentVersionedPath(@"C:\Program Files\Tool\tool.exe"));
        Assert.Null(AppLauncher.FindCurrentVersionedPath(Path.Combine(_root, "app-1.0.9260", "Discord.exe")));
        Assert.Null(AppLauncher.FindCurrentVersionedPath(""));
    }
}
