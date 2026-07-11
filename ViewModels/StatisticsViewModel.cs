using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastApp.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using static FastApp.Services.AppDbContext;
using CommunityToolkit.Mvvm.Messaging;

namespace FastApp.ViewModels
{
    public partial class StatisticsViewModel : ObservableObject
    {

        private bool _hasLoadedOnce = false;
        // Global Stats
        [ObservableProperty] private string _pcTimeToday;
        [ObservableProperty] private string _pcTimeWeek;
        [ObservableProperty] private string _pcTimeMonth;
        [ObservableProperty] private string _pcTimeYear;

        // NEW: Daily Averages
        [ObservableProperty] private string _pcDailyAverageWeek;
        [ObservableProperty] private string _pcDailyAverageMonth;
        [ObservableProperty] private string _pcDailyAverageYear;

        [ObservableProperty] private ObservableCollection<AppStatItem> _topApps = new();
        [ObservableProperty] private ObservableCollection<HiddenApp> _hiddenAppsList = new();
        [ObservableProperty] private ObservableCollection<HeatmapDay> _heatmapDays = new();

        // Search Filter
        [ObservableProperty] private string _statsSearchText;
        public ICollectionView FilteredTopApps { get; private set; }

        // UI Navigation State
        [ObservableProperty] private Visibility _mainViewVisibility = Visibility.Visible;
        [ObservableProperty] private Visibility _detailViewVisibility = Visibility.Collapsed;
        [ObservableProperty] private Visibility _addButtonVisibility = Visibility.Collapsed;
        [ObservableProperty] private Visibility _openExplorerVisibility = Visibility.Collapsed;

        // Detail View Properties
        [ObservableProperty] private string _detailAppName;
        [ObservableProperty] private string _detailTimeToday;
        [ObservableProperty] private string _detailTimeWeek;
        [ObservableProperty] private string _detailTimeMonth;
        [ObservableProperty] private string _detailDailyAverage;
        [ObservableProperty] private string _detailConsistency;
        [ObservableProperty] private string _detailFirstDiscovered;
        [ObservableProperty] private string _detailExecutablePath;
        [ObservableProperty] private string _detailTimeYear;
        [ObservableProperty] private string _detailTimeAllTime;
        [ObservableProperty] private string _detailPercentageOfPc;
        [ObservableProperty] private string _detailLongestDay;
        [ObservableProperty] private string _detailUsageBias;

        // AFK Toggle
        [ObservableProperty] private bool _excludeAfkTime;

        // OFF-LOADED AFK TOGGLE METHOD TO FIX UI LAG
        partial void OnExcludeAfkTimeChanged(bool value)
        {
            // Running on a background task so it doesn't freeze the toggle UI
            System.Threading.Tasks.Task.Run(() =>
            {
                lock (_dbContext)
                {
                    RefreshStats();
                }
            });
        }

        // Active Time Detail Fields
        [ObservableProperty] private string _detailActiveTimeToday;
        [ObservableProperty] private string _detailActiveTimeWeek;
        [ObservableProperty] private string _detailActiveTimeMonth;
        [ObservableProperty] private string _detailActiveTimeYear;
        [ObservableProperty] private string _detailActiveTimeAllTime;

        [ObservableProperty] private ObservableCollection<DietSegment> _dietSegments = new();

        private readonly AppDbContext _dbContext;
        private readonly MainViewModel _mainVM;

        // ==========================================
        // FASTAPP WRAPPED PROPERTIES
        // ==========================================
        [ObservableProperty] private Visibility _wrappedVisibility = Visibility.Collapsed;
        [ObservableProperty] private string _wrappedMonthName;
        [ObservableProperty] private string _wrappedTopApp;
        [ObservableProperty] private string _wrappedTopAppTime;
        [ObservableProperty] private string _wrappedDistraction;
        [ObservableProperty] private string _wrappedDistractionTime;
        [ObservableProperty] private string _wrappedPeakDayDate;
        [ObservableProperty] private string _wrappedPeakDayTime;
        [ObservableProperty] private string _wrappedMacros;
        [ObservableProperty] private string _wrappedTotalTime;

        // THE MASTER LIST: Bind the UI directly to this pure string list
        public List<string> AvailableCategories { get; } = new List<string>
        {
            "Development", "Gaming", "Productivity", "Browsing", "Communication",
            "Media Production", "Music", "Fun", "Education", "Utilities", "Other"
        };

        private string GetCategoryColor(string category)
        {
            var categoryColors = new Dictionary<string, string> {
                {"Development", "#9D00FF"}, {"Gaming", "#FF8C00"},
                {"Productivity", "#0078D7"}, {"Browsing", "#26A641"},
                {"Communication", "#FF3366"}, {"Media Production", "#FFD700"},
                {"Music", "#1DB954"}, {"Fun", "#FF00FF"},
                {"Education", "#00CED1"}, {"Utilities", "#808080"},
                {"Other", "#555555"}
            };
            return categoryColors.GetValueOrDefault(category, "#555555");
        }

        public StatisticsViewModel(AppDbContext dbContext, MainViewModel mainVM)
        {
            _dbContext = dbContext;
            _mainVM = mainVM;

            FilteredTopApps = CollectionViewSource.GetDefaultView(TopApps);
            FilteredTopApps.Filter = (item) =>
            {
                if (string.IsNullOrWhiteSpace(StatsSearchText)) return true;
                return ((AppStatItem)item).AppName.Contains(StatsSearchText, StringComparison.OrdinalIgnoreCase);
            };

            // --- NEW: THE TRUE TWO-WAY SYNC ---
            WeakReferenceMessenger.Default.Register<UpdateCategoryCommand>(this, (recipient, message) =>
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    // 1. Force the Statistics Database to update its internal category mapping
                    // FIX: Use .ToLower() for database queries instead of StringComparison!
                    var dbCat = _dbContext.AppCategories.FirstOrDefault(c => c.AppName.ToLower() == message.AppName.ToLower());

                    if (dbCat != null)
                    {
                        dbCat.Category = message.NewCategory;
                    }
                    else
                    {
                        _dbContext.AppCategories.Add(new AppCategoryMapping { AppName = message.AppName, Category = message.NewCategory });
                    }
                    _dbContext.SaveChanges(); // Save it so the UI redraws correctly!

                    // 2. If the details panel is open for this exact app, update the ComboBox visually
                    if (!string.IsNullOrEmpty(DetailAppName) && DetailAppName.Equals(message.AppName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Setting this automatically triggers the UI redraw and updates the charts
                        DetailCategory = message.NewCategory;
                    }
                    else
                    {
                        // 3. If the details panel is NOT open, we still need to refresh the Top Apps list manually
                        RefreshStats();
                        _mainVM.UpdateGamingProcessCache();
                    }
                });
            });
        }

        [ObservableProperty] private string _detailCategory;

        partial void OnDetailCategoryChanged(string value)
        {
            if (string.IsNullOrEmpty(DetailAppName) || string.IsNullOrEmpty(value)) return;

            // Save the clean category independently for this specific app in the Stats Database
            var dbCat = _dbContext.AppCategories.FirstOrDefault(c => c.AppName == DetailAppName);
            if (dbCat != null)
            {
                dbCat.Category = value;
            }
            else
            {
                _dbContext.AppCategories.Add(new AppCategoryMapping { AppName = DetailAppName, Category = value });
            }

            _dbContext.SaveChanges();
            RefreshStats();
            _mainVM.UpdateGamingProcessCache();
        }

        partial void OnStatsSearchTextChanged(string value)
        {
            FilteredTopApps.Refresh();
        }

        public void RefreshStats(bool forceLoad = false)
        {
            if (!_hasLoadedOnce && !forceLoad)
            {
                return;
            }
            _hasLoadedOnce = true;

            DateTime today = DateTime.Today;
            int diff = (int)today.DayOfWeek == 0 ? 6 : (int)today.DayOfWeek - 1;
            DateTime startOfWeek = today.AddDays(-diff);
            DateTime startOfMonth = new DateTime(today.Year, today.Month, 1);
            DateTime startOfYear = new DateTime(today.Year, 1, 1);

            int daysThisWeek = Math.Max(1, (int)(today - startOfWeek).TotalDays + 1);
            int daysThisMonth = Math.Max(1, today.Day);
            int daysThisYear = Math.Max(1, today.DayOfYear);

            // ==========================================
            // PC UPTIME — SQL-SIDE AGGREGATION
            // ==========================================
            // ==========================================
            // PC UPTIME — ONE QUERY INSTEAD OF SEVEN
            // ==========================================
            var pcRows = _dbContext.DailyLogs
                .Where(l => l.AppName == "SYSTEM_PC" && l.Date >= startOfYear)
                .Select(l => new { l.Date, l.TimeSpentTicks, l.AfkTimeSpentTicks })
                .ToList();

            long PcTicksFor(DateTime from) =>
                ExcludeAfkTime
                    ? pcRows.Where(l => l.Date >= from).Sum(l => Math.Max(0, (l.TimeSpentTicks ?? 0) - (l.AfkTimeSpentTicks ?? 0)))
                    : pcRows.Where(l => l.Date >= from).Sum(l => l.TimeSpentTicks ?? 0);

            PcTimeToday = FormatTime(PcTicksFor(today));
            PcTimeWeek = FormatTime(PcTicksFor(startOfWeek));
            PcTimeMonth = FormatTime(PcTicksFor(startOfMonth));
            PcTimeYear = FormatTime(PcTicksFor(startOfYear));

            PcDailyAverageWeek = FormatTime(PcTicksFor(startOfWeek) / daysThisWeek);
            PcDailyAverageMonth = FormatTime(PcTicksFor(startOfMonth) / daysThisMonth);
            PcDailyAverageYear = FormatTime(PcTicksFor(startOfYear) / daysThisYear);

            // Fetch Hidden Apps
            var hiddenAppNames = _dbContext.HiddenApps.Select(h => h.AppName).ToHashSet();
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                HiddenAppsList.Clear();
                foreach (var hidden in _dbContext.HiddenApps.ToList())
                    HiddenAppsList.Add(hidden);
            });

            // ==========================================
            // DIGITAL DIET CHART DATA — SQL-SIDE AGGREGATION
            // ==========================================
            var categoryMap = _dbContext.AppCategories.ToDictionary(c => c.AppName, c => c.Category);

            // SQLite groups by AppName and sums both tick columns; only per-app
            // aggregates (today's apps — a few dozen rows at most) come back.
            var rawDietTotals = _dbContext.DailyLogs
                .Where(l => l.Date == today && l.AppName != "SYSTEM_PC" && !hiddenAppNames.Contains(l.AppName))
                .GroupBy(l => l.AppName)
                .Select(g => new {
                    AppName = g.Key,
                    Total = g.Sum(x => (long?)x.TimeSpentTicks) ?? 0,
                    Afk = g.Sum(x => (long?)x.AfkTimeSpentTicks) ?? 0
                })
                .ToList();

            // Category remapping happens on this tiny in-memory set, not the raw rows
            var dietData = rawDietTotals
                .GroupBy(x => categoryMap.GetValueOrDefault(x.AppName, "Other"))
                .Select(g => new {
                    Category = g.Key,
                    Ticks = g.Sum(x => ExcludeAfkTime ? Math.Max(0, x.Total - x.Afk) : x.Total)
                }).ToList();

            long totalDietTicks = dietData.Sum(x => x.Ticks);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                DietSegments.Clear();
                foreach (var item in dietData.OrderByDescending(x => x.Ticks))
                {
                    DietSegments.Add(new DietSegment
                    {
                        Category = item.Category,
                        Percentage = totalDietTicks > 0 ? (double)item.Ticks / totalDietTicks * 100 : 0,
                        Color = GetCategoryColor(item.Category)
                    });
                }
            });

            // ==========================================
            // SPOTIFY RANKING MATH — SQL-SIDE AGGREGATION
            // ==========================================
            var yesterday = today.AddDays(-1);

            var yesterdayRawTotals = _dbContext.DailyLogs
                .Where(l => l.AppName != "SYSTEM_PC" && !hiddenAppNames.Contains(l.AppName) && l.Date <= yesterday)
                .GroupBy(l => l.AppName)
                .Select(g => new {
                    AppName = g.Key,
                    Total = g.Sum(x => (long?)x.TimeSpentTicks) ?? 0,
                    Afk = g.Sum(x => (long?)x.AfkTimeSpentTicks) ?? 0
                })
                .ToList();

            var yesterdayRanks = yesterdayRawTotals
                .Select(x => new { x.AppName, TotalTicks = ExcludeAfkTime ? Math.Max(0, x.Total - x.Afk) : x.Total })
                .OrderByDescending(x => x.TotalTicks)
                .Select((x, index) => new { x.AppName, Rank = index + 1 })
                .ToDictionary(x => x.AppName, x => x.Rank);

            // One row per distinct app, ever — not one row per day per app
            var rawAppTotals = _dbContext.DailyLogs
               .Where(l => l.AppName != "SYSTEM_PC" && !hiddenAppNames.Contains(l.AppName))
               .GroupBy(l => l.AppName)
               .Select(g => new {
                   AppName = g.Key,
                   Total = g.Sum(x => (long?)x.TimeSpentTicks) ?? 0,
                   Afk = g.Sum(x => (long?)x.AfkTimeSpentTicks) ?? 0
               })
               .ToList();

            var appGroups = rawAppTotals
               .Select(x => new { x.AppName, TotalTicks = ExcludeAfkTime ? Math.Max(0, x.Total - x.Afk) : x.Total })
               .OrderByDescending(a => a.TotalTicks)
               .ToList();

            long maxTicks = appGroups.FirstOrDefault()?.TotalTicks ?? 1;

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                TopApps.Clear();
                int currentRank = 1;
                foreach (var app in appGroups)
                {
                    int historicalRank = yesterdayRanks.GetValueOrDefault(app.AppName, currentRank);
                    string cat = categoryMap.GetValueOrDefault(app.AppName, "Other");

                    TopApps.Add(new AppStatItem
                    {
                        AppName = app.AppName,
                        DisplayTime = FormatTime(app.TotalTicks),
                        PercentageOfMax = (double)app.TotalTicks / maxTicks * 100,
                        CurrentRank = currentRank,
                        RankChange = historicalRank - currentRank,
                        Category = cat,
                        CategoryColor = GetCategoryColor(cat)
                    });
                    currentRank++;
                }

                // Heatmap still needs per-day PC totals, so this stays a targeted 30-row query
                HeatmapDays.Clear();
                DateTime heatmapStart = today.AddDays(-29);
                var pcDailyTotals = _dbContext.DailyLogs
                    .Where(l => l.AppName == "SYSTEM_PC" && l.Date >= heatmapStart)
                    .Select(l => new { l.Date, Ticks = l.TimeSpentTicks ?? 0 })
                    .ToDictionary(x => x.Date, x => x.Ticks);

                for (int i = 0; i <= 29; i++)
                {
                    DateTime d = heatmapStart.AddDays(i);
                    double hours = TimeSpan.FromTicks(pcDailyTotals.GetValueOrDefault(d, 0)).TotalHours;
                    string color = hours <= 0 ? "#161B22" : hours <= 2 ? "#0E4429" : hours <= 5 ? "#006D32" : hours <= 8 ? "#26A641" : "#39D353";
                    HeatmapDays.Add(new HeatmapDay { ColorHex = color, Tooltip = $"{hours:F1} hours on {d.ToString("MMM dd")}" });
                }
            });
        }

        // NEW: Command to securely launch default browser to local Web Dashboard
        [RelayCommand]
        private void OpenWebDashboard()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    // FIXED: Now points directly to the dashboard file
                    FileName = "http://127.0.0.1:5050/dashboard.html",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open web dashboard: {ex.Message}");
            }
        }

        [RelayCommand]
        public void GenerateWrapped()
        {
            DateTime today = DateTime.Today;
            DateTime startOfMonth = new DateTime(today.Year, today.Month, 1);
            WrappedMonthName = today.ToString("MMMM").ToUpper() + " WRAPPED";

            var hiddenAppNames = _dbContext.HiddenApps.Select(h => h.AppName).ToHashSet();
            var categoryMap = _dbContext.AppCategories.ToDictionary(c => c.AppName, c => c.Category);

            var monthLogs = _dbContext.DailyLogs.Where(l => l.Date >= startOfMonth).ToList();

            // 1. Total PC Time
            long pcTicks = monthLogs.Where(l => l.AppName == "SYSTEM_PC").Sum(l => GetTicks(new[] { l }));
            WrappedTotalTime = FormatTime(pcTicks);

            // 2. Top App
            var appGroups = monthLogs.Where(l => l.AppName != "SYSTEM_PC" && !hiddenAppNames.Contains(l.AppName))
                .GroupBy(l => l.AppName)
                .Select(g => new { AppName = g.Key, Ticks = GetTicks(g), Cat = categoryMap.GetValueOrDefault(g.Key, "Other") })
                .OrderByDescending(x => x.Ticks).ToList();

            var topApp = appGroups.FirstOrDefault();
            WrappedTopApp = topApp != null ? topApp.AppName : "No Data";
            WrappedTopAppTime = topApp != null ? FormatTime(topApp.Ticks) : "0h 00m";

            // 3. Biggest Distraction (Filters specifically for fun/gaming)
            var distractionCats = new[] { "Gaming", "Fun", "Browsing", "Media Production" };
            var distraction = appGroups.FirstOrDefault(a => distractionCats.Contains(a.Cat));
            WrappedDistraction = distraction != null ? distraction.AppName : "You were 100% productive!";
            WrappedDistractionTime = distraction != null ? FormatTime(distraction.Ticks) : "-";

            // 4. Peak Day
            var peakDay = monthLogs.Where(l => l.AppName == "SYSTEM_PC")
                .GroupBy(l => l.Date)
                .Select(g => new { Date = g.Key, Ticks = GetTicks(g) })
                .OrderByDescending(x => x.Ticks).FirstOrDefault();

            WrappedPeakDayDate = peakDay != null ? peakDay.Date.ToString("MMMM dd") : "No Data";
            WrappedPeakDayTime = peakDay != null ? FormatTime(peakDay.Ticks) : "0h 00m";

            // 5. Total Macros Fired
            WrappedMacros = (_mainVM?.ManagedApps?.Sum(m => m.HotkeyTriggerCount) ?? 0).ToString();

            // Trigger Overlay
            WrappedVisibility = Visibility.Visible;
        }

        [RelayCommand]
        public void CloseWrapped()
        {
            WrappedVisibility = Visibility.Collapsed;
        }

        [RelayCommand]
        public void ShowAppDetails(AppStatItem app)
        {
            if (app == null) return;
            DetailAppName = app.AppName;

            // Set current category from the app definition
            var managedApp = _mainVM.ManagedApps.FirstOrDefault(a => a.Name == app.AppName);
            // Set current category from the dedicated Stats Database (Defaults to "Other")
            var dbCategory = _dbContext.AppCategories.FirstOrDefault(c => c.AppName == app.AppName);
            DetailCategory = dbCategory != null ? dbCategory.Category : "Other";

            DateTime today = DateTime.Today;
            // Shift the .NET logic so Monday = 0 days subtracted, and Sunday = 6 days subtracted.
            int diff = (int)today.DayOfWeek == 0 ? 6 : (int)today.DayOfWeek - 1;
            DateTime startOfWeek = today.AddDays(-diff);
            DateTime startOfMonth = new DateTime(today.Year, today.Month, 1);
            DateTime startOfYear = new DateTime(today.Year, 1, 1);
            DateTime thirtyDaysAgo = today.AddDays(-30);

            var appLogs = _dbContext.DailyLogs.Where(l => l.AppName == app.AppName).ToList();

            // 1. Zwykły Czas (Total)
            DetailTimeToday = FormatTime(appLogs.Where(l => l.Date == today).Sum(l => l.TimeSpent.Ticks));
            DetailTimeWeek = FormatTime(appLogs.Where(l => l.Date >= startOfWeek).Sum(l => l.TimeSpent.Ticks));
            DetailTimeMonth = FormatTime(appLogs.Where(l => l.Date >= startOfMonth).Sum(l => l.TimeSpent.Ticks));
            DetailTimeYear = FormatTime(appLogs.Where(l => l.Date >= startOfYear).Sum(l => l.TimeSpent.Ticks));

            long allTimeTicks = appLogs.Sum(l => l.TimeSpent.Ticks);
            DetailTimeAllTime = FormatTime(allTimeTicks);

            // 2. Czas Aktywny (BEZ AFK)
            DetailActiveTimeToday = FormatTime(appLogs.Where(l => l.Date == today).Sum(l => Math.Max(0, (l.TimeSpent - l.AfkTimeSpent).Ticks)));
            DetailActiveTimeWeek = FormatTime(appLogs.Where(l => l.Date >= startOfWeek).Sum(l => Math.Max(0, (l.TimeSpent - l.AfkTimeSpent).Ticks)));
            DetailActiveTimeMonth = FormatTime(appLogs.Where(l => l.Date >= startOfMonth).Sum(l => Math.Max(0, (l.TimeSpent - l.AfkTimeSpent).Ticks)));
            DetailActiveTimeYear = FormatTime(appLogs.Where(l => l.Date >= startOfYear).Sum(l => Math.Max(0, (l.TimeSpent - l.AfkTimeSpent).Ticks)));
            DetailActiveTimeAllTime = FormatTime(appLogs.Sum(l => Math.Max(0, (l.TimeSpent - l.AfkTimeSpent).Ticks)));

            // 3. Procent życia komputera
            long totalPcTicks = _dbContext.DailyLogs.Where(l => l.AppName == "SYSTEM_PC").AsEnumerable().Sum(l => l.TimeSpent.Ticks);
            double pct = totalPcTicks > 0 ? ((double)allTimeTicks / totalPcTicks) * 100 : 0;
            DetailPercentageOfPc = $"{pct:F1}% of Total PC Time";

            // 4. Nerd Stat: Rekordowy Dzień
            var maxDay = appLogs.OrderByDescending(l => l.TimeSpent).FirstOrDefault();
            DetailLongestDay = maxDay != null && maxDay.TimeSpent.TotalMinutes > 0
                ? $"{FormatTime(maxDay.TimeSpent.Ticks)} (on {maxDay.Date:MMM dd, yyyy})"
                : "No data yet";

            // 5. Nerd Stat: Preferencje Dni (Weekend vs Weekday)
            long weekendTicks = appLogs.Where(l => l.Date.DayOfWeek == DayOfWeek.Saturday || l.Date.DayOfWeek == DayOfWeek.Sunday).Sum(l => l.TimeSpent.Ticks);
            long weekdayTicks = allTimeTicks - weekendTicks;

            double avgWeekend = weekendTicks / 2.0;
            double avgWeekday = weekdayTicks / 5.0;

            if (avgWeekend > avgWeekday * 1.5) DetailUsageBias = "Heavy Weekend Bias";
            else if (avgWeekday > avgWeekend * 1.5) DetailUsageBias = "Heavy Weekday Bias";
            else DetailUsageBias = "Evenly Distributed";

            // 6. Nerd Stat: Systematyczność
            int uniqueDaysUsed = appLogs.Select(l => l.Date).Distinct().Count();
            DetailDailyAverage = uniqueDaysUsed > 0 ? FormatTime(allTimeTicks / uniqueDaysUsed) : "0h 00m";

            int daysInLast30 = appLogs.Where(l => l.Date >= thirtyDaysAgo).Select(l => l.Date).Distinct().Count();
            DetailConsistency = $"{daysInLast30} of the last 30 days";

            // Detekcja ścieżki i przycisków
            bool isAlreadyManaged = _mainVM.ManagedApps.Any(m => m.Name.Equals(app.AppName, StringComparison.OrdinalIgnoreCase));
            AddButtonVisibility = isAlreadyManaged ? Visibility.Collapsed : Visibility.Visible;

            var managedMatch = _mainVM.ManagedApps.FirstOrDefault(m => m.Name.Equals(app.AppName, StringComparison.OrdinalIgnoreCase));
            if (managedMatch != null && !string.IsNullOrEmpty(managedMatch.ExecutablePath))
            {
                DetailExecutablePath = managedMatch.ExecutablePath;
                OpenExplorerVisibility = Visibility.Visible;
            }
            else
            {
                string foundPath = null;
                try { foundPath = Process.GetProcessesByName(app.AppName.ToLower()).FirstOrDefault()?.MainModule?.FileName; } catch { }

                if (!string.IsNullOrEmpty(foundPath))
                {
                    DetailExecutablePath = foundPath;
                    OpenExplorerVisibility = Visibility.Visible;
                }
                else
                {
                    DetailExecutablePath = "Path hidden (App is closed or system managed)";
                    OpenExplorerVisibility = Visibility.Collapsed;
                }
            }

            // Pokaż panel detali
            MainViewVisibility = Visibility.Collapsed;
            DetailViewVisibility = Visibility.Visible;
        }

        private long GetTicks(IEnumerable<DailyUsageLog> logs) => logs.Sum(l => ExcludeAfkTime ? Math.Max(0, (l.TimeSpent - l.AfkTimeSpent).Ticks) : l.TimeSpent.Ticks);

        private long GetEffectiveTicks(IQueryable<DailyUsageLog> query)
        {
            long total = query.Sum(l => (long?)l.TimeSpentTicks) ?? 0;
            if (!ExcludeAfkTime) return total;

            long afk = query.Sum(l => (long?)l.AfkTimeSpentTicks) ?? 0;
            return Math.Max(0, total - afk);
        }

        private string FormatTime(long ticks)
        {
            TimeSpan ts = TimeSpan.FromTicks(ticks);
            return $"{(int)ts.TotalHours:D2}h {ts.Minutes:D2}m";
        }

        // ==========================================
        // THE MISSING BUTTON COMMANDS
        // ==========================================

        [RelayCommand]
        private void HideApp()
        {
            if (!_dbContext.HiddenApps.Any(h => h.AppName == DetailAppName))
            {
                _dbContext.HiddenApps.Add(new HiddenApp { AppName = DetailAppName });
                _dbContext.SaveChanges();
            }
            CloseDetails();
            RefreshStats();
        }

        [RelayCommand]
        private void UnhideApp(string appName)
        {
            var appToUnhide = _dbContext.HiddenApps.FirstOrDefault(h => h.AppName == appName);
            if (appToUnhide != null)
            {
                _dbContext.HiddenApps.Remove(appToUnhide);
                _dbContext.SaveChanges();
                RefreshStats();
            }
        }

        [RelayCommand]
        private void CloseDetails()
        {
            MainViewVisibility = Visibility.Visible;
            DetailViewVisibility = Visibility.Collapsed;
        }

        [RelayCommand]
        private void AddToManagedApps()
        {
            _mainVM.SearchText = DetailAppName;
            _mainVM.AddApplicationCommand.Execute(null);
        }

        [RelayCommand]
        private void OpenInExplorer()
        {
            if (!string.IsNullOrEmpty(DetailExecutablePath) && System.IO.File.Exists(DetailExecutablePath))
            {
                Process.Start("explorer.exe", $"/select,\"{DetailExecutablePath}\"");
            }
        }
    }

    public class AppStatItem
    {
        public string AppName { get; set; }
        public string DisplayTime { get; set; }
        public double PercentageOfMax { get; set; }

        // Spotify Ranking Properties
        public int CurrentRank { get; set; }
        public int RankChange { get; set; }

        // NEW: Category Display
        public string Category { get; set; }
        public string CategoryColor { get; set; }

        public string RankChangeDisplay => Math.Abs(RankChange).ToString();
        public Visibility ShowUpArrow => RankChange > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ShowDownArrow => RankChange < 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ShowDash => RankChange == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public class HeatmapDay
    {
        public string ColorHex { get; set; }
        public string Tooltip { get; set; }
    }

    public class DietSegment
    {
        public string Category { get; set; }
        public double Percentage { get; set; }
        public string Color { get; set; }

        public string TooltipText => $"{Category} ({Percentage:F0}%)";
    }
}