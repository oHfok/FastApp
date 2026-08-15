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

            // Toasts (daily-limit warnings/kills) anchor to this same persistent
            // icon instead of spinning up their own — see NotificationService.
            NotificationService.RegisterTrayIcon(_notifyIcon);

            // When you double-click the icon, show the app
            _notifyIcon.DoubleClick += (s, e) => ShowWindow();

            // Create a right-click menu
            _notifyIcon.ContextMenuStrip = new ContextMenuStrip();
            _notifyIcon.ContextMenuStrip.Items.Add("Open Manager", null, (s, e) => ShowWindow());
            _notifyIcon.ContextMenuStrip.Items.Add("View Stats", null, (s, e) => OpenDashboard());
            _notifyIcon.ContextMenuStrip.Items.Add("Extend App Time…", null, (s, e) => ShowExtendDialog());
            _notifyIcon.ContextMenuStrip.Items.Add("-"); // Adds a separator line
            _notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => ExitApplication());
        }

        private void ShowWindow()
        {
            _mainWindow.Show();
            _mainWindow.WindowState = System.Windows.WindowState.Normal;
            _mainWindow.Activate(); // Brings it to the front
        }

        private void OpenDashboard()
        {
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
            // Removes the icon from the taskbar when the app finally dies
            _notifyIcon?.Dispose();
        }
    }
}