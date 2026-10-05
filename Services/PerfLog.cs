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
        private static System.Threading.Timer _sampler, _pinger;
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
                        _sampler?.Dispose(); _pinger?.Dispose();
                        _sampler = _pinger = null;
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
                }
                catch { }
            }
        }

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

        internal static void Sample()
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                var now = DateTime.UtcNow;
                double wall = (now - _lastSample).TotalSeconds;
                var cpu = p.TotalProcessorTime;
                double procCpu = wall > 0 ? (cpu - _lastCpu).TotalSeconds / wall / Environment.ProcessorCount * 100 : 0;
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
                    Inv($"cpu={procCpu:0.0}% sysCpu={sysCpu} sysMem={(mem.TotalAvailableMemoryBytes > 0 ? mem.MemoryLoadBytes * 100 / mem.TotalAvailableMemoryBytes : 0)}% ") +
                    Inv($"gc0={g0 - _lastGc0} gc1={g1 - _lastGc1} gc2={g2 - _lastGc2} gcPause={(pause - _lastPause).TotalMilliseconds:0}ms ") +
                    $"pool={ThreadPool.ThreadCount}/{ThreadPool.PendingWorkItemCount}";
                _lastGc0 = g0; _lastGc1 = g1; _lastGc2 = g2; _lastPause = pause;
                Write("sample", line);

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
