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

        /// <summary>
        /// The AppSettings key holding the choice. In the table rather than in
        /// a text file beside the database, so the dashboard can see it too.
        /// </summary>
        public const string PreferenceKey = "ThemePreference";

        public const string FollowSystem = "system";
        public const string AlwaysDark = "dark";
        public const string AlwaysLight = "light";

        private static bool _isLight;
        private static bool _started;
        private static string _preference = FollowSystem;

        /// <summary>True when the app should draw itself light.</summary>
        public static bool IsLight => _isLight;

        /// <summary>
        /// Which of the three the person chose: follow Windows, or override it
        /// in either direction.
        ///
        /// Following the OS is the right default and is what 3.0.0 shipped, but
        /// it is not the same thing as a preference. Plenty of people run
        /// Windows light and still want one dark window, or the reverse, and a
        /// tracker that sits on top of whatever you are doing is exactly the
        /// kind of app that gets an exception made for it.
        /// </summary>
        public static string Preference => _preference;

        /// <summary>Raised on the UI thread after <see cref="IsLight"/> changes.</summary>
        public static event Action Changed;

        public static void Start()
        {
            if (_started) return;
            _started = true;

            // Only when there is a database to read. This runs from OnStartup,
            // before the view model has migrated anything, and SQLite opens
            // read-write-create: a settings read is not a reason to bring an
            // empty file into existence ahead of its own schema. On a first run
            // there is no preference to find anyway.
            _preference = System.IO.File.Exists(AppDbContext.GetDbPath())
                ? Normalise(AppSettingsStore.Get(PreferenceKey, FollowSystem))
                : FollowSystem;
            _isLight = Resolve();

            // The registry has no managed change notification for a value, and
            // a raw RegNotifyChangeKeyValue thread is more machinery than this
            // deserves: Windows also broadcasts the change as a user-preference
            // event, which WPF surfaces here and delivers on the UI thread.
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category != UserPreferenceCategory.General) return;

                // Still worth listening while overridden: the person can switch
                // back to Follow Windows at any point, and _isLight has to be
                // right the moment they do rather than one OS change later.
                bool now = Resolve();
                if (now == _isLight) return;

                _isLight = now;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
            };
        }

        /// <summary>
        /// Record a new choice and, if it changes how the app should look, tell
        /// everyone who draws itself by hand.
        ///
        /// Saved before the event goes out: a listener that reads the store
        /// back -- the dashboard does -- must not see the old value.
        /// </summary>
        public static void SetPreference(string preference)
        {
            string next = Normalise(preference);
            if (next == _preference) return;

            _preference = next;
            AppSettingsStore.Set(PreferenceKey, next);

            bool light = Resolve();
            if (light == _isLight) return;

            _isLight = light;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
        }

        /// <summary>
        /// Anything unrecognised means follow Windows. The value is read back
        /// out of a database anyone can edit, and an unknown string is not a
        /// reason to render an app nobody can read.
        /// </summary>
        private static string Normalise(string preference) =>
            preference == AlwaysDark ? AlwaysDark
            : preference == AlwaysLight ? AlwaysLight
            : FollowSystem;

        private static bool Resolve() =>
            _preference == AlwaysLight ? true
            : _preference == AlwaysDark ? false
            : ReadIsLight();

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
