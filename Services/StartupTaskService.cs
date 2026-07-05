using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace FastApp.Services
{
    public static class StartupTaskService
    {
        private const string TaskName = "FastApp_LogonTask";
        private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "FastApp";

        // Checks if the app is registered AND if the path perfectly matches where the .exe is right now
        public static bool IsStartupCorrectlyRegistered()
        {
            string currentPath = Environment.ProcessPath;

            // 1. Check Task Scheduler First
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    // Querying as XML allows us to easily search for the exact string path
                    Arguments = $"/query /tn \"{TaskName}\" /xml",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && output.Contains(currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true; // Task exists and points to the correct, current path!
                }
            }
            catch { }

            // 2. Fallback: Check Registry
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                string regValue = key?.GetValue(AppName) as string;
                if (regValue != null && regValue.Contains(currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true; // Registry exists and points to the correct, current path!
                }
            }
            catch { }

            return false; // Not registered, or the user moved the file to a new folder!
        }

        public static void SetStartup(bool enable)
        {
            string currentPath = Environment.ProcessPath;
            string commandArgs = $"\"{currentPath}\" --minimized";

            if (enable)
            {
                // 1. Try Task Scheduler (Zero-Delay Boot)
                bool taskSuccess = false;
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "schtasks",
                        // /f forcefully overwrites the old rule if the user moved the file
                        Arguments = $"/create /tn \"{TaskName}\" /tr \"\\\"{currentPath}\\\" --minimized\" /sc onlogon /f",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = Process.Start(psi);
                    process.WaitForExit();
                    taskSuccess = (process.ExitCode == 0);
                }
                catch { }

                // 2. Fallback to Registry if Task Scheduler blocks us (Group Policy/Permissions)
                if (!taskSuccess)
                {
                    try
                    {
                        using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                        key.SetValue(AppName, commandArgs);
                    }
                    catch { }
                }
            }
            else
            {
                // Delete from both places to be absolutely clean
                try
                {
                    var psi = new ProcessStartInfo { FileName = "schtasks", Arguments = $"/delete /tn \"{TaskName}\" /f", UseShellExecute = false, CreateNoWindow = true };
                    Process.Start(psi)?.WaitForExit();
                }
                catch { }

                try
                {
                    using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                    key.DeleteValue(AppName, false);
                }
                catch { }
            }
        }
    }
}