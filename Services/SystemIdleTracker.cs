using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace FastApp.Services
{
    public static class SystemIdleTracker
    {
        // --- 1. EXISTING INPUT TRACKER ---
        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        // --- 2. NOTIFICATION STATE API ---
        [DllImport("shell32.dll")]
        static extern int SHQueryUserNotificationState(out USER_NOTIFICATION_STATE pquns);

        public enum USER_NOTIFICATION_STATE
        {
            QUNS_NOT_PRESENT = 1,
            QUNS_BUSY = 2,
            QUNS_RUNNING_D3D_FULL_SCREEN = 3,
            QUNS_PRESENTATION_MODE = 4,
            QUNS_ACCEPTS_NOTIFICATIONS = 5,
            QUNS_QUIET_TIME = 6,
            QUNS_APP = 7
        }

        // Returns the raw idle time based on physical mouse/keyboard movement
        public static TimeSpan GetIdleTime()
        {
            var lastInputInfo = new LASTINPUTINFO();
            lastInputInfo.cbSize = (uint)Marshal.SizeOf(lastInputInfo);

            if (GetLastInputInfo(ref lastInputInfo))
            {
                uint idleTicks = (uint)Environment.TickCount - lastInputInfo.dwTime;
                return TimeSpan.FromMilliseconds(idleTicks);
            }
            return TimeSpan.Zero;
        }

        // Checks if Windows is in Fullscreen Presentation or Gaming mode
        private static bool IsUserPassivelyEngaged()
        {
            if (SHQueryUserNotificationState(out USER_NOTIFICATION_STATE state) == 0) // S_OK
            {
                if (state == USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE ||
                    state == USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN)
                {
                    return true;
                }
            }
            return false;
        }

        // --- 3. NEW: MODERN MEDIA TRACKER ---
        // Checks if Spotify, YouTube, Netflix, etc., is currently playing media
        private static async Task<bool> IsMediaPlayingAsync()
        {
            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var session = manager.GetCurrentSession();

                if (session != null)
                {
                    var playbackInfo = session.GetPlaybackInfo();
                    if (playbackInfo != null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Failsafes if the OS doesn't support it or COM object gets disconnected
            }
            return false;
        }

        // The fullscreen/media exemptions below are meant to stop a video or a
        // game from getting wrongly marked AFK during the normal few-minutes-of-
        // no-input window — not to grant indefinite immunity. Without a ceiling,
        // something like a Discord call left running (which can register as an
        // active "Playing" media session for as long as it's open) would keep
        // reporting "not AFK" forever even after you've actually left. Nobody
        // genuinely engaged produces zero physical input for this long — even
        // watching a video, people click, scroll, or type in chat sometimes — so
        // past this point it's AFK regardless of what fullscreen/media state says.
        private static readonly TimeSpan HardIdleCeiling = TimeSpan.FromMinutes(30);

        // --- THE MASTER AFK CHECK ---
        public static async Task<bool> IsTrulyAfkAsync(TimeSpan afkThreshold)
        {
            TimeSpan idleTime = GetIdleTime();

            // 1. Fast Check: Physical mouse/keyboard inputs
            if (idleTime < afkThreshold)
                return false;

            // 1.5. Hard ceiling: no exemption survives this long with zero input
            if (idleTime >= HardIdleCeiling)
                return true;

            // 2. Fast Check: Fullscreen video or DirectX Game
            if (IsUserPassivelyEngaged())
                return false;

            // 3. Deep Check: Active Windowed Media (YouTube, Spotify, etc.)
            if (await IsMediaPlayingAsync())
                return false;

            // If we made it here, they are completely idle and consuming no media.
            return true;
        }
    }
}