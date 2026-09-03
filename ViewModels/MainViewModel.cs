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

        // Read from the low-level keyboard-hook thread, rebuilt from the UI thread.
        // Published as an immutable snapshot swapped in with one reference
        // assignment rather than mutated in place: a Dictionary cleared and
        // refilled while the hook thread is enumerating it throws, and an
        // exception raised inside a hook callback is exactly how Windows tears
        // the hook down -- after which every hotkey stops working until restart.
        private volatile KeyValuePair<AppItemModel, HashSet<Key>>[] _compiledHotkeys =
            Array.Empty<KeyValuePair<AppItemModel, HashSet<Key>>>();
        private readonly Channel<AppItemModel> _triggerQueue = Channel.CreateUnbounded<AppItemModel>();
        private HashSet<string> _gamingProcessNames = new(StringComparer.OrdinalIgnoreCase);

        // NEW: Thread-safe queues for the shadow tables
        private readonly ConcurrentQueue<ViewModels.SessionLog> _pendingSessions = new();
        private readonly ConcurrentQueue<ViewModels.MacroEventLog> _pendingMacros = new();

        // The tracker only flushes to disk every ~60s (see StartProcessTrackerAsync)
        // — without this, a normal "Exit" could silently drop up to a minute of the
        // day's tracking plus whatever session was still open. RequestShutdownFlushAsync
        // cancels the tracker's wait; the tracker does one last flush in its finally
        // block and signals _trackerStoppedTcs when it's actually safe to exit.
        //
        // Not readonly, because the flush is no longer always terminal: Windows can
        // raise SessionEnding and then have the shutdown vetoed by another app, which
        // would otherwise leave this process alive with a permanently dead tracker.
        // RestartTrackerIfStopped swaps in a fresh pair to recover from exactly that.
        private CancellationTokenSource _trackerCts = new();
        private TaskCompletionSource<bool> _trackerStoppedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Guards against ever running two tracker loops at once (they share
        // _dbContext and the pending queues, so a duplicate would double-count).
        private readonly object _trackerLifecycleLock = new();

        public StatisticsViewModel StatisticsVM { get; }

        [ObservableProperty]
        private ICollectionView _detectedAppsView;

        // Windows startup toggle
        [ObservableProperty] private bool _launchOnSystemStartup;
        [ObservableProperty] private bool _isStartupToggleBusy;

        // Set when something else owns the startup registration -- normally a
        // second copy of FastApp that has claimed the scheduled task. Surfaced
        // as a banner with a Fix button rather than corrected on sight, because
        // correcting it costs a UAC prompt nobody asked for.
        [ObservableProperty] private bool _hasStartupConflict;
        [ObservableProperty] private string _startupConflictText = string.Empty;

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

        // Centered "Opening X of Y" popup shown while auto-launch apps are starting
        [ObservableProperty] private bool _showAutoLaunchProgress;

        // Notification settings. Held here for binding and mirrored into
        // NotificationService, which is static because it is called from the
        // tracker thread and has no view model to reach for.
        [ObservableProperty] private bool _notificationsEnabled = true;
        [ObservableProperty] private bool _quietHoursEnabled;
        [ObservableProperty] private string _quietHoursFrom = "22:00";
        [ObservableProperty] private string _quietHoursTo = "07:00";

        // Mirrors DashboardServerService's real state into the Settings card, which
        // previously hardcoded "The web interface is currently running on ..." and
        // said so even when the server had failed to bind its port.
        [ObservableProperty] private string _dashboardStatusText = "Starting the dashboard server…";

        // App updates (Velopack) — CurrentVersionText is set once at construction
        // since it never changes for the lifetime of the process; a real update
        // restarts into a new process entirely rather than mutating this one.
        [ObservableProperty] private string _updateVersionText = Services.UpdateService.CurrentVersionText;
        [ObservableProperty] private string _updateStatusText = string.Empty;
        [ObservableProperty] private bool _isCheckingForUpdates;
        [ObservableProperty] private bool _isUpdateReadyToApply;
        private Velopack.UpdateInfo? _pendingUpdateInfo;

        // "What's New" for the version actually running. The desktop card shows a
        // trimmed, plain-text rendering; the dashboard's What's New tab has the
        // full history with the markdown rendered properly. Loaded lazily and
        // never awaited on startup -- it is a GitHub call, and the app must open
        // whether or not the network is there.
        [ObservableProperty] private string _whatsNewText = string.Empty;
        [ObservableProperty] private bool _hasWhatsNew;
        private bool _whatsNewRequested;

        public async Task LoadWhatsNewAsync()
        {
            if (_whatsNewRequested) return;
            _whatsNewRequested = true;
            try
            {
                var release = await Services.ReleaseFeedService.GetReleaseAsync(UpdateVersionText);
                if (release == null || string.IsNullOrWhiteSpace(release.NotesMarkdown)) return;

                WhatsNewText = Services.ReleaseFeedService.ToPlainText(release.NotesMarkdown, maxLines: 14);
                HasWhatsNew = !string.IsNullOrWhiteSpace(WhatsNewText);
            }
            catch
            {
                // The card just stays hidden; nothing here is worth interrupting for.
                _whatsNewRequested = false;
            }
        }

        // ---------- Rolling back to an earlier version ----------
        // Only versions Velopack still has a package for are offered; the very
        // first releases show in the dashboard's history but cannot be installed.
        public System.Collections.ObjectModel.ObservableCollection<string> RollbackVersions { get; } = new();

        [ObservableProperty] private string _selectedRollbackVersion;
        [ObservableProperty] private string _rollbackWarningText = string.Empty;
        // A plain bool so the existing BoolToVisibilityConverter can hide the
        // warning line; there is no string-to-visibility converter in this app
        // and one more converter for one binding is not worth the class.
        [ObservableProperty] private bool _hasRollbackWarning;
        // Drives the card's visibility. Not CountToVisibilityConverter, which is
        // the empty-state converter: it shows at zero and hides above it, so
        // binding the card to it would offer rollback only when there was
        // nothing to roll back to.
        [ObservableProperty] private bool _hasRollbackVersions;
        [ObservableProperty] private string _rollbackStatusText = string.Empty;
        [ObservableProperty] private bool _isRollbackBusy;
        private Velopack.UpdateInfo? _preparedRollback;

        partial void OnSelectedRollbackVersionChanged(string value)
        {
            _preparedRollback = null;
            RollbackStatusText = string.Empty;
            RollbackWarningText = string.Empty;
            HasRollbackWarning = false;
            if (string.IsNullOrWhiteSpace(value)) return;

            _ = Task.Run(async () =>
            {
                var release = await Services.ReleaseFeedService.GetReleaseAsync(value);
                if (release == null) return;
                string risk = Services.UpdateService.DescribeSchemaRisk(release.PublishedAt.ToUniversalTime());
                if (!string.IsNullOrEmpty(risk))
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        RollbackWarningText = risk;
                        HasRollbackWarning = true;
                    });
            });
        }

        public async Task LoadRollbackVersionsAsync()
        {
            if (RollbackVersions.Count > 0) return;
            try
            {
                var releases = await Services.ReleaseFeedService.GetReleasesAsync();
                string current = UpdateVersionText.TrimStart('v');
                foreach (var r in releases)
                {
                    if (!r.IsInstallable || r.Version == current) continue;
                    RollbackVersions.Add(r.Version);
                }
                HasRollbackVersions = RollbackVersions.Count > 0;
            }
            catch { /* leaves the picker empty, which hides the card */ }
        }

        [RelayCommand]
        private async Task RollBack()
        {
            if (IsRollbackBusy || string.IsNullOrWhiteSpace(SelectedRollbackVersion)) return;

            // This restarts the app and replaces the installed build, so it asks
            // first rather than acting on a single click.
            string question = $"Reinstall version {SelectedRollbackVersion}?\n\n"
                            + "FastApp will close and reopen on that version. Your database is backed up first, "
                            + "and you can update again at any time."
                            + (string.IsNullOrEmpty(RollbackWarningText) ? string.Empty : "\n\n" + RollbackWarningText);
            var answer = System.Windows.MessageBox.Show(question, "Reinstall earlier version",
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.OK) return;

            IsRollbackBusy = true;
            RollbackStatusText = $"Downloading {SelectedRollbackVersion}…";

            var result = await Services.UpdateService.PrepareRollbackAsync(SelectedRollbackVersion);
            RollbackStatusText = result.Message;
            _preparedRollback = result.UpdateInfo;

            if (result.Success && _preparedRollback != null)
            {
                RollbackStatusText = "Backing up your data and restarting…";
                await Services.UpdateService.RollBackAndRestartAsync(_preparedRollback, RequestShutdownFlushAsync);
            }
            IsRollbackBusy = false;
        }

        [RelayCommand]
        private async Task CheckForUpdatesAsync()
        {
            if (IsCheckingForUpdates) return;

            IsCheckingForUpdates = true;
            IsUpdateReadyToApply = false;
            UpdateStatusText = "Checking for updates…";

            var result = await Services.UpdateService.CheckForUpdatesAsync();

            UpdateStatusText = result.Message;
            _pendingUpdateInfo = result.UpdateInfo;
            IsUpdateReadyToApply = result.Success && result.UpdateInfo != null;
            IsCheckingForUpdates = false;
        }

        [RelayCommand]
        private async Task ApplyPendingUpdate()
        {
            if (_pendingUpdateInfo == null) return;
            UpdateStatusText = "Restarting to apply update…";
            await Services.UpdateService.ApplyAndRestartAsync(_pendingUpdateInfo, RequestShutdownFlushAsync);
        }


        // ==========================================
        // INITIALIZATION
        // ==========================================
        public MainViewModel()
        {
            LoadOsdSetting();
            LoadAutoLaunchProgressSetting();
            LoadNotificationSettings();

            // After
            _dbContext = new AppDbContext();
            _dbContext.Database.Migrate();

            _dbContext.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

            // AppSettings/HiddenApps aren't EF-migration-tracked tables — they're
            // normally created by DashboardServerService.StartAsync()'s own raw-SQL
            // init. That runs fire-and-forget from the background-tasks block further
            // down this constructor, so on a genuinely fresh install (first launch
            // ever, no existing db) it hasn't run yet by the time the synchronous
            // AppSettings read just below fires — "no such table: AppSettings".
            // Creating them here too, before that read, closes the race.
            _dbContext.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS HiddenApps (AppName TEXT PRIMARY KEY);");
            _dbContext.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS AppSettings (Key TEXT PRIMARY KEY, Value TEXT);");

            // --- ONE-TIME MIGRATION: backfill the fast INTEGER Ticks columns for rows
            // that predate them. Gated behind a completed-flag in AppSettings — without
            // it, this was scanning the entire DailyLogs table on every single startup
            // forever, not just the one time it actually needed to.
            bool ticksMigrationDone;
            using (var checkCmd = _dbContext.Database.GetDbConnection().CreateCommand())
            {
                checkCmd.CommandText = "SELECT Value FROM AppSettings WHERE Key = 'TicksMigrationComplete'";
                _dbContext.Database.OpenConnection();
                using var checkResult = checkCmd.ExecuteReader();
                ticksMigrationDone = checkResult.Read() && checkResult.GetString(0) == "true";
            }

            if (!ticksMigrationDone)
            {
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
                _dbContext.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO AppSettings (Key, Value) VALUES ('TicksMigrationComplete', 'true');");
            }



            // --- PHASE 5: UPGRADED DATABASE CLEANUP ---
            Task.Run(() =>
            {
                try
                {
                    using var cleanupDb = new AppDbContext();
                    // Keep Forever unless the setting says otherwise. This fallback
                    // is used when the setting can't be read at all -- including on a
                    // first run, where this task can race ahead of the dashboard
                    // server's seeding of AppSettings. It must therefore be the
                    // SAFE value: a 90 here meant "couldn't read the setting, so
                    // permanently delete three-month-old history anyway."
                    int retentionDays = 99999;
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

            // Restoring from a backup, triggered from the web dashboard's
            // Settings tab. Fire-and-forget from this handler (not awaited
            // inline) since WeakReferenceMessenger.Send runs handlers
            // synchronously on the calling thread -- awaiting here would
            // block /api/restore's request thread for the whole flush+copy
            // sequence instead of letting it return immediately.
            WeakReferenceMessenger.Default.Register<RestoreBackupCommand>(this, (recipient, message) =>
            {
                _ = PerformRestoreAsync(message.StagingFilePath);
            });

            foreach (var app in ManagedApps)
            {
                app.PropertyChanged += SaveOnAppPropertyChanged;
            }

            ManagedApps.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                {
                    foreach (AppItemModel newItem in e.NewItems)
                        newItem.PropertyChanged += SaveOnAppPropertyChanged;
                }
                lock (_dbContext) { _dbContext.SaveChanges(); }
            };

           

            // 5. FIRE AND FORGET: Background tasks
            _ = Task.Run(async () =>
            {
                // RunAutoLaunchAsync is awaited so the toggle-sync code below still runs
                // after every auto-launch app has been checked and started. Kicking off
                // the other three first means the dashboard server, tracker, and trigger
                // loop all actually start immediately instead of waiting on auto-launch
                // to finish first; none of them depend on it.
                _ = ProcessTriggersAsync();
                StartTracker();
                _ = Services.DashboardServerService.StartAsync();
                // The update still downloads concurrently -- that is the slow part
                // and there is no reason to hold it up. Only the restart waits:
                // ApplyUpdatesAndRestart hard-kills this process, and landing that
                // mid-pass left the user with half their apps open and nothing
                // saying why.
                _ = Services.UpdateService.CheckAndApplyOnStartupAsync(async () =>
                {
                    await _autoLaunchFinished.Task;
                    await RequestShutdownFlushAsync();
                });
                _ = PublishDashboardStatusAsync();
                await RunAutoLaunchAsync();

                RefreshStartupState();
            });
        }

        // ==========================================
        // UI & LAYOUT STATE
        // ==========================================
        [ObservableProperty]
        private bool _isCompactMode = false;

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

            lock (_dbContext) { _dbContext.SaveChanges(); }
            FilteredManagedApps.Refresh();
        }


        private string GetActiveProcessName()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hWnd, out uint pid);
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);

            if (hProcess == IntPtr.Zero)
            {
                // Most commonly: the foreground window belongs to a process running
                // elevated while FastApp isn't (Task Manager, an installer, "Run as
                // Administrator", etc.) — OpenProcess can't get a handle to it, so
                // QueryFullProcessImageName below is off the table. The process's
                // bare name is still readable without a handle, though (the same
                // reason Task Manager can show an elevated process's name without
                // itself being elevated) — without this fallback, focus time in
                // that window silently stopped being tracked at all, rather than
                // just losing the full executable path.
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    return proc.ProcessName.ToLower();
                }
                catch { return null; }
            }

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

            // The notes and the rollback list used to be fetched here, when the
            // old window switched to its Settings tab. There are no tabs any
            // more and this never runs, so PaletteWindow.ShowSettings asks for
            // them instead. Keeping the call here as well would only make it
            // look like two things trigger the load when one does.
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

        private bool _suppressNotificationSettingSave;

        private const string NotificationsEnabledKey = "NotificationsEnabled";
        private const string QuietHoursEnabledKey = "QuietHoursEnabled";
        private const string QuietHoursFromKey = "QuietHoursFrom";
        private const string QuietHoursToKey = "QuietHoursTo";

        private void LoadNotificationSettings()
        {
            // Suppressed so restoring saved values does not immediately write
            // them back, once per property, on every launch.
            _suppressNotificationSettingSave = true;
            try
            {
                NotificationsEnabled = Services.AppSettingsStore.GetBool(NotificationsEnabledKey, true);
                QuietHoursEnabled = Services.AppSettingsStore.GetBool(QuietHoursEnabledKey, false);
                QuietHoursFrom = Services.AppSettingsStore.Get(QuietHoursFromKey, "22:00");
                QuietHoursTo = Services.AppSettingsStore.Get(QuietHoursToKey, "07:00");
            }
            finally
            {
                _suppressNotificationSettingSave = false;
            }

            ApplyNotificationSettings();
        }

        /// <summary>
        /// Pushes the current settings into the static NotificationService. An
        /// unparseable time leaves quiet hours off rather than guessing at a
        /// window the user did not ask for.
        /// </summary>
        private void ApplyNotificationSettings()
        {
            Services.NotificationService.Enabled = NotificationsEnabled;

            if (QuietHoursEnabled
                && TryParseTimeOfDay(QuietHoursFrom, out int from)
                && TryParseTimeOfDay(QuietHoursTo, out int to))
            {
                Services.NotificationService.QuietFromMinutes = from;
                Services.NotificationService.QuietToMinutes = to;
            }
            else
            {
                Services.NotificationService.QuietFromMinutes = null;
                Services.NotificationService.QuietToMinutes = null;
            }
        }

        /// <summary>"22:00" or "7:5" to minutes past midnight. Invariant on purpose:
        /// this is a stored format, not something to render in the user's locale.</summary>
        internal static bool TryParseTimeOfDay(string text, out int minutes)
        {
            minutes = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string[] parts = text.Trim().Split(':');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int h)) return false;
            if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int m)) return false;
            if (h is < 0 or > 23 || m is < 0 or > 59) return false;

            minutes = h * 60 + m;
            return true;
        }

        partial void OnNotificationsEnabledChanged(bool value)
        {
            if (_suppressNotificationSettingSave) return;
            Services.AppSettingsStore.SetBool(NotificationsEnabledKey, value);
            ApplyNotificationSettings();
        }

        partial void OnQuietHoursEnabledChanged(bool value)
        {
            if (_suppressNotificationSettingSave) return;
            Services.AppSettingsStore.SetBool(QuietHoursEnabledKey, value);
            ApplyNotificationSettings();
        }

        partial void OnQuietHoursFromChanged(string value)
        {
            if (_suppressNotificationSettingSave) return;
            if (!TryParseTimeOfDay(value, out _)) return;   // half-typed input is not a setting
            Services.AppSettingsStore.Set(QuietHoursFromKey, value);
            ApplyNotificationSettings();
        }

        partial void OnQuietHoursToChanged(string value)
        {
            if (_suppressNotificationSettingSave) return;
            if (!TryParseTimeOfDay(value, out _)) return;
            Services.AppSettingsStore.Set(QuietHoursToKey, value);
            ApplyNotificationSettings();
        }

        partial void OnShowAutoLaunchProgressChanged(bool value)
        {
            try
            {
                File.WriteAllText(GetAutoLaunchProgressSettingsPath(), value.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save auto-launch progress setting: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the real registration state and reflects it in the toggle and the
        /// conflict banner. Safe to call from any thread; the queries shell out to
        /// schtasks, so the work is pushed off whatever thread asked.
        /// </summary>
        public void RefreshStartupState()
        {
            Task.Run(() =>
            {
                var info = StartupTaskService.GetStartupInfo();

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    _suppressStartupToggleHandler = true;
                    LaunchOnSystemStartup = info.State == StartupTaskService.StartupState.RegisteredForThisExe;
                    _suppressStartupToggleHandler = false;

                    HasStartupConflict = info.State == StartupTaskService.StartupState.RegisteredForAnotherExe;
                    StartupConflictText = HasStartupConflict
                        ? $"Windows currently starts a different copy of FastApp:\n{info.RegisteredPath}"
                        : string.Empty;
                });
            });
        }

        /// <summary>
        /// Points the startup registration at this copy. Only ever runs because the
        /// user pressed Fix, so the UAC prompt it triggers is expected.
        /// </summary>
        [RelayCommand]
        private void FixStartupRegistration()
        {
            IsStartupToggleBusy = true;
            Task.Run(() =>
            {
                StartupTaskService.SetStartup(true);
                System.Windows.Application.Current?.Dispatcher.Invoke(() => IsStartupToggleBusy = false);
                RefreshStartupState();
            });
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

                // Then confirm against what is actually registered, rather than
                // trusting the exit code -- this is also what clears or raises the
                // conflict banner after the change lands.
                RefreshStartupState();
            });
        }

        // There was an OnPropertyChanged override here that called
        // _dbContext.SaveChanges() on every property change of this view model.
        // None of this class's observable properties are EF entities -- they are
        // UI state, update state, or persisted elsewhere (text files, Task
        // Scheduler) -- so it never saved any of them. What it did do was write
        // to SQLite once per keystroke typed into the app search box, and it was
        // the one _dbContext access in this file outside lock (_dbContext),
        // which every other access here treats as mandatory.
        //
        // Entity writes are already covered three ways: SaveOnAppPropertyChanged
        // is attached to every AppItemModel (both in the initial loop and in
        // ManagedApps.CollectionChanged), the collection-changed handler saves,
        // and SaveDatabase() is called explicitly where edits are committed.


        // ==========================================
        // CACHE & HOTKEY COMPILERS
        // ==========================================
        public void RecompileHotkeys()
        {
            var rebuilt = new List<KeyValuePair<AppItemModel, HashSet<Key>>>();

            foreach (var app in ManagedApps)
            {
                if (string.IsNullOrEmpty(app.HotkeySequence)) continue;

                var keys = new HashSet<Key>();
                bool parsed = true;

                foreach (var token in app.HotkeySequence.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    // Enum.Parse threw here on a single malformed token, which
                    // escaped this whole method and left the map empty -- one
                    // unreadable row disabled every hotkey in the app rather
                    // than just its own. Skip the row, keep the rest.
                    if (Enum.TryParse<Key>(token.Trim(), ignoreCase: true, out var key))
                    {
                        keys.Add(key);
                    }
                    else
                    {
                        parsed = false;
                        break;
                    }
                }

                if (parsed && keys.Count > 0)
                {
                    rebuilt.Add(new KeyValuePair<AppItemModel, HashSet<Key>>(app, keys));
                }
                else
                {
                    Debug.WriteLine($"Ignoring unreadable hotkey for {app.Name}: '{app.HotkeySequence}'");
                }
            }

            // The swap itself, and the only write to the field.
            _compiledHotkeys = rebuilt.ToArray();
        }

        public void UpdateGamingProcessCache()
        {
            try
            {
                List<string> gamingApps;
                lock (_dbContext)
                {
                    gamingApps = _dbContext.AppCategories
                        .Where(c => c.Category == "Gaming")
                        .Select(c => c.AppName)
                        .ToList();
                }

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

        // TimeRunning is updated every ~5s by the tracker for every running app —
        // saving immediately on every tick (per running app) was dozens of avoidable
        // DB writes a minute. It's still a normal tracked/persisted property, just
        // not one that needs its own dedicated save: any change to it rides along on
        // the tracker's next flush (or whatever save happens next for any other
        // reason) — same tolerance the rest of the tracker's data already has.
        //
        // _dbContext is shared between the UI thread (this handler, and the various
        // RelayCommands below) and the background tracker loop, which is not safe to
        // touch concurrently from multiple threads — every access is wrapped in
        // lock (_dbContext), the same convention already used by
        // OnExcludeAfkTimeChanged in StatisticsViewModel.
        private void SaveOnAppPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppItemModel.TimeRunning)) return;

            // This runs inside a PropertyChanged handler, which is raised from a
            // binding's source update. An exception thrown here escapes into WPF's
            // binding machinery, where it is of no use to anyone: the edit is lost
            // and nothing says so. SQLite can genuinely fail here transiently -- the
            // tracker thread writes to this same connection every 60 seconds.
            //
            // Swallowing is safe because it is not the only chance to persist: the
            // entity stays tracked and Modified, so the tracker's next flush calls
            // SaveChanges again and picks it up. This only has to avoid making
            // things worse while that happens.
            try
            {
                lock (_dbContext) { _dbContext.SaveChanges(); }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Deferred saving {e.PropertyName}: {ex.Message}");
            }
        }


        // ==========================================
        // THE MACRO ENGINE (Zero Lag)
        // ==========================================
        /// <summary>
        /// Answers the keyboard hook, synchronously, on the key-down that
        /// completes a combination: should this keystroke be withheld from the
        /// focused application? Reads the same immutable snapshot
        /// CheckForHotkeys does, so it allocates nothing and takes no lock --
        /// this runs inside the hook, where being slow gets the hook removed.
        /// </summary>
        public bool ShouldSuppressHotkey(HashSet<Key> currentlyPressedKeys)
        {
            if (currentlyPressedKeys.Count == 0) return false;

            var snapshot = _compiledHotkeys;
            foreach (var kvp in snapshot)
            {
                if (kvp.Key.SuppressHotkeyPassthrough && currentlyPressedKeys.SetEquals(kvp.Value))
                    return true;
            }
            return false;
        }

        public void CheckForHotkeys(HashSet<Key> currentlyPressedKeys)
        {
            if (currentlyPressedKeys.Count == 0) return;

            // One volatile read, then iterate that snapshot: RecompileHotkeys can
            // swap a new array in at any point without this loop seeing a torn
            // state. Kept allocation-free and lock-free because this runs inside
            // the keyboard hook, where going slow gets the hook uninstalled.
            var snapshot = _compiledHotkeys;

            foreach (var kvp in snapshot)
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

                if (blockMacro)
                {
                    // Silently dropping this was indistinguishable from a broken
                    // hotkey. The OSD says so instead -- deliberately not a
                    // toast, since the reason you are being blocked is that you
                    // are mid-game and a toast would be worse than the macro.
                    if (EnableOsd)
                    {
                        Services.OsdService.Show($"{app.DisplayNamePrimary} blocked in game", app.IsAction);
                    }
                    continue;
                }
                // ----------------------------------
                _pendingMacros.Enqueue(new ViewModels.MacroEventLog
                {
                    AppName = app.Name,
                    Timestamp = DateTime.Now
                });

                // 1. Execute the heavy Action entirely in the background
                var result = Services.ActionHookEngine.Execute(app);

                if (!result.Success)
                {
                    // Failures here used to vanish into a bare catch {}, so a
                    // hotkey pointing at a moved executable behaved exactly like
                    // one that was never bound.
                    Services.NotificationService.Show(
                        $"{app.DisplayNamePrimary} did not run",
                        result.Message,
                        Services.NotificationSeverity.Warning);
                }

                if (!result.Success) continue;

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
        /// <summary>What happened to one app during an auto-launch pass.</summary>
        private enum LaunchOutcome { Started, AlreadyRunning, NotFound, Failed }

        // Completes when the startup auto-launch pass has finished (or when there
        // was nothing to do). The update check awaits this before restarting the
        // process: both are kicked off together at startup, and an update landing
        // mid-pass used to kill FastApp halfway through opening the user's apps,
        // leaving a half-started desktop and no record of why.
        private readonly TaskCompletionSource _autoLaunchFinished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// A human-readable account of the last auto-launch pass, including which
        /// apps failed and why. Previously failures went to Debug.WriteLine, which
        /// in a release build means nowhere at all.
        /// </summary>
        [ObservableProperty] private string _lastAutoLaunchReport = string.Empty;

        private async Task RunAutoLaunchAsync()
        {
            try
            {
                await RunAutoLaunchCoreAsync();
            }
            finally
            {
                // Must be signalled on every path, or a failure here would block
                // the updater's restart for the rest of the session.
                _autoLaunchFinished.TrySetResult();
            }
        }

        private async Task RunAutoLaunchCoreAsync()
        {
            // Startup order is the order shown in the list, which the user can
            // already drag to rearrange -- rather than a second, invisible order
            // they would have to keep in sync with it.
            var appsToLaunch = ManagedApps
                .Where(app => app.LaunchOnStartup && !string.IsNullOrEmpty(app.ExecutablePath))
                .OrderBy(app => app.OrderIndex)
                .ToList();

            if (appsToLaunch.Count == 0) return;

            bool showProgress = ShowAutoLaunchProgress;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var outcomes = new List<(AppItemModel App, LaunchOutcome Outcome, string Detail)>();

            // Enumerated once, not once per app. The old loop called
            // Process.GetProcesses() on every iteration -- N full process
            // enumerations, none of them disposed, during boot, which is exactly
            // when the machine can least afford it. Apps started by this pass are
            // added to the set as we go, which is what the re-enumeration was
            // actually for.
            var running = SnapshotProcessNames();

            for (int i = 0; i < appsToLaunch.Count; i++)
            {
                var app = appsToLaunch[i];

                if (app.LaunchDelaySeconds > 0)
                {
                    if (showProgress)
                    {
                        Services.AutoLaunchProgressService.ShowProgress(
                            i + 1, appsToLaunch.Count, $"{app.Name} (waiting {app.LaunchDelaySeconds}s)");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(app.LaunchDelaySeconds));
                }

                if (showProgress)
                {
                    Services.AutoLaunchProgressService.ShowProgress(i + 1, appsToLaunch.Count, app.Name);
                }

                string exeName = Path.GetFileNameWithoutExtension(app.ExecutablePath).ToLower();

                if (running.Contains(exeName))
                {
                    outcomes.Add((app, LaunchOutcome.AlreadyRunning, null));
                }
                else
                {
                    if (Services.AppLauncher.TryStart(app, out string error))
                    {
                        running.Add(exeName);
                        outcomes.Add((app, LaunchOutcome.Started, null));
                    }
                    else
                    {
                        outcomes.Add((app, LaunchOutcome.Failed, error));
                    }
                }

                // Small stagger so launches don't all fire in the same instant
                // (reduces launch-storm/race risk) and so the progress window is
                // actually readable. Skipped after the last one.
                if (i < appsToLaunch.Count - 1)
                {
                    await Task.Delay(350);
                }
            }

            stopwatch.Stop();
            ReportAutoLaunch(outcomes, stopwatch.Elapsed, showProgress);
        }

        /// <summary>
        /// Lower-cased names of every running process. The Process objects are
        /// disposed: Process.GetProcesses() hands back one OS handle per process,
        /// and this used to run several times a pass without releasing any.
        /// </summary>
        private static HashSet<string> SnapshotProcessNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = Process.GetProcesses();
            try
            {
                foreach (var proc in all) names.Add(proc.ProcessName.ToLower());
            }
            finally
            {
                foreach (var proc in all) proc.Dispose();
            }
            return names;
        }

        private void ReportAutoLaunch(
            List<(AppItemModel App, LaunchOutcome Outcome, string Detail)> outcomes,
            TimeSpan elapsed,
            bool showProgress)
        {
            int started = outcomes.Count(o => o.Outcome == LaunchOutcome.Started);
            int already = outcomes.Count(o => o.Outcome == LaunchOutcome.AlreadyRunning);
            var problems = outcomes.Where(o => o.Outcome is LaunchOutcome.NotFound or LaunchOutcome.Failed).ToList();

            // "Opened N apps" used to count apps that were already running, so a
            // pass that opened nothing at all still claimed to have opened
            // everything. Each outcome is now counted as what it was.
            var parts = new List<string>();
            if (started > 0) parts.Add($"Opened {started}");
            if (already > 0) parts.Add($"{already} already running");
            if (problems.Count > 0) parts.Add($"{problems.Count} failed");
            if (parts.Count == 0) parts.Add("Nothing to open");

            string headline = $"{string.Join(", ", parts)} in {elapsed.TotalSeconds:F1}s";

            var report = new StringBuilder(headline);
            foreach (var (app, outcome, detail) in problems)
            {
                report.Append(Environment.NewLine);
                report.Append(outcome == LaunchOutcome.NotFound
                    ? $"{app.Name}: not found at {detail}"
                    : $"{app.Name}: {detail}");
            }

            LastAutoLaunchReport = report.ToString();

            // Failures were previously Debug.WriteLine only, which is to say
            // invisible in a release build. Until the notification work lands,
            // they at least reach a file and the summary popup.
            if (problems.Count > 0)
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(AppDbContext.GetDbFolder(), "autolaunch.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {report}{Environment.NewLine}");
                }
                catch { /* a log that cannot be written must not break startup */ }
            }

            if (showProgress)
            {
                Services.AutoLaunchProgressService.ShowSummary(
                    problems.Count > 0 ? report.ToString() : headline);
            }

            // The progress popup is opt-in and self-dismisses in 2.5s, which is
            // no way to learn that an app you rely on did not start. A failure
            // is worth a notification whether or not that popup is enabled.
            if (problems.Count > 0)
            {
                NotificationService.Show(
                    problems.Count == 1 ? "A startup app did not open" : $"{problems.Count} startup apps did not open",
                    string.Join(", ", problems.Select(pr => pr.App.Name)),
                    NotificationSeverity.Warning,
                    new[] { new NotificationAction("show-window", "Open FastApp") });
            }
        }

        // Called from the tray "Exit" path, and from UpdateService before an
        // auto-update restarts the process, so both go through the exact same
        // shutdown sequence. Cancels the tracker's tick wait, which makes it
        // fall into its own finally block and do one last flush (close the
        // open session, write whatever's accumulated since the last 60s
        // flush). Bounded by a short timeout so a stuck flush can never hang
        // app exit or delay an update.
        //
        // Also checkpoints the WAL afterward: an update restart means Velopack
        // is about to hard-kill this process on purpose (it has no way to know
        // about this app's own shutdown sequence) to free the files it's
        // replacing. SQLite's WAL mode is meant to tolerate a kill at any
        // point without corruption, but on 2026-08-19 the live database ended
        // up corrupted anyway shortly after an auto-update restart -- most
        // likely third-party software on this machine (anti-cheat/RGB tools
        // routinely hook other processes' I/O) violating an assumption WAL
        // mode depends on. Checkpointing first can't fully close that gap,
        // but it minimizes the window where a kill could land mid-write.
        public async Task RequestShutdownFlushAsync()
        {
            _trackerCts.Cancel();
            await Task.WhenAny(_trackerStoppedTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));

            try
            {
                lock (_dbContext)
                {
                    _dbContext.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
                }
            }
            catch { /* best-effort -- never block shutdown/restart on this */ }
        }

        // The dashboard server is started fire-and-forget and settles a moment
        // later (bind succeeds, or fails because something else holds the port),
        // so its outcome is polled briefly rather than awaited -- awaiting
        // StartAsync directly would mean waiting for the server's entire lifetime,
        // since it only returns once the host shuts down.
        private async Task PublishDashboardStatusAsync()
        {
            for (int i = 0; i < 20; i++) // ~10s, well past a normal bind
            {
                await Task.Delay(500);
                if (Services.DashboardServerService.IsRunning ||
                    !Services.DashboardServerService.StatusMessage.StartsWith("Starting", StringComparison.Ordinal))
                {
                    break;
                }
            }

            string status = Services.DashboardServerService.StatusMessage;
            System.Windows.Application.Current?.Dispatcher.Invoke(() => DashboardStatusText = status);
        }

        // Recovery path for a shutdown that didn't actually happen. Windows raises
        // SessionEnding before the shutdown is final, and any app can still veto it
        // (the user then sees the "app is preventing shutdown" screen and can back
        // out). We've already flushed and stopped the tracker by that point, so
        // without this the app would sit there looking fine while silently recording
        // nothing until its next restart.
        //
        // No-ops unless the tracker has actually stopped, so calling it when nothing
        // was wrong is harmless.
        public void RestartTrackerIfStopped()
        {
            lock (_trackerLifecycleLock)
            {
                if (!_trackerCts.IsCancellationRequested) return; // still running — nothing to do

                // Only once the previous loop has actually reached its finally block
                // and signalled. Cancellation alone isn't proof it's finished, and
                // starting a second loop alongside one that's still winding down
                // would double-count everything they both flush.
                if (!_trackerStoppedTcs.Task.IsCompleted) return;

                _trackerCts.Dispose();
                _trackerCts = new CancellationTokenSource();
                _trackerStoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                StartTracker();
            }
        }

        // Swaps in a validated backup and restarts into it. stagingFilePath has
        // already passed SQLite-header, integrity, and table-shape checks in
        // /api/restore -- this method's job is just to do the swap safely, not
        // to re-validate the file.
        //
        // Order matters here: the safety copy of the CURRENT database happens
        // BEFORE the tracker is stopped, so if anything fails before that
        // point, the app is untouched and just keeps running normally instead
        // of being left with a permanently-stopped tracker and no restart to
        // recover from (RequestShutdownFlushAsync cancels a CancellationTokenSource
        // that can't be un-cancelled).
        private async Task PerformRestoreAsync(string stagingFilePath)
        {
            string dbPath = AppDbContext.GetDbPath();
            string folder = Path.GetDirectoryName(dbPath);

            void RestartAndExit()
            {
                var restartArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    Arguments = string.Join(" ", restartArgs),
                    UseShellExecute = true
                });
                Environment.Exit(0);
            }

            try
            {
                string safetyDir = Path.Combine(folder, "pre-restore-backups");
                Directory.CreateDirectory(safetyDir);
                string safetyPath = Path.Combine(safetyDir, $"appmanager-before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.db");
                if (File.Exists(dbPath)) File.Copy(dbPath, safetyPath, overwrite: true);

                // Only stop the tracker once the safety copy has actually succeeded.
                await RequestShutdownFlushAsync();

                if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
                if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
                File.Copy(stagingFilePath, dbPath, overwrite: true);
                try { File.Delete(stagingFilePath); } catch { /* best-effort cleanup of the staging copy */ }

                RestartAndExit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RESTORE] Failed: {ex}");
                // If the tracker was already stopped (past the point above) but
                // something after that failed, restarting -- into whatever state
                // the db file ends up in, restored or not -- is still safer than
                // leaving the app running with its tracker permanently cancelled
                // and no way to resurrect it in-place.
                try { RestartAndExit(); } catch { /* truly best-effort at this point */ }
            }
        }

        /// <summary>
        /// Start the tracking loop and, crucially, watch it.
        ///
        /// This was `_ = StartProcessTrackerAsync()`. An unhandled exception in an
        /// unobserved task is not reported anywhere -- the loop ends, the app
        /// carries on looking fine, and no time is recorded again until someone
        /// restarts it. That is exactly what happened on 2026-09-02.
        /// </summary>
        private void StartTracker()
        {
            _ = StartProcessTrackerAsync().ContinueWith(
                t => Services.DatabaseHealth.ReportTrackerStopped(t.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private async Task StartProcessTrackerAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            var timeCache = new Dictionary<string, TimeSpan>();
            var afkCache = new Dictionary<string, TimeSpan>();
            var focusCache = new Dictionary<string, TimeSpan>(); // NEW: Focus Cache

            // Daily-limit enforcement: HasNotifiedToday is in-memory only (never
            // persisted), so it has to be reset by hand once a day rolls over.
            // Seeded to today, not null: baselineMinutesToday below is read from
            // the DB for today at startup, so there is no rollover owing on the
            // first tick. Leaving it null made the first tick "roll over" and
            // clear that freshly-seeded baseline, which stopped limits being
            // enforced for the first minute after every launch.
            DateTime? lastLimitResetDate = DateTime.Today;
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
                // Runs on this background thread as soon as the tracker starts, which
                // by now can already overlap with UI-thread activity — same _dbContext
                // sharing concern as everywhere else, so it's locked too.
                lock (_dbContext)
                {
                    foreach (var existingLog in _dbContext.DailyLogs.AsNoTracking().Where(l => l.Date == DateTime.Today))
                    {
                        baselineMinutesToday[existingLog.AppName] = existingLog.TimeSpent.TotalMinutes;
                    }
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

            // Shared by the periodic 60s flush and the final flush-on-exit below,
            // so a normal quit persists exactly the same way a scheduled flush does.
            void FlushDailySummaries(DateTime today)
            {
                foreach (var kvp in timeCache)
                {
                    string appName = kvp.Key;
                    TimeSpan addedTotal = kvp.Value;
                    TimeSpan addedAfk = afkCache.GetValueOrDefault(appName);
                    TimeSpan addedFocus = focusCache.GetValueOrDefault(appName);

                    var log = _dbContext.DailyLogs.FirstOrDefault(l => l.Date == today && l.AppName == appName);
                    if (log == null)
                    {
                        log = new ViewModels.DailyUsageLog { Date = today, AppName = appName, TimeSpent = TimeSpan.Zero, AfkTimeSpent = TimeSpan.Zero, TimeFocused = TimeSpan.Zero };
                        _dbContext.DailyLogs.Add(log);
                    }

                    log.TimeSpent = log.TimeSpent.Add(addedTotal);
                    log.AfkTimeSpent = log.AfkTimeSpent.Add(addedAfk);
                    log.TimeFocused = log.TimeFocused.Add(addedFocus);
                    log.TimeSpentTicks = log.TimeSpent.Ticks;
                    log.AfkTimeSpentTicks = log.AfkTimeSpent.Ticks;
                    log.TimeFocusedTicks = log.TimeFocused.Ticks;

                    baselineMinutesToday[appName] = log.TimeSpent.TotalMinutes;
                }
            }

            void FlushPendingQueues()
            {
                while (_pendingSessions.TryDequeue(out var session)) _dbContext.SessionLogs.Add(session);
                while (_pendingMacros.TryDequeue(out var macro)) _dbContext.MacroEventLogs.Add(macro);
            }

            try
            {
            while (await timer.WaitForNextTickAsync(_trackerCts.Token))
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

                // The day rolls over here, on the tick that first sees it, rather
                // than waiting for the 60s flush below. Enforcement (D) runs every
                // tick against baselineMinutesToday, so leaving the reset to the
                // flush meant that for up to a minute past midnight yesterday's
                // totals were still in force -- a strict-mode app relaunched at
                // 00:00 got killed again on a fresh day's allowance.
                //
                // Whatever accumulated since the last flush belongs to the day
                // that just ended, so it is committed under the OLD date before
                // anything is cleared. Flushing it afterwards, as the 60s path
                // did, filed those final seconds under the new day.
                if (lastLimitResetDate is DateTime previousDay && previousDay != DateTime.Today)
                {
                    lock (_dbContext)
                    {
                        FlushDailySummaries(previousDay);
                        FlushPendingQueues();
                        try
                        {
                            _dbContext.SaveChanges();
                            Services.DatabaseHealth.ReportWriteSucceeded();
                        }
                        catch (Exception ex)
                        {
                            // Same reasoning as the 60s flush: midnight is a
                            // spectacularly bad moment to stop tracking.
                            Services.DatabaseHealth.ReportWriteFailed(ex);
                        }

                        foreach (var a in ManagedApps)
                        {
                            a.HasNotifiedToday = false;
                            a.HasWarnedToday = false;
                            if (a.BonusMinutesDate?.Date != DateTime.Today) a.TodayBonusMinutes = 0;
                        }
                    }

                    timeCache.Clear();
                    afkCache.Clear();
                    focusCache.Clear();
                    baselineMinutesToday.Clear();
                    lastLimitResetDate = DateTime.Today;
                }

                // --- NEW: FOCUS & SESSION TRACKING ---
                string rawActiveExe = GetActiveProcessName();
                string activeAppName = null;

                if (!string.IsNullOrEmpty(rawActiveExe))
                {
                    activeAppName = managedAppLookup.ContainsKey(rawActiveExe)
                                     ? managedAppLookup[rawActiveExe]
                                     : char.ToUpper(rawActiveExe[0]) + rawActiveExe.Substring(1);
                }

                // Did the user switch windows, or (when title capture is on)
                // change the title within the same app — e.g. a browser tab
                // change? Both close out the current session: otherwise a
                // single long Chrome session would freeze on whatever title
                // was showing when Chrome first got focus, and everything
                // after that (every other site visited) would be invisible.
                string liveTitle = (activeAppName != null && captureWindowTitles) ? GetActiveWindowTitle() : null;
                bool appChanged = activeAppName != currentFocusedApp;
                bool titleChanged = !appChanged && activeAppName != null && captureWindowTitles && liveTitle != currentSessionTitle;

                if (appChanged || titleChanged)
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

                    // 2. Start the new session with a freshly-sampled title.
                    currentFocusedApp = activeAppName;
                    currentSessionStart = activeAppName != null ? now : null;
                    currentSessionTitle = liveTitle;
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


                // C & D touch properties on the shared, UI-thread-bound ManagedApps
                // entities (TimeRunning, HasNotifiedToday, etc.) — _dbContext is not
                // safe to touch concurrently from multiple threads, so both sections
                // are locked the same way every other _dbContext access in this app is.
                lock (_dbContext)
                {
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
                                    NotificationService.Show(
                                        "Daily limit reached",
                                        $"{limitedApp.Name} has hit its {effectiveLimit}-minute daily limit.",
                                        NotificationSeverity.Warning,
                                        // Extending is PIN-gated, so the button opens
                                        // that dialog rather than granting the time --
                                        // a toast must not be a way around the PIN.
                                        new[]
                                        {
                                            new NotificationAction("extend", "Extend time\u2026"),
                                            new NotificationAction("dashboard", "Open dashboard")
                                        });
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
                                NotificationService.Show(
                                    "Almost at today's limit",
                                    $"{limitedApp.Name} has about {Math.Ceiling(remaining)} minute(s) left today.",
                                    NotificationSeverity.Info,
                                    new[] { new NotificationAction("extend", "Extend time\u2026") });
                            }
                            catch { /* Toast failures should never take the tracker down */ }
                        }
                    }
                }

                // --- DATABASE FLUSH ---
                tickCount++;
                if (tickCount >= FlushIntervalTicks)
                {
                    DateTime today = DateTime.Today;

                    // Locked as one unit: FlushDailySummaries/FlushPendingQueues/
                    // SaveChanges all touch the shared _dbContext, and RefreshStats
                    // (which internally re-enters this same lock — safe, lock is
                    // reentrant per-thread) queries it too.
                    lock (_dbContext)
                    {
                        // 1. Flush Daily Summaries
                        FlushDailySummaries(today);

                        // 2 & 3. Flush Shadow Sessions & Macros
                        FlushPendingQueues();

                        // A failed save must not leave this loop. It used to:
                        // the exception escaped into a fire-and-forget task and
                        // tracking stopped dead, silently, for twelve hours on
                        // 2026-09-02. The caches are folded into tracked entities
                        // by the two calls above and are cleared below either
                        // way, so the work is not lost or double-counted -- the
                        // entities stay pending and the next successful save
                        // writes them along with everything since.
                        try
                        {
                            _dbContext.SaveChanges();
                            Services.DatabaseHealth.ReportWriteSucceeded();
                            StatisticsVM?.RefreshStats();
                        }
                        catch (Exception ex)
                        {
                            Services.DatabaseHealth.ReportWriteFailed(ex);
                        }
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

                    timeCache.Clear();
                    afkCache.Clear();
                    focusCache.Clear();
                    tickCount = 0;
                }
            }
            }
            catch (OperationCanceledException)
            {
                // RequestShutdownFlushAsync canceled the wait — fall through to
                // the final flush below instead of losing whatever's in the
                // caches, or leaving the in-progress session unrecorded.
            }
            finally
            {
                try
                {
                    // Close out whatever session was still open, same shape as
                    // the mid-loop "did the user switch windows" close above —
                    // just triggered by shutdown instead of a window switch.
                    if (currentFocusedApp != null && currentSessionStart.HasValue)
                    {
                        _pendingSessions.Enqueue(new ViewModels.SessionLog
                        {
                            AppName = currentFocusedApp,
                            StartTime = currentSessionStart.Value,
                            EndTime = DateTime.Now,
                            WindowTitle = currentSessionTitle
                        });
                    }

                    lock (_dbContext)
                    {
                        FlushDailySummaries(DateTime.Today);
                        FlushPendingQueues();
                        _dbContext.SaveChanges();
                    }
                }
                catch { /* best-effort — the app is exiting either way */ }

                _trackerStoppedTcs.TrySetResult(true);
            }
        }


        // ==========================================
        // COMMANDS & UTILITIES
        // ==========================================
        // Both settings files live in FastAppData (AppDbContext.GetDbFolder()),
        // never in %LocalAppData%\FastApp — that one is Velopack's install-managed
        // root, and an install wiping it is exactly what destroyed the database on
        // 2026-08-19. osd_setting.txt used to sit there and was one reinstall away
        // from silently resetting itself.
        private string GetSettingsPath()
        {
            return Path.Combine(AppDbContext.GetDbFolder(), "osd_setting.txt");
        }

        private void LoadOsdSetting()
        {
            string path = GetSettingsPath();

            // One-time move of the value from the old, Velopack-owned location so
            // the toggle doesn't silently flip back to its default for anyone who
            // had already set it. Read-and-rewrite rather than File.Move: the old
            // copy may be gone, locked, or already migrated, and none of those are
            // worth failing startup over.
            if (!File.Exists(path))
            {
                try
                {
                    string legacyPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "FastApp", "osd_setting.txt");
                    if (File.Exists(legacyPath))
                    {
                        File.WriteAllText(path, File.ReadAllText(legacyPath));
                        try { File.Delete(legacyPath); } catch { /* leaving a stale copy behind is harmless */ }
                    }
                }
                catch { /* fall through to the default below */ }
            }

            if (File.Exists(path))
            {
                EnableOsd = File.ReadAllText(path) == "True";
            }
            else
            {
                EnableOsd = true;
            }
        }

        private string GetAutoLaunchProgressSettingsPath()
        {
            return Path.Combine(AppDbContext.GetDbFolder(), "autolaunch_progress_setting.txt");
        }

        private void LoadAutoLaunchProgressSetting()
        {
            string path = GetAutoLaunchProgressSettingsPath();
            if (File.Exists(path))
            {
                ShowAutoLaunchProgress = File.ReadAllText(path) == "True";
            }
            else
            {
                ShowAutoLaunchProgress = true;
            }
        }

        public void SaveDatabase()
        {
            lock (_dbContext) { _dbContext.SaveChanges(); }
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

                lock (_dbContext)
                {
                    _dbContext.ManagedApps.Add(newApp);
                    _dbContext.SaveChanges();
                }

                // ManagedApps.Add below fires the constructor's CollectionChanged
                // handler, which wires SaveOnAppPropertyChanged for us — no need to
                // do it again here.
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

            lock (_dbContext)
            {
                _dbContext.ManagedApps.Add(newAction);
                _dbContext.SaveChanges();
            }

            // Same as AddCustomFile — ManagedApps.Add below wires the auto-save
            // handler via the constructor's CollectionChanged subscription.
            ManagedApps.Add(newAction);
        }

        [RelayCommand]
        private void SaveDetectedApp(AppItemModel appToSave)
        {
            if (appToSave == null) return;

            lock (_dbContext)
            {
                _dbContext.ManagedApps.Add(appToSave);
                _dbContext.SaveChanges();
            }

            ManagedApps.Add(appToSave);
            DetectedApps.Remove(appToSave);
        }

        [RelayCommand]
        private void RemoveApplication(AppItemModel appToRemove)
        {
            if (appToRemove == null) return;

            lock (_dbContext)
            {
                _dbContext.ManagedApps.Remove(appToRemove);
                _dbContext.SaveChanges();
            }

            ManagedApps.Remove(appToRemove);
        }
    }
    // Drop this at the bottom of MainViewModel.cs
    public record CategoryUpdatedMessage(string AppName, string NewCategory);

    public record UpdateCategoryCommand(string AppName, string NewCategory);

    public record UpdateLimitCommand(string AppName, int DailyLimitMinutes, bool StrictFocusMode);

    public record GrantExtensionCommand(string AppName, int ExtraMinutes);

    // StagingFilePath has already been validated (SQLite header, integrity
    // check, has the right tables) by /api/restore before this is ever sent.
    public record RestoreBackupCommand(string StagingFilePath);

}