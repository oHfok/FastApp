using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace FastApp.Services
{
    public static class StartupTaskService
    {
        // This is the standard Windows Registry key for user-specific startup apps
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "FastApp";

        public static bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                return key?.GetValue(AppName) != null;
            }
        }

        public static void SetStartup(bool enable)
        {
            string exePath = Process.GetCurrentProcess().MainModule.FileName;

            // We pass the argument --minimized so it stays in the tray
            string command = $"\"{exePath}\" --minimized";

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (enable)
                    {
                        key.SetValue(AppName, command);
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch
            {
                // Registry access failed (permissions issue)
            }
        }
    }
}