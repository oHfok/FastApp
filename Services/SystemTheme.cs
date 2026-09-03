using System;
using System.Windows;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace FastApp.Services
{
    /// <summary>
    /// Whether Windows is set to light or dark, and telling anyone who asked
    /// when that changes.
    ///
    /// FastApp had no idea: WPF-UI was pinned to Dark, the palette's window
    /// background was the literal #0A0B10, and the tray menu carried its own
    /// copy of the dark palette. A person running Windows in Light got a
    /// permanently dark application with no setting to change it.
    ///
    /// The web surfaces do this for themselves through prefers-color-scheme,
    /// which the browser and WebView2 both answer correctly. This exists for
    /// the parts of the app that are not a web page: the window behind the
    /// WebView2, the tray menu, and the two overlay popups.
    /// </summary>
    public static class SystemTheme
    {
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string AppsUseLightValue = "AppsUseLightTheme";

        private static bool _isLight;
        private static bool _started;

        /// <summary>True when Windows is set to light for applications.</summary>
        public static bool IsLight => _isLight;

        /// <summary>Raised on the UI thread after <see cref="IsLight"/> changes.</summary>
        public static event Action Changed;

        public static void Start()
        {
            if (_started) return;
            _started = true;

            _isLight = ReadIsLight();

            // The registry has no managed change notification for a value, and
            // a raw RegNotifyChangeKeyValue thread is more machinery than this
            // deserves: Windows also broadcasts the change as a user-preference
            // event, which WPF surfaces here and delivers on the UI thread.
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category != UserPreferenceCategory.General) return;

                bool now = ReadIsLight();
                if (now == _isLight) return;

                _isLight = now;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
            };
        }

        private static bool ReadIsLight()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                // Absent means dark: the value is only written once someone has
                // been to the personalisation settings, and Windows' own default
                // for apps is dark when it is missing on the editions that omit
                // it.
                return key?.GetValue(AppsUseLightValue) is int v && v != 0;
            }
            catch
            {
                // A locked-down or roaming profile can refuse the read. Dark is
                // what the app has always been, so it is the safe answer.
                return false;
            }
        }
    }
}
