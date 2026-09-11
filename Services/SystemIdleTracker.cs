using System;
using System.Collections.Generic;
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

        /// <summary>
        /// The SourceAppUserModelId of every media session currently reporting
        /// Playing, as a case-insensitive set. Empty on any failure.
        ///
        /// Walks every session, not just the OS's notion of the "current" one,
        /// so the tracker can tell <em>which</em> app is producing sound —
        /// used both to attribute music-listening time to that app rather than
        /// to whatever is in the foreground, and by the AFK check below to tell
        /// a video or a call (which should hold "away" off) apart from music
        /// (which shouldn't). The id is "Spotify.exe" for the Spotify desktop
        /// app, a package family name for Store apps, "chrome"/"msedge" for a
        /// browser tab, and so on.
        /// </summary>
        public static async Task<HashSet<string>> GetPlayingMediaSourcesAsync()
        {
            var playing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                foreach (var session in manager.GetSessions())
                {
                    try
                    {
                        var info = session.GetPlaybackInfo();
                        if (info != null
                            && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            string source = session.SourceAppUserModelId;
                            if (!string.IsNullOrWhiteSpace(source))
                                playing.Add(source);
                        }
                    }
                    catch { /* a session disconnected mid-enumeration */ }
                }
            }
            catch { /* OS without the API, or the manager itself is unavailable */ }
            return playing;
        }

        // --- CONFIGURABLE THRESHOLDS ---
        // Both default to the values that were hard-coded here before Settings
        // could reach them. MainViewModel loads the stored values from
        // AppSettings at startup and writes them back on every change, so the
        // tracker loop picks up a new value on its next 5-second tick with no
        // restart. Written from the UI thread, read from the tracker thread —
        // the same single-writer/single-reader sharing NotificationService
        // already does with its quiet-hours minutes.

        /// <summary>
        /// How long with no physical mouse or keyboard input before the user is
        /// considered away, subject to the fullscreen/media exemptions below.
        /// Default 5 minutes.
        /// </summary>
        public static TimeSpan AfkThreshold { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// The fullscreen/media exemptions are meant to stop a video or a game
        /// from being wrongly marked AFK during the normal few-minutes-of-no-
        /// input window — not to grant indefinite immunity. Without a ceiling,
        /// something like a Discord call left running (which can register as an
        /// active "Playing" media session for as long as it's open) would keep
        /// reporting "not AFK" forever even after you've actually left. Nobody
        /// genuinely engaged produces zero physical input for this long — even
        /// watching a video, people click, scroll, or type in chat sometimes —
        /// so past this point it's AFK regardless of fullscreen/media state.
        /// Default 30 minutes; never usefully below <see cref="AfkThreshold"/>,
        /// which MainViewModel enforces before setting it.
        /// </summary>
        public static TimeSpan PassiveMediaGracePeriod { get; set; } = TimeSpan.FromMinutes(30);

        // --- THE MASTER AFK CHECK ---
        //
        // nonMusicMediaPlaying is computed by the caller from
        // GetPlayingMediaSourcesAsync() plus which of those sources resolve to
        // a Music-category app (MainViewModel has the category map and the
        // running-process names this needs; this class doesn't). Music is
        // deliberately excluded from the media exemption below: you can have
        // Spotify going without being at the machine in any sense that matters
        // for a daily limit, unlike a video playing in front of you or a call
        // you're on. An app FastApp can't positively attribute counts as
        // non-Music here, so an unrecognised player still gets the exemption
        // it always has.
        public static async Task<bool> IsTrulyAfkAsync(bool nonMusicMediaPlaying)
        {
            TimeSpan idleTime = GetIdleTime();

            // 1. Fast Check: Physical mouse/keyboard inputs
            if (idleTime < AfkThreshold)
                return false;

            // 1.5. Hard ceiling: no exemption survives this long with zero input
            if (idleTime >= PassiveMediaGracePeriod)
                return true;

            // 2. Fast Check: Fullscreen video or DirectX Game
            if (IsUserPassivelyEngaged())
                return false;

            // 3. Active windowed media (YouTube, a call, etc.) -- but not music.
            if (nonMusicMediaPlaying)
                return false;

            // If we made it here, they are completely idle and consuming no
            // non-music media.
            return true;
        }
    }
}