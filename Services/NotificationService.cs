using System.Drawing;
using System.Windows.Forms;

namespace FastApp.Services
{
    public static class NotificationService
    {
        // Set once by TrayService at startup so toasts anchor to the app's real,
        // persistently-visible tray icon. Creating a throwaway NotifyIcon per call
        // and disposing it right after ShowBalloonTip() (the old approach) yanks
        // the icon away before Windows finishes rendering the balloon, so it never
        // actually appeared — that was the bug.
        private static NotifyIcon _sharedIcon;

        public static void RegisterTrayIcon(NotifyIcon icon)
        {
            _sharedIcon = icon;
        }

        public static void ShowToast(string title, string message)
        {
            if (_sharedIcon != null)
            {
                _sharedIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Warning);
                return;
            }

            // Fallback for the (practically unreachable) case this fires before the
            // tray icon exists. Deliberately not disposed afterward — an early
            // dispose is exactly the bug this file exists to avoid.
            var fallbackIcon = new NotifyIcon { Icon = SystemIcons.Warning, Visible = true };
            fallbackIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Warning);
        }
    }
}
