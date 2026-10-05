using FastApp.Services;
using System.Diagnostics;

namespace FastApp.Tests;

[Collection("Db")]
public class PerfLogTests
{
    public PerfLogTests() => PerfLog.SetEnabled(true, persist: false);

    [Fact]
    public void NothingIsRecordedWhileTurnedOff()
    {
        PerfLog.SetEnabled(false, persist: false);
        PerfLog.Done("test.while-off", Stopwatch.GetTimestamp() - Stopwatch.Frequency, 500);
        PerfLog.SetEnabled(true, persist: false);

        Assert.DoesNotContain("test.while-off", PerfLog.BuildReport());
    }

    [Fact]
    public void SlowOperationIsLoggedAndFastOneIsNot()
    {
        long slow = Stopwatch.GetTimestamp() - Stopwatch.Frequency; // pretend it started a second ago
        PerfLog.Done("test.slow-op", slow, 500);
        PerfLog.Done("test.fast-op", Stopwatch.GetTimestamp(), 500);

        string report = PerfLog.BuildReport();
        Assert.Contains("slow test.slow-op", report);
        Assert.DoesNotContain("slow test.fast-op", report);
    }

    [Fact]
    public void SampleWritesMachineLineAndPerOperationSummary()
    {
        PerfLog.Done("test.summarised-op", Stopwatch.GetTimestamp(), 10_000);
        PerfLog.Sample();

        string report = PerfLog.BuildReport();
        Assert.Contains(" sample ws=", report);
        Assert.Contains(" ops test.summarised-op n=1", report);
        Assert.Contains("FastApp diagnostics report", report);
    }
}
