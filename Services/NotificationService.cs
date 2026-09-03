using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace FastApp.Services
{
    /// <summary>What kind of thing happened. Decides the toast's icon and tone.</summary>
    public enum NotificationSeverity
    {
        Info,
        Success,
        Warning
    }

    /// <summary>A button on the toast. <see cref="Id"/> comes back on activation.</summary>
    public sealed record NotificationAction(string Id, string Label);

    /// <summary>
    /// Windows toasts, sent through the AUMID of the Start Menu shortcut Velopack
    /// installs ("velopack.FastApp"). Toasts are what balloon tips have not been
    /// since Windows 10: they carry buttons, they survive in the Action Center,
    /// and clicking one can bring you somewhere useful.
    ///
    /// Activation is handled in-process, on the ToastNotification object, rather
    /// than through a registered COM activator. That is enough here because
    /// FastApp lives in the tray and is always running when its own notification
    /// is clicked; a COM activator only earns its complexity for an app that has
    /// to be launched by the click. The tradeoff is that a notification clicked
    /// from the Action Center after FastApp has exited does nothing.
    /// </summary>
    public static class NotificationService
    {
        private const string Aumid = "velopack.FastApp";

        private static NotifyIcon _sharedIcon;

        // Toasts are handed to the OS but activation is raised back on this
        // object, so dropping it means the event never arrives. Bounded so a
        // long-running session cannot accumulate them.
        private const int MaxTrackedToasts = 32;
        private static readonly Queue<ToastNotification> _live = new();
        private static readonly object _gate = new();

        /// <summary>Raised on the click, carrying the action's Id ("" for the body).</summary>
        public static event Action<string> ActionInvoked;

        /// <summary>Master switch. Enforcement still happens when off; only the telling stops.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// Inclusive start / exclusive end of a nightly window where nothing is
        /// shown. Null disables it. Stored as minutes past midnight so a window
        /// crossing midnight (22:00-07:00) is just a wrapped comparison.
        /// </summary>
        public static int? QuietFromMinutes { get; set; }
        public static int? QuietToMinutes { get; set; }

        public static void RegisterTrayIcon(NotifyIcon icon) => _sharedIcon = icon;

        internal static bool InQuietHours(DateTime now)
        {
            if (QuietFromMinutes is not int from || QuietToMinutes is not int to) return false;
            if (from == to) return false;

            int m = now.Hour * 60 + now.Minute;
            return from < to
                ? m >= from && m < to        // 09:00-17:00
                : m >= from || m < to;       // 22:00-07:00, wrapping midnight
        }

        /// <param name="force">
        /// Show this even with notifications switched off or during quiet hours.
        /// Reserved for faults the user has to know about -- being unable to save
        /// their data, chiefly -- because those settings mean "do not interrupt
        /// me about ordinary things", not "hide it when the app breaks".
        /// </param>
        public static void Show(
            string title,
            string message,
            NotificationSeverity severity = NotificationSeverity.Info,
            IReadOnlyList<NotificationAction> actions = null,
            bool force = false)
        {
            if (!force && (!Enabled || InQuietHours(DateTime.Now))) return;

            if (TryShowToast(title, message, severity, actions)) return;

            ShowBalloonFallback(title, message, severity);
        }

        private static bool TryShowToast(
            string title,
            string message,
            NotificationSeverity severity,
            IReadOnlyList<NotificationAction> actions)
        {
            try
            {
                var xml = new XmlDocument();
                xml.LoadXml(BuildToastXml(title, message, severity, actions));

                var toast = new ToastNotification(xml);

                toast.Activated += (sender, args) =>
                {
                    // The body of the toast reports no argument; a button reports
                    // whatever went into its arguments attribute.
                    string id = (args as ToastActivatedEventArgs)?.Arguments ?? string.Empty;
                    try { ActionInvoked?.Invoke(id); }
                    catch { /* a bad handler must not take down the notifier */ }
                    Forget(toast);
                };
                toast.Dismissed += (sender, args) => Forget(toast);
                toast.Failed += (sender, args) => Forget(toast);

                Remember(toast);
                ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
                return true;
            }
            catch
            {
                // No shortcut, no AUMID, notifications disabled by policy, an
                // older Windows -- any of these and the balloon still works.
                return false;
            }
        }

        private static string BuildToastXml(
            string title,
            string message,
            NotificationSeverity severity,
            IReadOnlyList<NotificationAction> actions)
        {
            var sb = new StringBuilder();
            sb.Append("<toast activationType='foreground'><visual><binding template='ToastGeneric'>");
            sb.Append($"<text>{Escape(title)}</text>");
            sb.Append($"<text>{Escape(message)}</text>");
            sb.Append("</binding></visual>");

            // Warnings get the alarm scenario so they survive longer on screen;
            // everything else stays a normal, self-dismissing toast.
            if (actions is { Count: > 0 })
            {
                sb.Append("<actions>");
                foreach (var action in actions.Take(5))
                {
                    sb.Append($"<action content='{Escape(action.Label)}' " +
                              $"arguments='{Escape(action.Id)}' activationType='foreground'/>");
                }
                sb.Append("</actions>");
            }

            sb.Append("</toast>");

            string audio = severity == NotificationSeverity.Warning
                ? "<audio src='ms-winsoundevent:Notification.Looping.Alarm2' loop='false'/>"
                : "<audio src='ms-winsoundevent:Notification.Default'/>";

            return sb.ToString().Replace("</toast>", audio + "</toast>");
        }

        private static void Remember(ToastNotification toast)
        {
            lock (_gate)
            {
                _live.Enqueue(toast);
                while (_live.Count > MaxTrackedToasts) _live.Dequeue();
            }
        }

        private static void Forget(ToastNotification toast)
        {
            lock (_gate)
            {
                if (_live.Count == 0) return;
                var kept = _live.Where(t => !ReferenceEquals(t, toast)).ToList();
                _live.Clear();
                foreach (var t in kept) _live.Enqueue(t);
            }
        }

        /// <summary>
        /// The old balloon-tip path, kept only for when a real toast cannot be
        /// sent. It anchors to the tray icon rather than creating its own: a
        /// throwaway NotifyIcon disposed straight after ShowBalloonTip() takes
        /// the icon away before Windows has drawn the balloon, so nothing ever
        /// appeared -- that was the original bug this file was written for.
        /// </summary>
        private static void ShowBalloonFallback(string title, string message, NotificationSeverity severity)
        {
            var icon = severity switch
            {
                NotificationSeverity.Warning => ToolTipIcon.Warning,
                NotificationSeverity.Success => ToolTipIcon.Info,
                _ => ToolTipIcon.Info
            };

            if (_sharedIcon != null)
            {
                _sharedIcon.ShowBalloonTip(5000, title, message, icon);
                return;
            }

            // Practically unreachable: the tray icon is registered before any
            // code path that notifies. Deliberately not disposed, per above.
            var fallbackIcon = new NotifyIcon { Icon = SystemIcons.Warning, Visible = true };
            fallbackIcon.ShowBalloonTip(5000, title, message, icon);
        }

        private static string Escape(string value) =>
            System.Security.SecurityElement.Escape(value ?? string.Empty);
    }
}
