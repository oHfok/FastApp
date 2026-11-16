using System;
using System.Diagnostics;
using System.IO;

namespace FastApp.Services
{
    /// <summary>
    /// Keeps Spicetify applied across Spotify updates: when Spotify's version no
    /// longer matches the one spicetify backed up, runs "spicetify backup apply"
    /// as the user would by hand. -q answers yes to the "clear the old backup?" prompt.
    /// </summary>
    public static class SpicetifyGuard
    {
        public const string SettingKey = "KeepSpicetifyApplied";

        private static readonly string SpotifyDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify");
        private static readonly string Spicetify =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "spicetify", "spicetify.exe");

        private static System.Threading.Timer _timer;
        private static bool _seenLastTick;
        private static string _lastAttemptedVersion;

        public static void Start() =>
            _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));

        private static readonly string Config =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "spicetify", "config-xpui.ini");

        private static string SpotifyVersion =>
            FileVersionInfo.GetVersionInfo(Path.Combine(SpotifyDir, "Spotify.exe")).FileVersion;

        /// <summary>
        /// Spicetify records the Spotify version it patched under [Backup] version
        /// ("1.3.8.620.g39b276d5d7ac"); Spotify.exe reports "1.3.8.620". A mismatch
        /// means Spotify updated and wiped the patch.
        /// </summary>
        public static bool NeedsApply
        {
            get
            {
                try
                {
                    if (!File.Exists(Spicetify) || !File.Exists(Config)) return false;
                    return IsStale(BackupVersion(File.ReadAllLines(Config)), SpotifyVersion);
                }
                catch { return false; }   // Spotify mid-update, file locked: try next tick
            }
        }

        internal static bool IsStale(string backup, string spotify) =>
            !string.IsNullOrEmpty(spotify) && backup != spotify && !(backup ?? "").StartsWith(spotify + ".");

        internal static string BackupVersion(string[] ini)
        {
            bool inBackup = false;
            foreach (string raw in ini)
            {
                string line = raw.Trim();
                if (line.StartsWith("[")) inBackup = line == "[Backup]";
                else if (inBackup && line.StartsWith("version") && line.Contains('='))
                    return line[(line.IndexOf('=') + 1)..].Trim();
            }
            return null;
        }

        private static void Tick()
        {
            bool needs = AppSettingsStore.GetBool(SettingKey, false) && NeedsApply;
            // Seen on two ticks in a row, so we never patch while the updater is still writing files.
            bool act = needs && _seenLastTick;
            _seenLastTick = needs;
            if (!act) return;

            // One try per Spotify version: if spicetify can't handle this version yet,
            // don't restart Spotify every minute retrying.
            string version = SpotifyVersion;
            if (version == _lastAttemptedVersion) return;
            _lastAttemptedVersion = version;

            bool ok = Run();
            NotificationService.Show(
                ok ? "Spicetify re-applied" : "Spicetify could not be re-applied",
                ok ? $"Spotify updated to {version}; your Spicetify setup is back."
                   : $"Spotify {version} may need a newer Spicetify. Try running \"spicetify upgrade\".",
                ok ? NotificationSeverity.Success : NotificationSeverity.Warning);
        }

        private static bool Run()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Spicetify, "-q backup apply")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,   // closed below: a prompt we didn't expect reads EOF instead of hanging
                });
                p.StandardInput.Close();
                if (!p.WaitForExit(120_000)) { p.Kill(); return false; }
                return p.ExitCode == 0 && !NeedsApply;
            }
            catch (Exception ex)
            {
                CrashLog.Log("spicetify", ex);
                return false;
            }
        }
    }
}
