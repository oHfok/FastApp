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
            var savedApps = _dbContext.ManagedApps.ToList();
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
            });
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


        // ==========================================
        // UI INTERACTION HANDLERS
        // ==========================================

        // This method automatically fires the moment the user clicks a tab
        partial void OnSelectedTabIndexChanged(int value)
        {
            // Index 0 = Apps Tab
            // Index 1 = Statistics Tab
            if (value == 1)
            {
                // The user just looked at the stats for the first time! Force the load.
                StatisticsVM?.RefreshStats(forceLoad: true);
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


            int tickCount = 0;
            const int FlushIntervalTicks = 12; // 60 seconds

            // NEW: Session State Trackers
            string currentFocusedApp = null;
            DateTime? currentSessionStart = null;

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

                bool isAfk = Services.SystemIdleTracker.GetIdleTime().TotalMinutes >= 5;
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
                            EndTime = now
                        });
                    }

                    // 2. Start the new session
                    currentFocusedApp = activeAppName;
                    currentSessionStart = activeAppName != null ? now : null;
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

                // --- DATABASE FLUSH ---
                tickCount++;
                if (tickCount >= FlushIntervalTicks)
                {
                    DateTime today = DateTime.Today;

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

}