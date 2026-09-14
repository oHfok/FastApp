using System;
using Microsoft.Win32;

namespace FastApp.Services
{
    /// <summary>
    /// When the screen was actually locked, as start/end timestamps -- kept
    /// entirely separate from AFK. SystemIdleTracker's isAfk is built purely
    /// on GetLastInputInfo (physical input idle time) plus its own fullscreen/
    /// media exemptions; it cannot tell "no input because idle at an unlocked
    /// desktop" apart from "no input because the screen is locked", and both
    /// already fold into the same AFK bucket. This doesn't change that -- a
    /// locked screen is still correctly AFK time -- it just adds the one bit
    /// AFK alone can't answer: was the machine actually secured, or just
    /// unattended. Same loose-table shape as AfkIntervalStore/PauseIntervalStore.
    /// </summary>
    public static class SystemLockTracker
    {
        private const string Table = "LockIntervals";
        private static DateTime? _lockedAt;
        private static bool _started;

        public static string CreateTableSql => IntervalStore.CreateTableSql(Table);

        /// <summary>
        /// Hooks Microsoft.Win32.SystemEvents.SessionSwitch once for the life
        /// of the process. Idempotent -- safe to call more than once.
        /// </summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    _lockedAt = DateTime.Now;
                    break;

                case SessionSwitchReason.SessionUnlock:
                    if (_lockedAt.HasValue)
                    {
                        IntervalStore.RecordInterval(Table, _lockedAt.Value, DateTime.Now);
                        _lockedAt = null;
                    }
                    break;
            }
        }
    }
}
