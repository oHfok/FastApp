using FastApp;
using System;
using System.Diagnostics;
using System.Windows.Forms; // We are using the WinForms namespace here

namespace FastApp.Services
{
    public class TrayService : IDisposable
    {
        // Matches DashboardServerService's builder.WebHost.UseUrls(...). There's
        // no default-file mapping on the server, so this has to point at the
        // actual file, not just the bare origin.
        private const string DashboardUrl = "http://127.0.0.1:5050/dashboard.html";

        private NotifyIcon _notifyIcon;
        private MainWindow _mainWindow;

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

            // Create a right-click menu
            _notifyIcon.ContextMenuStrip = new ContextMenuStrip();
            _notifyIcon.ContextMenuStrip.Items.Add("Open FastApp", null, (s, e) => ShowPalette());
            _notifyIcon.ContextMenuStrip.Items.Add("View Stats", null, (s, e) => OpenDashboard());
            _notifyIcon.ContextMenuStrip.Items.Add("Extend App Time…", null, (s, e) => ShowExtendDialog());
            _notifyIcon.ContextMenuStrip.Items.Add("-"); // Adds a separator line
            _notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => ExitApplication());
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

        private void ShowPalette()
        {
            _mainWindow.ShowPalette();
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
                Process.Start(new ProcessStartInfo { FileName = DashboardUrl, UseShellExecute = true });
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