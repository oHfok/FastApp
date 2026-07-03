using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastApp.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input; // NOWE: Wymagane do rozpoznawania klawiszy (enum Key)
using Microsoft.Win32;

namespace FastApp.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        [ObservableProperty]
        private ObservableCollection<AppItemModel> _managedApps;

        [ObservableProperty]
        private ObservableCollection<AppItemModel> _detectedApps = new();

        public StatisticsViewModel StatisticsVM { get; }

        [ObservableProperty]
        private ICollectionView _detectedAppsView;

        // NEW: Search filter for Tab 1
        [ObservableProperty] private string _appSearchText;
        public ICollectionView FilteredManagedApps { get; }

        // This method automatically runs every time you type a letter into the search box
        partial void OnAppSearchTextChanged(string value)
        {
            FilteredManagedApps.Refresh();
        }

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

        public MainViewModel()
        {
            

            LoadOsdSetting();

            _dbContext = new AppDbContext();
            _dbContext.Database.EnsureCreated();

            // 1. Load the apps FIRST
            var savedApps = _dbContext.ManagedApps.ToList();
            ManagedApps = new ObservableCollection<AppItemModel>(savedApps);

            // 2. NOW initialize the Statistics page (so ManagedApps is not null)
            StatisticsVM = new StatisticsViewModel(_dbContext, this);

            // NEW: Set up the smart lens for Tab 1
            FilteredManagedApps = CollectionViewSource.GetDefaultView(ManagedApps);
            FilteredManagedApps.Filter = (item) =>
            {
                if (string.IsNullOrWhiteSpace(AppSearchText)) return true;

                var app = (AppItemModel)item;
                bool matchesName = app.Name?.Contains(AppSearchText, StringComparison.OrdinalIgnoreCase) == true;
                bool matchesCustom = app.CustomName?.Contains(AppSearchText, StringComparison.OrdinalIgnoreCase) == true;

                return matchesName || matchesCustom;
            };

            // FIX: Instantly save to the database whenever ANY setting is changed on a card
            foreach (var app in ManagedApps)
            {
                app.PropertyChanged += (s, e) => _dbContext.SaveChanges();
            }

            // Also ensure newly added apps get the same instant-save behavior
            ManagedApps.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                {
                    foreach (AppItemModel newItem in e.NewItems)
                        newItem.PropertyChanged += (sender, args) => _dbContext.SaveChanges();
                }
                _dbContext.SaveChanges();
            };



            // Ask Windows for every process currently running right now
            var runningProcesses = Process.GetProcesses()
                                          .Select(p => p.ProcessName.ToLower())
                                          .ToHashSet();

            // Uruchamianie aplikacji przy starcie
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

            _ = StartProcessTrackerAsync();
        }

        private string GetSettingsPath()
        {
            // 1. Get the path to %LocalAppData%\FastApp
            string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastApp");

            // 2. Ensure the folder exists (it won't exist the first time the app runs!)
            System.IO.Directory.CreateDirectory(folder);

            // 3. Return the full path to the file
            return System.IO.Path.Combine(folder, "osd_setting.txt");
        }

        // NEW: Global OSD Toggle
        [ObservableProperty] private bool _enableOsd;

        // Load OSD setting (Defaults to True)
        private void LoadOsdSetting()
        {
            string path = GetSettingsPath();
            if (System.IO.File.Exists(path))
            {
                EnableOsd = System.IO.File.ReadAllText(path) == "True";
            }
            else
            {
                EnableOsd = true;
            }
        }

        partial void OnEnableOsdChanged(bool value)
        {
            // Save choice to the safe AppData location
            try
            {
                System.IO.File.WriteAllText(GetSettingsPath(), value.ToString());
            }
            catch (Exception ex)
            {
                // Optional: Log this error, but the app won't crash anymore!
                System.Diagnostics.Debug.WriteLine($"Failed to save OSD setting: {ex.Message}");
            }
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            _dbContext?.SaveChanges();
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

        // NEW: Manually browse for any custom file
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

        // NEW: Instantly create a blank System Action (No app required)
        [RelayCommand]
        private void AddCustomAction()
        {
            var newAction = new AppItemModel
            {
                Name = "New System Action",
                ExecutablePath = string.Empty,
                ActionType = 1, // Defaults to System Mute
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

        // ZAKTUALIZOWANE: Usuwanie aplikacji nie wymaga już odrejestrowywania z Windowsa,
        // ponieważ nasz nowy AdvancedKeyboardHook automatycznie ignoruje usunięte aplikacje.
        [RelayCommand]
        private void RemoveApplication(AppItemModel appToRemove)
        {
            if (appToRemove == null) return;

            _dbContext.ManagedApps.Remove(appToRemove);
            _dbContext.SaveChanges();

            ManagedApps.Remove(appToRemove);
        }

        // NOWE: Główny silnik sprawdzający kombinacje klawiszy z AdvancedKeyboardHook
        // NOWE: Główny silnik sprawdzający kombinacje klawiszy z AdvancedKeyboardHook
        public void CheckForHotkeys(HashSet<Key> currentlyPressedKeys)
        {
            if (currentlyPressedKeys.Count == 0) return;

            foreach (var app in ManagedApps)
            {
                if (string.IsNullOrEmpty(app.HotkeySequence)) continue;

                try
                {
                    // Convert the database string ("LeftCtrl,V,A,L") back into a HashSet of Keys
                    var dbKeys = app.HotkeySequence
                                    .Split(',')
                                    .Select(k => (Key)Enum.Parse(typeof(Key), k))
                                    .ToHashSet();

                    // SetEquals guarantees a perfect match, regardless of what order you press them in!
                    if (currentlyPressedKeys.SetEquals(dbKeys))
                    {
                        // 1. Update the UI counter
                        app.HotkeyTriggerCount++;
                        SaveDatabase();

                        // 2. FIX: Instead of just Process.Start, hand the app to the new Action Engine!
                        // Execute the Action
                        Services.ActionHookEngine.Execute(app);

                        // NEW: Fire the Visual OSD if the user has it enabled!
                        if (EnableOsd)
                        {
                            Services.OsdService.Show($"{app.DisplayNamePrimary} Activated", app.IsAction);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Hotkey Execution failed: {ex.Message}");
                }
            }
        }


        private async Task StartProcessTrackerAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            // NEW: In-Memory Cache to accumulate time without spamming the SSD
            // NEW: Added AFK Cache
            var timeCache = new Dictionary<string, TimeSpan>();
            var afkCache = new Dictionary<string, TimeSpan>();
            int tickCount = 0;
            const int FlushIntervalTicks = 12; // 60 seconds

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

                // NEW: Are we currently AFK? (Idle for more than 5 minutes)
                bool isAfk = Services.SystemIdleTracker.GetIdleTime().TotalMinutes >= 5;
                TimeSpan tickDuration = TimeSpan.FromSeconds(5);

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

                    foreach (var kvp in timeCache)
                    {
                        string appName = kvp.Key;
                        TimeSpan addedTotal = kvp.Value;
                        TimeSpan addedAfk = afkCache.GetValueOrDefault(appName); // Get the AFK portion

                        var log = _dbContext.DailyLogs.FirstOrDefault(l => l.Date == today && l.AppName == appName);
                        if (log == null)
                        {
                            log = new DailyUsageLog { Date = today, AppName = appName, TimeSpent = TimeSpan.Zero, AfkTimeSpent = TimeSpan.Zero };
                            _dbContext.DailyLogs.Add(log);
                        }

                        log.TimeSpent = log.TimeSpent.Add(addedTotal);
                        log.AfkTimeSpent = log.AfkTimeSpent.Add(addedAfk);
                    }

                    _dbContext.SaveChanges();
                    StatisticsVM?.RefreshStats();

                    timeCache.Clear();
                    afkCache.Clear();
                    tickCount = 0;
                }
            }
        }
    }
}