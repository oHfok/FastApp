using FastApp;
using System;
using System.Windows.Forms; // We are using the WinForms namespace here

namespace FastApp.Services
{
    public class TrayService : IDisposable
    {
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

            // When you double-click the icon, show the app
            _notifyIcon.DoubleClick += (s, e) => ShowWindow();

            // Create a right-click menu
            _notifyIcon.ContextMenuStrip = new ContextMenuStrip();
            _notifyIcon.ContextMenuStrip.Items.Add("Open Manager", null, (s, e) => ShowWindow());
            _notifyIcon.ContextMenuStrip.Items.Add("-"); // Adds a separator line
            _notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => ExitApplication());
        }

        private void ShowWindow()
        {
            _mainWindow.Show();
            _mainWindow.WindowState = System.Windows.WindowState.Normal;
            _mainWindow.Activate(); // Brings it to the front
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