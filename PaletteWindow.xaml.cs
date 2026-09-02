using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FastApp.Services;
using FastApp.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Web.WebView2.Core;

namespace FastApp
{
    /// <summary>
    /// The 2.0 desktop surface: a frameless window hosting the palette, which is
    /// the same HTML/CSS design system the web dashboard is built from.
    ///
    /// Two things make it feel native rather than like a browser in a box.
    /// First, the WebView2 is created once at application start and the window
    /// is hidden rather than closed, because a cold WebView2 takes a few hundred
    /// milliseconds to appear and a palette you wait for is a palette you stop
    /// summoning. Second, nothing that needs the OS goes over HTTP -- launching,
    /// focusing and closing travel the message bridge straight into the same
    /// services the WPF UI already calls.
    /// </summary>
    public partial class PaletteWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private bool _ready;

        private static readonly JsonSerializerOptions JsonOptions =
            new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

        public PaletteWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            Loaded += async (_, _) => await InitialiseAsync();
        }

        private async Task InitialiseAsync()
        {
            if (_ready) return;

            try
            {
                // User data lives beside the database rather than next to the
                // executable: the install directory is replaced wholesale on
                // every Velopack update, which would discard it each time.
                string profile = Path.Combine(AppDbContext.GetDbFolder(), "WebView2");
                Directory.CreateDirectory(profile);

                var environment = await CoreWebView2Environment.CreateAsync(null, profile);
                await Web.EnsureCoreWebView2Async(environment);

                var core = Web.CoreWebView2;

                // Served from disk under a virtual host rather than from the
                // dashboard's HTTP server: the palette must open even when that
                // server failed to start, and file:// would put every asset in
                // an opaque origin.
                core.SetVirtualHostNameToFolderMapping(
                    "fastapp.ui", UiFolder(), CoreWebView2HostResourceAccessKind.Allow);

                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDevToolsEnabled = true;   // still useful while 2.0 is being built
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;

                core.WebMessageReceived += OnWebMessage;

                // Anything that is not our own UI opens in the real browser
                // instead of navigating the palette away from itself.
                core.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    OpenExternally(e.Uri);
                };

                // Log what actually happens rather than assuming it worked: a
                // blank palette and a failed navigation look identical.
                core.NavigationCompleted += (_, args) =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[palette] navigation success={args.IsSuccess} status={args.WebErrorStatus}");
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:HH:mm:ss}] navigation success={args.IsSuccess} status={args.WebErrorStatus}{Environment.NewLine}");
                };
                core.ProcessFailed += (_, args) =>
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:HH:mm:ss}] process failed: {args.ProcessFailedKind}{Environment.NewLine}");

                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss}] mapping fastapp.ui -> {UiFolder()} (exists={Directory.Exists(UiFolder())}){Environment.NewLine}");

                core.Navigate("https://fastapp.ui/app/palette.html");
                _ready = true;
            }
            catch (Exception ex)
            {
                // A missing WebView2 runtime is the realistic case. The old
                // window is still there to fall back to, so this must not be
                // fatal -- it just means the palette is unavailable.
                System.Diagnostics.Debug.WriteLine($"Palette unavailable: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Native window shape
        // ------------------------------------------------------------------

        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Rounded corners and the system drop shadow, from the compositor
            // rather than from WPF. Doing it this way is what lets the window
            // stay on the hardware path that WebView2 needs; asking WPF for the
            // same look via AllowsTransparency renders the palette black.
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int preference = DwmwcpRound;
                DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            }
            catch
            {
                // Windows 10 has no such attribute; square corners are the only
                // cost, so this is not worth surfacing.
            }
        }

        private static string LogPath =>
            Path.Combine(Path.GetTempPath(), "fastapp_palette.log");

        private static string UiFolder()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, "wwwroot");
        }

        // ------------------------------------------------------------------
        // Bridge
        // ------------------------------------------------------------------

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.TryGetWebMessageAsString(); }
            catch { return; }

            Message message;
            try { message = JsonSerializer.Deserialize<Message>(raw, JsonOptions); }
            catch { return; }
            if (message?.Type == null) return;


            switch (message.Type)
            {
                case "ready":
                    PushState();
                    break;

                case "activate-app":
                    ActivateApp(message.Id);
                    break;

                case "edit-app":
                    PushApp(message.Id);
                    break;

                case "save-app":
                    SaveApp(message.App);
                    break;

                case "delete-app":
                    DeleteApp(message.Id);
                    break;

                case "capture-hotkey":
                    BeginCapture();
                    break;

                case "cancel-capture":
                    _mainWindow?.CancelHotkeyCapture();
                    break;

                case "reorder-app":
                    ReorderApp(message.Id, message.Delta);
                    break;

                case "new-action":
                    CreateAction();
                    break;

                case "browse-files":
                    BrowseForApp();
                    break;

                case "open-scanner":
                    OpenScanner();
                    break;

                case "set-setting":
                    ApplySetting(message.Key, message.Value, message.Text);
                    break;

                case "settings-command":
                    RunSettingsCommand(message.Id);
                    break;

                case "resize":
                    ResizeTo(message.Width, message.Height);
                    break;

                case "set-pinned":
                    // While an editing view is open, dismiss-on-deactivate would
                    // throw away half-finished work.
                    _pinned = message.Value;
                    break;

                case "run-command":
                    RunCommand(message.Id);
                    break;

                case "close":
                    HidePalette();
                    break;
            }
        }

        private void ActivateApp(string id)
        {
            var app = FindApp(id);
            if (app == null) return;

            HidePalette();
            Task.Run(() =>
            {
                var result = ActionHookEngine.Execute(app);
                if (!result.Success)
                {
                    NotificationService.Show($"{app.DisplayNamePrimary} did not run",
                        result.Message, NotificationSeverity.Warning);
                }
            });
        }

        private void RunCommand(string id)
        {
            switch (id)
            {
                case "dashboard":
                    HidePalette();
                    OpenExternally(DashboardServerService.DashboardUrl);
                    break;
                case "manage":
                    Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"show-manage\"}");
                    break;

                case "settings":
                    ShowSettings();
                    break;
                case "scan":
                    ShowManager();
                    break;
            }
        }

        private bool _pinned;
        private MainWindow _mainWindow;
        private bool _captureHooked;

        /// <summary>Send one app's full editable state to the palette.</summary>
        private void PushApp(string id)
        {
            var app = FindApp(id);
            if (app == null || Web.CoreWebView2 == null) return;

            var (today, _) = ReadToday();
            today.TryGetValue(app.Name, out var todaySpan);

            var payload = new
            {
                type = "app",
                app = new
                {
                    id = app.Id.ToString(),
                    name = app.Name,
                    displayName = app.DisplayNamePrimary,
                    customName = app.CustomName,
                    category = app.Category,
                    executablePath = app.ExecutablePath,
                    packaged = !string.IsNullOrWhiteSpace(app.PackagedAppId),
                    isAction = app.IsAction,
                    actionType = app.ActionType,
                    actionPayload = app.ActionPayload,
                    hotkeySequence = app.HotkeySequence,
                    hotkeyDisplay = app.HotkeyDisplayText,
                    suppressHotkeyPassthrough = app.SuppressHotkeyPassthrough,
                    launchOnStartup = app.LaunchOnStartup,
                    launchArguments = app.LaunchArguments,
                    launchDelaySeconds = app.LaunchDelaySeconds,
                    dailyLimitMinutes = app.DailyLimitMinutes,
                    strictFocusMode = app.StrictFocusMode,
                    limitsLocked = _viewModel.IsPinConfigured,
                    canReorder = true,
                    triggerCount = app.HotkeyTriggerCount,
                    today = FormatSpan(todaySpan)
                }
            };

            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
        }

        private void SaveApp(AppEdit edit)
        {
            if (edit == null) return;
            var app = FindApp(edit.Id);
            if (app == null) return;

            app.CustomName = edit.CustomName ?? string.Empty;
            app.LaunchArguments = edit.LaunchArguments ?? string.Empty;
            app.LaunchDelaySeconds = Math.Max(0, edit.LaunchDelaySeconds);
            app.LaunchOnStartup = edit.LaunchOnStartup;
            app.SuppressHotkeyPassthrough = edit.SuppressHotkeyPassthrough;
            if (!string.IsNullOrWhiteSpace(edit.Category)) app.Category = edit.Category;

            // Limits stay behind the PIN wherever they are edited from. A new
            // surface must not become a way around parental control.
            if (!_viewModel.IsPinConfigured)
            {
                app.DailyLimitMinutes = Math.Max(0, edit.DailyLimitMinutes);
                app.StrictFocusMode = edit.StrictFocusMode;
            }

            if (app.IsAction)
            {
                app.ActionType = Math.Clamp(edit.ActionType, 1, 3);
                app.ActionPayload = edit.ActionPayload ?? string.Empty;
            }

            if (edit.HotkeySequence != null)
            {
                app.HotkeySequence = edit.HotkeySequence;
                app.HotkeyDisplayText = string.IsNullOrWhiteSpace(edit.HotkeyDisplay)
                    ? "None"
                    : edit.HotkeyDisplay;
                _viewModel.RecompileHotkeys();
            }

            _viewModel.SaveDatabase();
            PushState();
        }

        private void DeleteApp(string id)
        {
            var app = FindApp(id);
            if (app == null) return;

            _viewModel.ManagedApps.Remove(app);
            _viewModel.SaveDatabase();
            _viewModel.RecompileHotkeys();
            PushState();
        }

        /// <summary>
        /// Move an entry up or down. OrderIndex is also the order startup apps
        /// are opened in, so this is not merely cosmetic; the whole list is
        /// renumbered afterwards because entries added over time can share an
        /// index or leave gaps, and swapping two equal values does nothing.
        /// </summary>
        private void ReorderApp(string id, int delta)
        {
            var app = FindApp(id);
            if (app == null || delta == 0) return;

            var ordered = _viewModel.ManagedApps.OrderBy(a => a.OrderIndex).ToList();
            int from = ordered.IndexOf(app);
            int to = from + delta;
            if (from < 0 || to < 0 || to >= ordered.Count) return;

            ordered.RemoveAt(from);
            ordered.Insert(to, app);
            for (int i = 0; i < ordered.Count; i++) ordered[i].OrderIndex = i;

            _viewModel.SaveDatabase();
            PushState();
        }

        /// <summary>
        /// Both of these defer to the view model's existing commands rather than
        /// building an entry here. A managed app has to be added to the
        /// DbContext and saved before it joins ManagedApps, or it never gets an
        /// Id and never reaches the database -- which is exactly what the first
        /// version of this did: the new action appeared in the list and was gone
        /// on restart. One persistence path, not two.
        /// </summary>
        private void CreateAction()
        {
            if (!_viewModel.AddCustomActionCommand.CanExecute(null)) return;
            _viewModel.AddCustomActionCommand.Execute(null);

            PushState();
            var created = _viewModel.ManagedApps.LastOrDefault();
            if (created != null) PushApp(created.Id.ToString());
        }

        private void BrowseForApp()
        {
            // Pinned first: the file dialog takes the foreground, and without
            // this the palette would dismiss itself the moment it opened.
            _pinned = true;
            if (_viewModel.AddCustomFileCommand.CanExecute(null))
                _viewModel.AddCustomFileCommand.Execute(null);
            PushState();
        }

        private void OpenScanner()
        {
            // The scanner is still its own WPF window in this stage; the palette
            // steps out of the way rather than competing with it.
            HidePalette();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_viewModel.AddApplicationCommand.CanExecute(null))
                    _viewModel.AddApplicationCommand.Execute(null);
            }));
        }

        // ------------------------------------------------------------------
        // Settings
        // ------------------------------------------------------------------

        private void ShowSettings()
        {
            // The version list is fetched lazily by the view model; ask for it
            // before the view appears so the rollback picker is not empty on
            // first open.
            _ = _viewModel.LoadRollbackVersionsAsync().ContinueWith(_ =>
                Dispatcher.BeginInvoke(new Action(PushSettings)));

            PushSettings();
            Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"show-settings\"}");
        }

        private void PushSettings()
        {
            if (Web.CoreWebView2 == null) return;

            var payload = new
            {
                type = "settings",
                settings = new
                {
                    launchOnStartup = _viewModel.LaunchOnSystemStartup,
                    startupBusy = _viewModel.IsStartupToggleBusy,
                    hasStartupConflict = _viewModel.HasStartupConflict,
                    startupConflictText = _viewModel.StartupConflictText,

                    enableOsd = _viewModel.EnableOsd,
                    showAutoLaunchProgress = _viewModel.ShowAutoLaunchProgress,

                    notificationsEnabled = _viewModel.NotificationsEnabled,
                    quietHoursEnabled = _viewModel.QuietHoursEnabled,
                    quietHoursFrom = _viewModel.QuietHoursFrom,
                    quietHoursTo = _viewModel.QuietHoursTo,

                    dashboardStatus = _viewModel.DashboardStatusText,
                    dashboardRunning = DashboardServerService.IsRunning,

                    version = _viewModel.UpdateVersionText,
                    updateStatus = _viewModel.UpdateStatusText,
                    checkingForUpdates = _viewModel.IsCheckingForUpdates,
                    updateReady = _viewModel.IsUpdateReadyToApply,

                    whatsNew = _viewModel.WhatsNewText,
                    hasWhatsNew = _viewModel.HasWhatsNew,

                    rollbackVersions = _viewModel.RollbackVersions.ToList(),
                    selectedRollback = _viewModel.SelectedRollbackVersion,
                    rollbackWarning = _viewModel.RollbackWarningText,
                    rollbackStatus = _viewModel.RollbackStatusText,
                    hasRollbackVersions = _viewModel.HasRollbackVersions,
                    rollbackBusy = _viewModel.IsRollbackBusy
                }
            };

            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
        }

        /// <summary>
        /// Setting changes go through the view model's own properties, never
        /// around them: each one carries the persistence and side effects
        /// (writing the AppSettings row, re-applying quiet hours, prompting for
        /// elevation) that the WPF settings tab already relies on.
        /// </summary>
        private void ApplySetting(string key, bool value, string text)
        {
            switch (key)
            {
                case "launchOnStartup": _viewModel.LaunchOnSystemStartup = value; break;
                case "enableOsd": _viewModel.EnableOsd = value; break;
                case "showAutoLaunchProgress": _viewModel.ShowAutoLaunchProgress = value; break;
                case "notificationsEnabled": _viewModel.NotificationsEnabled = value; break;
                case "quietHoursEnabled": _viewModel.QuietHoursEnabled = value; break;
                case "quietHoursFrom": _viewModel.QuietHoursFrom = text ?? string.Empty; break;
                case "quietHoursTo": _viewModel.QuietHoursTo = text ?? string.Empty; break;
                case "selectedRollback": _viewModel.SelectedRollbackVersion = text; break;
                default: return;
            }

            // Some of these are answered asynchronously (the startup toggle
            // waits on a UAC prompt), so reflect the truth shortly after rather
            // than trusting what was just sent.
            Dispatcher.BeginInvoke(new Action(PushSettings),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void RunSettingsCommand(string id)
        {
            switch (id)
            {
                case "fix-startup":
                    Execute(_viewModel.FixStartupRegistrationCommand);
                    break;
                case "check-updates":
                    Execute(_viewModel.CheckForUpdatesCommand);
                    break;
                case "apply-update":
                    Execute(_viewModel.ApplyPendingUpdateCommand);
                    break;
                case "rollback":
                    Execute(_viewModel.RollBackCommand);
                    break;
                case "open-dashboard":
                    HidePalette();
                    OpenExternally(DashboardServerService.DashboardUrl);
                    return;
                default:
                    return;
            }

            // These run for a while; poll the view model back to the page a few
            // times rather than wiring a property-changed subscription for a
            // view that is usually closed.
            for (int delay = 400; delay <= 3200; delay *= 2)
            {
                _ = Task.Delay(delay).ContinueWith(_ =>
                    Dispatcher.BeginInvoke(new Action(PushSettings)));
            }
        }

        private static void Execute(System.Windows.Input.ICommand command)
        {
            if (command != null && command.CanExecute(null)) command.Execute(null);
        }

        private void BeginCapture()
        {
            _mainWindow ??= System.Windows.Application.Current.MainWindow as MainWindow;
            if (_mainWindow == null) return;

            if (!_captureHooked)
            {
                _mainWindow.HotkeyCaptured += OnHotkeyCaptured;
                _captureHooked = true;
            }
            _mainWindow.BeginHotkeyCapture();
        }

        private void OnHotkeyCaptured(string sequence, string display)
        {
            if (Web.CoreWebView2 == null) return;
            var payload = new { type = "hotkey-captured", sequence, display };
            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
        }

        private void ResizeTo(int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            // Kept centred on whichever screen it is on, so a taller view grows
            // in both directions rather than pushing off the bottom.
            double centreX = Left + Width / 2, centreY = Top + Height / 2;
            Width = width;
            Height = height;
            Left = centreX - width / 2.0;
            Top = centreY - height / 2.0;
        }

        private AppItemModel FindApp(string id) =>
            int.TryParse(id, out int parsed)
                ? _viewModel.ManagedApps.FirstOrDefault(a => a.Id == parsed)
                : null;

        /// <summary>
        /// Today's focused time per app, and the day's total.
        ///
        /// Read from DailyLogs rather than from AppItemModel.TimeRunning: that
        /// property is the running total since the app was added, so using it
        /// here reported 233 hours "today". Its own short-lived context, so a
        /// palette summon never queues behind the tracker's writes.
        /// </summary>
        private static (Dictionary<string, TimeSpan> PerApp, TimeSpan Total) ReadToday()
        {
            var perApp = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
            TimeSpan total = TimeSpan.Zero;

            try
            {
                using var db = new AppDbContext();
                foreach (var log in db.DailyLogs.AsNoTracking().Where(l => l.Date == DateTime.Today))
                {
                    if (string.Equals(log.AppName, "SYSTEM_PC", StringComparison.OrdinalIgnoreCase))
                    {
                        total = log.TimeFocused;
                        continue;
                    }
                    perApp[log.AppName] = log.TimeFocused;
                }
            }
            catch
            {
                // An unreadable log is not worth failing the palette over; it
                // simply shows no figures.
            }

            return (perApp, total);
        }

        private void PushState()
        {
            if (Web.CoreWebView2 == null) return;

            var (today, focusTotal) = ReadToday();

            var apps = _viewModel.ManagedApps
                .OrderBy(a => a.OrderIndex)
                .Select(a => new
                {
                    id = a.Id.ToString(),
                    name = a.DisplayNamePrimary,
                    category = a.Category,
                    hotkey = string.IsNullOrWhiteSpace(a.HotkeySequence) ? null : a.HotkeyDisplayText,
                    autoStart = a.LaunchOnStartup,
                    limitMinutes = a.DailyLimitMinutes,
                    isAction = a.IsAction,
                    today = today.TryGetValue(a.Name, out var span) && span > TimeSpan.Zero
                        ? FormatSpan(span)
                        : "",
                    running = false
                })
                .ToList();

            var commands = new List<object>
            {
                new { id = "manage",    title = "Manage applications", hint = "add, reorder, remove" },
                new { id = "scan",      title = "Scan for new applications", hint = "Start menu + Store" },
                new { id = "settings",  title = "Settings", hint = (string)null },
                new { id = "dashboard", title = "Open statistics dashboard", hint = "opens in browser ↗" }
            };

            var payload = new
            {
                type = "state",
                state = new
                {
                    apps,
                    commands,
                    focusToday = FormatSpan(focusTotal),
                    tracking = true
                }
            };

            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
        }

        private static string FormatSpan(TimeSpan span) =>
            span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
                : $"{span.Minutes}m";

        // ------------------------------------------------------------------
        // Window behaviour
        // ------------------------------------------------------------------

        /// <summary>
        /// Build the WebView2 and render the palette once, invisibly, so the
        /// first real summon is a Show() rather than a browser start-up.
        /// </summary>
        public async Task PrewarmAsync()
        {
            // Shown unactivated and transparent purely to give the WebView2 a
            // visual tree to initialise in; the user never sees this.
            // Parked off-screen rather than hidden behind Opacity = 0: a WPF
            // window with fractional opacity is drawn through UpdateLayeredWindow,
            // the same software path AllowsTransparency puts it on, and a
            // WebView2 renders nothing there. Moving it off the desktop keeps
            // the window on the hardware path while still being unseen.
            _allowAutoHide = false;
            ShowActivated = false;
            double parkedLeft = Left, parkedTop = Top;
            Left = -32000;
            Top = -32000;
            Show();
            await InitialiseAsync();
            Hide();
            Left = parkedLeft;
            Top = parkedTop;
            ShowActivated = true;
        }

        // Auto-hide-on-deactivate is what makes this a palette rather than a
        // window, but it must not fire during the show itself: Show() briefly
        // deactivates before Activate() lands, and if something else owns the
        // foreground -- a fullscreen game, say -- the palette dismissed itself
        // the instant it appeared. Armed only once it has genuinely been
        // activated, so the first deactivation it acts on is the user leaving.
        private bool _allowAutoHide;

        /// <summary>Summon it. Hidden rather than closed, so this is instant.</summary>
        public void ShowPalette()
        {
            _allowAutoHide = false;
            _pinned = false;
            Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"reset\"}");
            Show();
            Activate();
            Web.Focus();

            // Some foregrounds refuse to yield activation. Rather than sit
            // there un-dismissable, arm the auto-hide shortly after showing
            // even if Activated never arrives.
            Dispatcher.BeginInvoke(new Action(PushState));
            _ = Task.Delay(400).ContinueWith(_ =>
                Dispatcher.BeginInvoke(new Action(() => _allowAutoHide = true)));
        }

        public void HidePalette() => Hide();

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _allowAutoHide = true;
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            // A palette that stays up after you click away is a window, not a
            // palette.
            if (_allowAutoHide && !_pinned && IsVisible) Hide();
        }

        protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        private void ShowManager()
        {
            HidePalette();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var main = System.Windows.Application.Current.MainWindow;
                if (main == null) return;
                main.Show();
                main.WindowState = System.Windows.WindowState.Normal;
                main.Activate();
            }));
        }

        private static void OpenExternally(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { /* nothing useful to do if the shell refuses */ }
        }

        private sealed class Message
        {
            public string Type { get; set; }
            public string Id { get; set; }
            public AppEdit App { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public bool Value { get; set; }
            public int Delta { get; set; }
            public string Key { get; set; }
            public string Text { get; set; }
        }

        /// <summary>
        /// The editable half of a managed app. Deliberately not the entity: the
        /// palette may only change these fields, so anything it sends for
        /// something else is ignored rather than trusted.
        /// </summary>
        private sealed class AppEdit
        {
            public string Id { get; set; }
            public string CustomName { get; set; }
            public string HotkeySequence { get; set; }
            public string HotkeyDisplay { get; set; }
            public bool SuppressHotkeyPassthrough { get; set; }
            public bool LaunchOnStartup { get; set; }
            public string LaunchArguments { get; set; }
            public int LaunchDelaySeconds { get; set; }
            public int DailyLimitMinutes { get; set; }
            public bool StrictFocusMode { get; set; }
            public string Category { get; set; }
            public int ActionType { get; set; }
            public string ActionPayload { get; set; }
        }
    }
}
