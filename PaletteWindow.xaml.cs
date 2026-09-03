using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using FastApp.Services;
using FastApp.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Web.WebView2.Core;

namespace FastApp
{
    /// <summary>Which view a summon should land on.</summary>
    public enum PaletteView
    {
        Search,
        Manage,
        Settings,
        Extend
    }

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

        /// <summary>Null when the palette works; otherwise why it does not.</summary>
        public string Unavailable { get; private set; }

        private static readonly JsonSerializerOptions JsonOptions =
            new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

        public PaletteWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            Loaded += async (_, _) => await InitialiseAsync();
        }

        /// <summary>
        /// Initialise once, however many callers ask.
        ///
        /// Two of them always do: PrewarmAsync calls this directly, and the
        /// Show() it performs raises Loaded, which calls it too. The old
        /// boolean guard was set only after the awaits, so both calls sailed
        /// past it and each built its own CoreWebView2Environment -- the second
        /// then threw "WebView2 was already initialized with a different
        /// CoreWebView2Environment". It won the race often enough to look fine.
        /// Memoising the task makes the second caller await the first.
        /// </summary>
        private Task _initialisation;

        private Task InitialiseAsync() => _initialisation ??= InitialiseCoreAsync();

        private async Task InitialiseCoreAsync()
        {
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
                // A missing WebView2 runtime is the realistic case. There is no
                // longer a manager window to fall back to, so this cannot be
                // swallowed: the reason is kept and shown when someone tries to
                // open FastApp, or the app looks simply broken.
                Unavailable = ex.Message;
                System.Diagnostics.Debug.WriteLine($"Palette unavailable: {ex.Message}");
                try
                {
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:HH:mm:ss}] palette unavailable: {ex}{Environment.NewLine}");
                }
                catch { }
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

        /// <summary>
        /// Where the palette's own files are served from.
        ///
        /// NOT the application directory. That directory belongs to Velopack: it
        /// is replaced wholesale on every update and actively tidied afterwards,
        /// and a WebView2 virtual host mapped into it stops serving -- navigation
        /// fails with ConnectionAborted while every file is still sitting there,
        /// readable, with ordinary permissions. Proven by elimination: the same
        /// files copied anywhere else load immediately, including a sibling
        /// folder inside the same install directory (which Velopack then deleted
        /// on the next launch, which is the tell).
        ///
        /// So the UI is staged beside the database instead, where nothing but
        /// this app writes, and re-staged whenever the version changes.
        /// </summary>
        private static string UiFolder()
        {
            string staged = Path.Combine(AppDbContext.GetDbFolder(), "ui");
            try
            {
                StageUi(staged);
                return staged;
            }
            catch (Exception ex)
            {
                // Fall back to serving in place rather than losing the interface
                // outright; it works everywhere except an installed build.
                System.Diagnostics.Debug.WriteLine($"Could not stage the UI: {ex.Message}");
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            }
        }

        /// <summary>
        /// Mirror the shipped wwwroot into <paramref name="staged"/>, but only
        /// when the version stamp differs, so an ordinary launch costs one file
        /// read rather than a recursive copy.
        /// </summary>
        private static void StageUi(string staged)
        {
            string source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);

            // Stamped with what the source actually is, rather than a version
            // number: the assembly version does not track the release version
            // here (it reported 1.2.0.0 on a 2.0.1 build), and a stamp that
            // never changes would serve the previous release's interface
            // forever after an update.
            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            long newest = 0;
            foreach (string f in files)
            {
                long t = File.GetLastWriteTimeUtc(f).Ticks;
                if (t > newest) newest = t;
            }
            string version = $"{files.Length}:{newest}";

            string stamp = Path.Combine(staged, ".staged-version");
            if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == version) return;

            if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
            Directory.CreateDirectory(staged);

            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(staged, Path.GetRelativePath(source, dir)));
            }
            foreach (string file in files)
            {
                File.Copy(file, Path.Combine(staged, Path.GetRelativePath(source, file)), overwrite: true);
            }

            File.WriteAllText(stamp, version);
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
                    _ = RunScanAsync();
                    break;

                case "add-scanned":
                    AddScanned(message.Paths);
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
                    // Kept so an older cached page cannot wedge the window open;
                    // nothing sends this any more. Every view dismisses on
                    // click-away now, because every field saves as it changes.
                    break;

                case "open-dashboard-app":
                    OpenDashboard($"?app={Uri.EscapeDataString(message.Text ?? string.Empty)}");
                    break;

                case "add-tracked":
                    AddTracked(message.Text);
                    break;

                case "extend-grant":
                    GrantExtension(message.Id, message.Minutes, message.Pin);
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
                    OpenDashboard(null);
                    break;
                case "manage":
                    ShowManage();
                    break;

                case "settings":
                    ShowSettings();
                    break;
                case "extend":
                    ShowExtend();
                    break;
                case "scan":
                    _ = RunScanAsync();
                    break;
            }
        }

        // Suppresses dismiss-on-click-away for the duration of a modal this
        // window opens itself. Not used for views: those all dismiss normally.
        private bool _pinned;
        private MainWindow _mainWindow;
        private bool _captureHooked;

        /// <summary>Send one app's full editable state to the palette.</summary>
        private void PushApp(string id)
        {
            var app = FindApp(id);
            if (app == null || Web.CoreWebView2 == null) return;

            var (today, _) = TodayUsage.Read();
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
                    category = CategoryMap.For(CategoryMap.Build(), app.Name),
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
                    today = FormatSpan(todaySpan),
                    running = !app.IsAction
                              && RunningApps.IsRunning(RunningApps.WindowOwners(), app.ExecutablePath)
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
            // The one place the flag is still needed: the file dialog takes the
            // foreground, and the palette would otherwise dismiss itself the
            // instant it opened. Released in a finally so a cancelled or failed
            // dialog cannot leave the window permanently undismissable, which is
            // what the first version of this did.
            _pinned = true;
            try
            {
                if (_viewModel.AddCustomFileCommand.CanExecute(null))
                    _viewModel.AddCustomFileCommand.Execute(null);
                PushState();
            }
            finally
            {
                _pinned = false;
            }
        }

        // ------------------------------------------------------------------
        // Scanner
        // ------------------------------------------------------------------

        // The last scan's results. The page sends back paths rather than
        // indices, so a re-scan between showing and choosing cannot silently
        // add the wrong application.
        private List<AppItemModel> _scanned = new();

        private async Task RunScanAsync()
        {
            if (Web.CoreWebView2 == null) return;

            Web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"show-scanner\"}");
            PushScan(scanning: true);

            List<AppItemModel> found;
            try
            {
                found = await AppScannerService.GetInstalledAppsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Scan failed: {ex.Message}");
                found = new List<AppItemModel>();
            }

            // Anything already managed is not a discovery; showing it only
            // invites adding a duplicate.
            var managed = _viewModel.ManagedApps
                .Where(a => !string.IsNullOrEmpty(a.ExecutablePath))
                .Select(a => a.ExecutablePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _scanned = found
                .Where(a => !string.IsNullOrEmpty(a.ExecutablePath) && !managed.Contains(a.ExecutablePath))
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            PushScan(scanning: false);
        }

        private void PushScan(bool scanning)
        {
            if (Web.CoreWebView2 == null) return;

            var payload = new
            {
                type = "scan",
                scan = new
                {
                    scanning,
                    apps = _scanned.Select(a => new
                    {
                        name = a.Name,
                        path = a.ExecutablePath,
                        packaged = !string.IsNullOrWhiteSpace(a.PackagedAppId)
                    }).ToList()
                }
            };

            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
        }

        private void AddScanned(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return;

            var wanted = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var app in _scanned.Where(a => wanted.Contains(a.ExecutablePath)).ToList())
            {
                // SaveDetectedApp is the one path that adds to the DbContext,
                // saves, and only then joins ManagedApps. An entry added
                // straight to the collection never gets an Id and never reaches
                // the database.
                if (_viewModel.SaveDetectedAppCommand.CanExecute(app))
                    _viewModel.SaveDetectedAppCommand.Execute(app);
            }

            _scanned = _scanned.Where(a => !wanted.Contains(a.ExecutablePath)).ToList();
            PushState();
            PushScan(scanning: false);
        }

        /// <summary>
        /// Add an application FastApp has been tracking but was never told
        /// about.
        ///
        /// The logs hold a process name and nothing else, so the executable has
        /// to be found: from the running process if it happens to be open, and
        /// otherwise from a scan of what is installed. When neither answers,
        /// say so and open the file picker rather than adding a broken entry --
        /// an app with no path cannot be launched, and would sit in the list
        /// looking configured.
        /// </summary>
        private async void AddTracked(string trackedName)
        {
            if (string.IsNullOrWhiteSpace(trackedName)) return;

            string path = TrackedApps.ResolvePath(trackedName);

            if (path == null)
            {
                // Scanning the Start menu and the Store takes a second or two,
                // and it happens after a click that otherwise shows nothing.
                Notify($"Looking for {trackedName}…");

                List<string> installed;
                try
                {
                    var found = await AppScannerService.GetInstalledAppsAsync();
                    installed = found.Select(a => a.ExecutablePath).ToList();
                }
                catch
                {
                    installed = new List<string>();
                }

                path = TrackedApps.ResolvePath(trackedName, installed);
            }

            if (path == null)
            {
                Notify($"FastApp could not find where {trackedName} is installed. Pick it yourself.");
                BrowseForApp();
                return;
            }

            if (_viewModel.ManagedApps.Any(a =>
                    string.Equals(a.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                Notify($"{trackedName} is already in your list.");
                return;
            }

            // Named after the tracked name rather than something friendlier:
            // the tracker keys its logs on the executable name, so calling it
            // anything else here would start a second history alongside the one
            // this app already has.
            var entry = new AppItemModel
            {
                Name = trackedName,
                ExecutablePath = path,
                Category = "Other",
                OrderIndex = _viewModel.ManagedApps.Count
            };

            // SaveDetectedApp is the one path that adds to the DbContext, saves,
            // and only then joins ManagedApps. An entry added straight to the
            // collection never gets an Id and never reaches the database.
            if (_viewModel.SaveDetectedAppCommand.CanExecute(entry))
                _viewModel.SaveDetectedAppCommand.Execute(entry);

            PushState();
            Notify($"{trackedName} added.");
        }

        private void Notify(string text)
        {
            var payload = new { type = "toast", text };
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
        }

        // ------------------------------------------------------------------
        // Extend time
        //
        // Replaces a WinForms dialog. The reason it exists at all is that the
        // web dashboard's version is useless in the case that matters most --
        // when the app being limited IS the browser, or a browser is off limits
        // entirely -- so it has to be reachable from FastApp's own window.
        // ------------------------------------------------------------------

        private void ShowExtend()
        {
            PushExtend();
            Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"show-extend\"}");
        }

        private void PushExtend()
        {
            if (Web.CoreWebView2 == null) return;

            var (today, _) = TodayUsage.Read();
            bool hasPin;
            try
            {
                using var db = new AppDbContext();
                hasPin = PinService.GetPinInfo(db).HasPin;
            }
            catch
            {
                // Unreadable means unverifiable, and granting without a check is
                // the one outcome this feature must never produce.
                hasPin = false;
            }

            var apps = _viewModel.ManagedApps
                .Where(a => a.DailyLimitMinutes > 0)
                .OrderBy(a => a.DisplayNamePrimary)
                .Select(a => new
                {
                    id = a.Id.ToString(),
                    name = a.DisplayNamePrimary,
                    limitMinutes = a.DailyLimitMinutes,
                    usedToday = today.TryGetValue(a.Name, out var span) ? (int)span.TotalMinutes : 0,
                    bonusToday = a.BonusMinutesDate?.Date == DateTime.Today ? a.TodayBonusMinutes : 0
                })
                .ToList();

            var payload = new { type = "extend", extend = new { apps, hasPin } };
            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
        }

        private void GrantExtension(string id, int minutes, string pin)
        {
            var app = FindApp(id);
            if (app == null) { ExtendResult(false, "That app is no longer in the list."); return; }
            if (minutes <= 0) { ExtendResult(false, "Pick how much time to grant."); return; }

            bool verified;
            try
            {
                using var db = new AppDbContext();
                var (hasPin, salt, hash) = PinService.GetPinInfo(db);
                if (!hasPin) { ExtendResult(false, "No PIN is set. Set one in Settings first."); return; }
                verified = PinService.VerifyPin(pin, salt, hash);
            }
            catch (Exception ex)
            {
                ExtendResult(false, $"The PIN could not be checked: {ex.Message}");
                return;
            }

            if (!verified) { ExtendResult(false, "That PIN is not right."); return; }

            // The same message the dashboard's /api/extend-limit sends, so both
            // routes land in one handler and cannot drift apart.
            WeakReferenceMessenger.Default.Send(
                new ViewModels.GrantExtensionCommand(app.Name, minutes));

            ExtendResult(true, $"{app.DisplayNamePrimary} has {minutes} more minutes today.");

            // Re-read so the usage line reflects the grant that just happened.
            Dispatcher.BeginInvoke(new Action(PushExtend));
        }

        private void ExtendResult(bool ok, string text)
        {
            var payload = new { type = "extend-result", value = ok, text };
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
        }

        // ------------------------------------------------------------------
        // Settings
        // ------------------------------------------------------------------

        private void ShowManage() =>
            Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"show-manage\"}");

        private void ShowSettings()
        {
            // Both of these are fetched lazily by the view model, and both used
            // to be triggered by switching to the old Settings tab. That tab is
            // gone, so opening Settings is what has to ask for them now --
            // without this the release notes were simply never loaded and the
            // card sat empty forever.
            _ = _viewModel.LoadWhatsNewAsync().ContinueWith(_ =>
                Dispatcher.BeginInvoke(new Action(PushSettings)));

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
                case "open-release-notes":
                    OpenDashboard("?settings=whatsnew");
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
        /// When each app was last in the foreground, newest session per app.
        ///
        /// Bounded to the last 60 days rather than grouping the whole table:
        /// anything older is not "recent" by any reading, and the palette is
        /// summoned constantly enough that this query should stay small.
        /// IX_SessionLogs_AppName_StartTime covers it.
        /// </summary>
        private static Dictionary<string, DateTime> ReadLastUsed()
        {
            var last = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-60);
                using var db = new AppDbContext();
                var rows = db.SessionLogs.AsNoTracking()
                    .Where(s => s.StartTime >= cutoff)
                    .GroupBy(s => s.AppName)
                    .Select(g => new { Name = g.Key, Last = g.Max(x => x.StartTime) })
                    .ToList();

                foreach (var row in rows)
                {
                    if (!string.IsNullOrEmpty(row.Name)) last[row.Name] = row.Last;
                }
            }
            catch
            {
                // No history simply means no ordering preference; the list falls
                // back to the order shown in Manage.
            }
            return last;
        }

        private void PushState()
        {
            if (Web.CoreWebView2 == null) return;

            var (today, focusTotal) = TodayUsage.Read();
            var lastUsed = ReadLastUsed();

            // One pass over the process table for the whole list, rather than
            // one per row. This used to be hardcoded false, which meant the
            // palette never once offered to focus an app it could see was
            // already open -- it only ever offered to launch it again.
            var windowOwners = RunningApps.WindowOwners();
            var categories = CategoryMap.Build();

            // Where each auto-launching app falls in the launch sequence. Its
            // position, not its OrderIndex: OrderIndex spans every managed app,
            // so an app that starts fourth in a list of eight is "1st" if the
            // three above it do not launch at login. The number people care
            // about is when it starts, not where it sits.
            var startupOrder = _viewModel.ManagedApps
                .Where(a => a.LaunchOnStartup)
                .OrderBy(a => a.OrderIndex)
                .Select((a, index) => new { a.Id, Position = index + 1 })
                .ToDictionary(x => x.Id, x => x.Position);

            var apps = _viewModel.ManagedApps
                .OrderBy(a => a.OrderIndex)
                .Select(a =>
                {
                    int usedToday = today.TryGetValue(a.Name, out var span)
                        ? (int)span.TotalMinutes
                        : 0;
                    int bonus = a.BonusMinutesDate?.Date == DateTime.Today ? a.TodayBonusMinutes : 0;

                    return new
                    {
                        id = a.Id.ToString(),
                        name = a.DisplayNamePrimary,
                        category = CategoryMap.For(categories, a.Name),
                        hotkey = string.IsNullOrWhiteSpace(a.HotkeySequence) ? null : a.HotkeyDisplayText,
                        // Never shown before, and only this app can know it: a
                        // binding used twice in two months is not earning its keys.
                        hotkeyUses = a.HotkeyTriggerCount,
                        autoStart = a.LaunchOnStartup,
                        startupPosition = startupOrder.TryGetValue(a.Id, out var position) ? position : 0,
                        limitMinutes = a.DailyLimitMinutes,
                        limitRemaining = a.DailyLimitMinutes > 0
                            ? a.DailyLimitMinutes + bonus - usedToday
                            : 0,
                        isAction = a.IsAction,
                        lastUsed = lastUsed.TryGetValue(a.Name, out var seen) ? seen.Ticks : 0L,
                        today = span > TimeSpan.Zero ? FormatSpan(span) : "",
                        running = !a.IsAction && RunningApps.IsRunning(windowOwners, a.ExecutablePath)
                    };
                })
                .ToList();

            var commands = new List<object>
            {
                new { id = "manage",    title = "Manage applications", hint = "add, reorder, remove" },
                new { id = "scan",      title = "Scan for new applications", hint = "Start menu + Store" },
                new { id = "settings",  title = "Settings", hint = (string)null },
                new { id = "extend",    title = "Extend app time", hint = "needs your PIN" },
                new { id = "dashboard", title = "Open statistics dashboard", hint = "opens in browser ↗" }
            };

            // Sent whole rather than queried per keystroke: it is a few hundred
            // names, it changes about as often as you use a new program, and a
            // round trip per character would make the search feel worse than
            // not having it.
            var trackable = TrackedApps
                .Unmanaged(_viewModel.ManagedApps.Select(a => a.Name))
                .Select(c => new { name = c.Name, minutes = c.Minutes })
                .ToList();

            var payload = new
            {
                type = "state",
                state = new
                {
                    apps,
                    trackable,
                    commands,
                    focusToday = FormatSpan(focusTotal),
                    tracking = true,

                    // Things the first screen should raise rather than bury in
                    // Settings. Both are already detected; neither was shown
                    // anywhere you would look.
                    attention = new
                    {
                        startupConflict = _viewModel.HasStartupConflict,
                        startupConflictText = _viewModel.StartupConflictText,
                        updateReady = _viewModel.IsUpdateReadyToApply
                    }
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
        // window, but it must not fire during the show itself.
        //
        // Arming on OnActivated was not enough, and in fact defeated the delay
        // below: activation lands within a millisecond or two of Show(), so the
        // window was armed almost immediately and then dismissed itself on the
        // very next deactivation -- which routinely arrives while the summon is
        // still settling. Taking the foreground from another process, and the
        // hotkey's own keys coming back up, both produce one. The window
        // appeared and vanished, and looked like a hotkey that had not worked.
        //
        // So arming is not the whole guard: there is also a floor. Nothing
        // dismisses the window within SettleWindow of a summon, however armed
        // it is. After that the first genuine deactivation closes it.
        private bool _allowAutoHide;
        private DateTime _shownAtUtc = DateTime.MinValue;

        private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(450);

        /// <summary>
        /// Summon it. Hidden rather than closed, so this is instant.
        ///
        /// The view is posted after the reset, and the bridge delivers messages
        /// in order, so asking for Manage lands on Manage rather than flashing
        /// the search list on the way there.
        /// </summary>
        public void ShowPalette(PaletteView view = PaletteView.Search)
        {
            _allowAutoHide = false;
            _pinned = false;
            _shownAtUtc = DateTime.UtcNow;
            Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"reset\"}");
            if (view == PaletteView.Manage) ShowManage();
            else if (view == PaletteView.Settings) ShowSettings();
            else if (view == PaletteView.Extend) ShowExtend();

            Show();
            Activate();

            // Activate() alone loses to the foreground lock: whatever you were
            // using owns the foreground and Windows refuses to hand it to a
            // background process, so the palette appeared without the keyboard
            // and had to be clicked before it could be typed into.
            WindowFocus.Bring(new System.Windows.Interop.WindowInteropHelper(this).Handle);

            Web.Focus();

            // And the caret. Window focus does not put it in the search box:
            // that is inside the page, which has to be told once the control
            // actually holds focus -- hence after the two calls above rather
            // than as part of the reset that preceded them.
            Web.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"focus-input\"}");

            // Some foregrounds refuse to yield activation. Rather than sit
            // there un-dismissable, arm the auto-hide shortly after showing
            // even if Activated never arrives. The floor in OnDeactivated is
            // what actually protects the summon; this only guarantees the
            // window can always be dismissed.
            Dispatcher.BeginInvoke(new Action(PushState));
            _ = Task.Delay(SettleWindow).ContinueWith(_ =>
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
            // palette -- but one that closes before you have seen it is worse.
            if (!_allowAutoHide || _pinned || !IsVisible) return;
            if (DateTime.UtcNow - _shownAtUtc < SettleWindow) return;

            Hide();
        }

        // Escape is deliberately NOT handled here. WebView2 forwards it to the
        // host as an accelerator key, so this used to fire on every view and
        // hide the whole window -- which meant Escape in Settings or Manage
        // closed FastApp instead of going back, even though the page had
        // already handled it and moved to the previous view. The page knows
        // which view it is on; it sends "close" itself when there is nowhere
        // left to go back to.

        /// <summary>
        /// Open the dashboard, optionally at somewhere in particular.
        ///
        /// The window goes away first: it is about to lose the foreground to a
        /// browser anyway, and leaving it up behind the tab it just opened
        /// looks like the click did nothing.
        /// </summary>
        private void OpenDashboard(string query)
        {
            HidePalette();

            if (!DashboardServerService.IsRunning)
            {
                System.Windows.MessageBox.Show(
                    DashboardServerService.StatusMessage,
                    "Dashboard unavailable",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            OpenExternally(DashboardServerService.DashboardUrl + (query ?? string.Empty));
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
            public List<string> Paths { get; set; }
            public int Minutes { get; set; }
            public string Pin { get; set; }
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
