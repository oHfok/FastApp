using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace FastApp.Services
{
    public static class StartupTaskService
    {
        private const string TaskName = "FastApp_LogonTask";
        private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "FastApp";

        // Special command-line flags used to briefly re-launch the app, elevated,
        // purely to register/remove the scheduled task. Program.cs's Main() must
        // check for these as its very first action and, if present, call
        // RunElevatedRegistrationWorker() and return immediately — no window,
        // no tray icon, no normal startup path.
        public const string ElevatedRegisterArg = "--register-startup-task";
        public const string ElevatedUnregisterArg = "--unregister-startup-task";

        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "FastApp_StartupTask.log");

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { /* logging must never crash the app */ }
        }

        public static bool IsStartupCorrectlyRegistered()
        {
            string currentPath = Environment.ProcessPath;

            // 1. Check Task Scheduler First
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = $"/query /tn \"{TaskName}\" /xml",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && output.Contains(currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (process.ExitCode != 0)
                {
                    Log($"Task query failed. ExitCode={process.ExitCode}, STDERR={error}");
                }
            }
            catch (Exception ex)
            {
                Log($"Task query threw exception: {ex}");
            }

            // 2. Fallback: Check Registry
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                string regValue = key?.GetValue(AppName) as string;
                if (regValue != null && regValue.Contains(currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Registry check threw exception: {ex}");
            }

            return false;
        }

        /// <summary>
        /// Call this from the UI (e.g. a settings toggle). Instead of touching Task
        /// Scheduler directly from the normal, non-elevated app process, this relaunches
        /// the app briefly with the "runas" verb — triggering exactly one UAC prompt —
        /// so the elevated instance can register the task. The main app itself never
        /// needs to run elevated on every launch.
        /// </summary>
        public static bool SetStartup(bool enable)
        {
            string currentPath = Environment.ProcessPath;
            string arg = enable ? ElevatedRegisterArg : ElevatedUnregisterArg;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = currentPath,
                    Arguments = arg,
                    UseShellExecute = true, // required for the "runas" verb to trigger UAC
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var process = Process.Start(psi);
                process.WaitForExit();

                // The elevated worker exits with 0 on success, 1 on failure.
                bool success = process.ExitCode == 0;
                Log(success
                    ? $"Elevated registration worker ({arg}) completed successfully."
                    : $"Elevated registration worker ({arg}) exited with code {process.ExitCode}.");
                return success;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED — user clicked "No" on the UAC prompt.
                Log("User declined the UAC elevation prompt; startup was not changed.");
                return false;
            }
            catch (Exception ex)
            {
                Log($"SetStartup({enable}) failed to launch elevated worker: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Runs entirely inside the brief, elevated, UI-less relaunch triggered by
        /// SetStartup(). Program.cs's Main() must check for ElevatedRegisterArg /
        /// ElevatedUnregisterArg as its very first action and, if present, call this
        /// and then return immediately — no window, tray icon, or normal startup path.
        /// </summary>
        public static int RunElevatedRegistrationWorker(bool enable)
        {
            string currentPath = Environment.ProcessPath;
            string commandArgs = $"\"{currentPath}\" --minimized";

            if (enable)
            {
                bool taskSuccess = false;
                try
                {
                    // Priority 4 = Normal (Default is 7: Below Normal)
                    // DisallowStartIfOnBatteries = false (Forces it to start even on laptops unplugged from the wall)
                    string xmlConfig = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions>
    <Exec>
      <Command>{currentPath}</Command>
      <Arguments>--minimized</Arguments>
    </Exec>
  </Actions>
</Task>";

                    // IMPORTANT: the XML declares encoding="UTF-16", so the file on disk
                    // must actually be written as UTF-16, not the .NET default of UTF-8,
                    // or schtasks will silently fail to import it.
                    string tempXmlPath = Path.Combine(Path.GetTempPath(), "FastAppTask.xml");
                    File.WriteAllText(tempXmlPath, xmlConfig, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

                    var psi = new ProcessStartInfo
                    {
                        FileName = "schtasks",
                        Arguments = $"/create /tn \"{TaskName}\" /xml \"{tempXmlPath}\" /f",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var process = Process.Start(psi);
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    taskSuccess = (process.ExitCode == 0);

                    if (!taskSuccess)
                    {
                        Log($"schtasks /create failed (elevated). ExitCode={process.ExitCode}\nSTDOUT: {stdout}\nSTDERR: {stderr}");
                    }
                    else
                    {
                        Log("schtasks /create succeeded (elevated).");
                    }

                    if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath);
                }
                catch (Exception ex)
                {
                    Log($"Elevated task-creation threw exception: {ex}");
                }

                // Fallback to Registry if Task Scheduler still blocks us even elevated
                if (!taskSuccess)
                {
                    try
                    {
                        using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                        key.SetValue(AppName, commandArgs);
                        Log("Fell back to registry Run key (elevated worker).");
                    }
                    catch (Exception ex)
                    {
                        Log($"Registry fallback also failed (elevated worker): {ex}");
                        return 1;
                    }
                }

                return 0;
            }
            else
            {
                bool anyFailure = false;

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "schtasks",
                        Arguments = $"/delete /tn \"{TaskName}\" /f",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    };
                    using var process = Process.Start(psi);
                    process?.WaitForExit();
                }
                catch (Exception ex)
                {
                    Log($"Task deletion threw exception (elevated worker): {ex}");
                    anyFailure = true;
                }

                try
                {
                    using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
                    key.DeleteValue(AppName, false);
                }
                catch (Exception ex)
                {
                    Log($"Registry deletion threw exception (elevated worker): {ex}");
                    anyFailure = true;
                }

                return anyFailure ? 1 : 0;
            }
        }
    }
}