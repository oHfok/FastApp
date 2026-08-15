using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace FastApp.ViewModels
{

    public partial class MainViewModel : ObservableObject
    {
        // ==========================================
        // WIN32 APIs (The Airtight Versions)
        // ==========================================
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // Window-title capture — opt-in only (see CaptureWindowTitles setting), since a
        // title (browser tab text, document names, etc.) is far more sensitive than a
        // bare process name.
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder text, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;


        // ==========================================
        // STATE VARIABLES
        // ==========================================
        [ObservableProperty]
        private ObservableCollection<AppItemModel> _managedApps;

        [ObservableProperty]
        private ObservableCollection<AppItemModel> _detectedApps = new();

        private readonly Dictionary<AppItemModel, HashSet<Key>> _compiledHotkeys = new();
        private readonly Channel<AppItemModel> _triggerQueue = Channel.CreateUnbounded<AppItemModel>();
        private HashSet<string> _gamingProcessNames = new(StringComparer.OrdinalIgnoreCase);

        // NEW: Thread-safe queues for the shadow tables
        private readonly ConcurrentQueue<ViewModels.SessionLog> _pendingSessions = new();
        private readonly ConcurrentQueue<ViewModels.MacroEventLog> _pendingMacros = new();

        public StatisticsViewModel StatisticsVM { get; }

        [ObservableProperty]
        private ICollectionView _detectedAppsView;

        // Windows startup toggle
        [ObservableProperty] private bool _launchOnSystemStartup;
        [ObservableProperty] private bool _isStartupToggleBusy;

        // Whether a Parental PIN is configured (checked once per tracker flush —
        // see StartProcessTrackerAsync). When set, the Daily Limit / Force Close
        // controls in the app's own Settings panel lock: editing them then has to
        // go through the web dashboard's PIN-gated /api/update-limit instead,
        // otherwise the PIN system is pointless — anyone could just clear the
        // limit here with zero friction.
        [ObservableProperty] private bool _isPinConfigured;
        public bool CanEditDailyLimit => !IsPinConfigured;
        partial void OnIsPinConfiguredChanged(bool value) => OnPropertyChanged(nameof(CanEditDailyLimit));

        private bool _suppressStartupToggleHandler;

        // Search filter for Tab 1
        [ObservableProperty] private string _appSearchText;
        public ICollectionView FilteredManagedApps { get; }

        [ObservableProperty]
        private int _selectedTabIndex;

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    DetectedAppsView?.Refresh();
                }
            }
        }

        private readonly AppDbContext _dbContext;

        // Global OSD Toggle
        [ObservableProperty] private bool _enableOsd;


        // ==========================================
        // INITIALIZATION
        // ==========================================
        public MainViewModel()
        {
            LoadOsdSetting();

            // After
            _dbContext = new AppDbContext();
            _dbContext.Database.Migrate();

            _dbContext.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

            // --- TEMPORARY MIGRATION SCRIPT (Run Once) ---
            var logsRequiringMigration = _dbContext.DailyLogs.Where(l => l.TimeSpentTicks == null).ToList();
            if (logsRequiringMigration.Any())
            {
                System.Diagnostics.Debug.WriteLine($"\n[DATABASE] Migrating {logsRequiringMigration.Count} logs to new integer format...");
                foreach (var log in logsRequiringMigration)
                {
                    log.TimeSpentTicks = log.TimeSpent.Ticks;
                    log.AfkTimeSpentTicks = log.AfkTimeSpent.Ticks;
                    log.TimeFocusedTicks = log.TimeFocused.Ticks;
                }
                _dbContext.SaveChanges();
                System.Diagnostics.Debug.WriteLine("[DATABASE] Migration complete!\n");
            }



            // --- PHASE 5: UPGRADED DATABASE CLEANUP ---
            Task.Run(() =>
            {
                try
                {
                    using var cleanupDb = new AppDbContext();
                    int retentionDays = 90;
                    using var command = cleanupDb.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT Value FROM AppSettings WHERE Key = 'RetentionDays'";
                    cleanupDb.Database.OpenConnection();
                    using var result = command.ExecuteReader();
                    if (result.Read() && int.TryParse(result.GetString(0), out int parsedDays))
                    {
                        retentionDays = parsedDays;
                    }

                    var cutoffDate = DateTime.Today.AddDays(-retentionDays);
                    string sqlDateFormat = cutoffDate.ToString("yyyy-MM-dd HH:mm:ss");

                    cleanupDb.Database.ExecuteSqlRaw($"DELETE FROM SessionLogs WHERE StartTime < '{sqlDateFormat}';");
                    cleanupDb.Database.ExecuteSqlRaw($"DELETE FROM MacroEventLogs WHERE Timestamp < '{sqlDateFormat}';");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"DB Cleanup Failed: {ex.Message}");
                }
            });

            // 1. QUICK LOAD: Read from SQLite
            var savedApps = _dbContext.ManagedApps.OrderBy(a => a.OrderIndex).ToList();
            ManagedApps = new ObservableCollection<AppItemModel>(savedApps);

            // 2. COMPILE Caches
            RecompileHotkeys();
            UpdateGamingProcessCache();

            // 3. DEFERRED STATS
            StatisticsVM = new StatisticsViewModel(_dbContext, this);

            // 4. SETUP FILTERS & HANDLERS 
            FilteredManagedApps = CollectionViewSource.GetDefaultView(ManagedApps);
            FilteredManagedApps.Filter = (item) =>
            {
                if (string.IsNullOrWhiteSpace(AppSearchText)) return true;
                var app = (AppItemModel)item;
                return (app.Name?.Contains(AppSearchText, StringComparison.OrdinalIgnoreCase) == true) ||
                       (app.CustomName?.Contains(AppSearchText, StringComparison.OrdinalIgnoreCase) == true);
            };

            // --- NEW: XAML-ALIGNED REMOTE CONTROL ---
            WeakReferenceMessenger.Default.Register<UpdateCategoryCommand>(this, (recipient, message) =>
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    var existingApp = ManagedApps.FirstOrDefault(a =>
                        a.Name.Equals(message.AppName, StringComparison.OrdinalIgnoreCase));

                    if (existingApp != null)
                    {
                        // 1. The EXACT list from your XAML
                        var masterCategories = new[] {
                            "Development", "Gaming", "Productivity", "Browsing", "Communication",
                            "Media Production", "Music", "Fun", "Education", "Utilities", "Other"
                        };

                        // 2. Sanitize and strictly match the incoming web text to your XAML list
                        string exactCategoryMatch = masterCategories.FirstOrDefault(c =>
                            c.Equals(message.NewCategory.Trim(), StringComparison.OrdinalIgnoreCase))
                            ?? message.NewCategory.Trim();

                        // 3. Update the base Category property (Saves to DB)
                        existingApp.Category = exactCategoryMatch;

                        // 4. CRITICAL: If your XAML is binding to DetailCategory, we MUST update it too!
                        // If DetailCategory is a property on the AppItemModel:
                        // existingApp.DetailCategory = exactCategoryMatch; 

                        // OR if DetailCategory is a property on MainViewModel itself tracking the selected item:
                        // if (this.SelectedApp == existingApp) { this.DetailCategory = exactCategoryMatch; }

                        // 5. Force the UI to physically redraw
                        FilteredManagedApps?.Refresh();
                    }
                });
            });

            // Daily limit / Strict Focus Mode, editable from the web dashboard.
            // Setting these ObservableProperty-backed fields fires PropertyChanged,
            // which the subscription below already wires up to auto-save.
            WeakReferenceMessenger.Default.Register<UpdateLimitCommand>(this, (recipient, message) =>
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    var existingApp = ManagedApps.FirstOrDefault(a =>
                        a.Name.Equals(message.AppName, StringComparison.OrdinalIgnoreCase));

                    if (existingApp != null)
                    {
                        existingApp.DailyLimitMinutes = Math.Max(0, message.DailyLimitMinutes);
                        existingApp.StrictFocusMode = message.StrictFocusMode;
                        existingApp.HasNotifiedToday = false; // let a newly-raised limit notify again today
                        existingApp.HasWarnedToday = false;
                        FilteredManagedApps?.Refresh();
                    }
                });
            });

            // PIN-gated time extension. The dashboard verifies the PIN itself (pure
            // data lookup, no live app state needed) and only sends this once it's
            // confirmed correct — this handler just has to apply the grant.
            WeakReferenceMessenger.Default.Register<GrantExtensionCommand>(this, (recipient, message) =>
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    var existingApp = ManagedApps.FirstOrDefault(a =>
                        a.Name.Equals(message.AppName, StringComparison.OrdinalIgnoreCase));

                    if (existingApp != null)
                    {
                        // A bonus from a previous day is stale — start fresh rather
                        // than stacking on top of a leftover value.
                        if (existingApp.BonusMinutesDate?.Date != DateTime.Today)
                        {
                            existingApp.TodayBonusMinutes = 0;
                        }
                        existingApp.TodayBonusMinutes += Math.Max(0, message.ExtraMinutes);
                        existingApp.BonusMinutesDate = DateTime.Today;
                        existingApp.HasNotifiedToday = false; // re-arm against the raised effective limit
                        existingApp.HasWarnedToday = false;
                        FilteredManagedApps?.Refresh();
                    }
                });
            });

            foreach (var app in ManagedApps)
            {
                app.PropertyChanged += (s, e) => _dbContext.SaveChanges();
            }

            ManagedApps.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                {
                    foreach (AppItemModel newItem in e.NewItems)
                        newItem.PropertyChanged += (sender, args) => _dbContext.SaveChanges();
                }
                _dbContext.SaveChanges();
            };

           

            // 5. FIRE AND FORGET: Background tasks
            _ = Task.Run(() =>
            {
                RunAutoLaunchAsync();
                _ = ProcessTriggersAsync();
                _ = StartProcessTrackerAsync();
                _ = Services.DashboardServerService.StartAsync();


                // NEW: reflect actual current registration state in the toggle
                bool isRegistered = StartupTaskService.IsStartupCorrectlyRegistered();
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    _suppressStartupToggleHandler = true;
                    LaunchOnSystemStartup = isRegistered;
                    _suppressStartupToggleHandler = false;
                });
            });
        }

        // ==========================================
        // UI & LAYOUT STATE
        // ==========================================
        [ObservableProperty]
        private bool _isCompactMode = false;

        [RelayCommand]
        private void ExpandCompactApp(AppItemModel app)
        {
            // When a user clicks a tiny row, immediately turn off compact mode 
            // so they can see the expanded details of the app they just clicked!
            IsCompactMode = false;
        }

        // Method to handle DB-backed Drag and Drop Reordering
        public void ReorderApps(int oldIndex, int newIndex)
        {
            if (oldIndex < 0 || oldIndex >= ManagedApps.Count || newIndex < 0 || newIndex >= ManagedApps.Count)
                return;

            // Move the item in the ObservableCollection
            var item = ManagedApps[oldIndex];
            ManagedApps.RemoveAt(oldIndex);
            ManagedApps.Insert(newIndex, item);

            // Rewrite the OrderIndex for the entire list so the DB remembers
            for (int i = 0; i < ManagedApps.Count; i++)
            {
                ManagedApps[i].OrderIndex = i;
            }

            _dbContext.SaveChanges();
            FilteredManagedApps.Refresh();
        }


        private string GetActiveProcessName()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hWnd, out uint pid);
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);

            if (hProcess == IntPtr.Zero) return null;

            try
            {
                int capacity = 1024;
                StringBuilder sb = new StringBuilder(capacity);
                if (QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
                {
                    return Path.GetFileNameWithoutExtension(sb.ToString()).ToLower();
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }

            return null;
        }

        // Opt-in only (CaptureWindowTitles setting) — reads the raw title text of
        // whatever window is currently focused, e.g. a browser tab title or a
        // document name. Called separately from GetActiveProcessName so that
        // method's other caller (the gaming guard) is unaffected.
        private string GetActiveWindowTitle()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return null;

            int length = GetWindowTextLength(hWnd);
            if (length <= 0) return null;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }


        // ==========================================
        // UI INTERACTION HANDLERS
        // ==========================================

        // This method automatically fires the moment the user clicks a tab
        partial void OnSelectedTabIndexChanged(int value)
        {
            if (value == 1)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                StatisticsVM?.RefreshStats(forceLoad: true);
                sw.Stop();
                System.Diagnostics.Debug.WriteLine($"[PERF] RefreshStats took {sw.ElapsedMilliseconds}ms");
            }
        }

        // This method automatically runs every time you type a letter into the search box
        partial void OnAppSearchTextChanged(string value)
        {
            FilteredManagedApps.Refresh();
        }

        partial void OnEnableOsdChanged(bool value)
        {
            try
            {
                File.WriteAllText(GetSettingsPath(), value.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save OSD setting: {ex.Message}");
            }
        }

        partial void OnLaunchOnSystemStartupChanged(bool value)
        {
            if (_suppressStartupToggleHandler) return;

            bool desiredState = value;
            IsStartupToggleBusy = true;

            Task.Run(() =>
            {
                bool success = StartupTaskService.SetStartup(desiredState);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    _suppressStartupToggleHandler = true;
                    // If SetStartup failed (e.g. the user clicked "No" on the UAC prompt),
                    // snap the toggle back to the previous, actually-true state instead of lying.
                    LaunchOnSystemStartup = success ? desiredState : !desiredState;
                    _suppressStartupToggleHandler = false;
                    IsStartupToggleBusy = false;
                });
            });
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            _dbContext?.SaveChanges();
        }


        // ==========================================
        // CACHE & HOTKEY COMPILERS
        // ==========================================
        public void RecompileHotkeys()
        {
            _compiledHotkeys.Clear();
            foreach (var app in ManagedApps)
            {
                if (!string.IsNullOrEmpty(app.HotkeySequence))
                {
                    var keys = app.HotkeySequence
                                  .Split(',')
                                  .Select(k => (Key)Enum.Parse(typeof(Key), k))
                                  .ToHashSet();

                    _compiledHotkeys[app] = keys;
                }
            }
        }

        public void UpdateGamingProcessCache()
        {
            try
            {
                var gamingApps = _dbContext.AppCategories
                    .Where(c => c.Category == "Gaming")
                    .Select(c => c.AppName)
                    .ToList();

                var newCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var app in ManagedApps)
                {
                    if (gamingApps.Contains(app.Name) && !string.IsNullOrEmpty(app.ExecutablePath))
                    {
                        string exeName = Path.GetFileNameWithoutExtension(app.ExecutablePath);
                        newCache.Add(exeName);
                    }
                }

                _gamingProcessNames = newCache;
            }
            catch { }
        }


        // ==========================================
        // THE MACRO ENGINE (Zero Lag)
        // ==========================================
        public void CheckForHotkeys(HashSet<Key> currentlyPressedKeys)
        {
            if (currentlyPressedKeys.Count == 0) return;

            foreach (var kvp in _compiledHotkeys)
            {
                if (currentlyPressedKeys.SetEquals(kvp.Value))
                {
                    // FIRE AND FORGET! Windows is immediately released to process your game movement.
                    _triggerQueue.Writer.TryWrite(kvp.Key);
                }
            }
        }

        private async Task ProcessTriggersAsync()
        {
            await foreach (var app in _triggerQueue.Reader.ReadAllAsync())
            {
                // --- THE AIRTIGHT GAMING GUARD ---
                bool blockMacro = false;
                IntPtr hWnd = GetForegroundWindow();

                if (hWnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);

                    if (hProcess == IntPtr.Zero)
                    {
                        blockMacro = true; // Fail-closed on Anti-Cheat blocks
                    }
                    else
                    {
                        try
                        {
                            int capacity = 1024;
                            StringBuilder sb = new StringBuilder(capacity);
                            if (QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
                            {
                                string exeName = Path.GetFileNameWithoutExtension(sb.ToString());
                                if (_gamingProcessNames.Contains(exeName))
                                {
                                    blockMacro = true; // Game detected!
                                }
                            }
                        }
                        finally
                        {
                            CloseHandle(hProcess);
                        }
                    }
                }

                if (blockMacro) continue; // Throw macro in the trash
                // ----------------------------------
                _pendingMacros.Enqueue(new ViewModels.MacroEventLog
                {
                    AppName = app.Name,
                    Timestamp = DateTime.Now
                });

                // 1. Execute the heavy Action entirely in the background
                Services.ActionHookEngine.Execute(app);

                // 2. Safely hop back to the UI thread to update the counter and save the DB (BeginInvoke = Non-blocking)
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    app.HotkeyTriggerCount++;
                });

                // 3. Show OSD
                if (EnableOsd)
                {
                    Services.OsdService.Show($"{app.DisplayNamePrimary} Activated", app.IsAction);
                }
            }
        }


        // ==========================================
        // BACKGROUND SERVICES
        // ==========================================
        private void RunAutoLaunchAsync()
        {
            var runningProcesses = Process.GetProcesses()
                                          .Select(p => p.ProcessName.ToLower())
                                          .ToHashSet();

            foreach (var app in ManagedApps)
            {
                if (app.LaunchOnStartup && !string.IsNullOrEmpty(app.ExecutablePath))
                {
                    string exeName = Path.GetFileNameWithoutExtension(app.ExecutablePath).ToLower();

                    if (!runningProcesses.Contains(exeName))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = app.ExecutablePath,
                                UseShellExecute = true
                            });
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to auto-launch {app.Name}: {ex.Message}");
                        }
                    }
                }
            }
        }

        private async Task StartProcessTrackerAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            var timeCache = new Dictionary<string, TimeSpan>();
            var afkCache = new Dictionary<string, TimeSpan>();
            var focusCache = new Dictionary<string, TimeSpan>(); // NEW: Focus Cache

            // Daily-limit enforcement: HasNotifiedToday is in-memory only (never
            // persisted), so it has to be reset by hand once a day rolls over.
            DateTime? lastLimitResetDate = null;
            const int WarningThresholdMinutes = 5;

            // Live per-tick minutes-today, per app: baseline (what's actually
            // committed to the DB as of the last flush) + timeCache (accumulated
            // since that flush). Checking against this every tick, instead of only
            // the value on disk, is what makes enforcement react within ~5s instead
            // of ~60s. Seeded from the DB once at startup so a mid-day restart of
            // FastApp itself doesn't reset anyone's count back to zero.
            var baselineMinutesToday = new Dictionary<string, double>();
            try
            {
                foreach (var existingLog in _dbContext.DailyLogs.Where(l => l.Date == DateTime.Today))
                {
                    baselineMinutesToday[existingLog.AppName] = existingLog.TimeSpent.TotalMinutes;
                }
            }
            catch { /* start from empty; the first flush will populate it anyway */ }

            // Window-title capture is opt-in (privacy — a title can contain a
            // browser tab's page name, a document name, etc., far more sensitive
            // than a bare process name). Re-checked once per flush, not every
            // tick, so flipping the setting in the dashboard takes effect within
            // ~60 seconds without needing an app restart.
            bool captureWindowTitles = false;

            int tickCount = 0;
            const int FlushIntervalTicks = 12; // 60 seconds

            // NEW: Session State Trackers
            string currentFocusedApp = null;
            DateTime? currentSessionStart = null;
            string currentSessionTitle = null;

            while (await timer.WaitForNextTickAsync())
            {
                var allProcesses = Process.GetProcesses();
                var allProcessNames = allProcesses.Select(p => p.ProcessName.ToLower()).ToHashSet();

                var visibleProcessNames = allProcesses
                    .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle))
                    .Select(p => p.ProcessName.ToLower())
                    .ToHashSet();

                var managedAppLookup = ManagedApps
                    .Where(a => !string.IsNullOrEmpty(a.ExecutablePath))
                    .GroupBy(a => Path.GetFileNameWithoutExtension(a.ExecutablePath).ToLower())
                    .ToDictionary(g => g.Key, g => g.First().Name);

                bool isAfk = await Services.SystemIdleTracker.IsTrulyAfkAsync(TimeSpan.FromMinutes(5));
                TimeSpan tickDuration = TimeSpan.FromSeconds(5);
                DateTime now = DateTime.Now;

                // --- NEW: FOCUS & SESSION TRACKING ---
                string rawActiveExe = GetActiveProcessName();
                string activeAppName = null;

                if (!string.IsNullOrEmpty(rawActiveExe))
                {
                    activeAppName = managedAppLookup.ContainsKey(rawActiveExe)
                                     ? managedAppLookup[rawActiveExe]
                                     : char.ToUpper(rawActiveExe[0]) + rawActiveExe.Substring(1);
                }

                // Did the user switch windows?
                if (activeAppName != currentFocusedApp)
                {
                    // 1. Close the old session and log it
                    if (currentFocusedApp != null && currentSessionStart.HasValue)
                    {
                        _pendingSessions.Enqueue(new ViewModels.SessionLog
                        {
                            AppName = currentFocusedApp,
                            StartTime = currentSessionStart.Value,
                            EndTime = now,
                            WindowTitle = currentSessionTitle
                        });
                    }

                    // 2. Start the new session. Title is captured once, at the
                    // moment focus lands on this app — not re-sampled if the
                    // title changes later without a window switch (e.g. a tab
                    // change within the same browser session).
                    currentFocusedApp = activeAppName;
                    currentSessionStart = activeAppName != null ? now : null;
                    currentSessionTitle = (activeAppName != null && captureWindowTitles) ? GetActiveWindowTitle() : null;
                }

                // Add to Focus Cache (Only if the user isn't AFK)
                if (activeAppName != null && !isAfk)
                {
                    focusCache[activeAppName] = focusCache.GetValueOrDefault(activeAppName) + tickDuration;
                    focusCache["SYSTEM_PC"] = focusCache.GetValueOrDefault("SYSTEM_PC") + tickDuration;
                }
                // -------------------------------------

                // A. PC Uptime
                timeCache["SYSTEM_PC"] = timeCache.GetValueOrDefault("SYSTEM_PC") + tickDuration;
                if (isAfk) afkCache["SYSTEM_PC"] = afkCache.GetValueOrDefault("SYSTEM_PC") + tickDuration;

                // B. Visible Applications
                foreach (var pName in visibleProcessNames)
                {
                    string logName = managedAppLookup.ContainsKey(pName)
                                     ? managedAppLookup[pName]
                                     : char.ToUpper(pName[0]) + pName.Substring(1);

                    timeCache[logName] = timeCache.GetValueOrDefault(logName) + tickDuration;
                    if (isAfk) afkCache[logName] = afkCache.GetValueOrDefault(logName) + tickDuration;
                }

                // C. Managed Apps Background Check
                foreach (var app in ManagedApps)
                {
                    if (string.IsNullOrEmpty(app.ExecutablePath)) continue;
                    string exeName = Path.GetFileNameWithoutExtension(app.ExecutablePath).ToLower();

                    if (allProcessNames.Contains(exeName))
                    {
                        app.TimeRunning = app.TimeRunning.Add(tickDuration);
                        if (!visibleProcessNames.Contains(exeName))
                        {
                            timeCache[app.Name] = timeCache.GetValueOrDefault(app.Name) + tickDuration;
                            if (isAfk) afkCache[app.Name] = afkCache.GetValueOrDefault(app.Name) + tickDuration;
                        }
                    }
                }

                // D. Daily limit enforcement — every tick (~5s), not gated behind
                // the 60s flush. That's what makes a relaunch of a blocked app get
                // killed again almost immediately instead of getting up to a
                // minute of free runway each time.
                foreach (var limitedApp in ManagedApps.Where(a => a.DailyLimitMinutes > 0 && !string.IsNullOrEmpty(a.ExecutablePath)))
                {
                    double liveMinutesToday = baselineMinutesToday.GetValueOrDefault(limitedApp.Name)
                        + timeCache.GetValueOrDefault(limitedApp.Name).TotalMinutes;

                    int bonusMinutes = limitedApp.BonusMinutesDate?.Date == DateTime.Today ? limitedApp.TodayBonusMinutes : 0;
                    int effectiveLimit = limitedApp.DailyLimitMinutes + bonusMinutes;
                    double remaining = effectiveLimit - liveMinutesToday;

                    if (remaining <= 0)
                    {
                        if (!limitedApp.HasNotifiedToday)
                        {
                            limitedApp.HasNotifiedToday = true;
                            try
                            {
                                NotificationService.ShowToast(
                                    "Daily limit reached",
                                    $"{limitedApp.Name} has hit its {effectiveLimit}-minute daily limit.");
                            }
                            catch { /* Toast failures should never take the tracker down */ }
                        }

                        if (limitedApp.StrictFocusMode)
                        {
                            string exeName = Path.GetFileNameWithoutExtension(limitedApp.ExecutablePath)?.ToLower();
                            if (!string.IsNullOrEmpty(exeName))
                            {
                                foreach (var proc in allProcesses.Where(p => p.ProcessName.ToLower() == exeName))
                                {
                                    try { proc.Kill(); } catch { /* already exited, access denied, etc. */ }
                                }
                            }
                        }
                    }
                    else if (remaining <= WarningThresholdMinutes && !limitedApp.HasWarnedToday)
                    {
                        limitedApp.HasWarnedToday = true;
                        try
                        {
                            NotificationService.ShowToast(
                                "Almost at today's limit",
                                $"{limitedApp.Name} has about {Math.Ceiling(remaining)} minute(s) left today.");
                        }
                        catch { /* Toast failures should never take the tracker down */ }
                    }
                }

                // --- DATABASE FLUSH ---
                tickCount++;
                if (tickCount >= FlushIntervalTicks)
                {
                    DateTime today = DateTime.Today;

                    if (lastLimitResetDate != today)
                    {
                        foreach (var a in ManagedApps)
                        {
                            a.HasNotifiedToday = false;
                            a.HasWarnedToday = false;
                            if (a.BonusMinutesDate?.Date != today) a.TodayBonusMinutes = 0;
                        }
                        baselineMinutesToday.Clear();
                        lastLimitResetDate = today;
                    }

                    // Re-read the window-title opt-in each flush. A short-lived,
                    // separate context (not the shared _dbContext) so this never
                    // fights the main context's connection/transaction state.
                    try
                    {
                        using var settingsDb = new AppDbContext();
                        using var settingsCmd = settingsDb.Database.GetDbConnection().CreateCommand();
                        settingsCmd.CommandText = "SELECT Value FROM AppSettings WHERE Key = 'CaptureWindowTitles'";
                        settingsDb.Database.OpenConnection();
                        using var settingsResult = settingsCmd.ExecuteReader();
                        captureWindowTitles = settingsResult.Read() && settingsResult.GetString(0) == "true";
                    }
                    catch
                    {
                        captureWindowTitles = false; // opt-in: default closed on any read failure
                    }

                    // Re-read PIN-configured state each flush too, so the Daily
                    // Limit controls lock (or unlock) within ~60s of setting or
                    // removing a PIN from the dashboard, no app restart needed.
                    try
                    {
                        using var pinDb = new AppDbContext();
                        IsPinConfigured = PinService.GetPinInfo(pinDb).HasPin;
                    }
                    catch
                    {
                        IsPinConfigured = false;
                    }

                    // 1. Flush Daily Summaries
                    foreach (var kvp in timeCache)
                    {
                        string appName = kvp.Key;
                        TimeSpan addedTotal = kvp.Value;
                        TimeSpan addedAfk = afkCache.GetValueOrDefault(appName);
                        TimeSpan addedFocus = focusCache.GetValueOrDefault(appName); // Pull from focus cache

                        var log = _dbContext.DailyLogs.FirstOrDefault(l => l.Date == today && l.AppName == appName);
                        if (log == null)
                        {
                            log = new ViewModels.DailyUsageLog { Date = today, AppName = appName, TimeSpent = TimeSpan.Zero, AfkTimeSpent = TimeSpan.Zero, TimeFocused = TimeSpan.Zero };
                            _dbContext.DailyLogs.Add(log);
                        }

                        log.TimeSpent = log.TimeSpent.Add(addedTotal);
                        log.AfkTimeSpent = log.AfkTimeSpent.Add(addedAfk);
                        log.TimeFocused = log.TimeFocused.Add(addedFocus); // Save Focus Time
                                                                           // Keep the fast INTEGER columns in sync so SQL-side SUM() reflects live data
                        log.TimeSpentTicks = log.TimeSpent.Ticks;
                        log.AfkTimeSpentTicks = log.AfkTimeSpent.Ticks;
                        log.TimeFocusedTicks = log.TimeFocused.Ticks;

                        // Baseline for the per-tick enforcement check below — keeps
                        // it anchored to what's actually committed, not just memory.
                        baselineMinutesToday[appName] = log.TimeSpent.TotalMinutes;
                    }

                    // 2. Flush Shadow Sessions
                    while (_pendingSessions.TryDequeue(out var session))
                    {
                        _dbContext.SessionLogs.Add(session);
                    }

                    // 3. Flush Shadow Macros
                    while (_pendingMacros.TryDequeue(out var macro))
                    {
                        _dbContext.MacroEventLogs.Add(macro);
                    }

                    _dbContext.SaveChanges();
                    StatisticsVM?.RefreshStats();

                    timeCache.Clear();
                    afkCache.Clear();
                    focusCache.Clear();
                    tickCount = 0;
                }
            }
        }


        // ==========================================
        // COMMANDS & UTILITIES
        // ==========================================
        private string GetSettingsPath()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastApp");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "osd_setting.txt");
        }

        private void LoadOsdSetting()
        {
            string path = GetSettingsPath();
            if (File.Exists(path))
            {
                EnableOsd = File.ReadAllText(path) == "True";
            }
            else
            {
                EnableOsd = true;
            }
        }

        public void SaveDatabase()
        {
            _dbContext.SaveChanges();
        }

        [RelayCommand]
        private void AddApplication()
        {
            SearchText = string.Empty;
            DetectedApps.Clear();

            var foundApps = AppScannerService.GetInstalledApps();
            foreach (var app in foundApps)
            {
                if (!ManagedApps.Any(m => m.ExecutablePath == app.ExecutablePath))
                {
                    DetectedApps.Add(app);
                }
            }

            DetectedAppsView = CollectionViewSource.GetDefaultView(DetectedApps);
            DetectedAppsView.Filter = item =>
            {
                if (string.IsNullOrWhiteSpace(SearchText)) return true;
                var app = (AppItemModel)item;
                return app.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
            };

            var scannerWindow = new AppScannerWindow
            {
                DataContext = this,
                Owner = App.Current.MainWindow
            };

            scannerWindow.ShowDialog();
        }

        [RelayCommand]
        private void AddCustomFile()
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*",
                Title = "Select a custom application or script"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                var newApp = new AppItemModel
                {
                    Name = Path.GetFileNameWithoutExtension(openFileDialog.FileName),
                    ExecutablePath = openFileDialog.FileName,
                    ActionType = 0,
                    LaunchOnStartup = false
                };

                _dbContext.ManagedApps.Add(newApp);
                _dbContext.SaveChanges();

                newApp.PropertyChanged += (s, e) => _dbContext.SaveChanges();
                ManagedApps.Add(newApp);
            }
        }

        [RelayCommand]
        private void AddCustomAction()
        {
            var newAction = new AppItemModel
            {
                Name = "New System Action",
                ExecutablePath = string.Empty,
                ActionType = 1,
                LaunchOnStartup = false
            };

            _dbContext.ManagedApps.Add(newAction);
            _dbContext.SaveChanges();

            newAction.PropertyChanged += (s, e) => _dbContext.SaveChanges();
            ManagedApps.Add(newAction);
        }

        [RelayCommand]
        private void SaveDetectedApp(AppItemModel appToSave)
        {
            if (appToSave == null) return;

            _dbContext.ManagedApps.Add(appToSave);
            _dbContext.SaveChanges();

            ManagedApps.Add(appToSave);
            DetectedApps.Remove(appToSave);
        }

        [RelayCommand]
        private void RemoveApplication(AppItemModel appToRemove)
        {
            if (appToRemove == null) return;

            _dbContext.ManagedApps.Remove(appToRemove);
            _dbContext.SaveChanges();

            ManagedApps.Remove(appToRemove);
        }
    }
    // Drop this at the bottom of MainViewModel.cs
    public record CategoryUpdatedMessage(string AppName, string NewCategory);

    public record UpdateCategoryCommand(string AppName, string NewCategory);

    public record UpdateLimitCommand(string AppName, int DailyLimitMinutes, bool StrictFocusMode);

    public record GrantExtensionCommand(string AppName, int ExtraMinutes);

}