namespace FastApp.Services
{
    /// <summary>
    /// Shows or hides the full-width "AFK DETECTED" strip at the top of the
    /// screen. Called once per tracker tick with the tick's AFK verdict; the
    /// tracker runs on its own background thread and has no view model
    /// binding to reach for, so <see cref="Enabled"/> mirrors
    /// MainViewModel.ShowAfkBar the same way NotificationService.Enabled
    /// mirrors NotificationsEnabled -- set from the UI thread whenever the
    /// setting changes, read from the tracker thread on every tick.
    /// </summary>
    public static class AfkBarService
    {
        private static AfkBarWindow _window;
        private static bool _visible;

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
            _visible = want;

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _window ??= new AfkBarWindow();
                if (want) _window.ShowBar();
                else _window.HideBar();
            });
        }
    }
}
