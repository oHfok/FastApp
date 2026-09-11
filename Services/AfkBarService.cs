using System;
using System.Windows.Threading;

namespace FastApp.Services
{
    /// <summary>
    /// Shows or hides the "AFK DETECTED" tag at the top of the screen. Called
    /// once per tracker tick (~5s) with that tick's AFK verdict; the tracker
    /// runs on its own background thread and has no view model binding to
    /// reach for, so <see cref="Enabled"/> mirrors MainViewModel.ShowAfkBar the
    /// same way NotificationService.Enabled mirrors NotificationsEnabled -- set
    /// from the UI thread whenever the setting changes, read from the tracker
    /// thread on every tick.
    /// </summary>
    public static class AfkBarService
    {
        private static AfkBarWindow _window;
        private static bool _visible;
        private static DispatcherTimer _fastHideTimer;

        /// <summary>Whether the setting is on. Off by default.</summary>
        public static bool Enabled { get; set; }

        /// <summary>
        /// Called every tick with whether this tick is AFK. A no-op when the
        /// wanted visibility already matches what's on screen -- without that
        /// guard this would re-run Show() every five seconds for the entire
        /// length of someone being away, for no visible difference.
        /// </summary>
        public static void SetVisible(bool afk)
        {
            bool want = Enabled && afk;
            if (want == _visible) return;

            System.Windows.Application.Current?.Dispatcher.Invoke(() => Apply(want));
        }

        // Runs on the UI thread already -- either marshalled by SetVisible
        // above, or called directly by the fast-hide timer below, which was
        // itself started on this thread -- so no further Dispatcher hop here.
        private static void Apply(bool want)
        {
            _visible = want;
            _window ??= new AfkBarWindow();

            if (want)
            {
                _window.ShowBar();
                // Set once immediately rather than waiting for the timer's
                // first tick, so the bar never shows a stale duration (or the
                // XAML's placeholder text) for the first quarter-second.
                _window.SetDuration(SystemIdleTracker.GetIdleTime());
                (_fastHideTimer ??= MakeFastHideTimer()).Start();
            }
            else
            {
                _window.HideBar();
                _fastHideTimer?.Stop();
            }
        }

        // The tracker's own 5-second tick is what decides you've gone AFK, but
        // it was also the only thing that noticed you'd come back -- so typing
        // a key the instant after the bar appeared still left it up for up to
        // five more seconds. GetIdleTime() is a synchronous, direct Win32 read
        // (no WinRT media-session query behind it), so polling it far more
        // often than the tracker tick costs nothing. The same reading now also
        // drives the bar's "how long" duration text, since the two questions
        // ("are you back yet" and "how long has this been going on") turn out
        // to want the same number.
        //
        // Hiding early is still the only direction this timer is allowed to
        // decide on its own: becoming AFK still waits for the tracker's own
        // tick, which also weighs the fullscreen/media exemptions a raw idle
        // reading knows nothing about, and premature-AFK, not late-AFK, is the
        // direction that would actually be wrong to shortcut.
        private static DispatcherTimer MakeFastHideTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, __) =>
            {
                if (!_visible) return;

                var idle = SystemIdleTracker.GetIdleTime();
                if (idle < SystemIdleTracker.AfkThreshold) { Apply(false); return; }

                _window.SetDuration(idle);
            };
            return timer;
        }
    }
}
