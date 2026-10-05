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

    [Fact]
    public void ReadLinesReturnsOnlyLinesNewerThanSince()
    {
        PerfLog.Done("test.read-old", Stopwatch.GetTimestamp() - Stopwatch.Frequency, 500);
        Thread.Sleep(30);
        string since = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        Thread.Sleep(30);
        PerfLog.Done("test.read-new", Stopwatch.GetTimestamp() - Stopwatch.Frequency, 500);

        var all = PerfLog.ReadLines("");
        var newer = PerfLog.ReadLines(since);
        Assert.Contains(all, l => l.Contains("test.read-old"));
        Assert.DoesNotContain(newer, l => l.Contains("test.read-old"));
        Assert.Contains(newer, l => l.Contains("test.read-new"));
    }

    [Fact]
    public void SustainedCpuWritesAnAnomalyNamingTheBusyThread()
    {
        double oldCpu = PerfLog.HotCoreCpu; int oldWin = PerfLog.HotWindows;
        PerfLog.HotCoreCpu = 0; PerfLog.HotWindows = 2;   // always "hot", fires on the second window
        try
        {
            PerfLog.Done("test.busy-op", Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10, 10_000);
            PerfLog.Watch();                              // window 1: takes the thread baseline
            var until = Stopwatch.StartNew();
            while (until.ElapsedMilliseconds < 300) { }   // burn CPU on this thread
            PerfLog.Watch();                              // window 2: reports

            string report = PerfLog.BuildReport();
            Assert.Contains(" anomaly cpu core=", report);
            Assert.Contains("| threads: ", report);
            Assert.Contains("worker#", report);           // the test thread is not the UI thread
            Assert.Contains("test.busy-op", report);
        }
        finally { PerfLog.HotCoreCpu = oldCpu; PerfLog.HotWindows = oldWin; }
    }

    [Fact]
    public void MemoryGrowthWritesAnAnomalyWithTheBreakdown()
    {
        double old = PerfLog.MemoryGrowthMb;
        PerfLog.MemoryGrowthMb = 0;                       // any growth at all counts
        try
        {
            for (int i = 0; i < 11; i++) PerfLog.Sample();   // needs ten minutes of history
            string report = PerfLog.BuildReport();
            Assert.Contains(" anomaly memory priv=", report);
            Assert.Contains("managed heap", report);
            Assert.Contains("WebView2", report);
        }
        finally { PerfLog.MemoryGrowthMb = old; }
    }
}
