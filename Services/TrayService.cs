using FastApp;
using System;
using System.Diagnostics;
using System.Windows.Forms; // We are using the WinForms namespace here

namespace FastApp.Services
{
    public class TrayService : IDisposable
    {
        private NotifyIcon _notifyIcon;
        private MainWindow _mainWindow;
        private ToolStripMenuItem _status;
        private ToolStripMenuItem _pause;
        private ToolStripMenuItem _open;

        public TrayService(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;

            // Create the system tray icon
            _notifyIcon = new NotifyIcon();

            // FIXED: Using Environment.ProcessPath guarantees the .exe path is found, even when Published!
            _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            // The tooltip is the one place the summon combination can be found by
            // someone who was never told it, and it used to read "FastApp
            // Manager" -- a name, at the exact moment a person is hovering the
            // icon wondering how to open the thing. NotifyIcon.Text caps at 63
            // characters, which this is comfortably inside.
            _notifyIcon.Text = $"FastApp  ·  {MainWindow.PaletteHotkeyDisplay} to open";

            _notifyIcon.Visible = true;

            // The balloon fallback anchors to this same persistent icon instead
            // of spinning up its own — see NotificationService.
            NotificationService.RegisterTrayIcon(_notifyIcon);

            // Toast buttons come back here. They deliberately reuse the tray's
            // own handlers rather than reimplementing anything: "Extend time"
            // opens the PIN-gated dialog, it does not grant time.
            NotificationService.ActionInvoked += OnNotificationAction;

            // When you double-click the icon, show the app
            _notifyIcon.DoubleClick += (s, e) => ShowPalette();

            _notifyIcon.ContextMenuStrip = BuildMenu();
        }

        /// <summary>
        /// The right-click menu.
        ///
        /// Grouped rather than listed: a status line, then the three ways into
        /// the app, then the two things you can do without opening it, then
        /// Exit on its own so it is never the neighbour of anything you meant
        /// to click. Manage and Settings are new here -- both were reachable
        /// only by summoning the palette and then navigating, which is two
        /// steps too many for the surface whose entire job is shortcuts.
        /// </summary>
        /// <summary>
        /// Re-read the summon combination into the places that quote it. Both
        /// the tooltip and the first menu line are built once at login, so
        /// changing the shortcut left them advising the old one until restart.
        /// </summary>
        public void RefreshHotkeyText()
        {
            try
            {
                _notifyIcon.Text = $"FastApp  ·  {MainWindow.PaletteHotkeyDisplay} to open";
                if (_open != null) _open.ShortcutKeyDisplayString = MainWindow.PaletteHotkeyDisplay;
            }
            catch { /* a tooltip is not worth an exception */ }
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            TrayMenuTheme.Apply(menu);

            _status = TrayMenuTheme.Header(string.Empty);

            // Pausing belongs here above all: the whole point is to stop
            // recording without opening anything, and the tray is the surface
            // you can already reach.
            _pause = TrayMenuTheme.Item("Pause tracking", (s, e) =>
            {
                // Only reachable while paused: with the durations hidden there
                // is no submenu to open, so the click lands here.
                if (_mainWindow?.ViewModel?.IsTrackingPaused == true)
                    _mainWindow.ViewModel.ResumeTracking();
            });
            _pause.DropDownItems.Add(TrayMenuTheme.Item("For 30 minutes",
                (s, e) => Pause(TimeSpan.FromMinutes(30))));
            _pause.DropDownItems.Add(TrayMenuTheme.Item("For 2 hours",
                (s, e) => Pause(TimeSpan.FromHours(2))));
            _pause.DropDownItems.Add(TrayMenuTheme.Item("Until I turn it back on",
                (s, e) => Pause(null)));
            TrayMenuTheme.Apply(_pause.DropDown as ToolStripDropDownMenu);

            // Read on open rather than on a timer: the figure is only ever
            // looked at in the second the menu is up, and polling the database
            // for a line nobody is reading would be worse than useless.
            menu.Opening += (s, e) =>
            {
                _status.Text = StatusLine();
                RefreshPauseItem();
                TrayMenuTheme.Refresh(menu);
            };

            menu.Items.AddRange(new ToolStripItem[]
            {
                _status,
                new ToolStripSeparator(),
                _open = TrayMenuTheme.Item("Open FastApp", (s, e) => ShowPalette(),
                    MainWindow.PaletteHotkeyDisplay),
                TrayMenuTheme.Item("Manage applications", (s, e) => ShowPalette(PaletteView.Manage)),
                TrayMenuTheme.Item("Settings", (s, e) => ShowPalette(PaletteView.Settings)),
                new ToolStripSeparator(),
                _pause,
                TrayMenuTheme.Item("Statistics dashboard", (s, e) => OpenDashboard()),
                TrayMenuTheme.Item("Extend app time…", (s, e) => ShowExtendDialog()),
                new ToolStripSeparator(),
                TrayMenuTheme.Item("Exit FastApp", (s, e) => ExitApplication())
            });

            return menu;
        }

        /// <summary>
        /// What the app is doing, in one line: the version you are actually
        /// running, and how much has been tracked today. Both are questions the
        /// tray used to answer only by opening something.
        /// </summary>
        private string StatusLine()
        {
            string version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3) ?? "?";
            var (_, total) = TodayUsage.Read();

            // A paused app says so here first. "2h 14m today" beside a stopped
            // tracker reads as though it is still counting.
            var viewModel = _mainWindow?.ViewModel;
            if (viewModel != null && viewModel.IsTrackingPaused)
                return $"FastApp {version}  ·  {viewModel.PauseDescription}";

            return $"FastApp {version}  ·  {TodayUsage.Describe(total)}";
        }

        /// <summary>
        /// One item that is either the pause or the way out of it, rather than
        /// two that contradict each other.
        /// </summary>
        private void RefreshPauseItem()
        {
            var viewModel = _mainWindow?.ViewModel;
            bool paused = viewModel?.IsTrackingPaused == true;

            _pause.DropDown.Visible = false;
            _pause.Text = paused ? "Resume tracking" : "Pause tracking";

            // A resume has nothing to choose, so the submenu goes away rather
            // than offering durations for something already stopped.
            foreach (ToolStripItem item in _pause.DropDownItems) item.Available = !paused;
        }

        private void Pause(TimeSpan? duration)
        {
            _mainWindow?.ViewModel?.PauseTracking(duration);
        }

        private void OnNotificationAction(string actionId)
        {
            // Raised on a WinRT callback thread; every handler below touches UI.
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                switch (actionId)
                {
                    case "extend":
                        ShowExtendDialog();
                        break;
                    case "dashboard":
                        OpenDashboard();
                        break;
                    // The toast body itself reports no argument, and the only
                    // sensible thing a bare click can mean is "show me".
                    case "show-window":
                    case "":
                        ShowPalette();
                        break;
                }
            });
        }

        private void ShowPalette(PaletteView view = PaletteView.Search)
        {
            _mainWindow.ShowPalette(view);
        }

        private void OpenDashboard()
        {
            // Opening a browser tab at a server that never started just produces a
            // confusing connection error with no hint of the real cause, so say
            // what actually went wrong instead.
            if (!DashboardServerService.IsRunning)
            {
                System.Windows.MessageBox.Show(
                    DashboardServerService.StatusMessage,
                    "Dashboard unavailable",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            try
            {
                // UseShellExecute:true is required on .NET Core+ to let the OS
                // hand a URL to the default browser — Process.Start(url) alone
                // throws Win32Exception here.
                Process.Start(new ProcessStartInfo
                {
                    FileName = DashboardServerService.DashboardUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Best-effort — nothing else to fall back to from a tray click.
            }
        }

        // Reachable without a browser — this is the whole point: it has to still
        // work when browser access is restricted, which includes the case where
        // the app being limited is the browser. It used to be a WinForms dialog
        // for that reason; the palette is FastApp's own window and satisfies the
        // same requirement, on the same design as everything else.
        private void ShowExtendDialog() => ShowPalette(PaletteView.Extend);

        private void ExitApplication()
        {
            // Call our custom shutdown method on the main window
            _mainWindow.ForceExit();
        }

        public void Dispose()
        {
            NotificationService.ActionInvoked -= OnNotificationAction;

            // Removes the icon from the taskbar when the app finally dies
            _notifyIcon?.Dispose();
        }
    }
}