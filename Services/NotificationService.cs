using System.Drawing;
using System.Windows.Forms;

namespace FastApp.Services
{
    public static class NotificationService
    {
        public static void ShowToast(string title, string message)
        {
            // Creates a native Windows tray balloon tip that pops up over all windows
            using var notifyIcon = new NotifyIcon();
            notifyIcon.Icon = SystemIcons.Warning;
            notifyIcon.Visible = true;
            notifyIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Warning);
        }
    }
}