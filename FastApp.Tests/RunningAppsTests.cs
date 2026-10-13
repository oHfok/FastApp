using FastApp.Services;
using System.Diagnostics;
using System.IO;

namespace FastApp.Tests;

public class RunningAppsTests
{
    [Fact]
    public void ImagePathReturnsTheExecutableOfARunningProcess()
    {
        string path = RunningApps.ImagePath(Environment.ProcessId);
        Assert.Equal(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(path), ignoreCase: true);
    }

    [Fact]
    public void ImagePathIsFastAndQuietForProcessesThatRefuse()
    {
        // Every process on the machine, protected and system ones included: the whole
        // sweep has to finish quickly, and a refusal is a null rather than an exception.
        var all = Process.GetProcesses();
        try
        {
            var sw = Stopwatch.StartNew();
            int found = all.Count(p => RunningApps.ImagePath(p.Id) != null);
            sw.Stop();

            Assert.True(found > 10, $"only {found} paths resolved");
            Assert.True(sw.ElapsedMilliseconds < 1500, $"{all.Length} processes took {sw.ElapsedMilliseconds} ms");
            Assert.Null(RunningApps.ImagePath(0));        // the idle process
            Assert.Null(RunningApps.ImagePath(-1));
        }
        finally { foreach (var p in all) p.Dispose(); }
    }
}
