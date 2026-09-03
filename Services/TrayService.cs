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

        public TrayService(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;

            // Create the system tray icon
            _notifyIcon = new NotifyIcon();

            // FIXED: Using Environment.ProcessPath guarantees the .exe path is found, even when Published!
            _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            _notifyIcon.Text = "FastApp Manager"; // Updated name just in case!
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
        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            TrayMenuTheme.Apply(menu);

            _status = TrayMenuTheme.Header(string.Empty);

            // Read on open rather than on a timer: the figure is only ever
            // looked at in the second the menu is up, and polling the database
            // for a line nobody is reading would be worse than useless.
            menu.Opening += (s, e) => _status.Text = StatusLine();

            menu.Items.AddRange(new ToolStripItem[]
            {
                _status,
                new ToolStripSeparator(),
                TrayMenuTheme.Item("Open FastApp", (s, e) => ShowPalette(),
                    MainWindow.PaletteHotkeyDisplay),
                TrayMenuTheme.Item("Manage applications", (s, e) => ShowPalette(PaletteView.Manage)),
                TrayMenuTheme.Item("Settings", (s, e) => ShowPalette(PaletteView.Settings)),
                new ToolStripSeparator(),
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
        private static string StatusLine()
        {
            string version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3) ?? "?";
            var (_, total) = TodayUsage.Read();
            return $"FastApp {version}  ·  {TodayUsage.Describe(total)}";
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

        // Reachable without opening the main window or a browser — this is the
        // whole point: it has to still work even if browser access is restricted.
        // ViewModel is read lazily here (not captured at TrayService construction
        // time), since the tray is created before the ViewModel is.
        private void ShowExtendDialog()
        {
            var viewModel = _mainWindow.ViewModel;
            if (viewModel == null) return;

            using var dialog = new ExtendTimeDialog(viewModel);
            dialog.ShowDialog();
        }

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