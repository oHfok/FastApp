using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FastApp.Services
{
    /// <summary>
    /// A flight recorder for "it felt laggy". Always on, tiny, and written so a
    /// single file can be sent over and read without the machine it came from:
    ///
    ///   - once a minute, a sample: memory, handles, threads, process and
    ///     whole-machine CPU, GC activity, and min/avg/max of every timed
    ///     operation since the last sample (the baseline a slow event is read
    ///     against);
    ///   - every second, a ping through the UI thread's message queue. How long
    ///     it waits to run is how long the window was unresponsive, and the line
    ///     says whether a garbage collection overlapped it;
    ///   - every timed operation that exceeds its own threshold, as it happens.
    ///
    /// Records only timings, counts and endpoint paths -- never app names,
    /// window titles, or query strings -- so the file is safe to send.
    /// Logging must never throw or block anything that matters.
    /// </summary>
    public static class PerfLog
    {
        private const long MaxFileBytes = 4_000_000;   // two files kept, ~8 MB worst case
        private const int MaxAggNames = 200;           // bounds the per-path HTTP aggregates
        private const double UiStallMs = 100;

        private static readonly object FileLock = new();
        private static readonly ConcurrentDictionary<string, Agg> Aggs = new();
        private static System.Threading.Timer _sampler, _pinger, _watcher;
        private static int _uiThreadId;
        private static int _pingPending;
        private static bool _started;

        private sealed class Agg { public int N; public double Total, Max; }

        private static string Dir => Path.Combine(AppDbContext.GetDbFolder(), "perf");
        public static string LogPath => Path.Combine(Dir, "perf.log");
        private static string OldPath => Path.Combine(Dir, "perf.old.log");

        // ---------------------------------------------------------------- timing API

        /// <summary>Start a measurement. Pair with <see cref="Done"/>.</summary>
        public static long Stamp() => Stopwatch.GetTimestamp();

        /// <summary>
        /// Finish a measurement: folded into the minute's min/avg/max, and written
        /// out on its own line when it took at least <paramref name="slowMs"/>.
        /// </summary>
        public static void Done(string name, long stamp, double slowMs, string detail = null)
        {
            if (!_enabled) return;
            try
            {
                double ms = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                if (Aggs.Count < MaxAggNames || Aggs.ContainsKey(name))
                {
                    var a = Aggs.GetOrAdd(name, _ => new Agg());
                    lock (a) { a.N++; a.Total += ms; if (ms > a.Max) a.Max = ms; }
                }
                // ponytail: no rate limit, a permanently slow op writes a line per
                // occurrence -- the 8 MB file cap bounds it; add a per-name limiter if that bites.
                if (ms >= slowMs)
                    Write("slow", Inv($"{name} {ms:0}ms") + (detail == null ? "" : " " + detail));
            }
            catch { }
        }

        /// <summary>A named point on the startup timeline: ms since the process began.</summary>
        public static void Milestone(string name)
        {
            if (!_enabled) return;
            try
            {
                using var p = Process.GetCurrentProcess();
                Write("milestone", Inv($"{name} +{(DateTime.Now - p.StartTime).TotalMilliseconds:0}ms"));
            }
            catch { }
        }

        public static void Event(string kind, string message) => Write(kind, message);

        // ---------------------------------------------------------------- lifecycle

        public const string SettingKey = "PerformanceLogging";
        private static readonly object StateLock = new();
        private static volatile bool _enabled;

        public static bool Enabled => _enabled;

        /// <summary>Called once at startup; the recorder is on unless the user turned it off.</summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;
            _uiThreadId = GetCurrentThreadId();   // Start runs on the UI thread
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Write("session-end", "clean exit");
            SetEnabled(AppSettingsStore.GetBool(SettingKey, true), persist: false);
        }

        /// <summary>
        /// Turns recording on or off immediately (timers included) and remembers the
        /// choice. Off means nothing new is written; the existing log stays until
        /// you delete the perf folder.
        /// </summary>
        public static void SetEnabled(bool on, bool persist = true)
        {
            lock (StateLock)
            {
                try
                {
                    if (persist) AppSettingsStore.SetBool(SettingKey, on);
                    if (on == _enabled) return;

                    if (!on)
                    {
                        Write("session-end", "recording turned off");   // last line, written while still enabled
                        _enabled = false;
                        _sampler?.Dispose(); _pinger?.Dispose(); _watcher?.Dispose();
                        _sampler = _pinger = _watcher = null;
                        _history.Clear(); _hot = 0; _threadBase = null;
                        Aggs.Clear();
                        return;
                    }

                    _enabled = true;
                    var mem = GC.GetGCMemoryInfo();
                    Write("session-start",
                        Inv($"version={Version()} os={Environment.OSVersion.Version} cores={Environment.ProcessorCount} ") +
                        Inv($"ram={mem.TotalAvailableMemoryBytes / (1024 * 1024)}MB runtime={Environment.Version} ") +
                        $"gc={(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}");

                    _lastSample = DateTime.UtcNow;
                    // Baselines, so the first minute's CPU figures are deltas too.
                    using (var self = Process.GetCurrentProcess()) _lastCpu = self.TotalProcessorTime;
                    if (GetSystemTimes(out var i0, out var k0, out var u0)) { _lastIdle = i0; _lastBusy = k0 + u0 - i0; }
                    _sampler = new System.Threading.Timer(_ => Sample(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
                    _pinger = new System.Threading.Timer(_ => Ping(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                    _watchAt = DateTime.UtcNow; _watchCpu = _lastCpu;
                    WebViewStats(1);   // baseline for the child processes' CPU
                    _lastAlloc = GC.GetTotalAllocatedBytes(false);
                    _watcher = new System.Threading.Timer(_ => Watch(), null, WatchEvery, WatchEvery);
                }
                catch { }
            }
        }

        private static string Delta(double v) { double r = Math.Round(v); return r >= 0 ? "+" + r.ToString("0", CultureInfo.InvariantCulture) : r.ToString("0", CultureInfo.InvariantCulture); }

        private static string Inv(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);

        private static string Version() =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

        // ---------------------------------------------------------------- UI responsiveness

        private static void Ping()
        {
            // One ping in flight at a time: a blocked UI thread produces a single
            // long measurement when it frees up, not a queue of stale ones.
            if (Interlocked.Exchange(ref _pingPending, 1) == 1) return;
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) { _pingPending = 0; return; }

                long t = Stamp();
                int gc2 = GC.CollectionCount(2);
                var pause = GC.GetTotalPauseDuration();
                dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    Interlocked.Exchange(ref _pingPending, 0);
                    Done("ui.latency", t, UiStallMs,
                        Inv($"gen2={GC.CollectionCount(2) - gc2} gcPause={(GC.GetTotalPauseDuration() - pause).TotalMilliseconds:0}ms"));
                }));
            }
            catch { _pingPending = 0; }
        }

        // ---------------------------------------------------------------- minute sample

        private static DateTime _lastSample = DateTime.UtcNow;
        private static TimeSpan _lastCpu;
        private static int _lastGc0, _lastGc1, _lastGc2;
        private static TimeSpan _lastPause;
        private static ulong _lastIdle, _lastBusy;
        private static long _lastAlloc;

        internal static void Sample()
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                var now = DateTime.UtcNow;
                double wall = (now - _lastSample).TotalSeconds;
                var cpu = p.TotalProcessorTime;
                double procCore = wall > 0 ? (cpu - _lastCpu).TotalSeconds / wall * 100 : 0;   // % of ONE core
                double procCpu = procCore / Environment.ProcessorCount;                          // % of the machine
                _lastSample = now; _lastCpu = cpu;

                string sysCpu = "?";
                if (GetSystemTimes(out var idle, out var kernel, out var user))
                {
                    ulong busy = kernel + user - idle;   // kernel time already includes idle
                    ulong total = busy + idle;
                    ulong dTotal = total - (_lastBusy + _lastIdle);
                    if (_lastBusy + _lastIdle > 0 && dTotal > 0)
                        sysCpu = Inv($"{(busy - _lastBusy) * 100.0 / dTotal:0}%");
                    _lastBusy = busy; _lastIdle = idle;
                }

                var mem = GC.GetGCMemoryInfo();
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                var pause = GC.GetTotalPauseDuration();
                string line =
                    Inv($"ws={p.WorkingSet64 / (1024 * 1024)}MB priv={p.PrivateMemorySize64 / (1024 * 1024)}MB ") +
                    Inv($"heap={GC.GetTotalMemory(false) / (1024 * 1024)}MB threads={p.Threads.Count} handles={p.HandleCount} ") +
                    Inv($"cpu={procCpu:0.0}% core={procCore:0.#}% sysCpu={sysCpu} sysMem={(mem.TotalAvailableMemoryBytes > 0 ? mem.MemoryLoadBytes * 100 / mem.TotalAvailableMemoryBytes : 0)}% ") +
                    Inv($"gc0={g0 - _lastGc0} gc1={g1 - _lastGc1} gc2={g2 - _lastGc2} gcPause={(pause - _lastPause).TotalMilliseconds:0}ms ") +
                    Inv($"pool={ThreadPool.ThreadCount}/{ThreadPool.PendingWorkItemCount} ") +
                    HeapBreakdown() + WebViewText(wall);
                _lastGc0 = g0; _lastGc1 = g1; _lastGc2 = g2; _lastPause = pause;
                Write("sample", line);
                CheckMemoryGrowth(p);

                foreach (var name in Aggs.Keys.ToList())
                {
                    if (!Aggs.TryRemove(name, out var a)) continue;
                    lock (a)
                        if (a.N > 0)
                            Write("ops", Inv($"{name} n={a.N} avg={a.Total / a.N:0.#}ms max={a.Max:0.#}ms"));
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- memory & child processes

        // Span-typed, so it cannot be captured by a local function: indexed fresh each call.
        private static double GenMb(int i)
        {
            var g = GC.GetGCMemoryInfo(GCKind.Any).GenerationInfo;
            return i < g.Length ? g[i].SizeAfterBytes / 1048576.0 : 0;
        }

        private static string HeapBreakdown()
        {
            // As of the last garbage collection: sizes of gen0/gen1/gen2, the large-object
            // heap and the pinned-object heap, plus what the app allocated this minute.
            // A leak climbs in gen2 or LOH; churn shows as high alloc with a flat heap.
            long alloc = GC.GetTotalAllocatedBytes(false);
            double allocMb = (alloc - _lastAlloc) / 1048576.0;
            _lastAlloc = alloc;
            return Inv($"sz0={GenMb(0):0.#}MB sz1={GenMb(1):0.#}MB sz2={GenMb(2):0.#}MB loh={GenMb(3):0.#}MB poh={GenMb(4):0.#}MB alloc={allocMb:0}MB ");
        }

        private static readonly Dictionary<int, TimeSpan> WebViewCpu = new();

        /// <summary>
        /// Memory and CPU of FastApp's own WebView2 processes (the palette's renderer, GPU
        /// and helpers). They are separate processes, so the main process's numbers never
        /// include them. Found by walking parent links from this process.
        /// </summary>
        private static (int count, double wsMb, double privMb, double coreCpu) WebViewStats(double wallSeconds)
        {
            var procs = Process.GetProcessesByName("msedgewebview2");
            try
            {
                var parent = procs.ToDictionary(x => x.Id, ParentOf);
                var ours = new HashSet<int> { Environment.ProcessId };
                bool grew;
                do
                {
                    grew = false;
                    foreach (var kv in parent)
                        if (!ours.Contains(kv.Key) && ours.Contains(kv.Value)) { ours.Add(kv.Key); grew = true; }
                } while (grew);

                int n = 0; double ws = 0, priv = 0, cpuSeconds = 0;
                var seen = new Dictionary<int, TimeSpan>();
                foreach (var x in procs)
                {
                    if (!ours.Contains(x.Id)) continue;
                    try
                    {
                        n++; ws += x.WorkingSet64; priv += x.PrivateMemorySize64;
                        var t = x.TotalProcessorTime; seen[x.Id] = t;
                        // A process not seen last time started within the last interval: all its CPU is new.
                        cpuSeconds += (t - (WebViewCpu.TryGetValue(x.Id, out var prev) ? prev : TimeSpan.Zero)).TotalSeconds;
                    }
                    catch { }
                }
                WebViewCpu.Clear();
                foreach (var kv in seen) WebViewCpu[kv.Key] = kv.Value;
                return (n, ws / 1048576.0, priv / 1048576.0, wallSeconds > 0 ? cpuSeconds / wallSeconds * 100 : 0);
            }
            catch { return (0, 0, 0, 0); }
            finally { foreach (var x in procs) x.Dispose(); }
        }

        private static string WebViewText(double wall)
        {
            var w = WebViewStats(wall);
            return Inv($"wv={w.count} wvWs={w.wsMb:0}MB wvPriv={w.privMb:0}MB wvCore={w.coreCpu:0.#}%");
        }

        private static int ParentOf(Process x)
        {
            try
            {
                var info = new ProcessBasicInformation();
                return NtQueryInformationProcess(x.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                    ? (int)info.InheritedFromUniqueProcessId : -1;
            }
            catch { return -1; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        // ---------------------------------------------------------------- anomaly capture

        // A sample says THAT something is wrong. These say WHAT was running when it was:
        // the busiest threads and the busiest timed operations at the moment, written once
        // when a condition is first met, then not again for a while.
        private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(10);
        internal static double HotCoreCpu = 20;                        // % of one core; FastApp idles around 1%
        internal static int HotWindows = 3;                            // sustained for 30 s
        internal static double MemoryGrowthMb = 100;                   // private-bytes growth over the last hour
        private static readonly TimeSpan CpuCooldown = TimeSpan.FromMinutes(5), MemCooldown = TimeSpan.FromMinutes(30);

        private static DateTime _watchAt, _baseAt, _cpuQuietUntil, _memQuietUntil;
        private static TimeSpan _watchCpu;
        private static int _hot;
        private static Dictionary<int, TimeSpan> _threadBase;

        private sealed record Point(DateTime T, double Priv, double Heap, double Loh, double Poh, double Sz2, int Handles, int Threads, double Wv);
        private static readonly Queue<Point> _history = new();   // one per minute, an hour deep

        internal static void Watch()
        {
            if (!_enabled) return;
            try
            {
                using var p = Process.GetCurrentProcess();
                var now = DateTime.UtcNow;
                var cpu = p.TotalProcessorTime;
                double wall = (now - _watchAt).TotalSeconds;
                double core = wall > 0 ? (cpu - _watchCpu).TotalSeconds / wall * 100 : 0;
                _watchAt = now; _watchCpu = cpu;

                if (core < HotCoreCpu) { _hot = 0; _threadBase = null; return; }

                _hot++;
                if (_hot == 1) { _threadBase = ThreadTimes(p); _baseAt = now; }
                else if (_hot == HotWindows && _threadBase != null && now >= _cpuQuietUntil)
                {
                    _cpuQuietUntil = now + CpuCooldown;
                    double span = (now - _baseAt).TotalSeconds;
                    var after = ThreadTimes(p);
                    var top = after
                        .Select(kv => (id: kv.Key, secs: (kv.Value - (_threadBase.TryGetValue(kv.Key, out var b) ? b : TimeSpan.Zero)).TotalSeconds))
                        .OrderByDescending(x => x.secs).Take(4).Where(x => x.secs > 0.05)
                        .Select(x => Inv($"{(x.id == _uiThreadId ? "UI thread" : "worker")}#{x.id} {x.secs / span * 100:0}%"));
                    Write("anomaly", Inv($"cpu core={core:0}% sustained={(HotWindows * WatchEvery.TotalSeconds):0}s pool={ThreadPool.ThreadCount}/{ThreadPool.PendingWorkItemCount}")
                        + " | threads: " + string.Join(", ", top) + Inv($" (% of one core over {span:0}s)")
                        + " | ops: " + BusyOps());
                }
            }
            catch { }
        }

        private static Dictionary<int, TimeSpan> ThreadTimes(Process p)
        {
            var d = new Dictionary<int, TimeSpan>();
            foreach (ProcessThread t in p.Threads)
            {
                try { d[t.Id] = t.TotalProcessorTime; } catch { }
                finally { t.Dispose(); }
            }
            return d;
        }

        /// <summary>The timed operations that have used the most time in the current minute so far.</summary>
        private static string BusyOps()
        {
            var top = Aggs.ToArray()
                .Select(kv => { lock (kv.Value) return (name: kv.Key, total: kv.Value.Total, n: kv.Value.N, max: kv.Value.Max); })
                .OrderByDescending(x => x.total).Take(4)
                .Select(x => Inv($"{x.name} total={x.total:0}ms n={x.n} max={x.max:0}ms"));
            var s = string.Join(", ", top);
            return s.Length > 0 ? s : "none";
        }

        private static void CheckMemoryGrowth(Process p)
        {
            var w = WebViewStats(0);
            var now = new Point(DateTime.UtcNow, p.PrivateMemorySize64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0,
                GenMb(3), GenMb(4), GenMb(2), p.HandleCount, p.Threads.Count, w.privMb);
            _history.Enqueue(now);
            while (_history.Count > 60) _history.Dequeue();

            var old = _history.Peek();
            double grew = now.Priv - _history.Min(h => h.Priv);
            if (_history.Count < 10 || grew < MemoryGrowthMb || now.T < _memQuietUntil) return;

            _memQuietUntil = now.T + MemCooldown;
            double mins = (now.T - old.T).TotalMinutes;
            Write("anomaly", Inv($"memory priv={now.Priv:0}MB growth={grew:0}MB over={mins:0}min")
                + Inv($" | change since {mins:0} min ago: private {Delta(now.Priv - old.Priv)}MB, managed heap {Delta(now.Heap - old.Heap)}MB, gen2 {Delta(now.Sz2 - old.Sz2)}MB, LOH {Delta(now.Loh - old.Loh)}MB, pinned {Delta(now.Poh - old.Poh)}MB, ")
                + Inv($"WebView2 {Delta(now.Wv - old.Wv)}MB, handles {Delta(now.Handles - old.Handles)}, threads {Delta(now.Threads - old.Threads)}")
                + " | ops: " + BusyOps());
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

        // ---------------------------------------------------------------- file

        private static void Write(string kind, string message)
        {
            if (!_enabled) return;
            try
            {
                string line = Inv($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {kind} {message}{Environment.NewLine}");
                lock (FileLock)
                {
                    Directory.CreateDirectory(Dir);
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > MaxFileBytes)
                        File.Move(LogPath, OldPath, overwrite: true);
                    File.AppendAllText(LogPath, line);
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- report

        /// <summary>
        /// Everything needed to diagnose a lag report in one text file: the
        /// machine, the database's size, the recorder's log, and the tail of the
        /// crash log if there is one.
        /// </summary>
        public static string BuildReport()
        {
            var sb = new StringBuilder();
            try
            {
                var mem = GC.GetGCMemoryInfo();
                sb.AppendLine("FastApp diagnostics report");
                sb.AppendLine(Inv($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}"));
                sb.AppendLine($"FastApp {Version()} ({UpdateService.CurrentVersionText}), {(Environment.Is64BitProcess ? "x64" : "x86")}, .NET {Environment.Version}");
                sb.AppendLine($"Windows {Environment.OSVersion.Version}, {Environment.ProcessorCount} logical cores, {mem.TotalAvailableMemoryBytes / (1024 * 1024)} MB RAM");
                using (var p = Process.GetCurrentProcess())
                    sb.AppendLine($"Running for {DateTime.Now - p.StartTime:d\\.hh\\:mm\\:ss}");
                sb.AppendLine("Contains timings and counts only -- no app names, window titles or web addresses.");
                sb.AppendLine();

                string folder = AppDbContext.GetDbFolder();
                foreach (var f in new[] { "appmanager.db", "appmanager.db-wal" })
                {
                    var fi = new FileInfo(Path.Combine(folder, f));
                    if (fi.Exists) sb.AppendLine($"{f}: {fi.Length / 1024} KB");
                }
                sb.AppendLine();
                sb.AppendLine("Line kinds: sample = once a minute; ops = per-operation avg/max over that minute;");
                sb.AppendLine("slow = one operation over its threshold; ui.latency slow = window was unresponsive that long.");
                sb.AppendLine();
                sb.AppendLine("===== perf log =====");
                sb.Append(Tail(OldPath, 600_000));
                sb.Append(Tail(LogPath, 1_400_000));

                string crash = Tail(CrashLog.LogPath, 20_000);
                if (crash.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("===== crash log (tail) =====");
                    sb.Append(crash);
                }
            }
            catch (Exception ex) { sb.AppendLine("Report failed part-way: " + ex.Message); }
            return sb.ToString();
        }

        /// <summary>Writes the report to the Desktop and returns its path.</summary>
        public static string SaveReportToDesktop()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"fastapp-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt");
            File.WriteAllText(path, BuildReport());
            return path;
        }

        /// <summary>
        /// Writes the report to the Desktop and shows it in Explorer, ready to send.
        /// Off the calling thread: the report reads the log files.
        /// </summary>
        public static void SaveReportAndReveal()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string path = SaveReportToDesktop();
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                }
                catch (Exception ex) { CrashLog.Log("Diagnostics report failed", ex); }
            });
        }

        /// <summary>
        /// The recorded lines newer than <paramref name="since"/> (a "yyyy-MM-dd HH:mm:ss.fff"
        /// timestamp, or empty for everything), oldest first. Lines start with a
        /// fixed-width timestamp, so ordinal comparison of the prefix is a time comparison.
        /// This is what the live debug page polls.
        /// </summary>
        public static List<string> ReadLines(string since)
        {
            bool filter = since != null && since.Length >= 23;
            var lines = new List<string>();
            foreach (var path in new[] { OldPath, LogPath })
                foreach (var line in Tail(path, int.MaxValue).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var l = line.TrimEnd('\r');
                    if (l.Length > 24 && (!filter || string.CompareOrdinal(l, 0, since, 0, 23) > 0))
                        lines.Add(l);
                }
            return lines;
        }

        private static string Tail(string path, int maxBytes)
        {
            try
            {
                lock (FileLock)
                {
                    if (!File.Exists(path)) return string.Empty;
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    bool cut = fs.Length > maxBytes;
                    if (cut) fs.Seek(-maxBytes, SeekOrigin.End);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    string text = sr.ReadToEnd();
                    if (cut) { int nl = text.IndexOf('\n'); if (nl >= 0) text = text[(nl + 1)..]; }
                    return text;
                }
            }
            catch { return string.Empty; }
        }
    }
}
