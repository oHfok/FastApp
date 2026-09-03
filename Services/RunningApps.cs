using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace FastApp.Services
{
    /// <summary>
    /// Whether a managed app is currently running.
    ///
    /// "Running" means a process whose name matches the executable AND which
    /// owns a top-level window. The window is the important half: background
    /// helpers, updaters and crash handlers ship under the same process name as
    /// the app itself, so matching on the name alone reports things as running
    /// that the user cannot see or switch to.
    ///
    /// The rule lives here because two places need it and they must agree --
    /// the palette, to decide whether a row offers to launch or to focus, and
    /// the hotkey engine, which acts on that same answer. If they disagreed the
    /// row would promise one thing and do the other.
    /// </summary>
    public static class RunningApps
    {
        public static bool Matches(Process process, string executablePath)
        {
            if (process == null || string.IsNullOrWhiteSpace(executablePath)) return false;

            try
            {
                return string.Equals(
                           process.ProcessName,
                           Path.GetFileNameWithoutExtension(executablePath),
                           StringComparison.OrdinalIgnoreCase)
                       && process.MainWindowHandle != IntPtr.Zero;
            }
            catch
            {
                // A process can exit between being listed and being asked about,
                // and a protected one refuses to answer at all. Either way it is
                // not something we can focus.
                return false;
            }
        }

        /// <summary>
        /// Process names, without extension, that currently own a window.
        ///
        /// Gathered in one pass and handed back as a set, because the caller is
        /// usually asking about every managed app at once and enumerating the
        /// process table once per app would turn a palette summon into a visible
        /// pause.
        /// </summary>
        public static HashSet<string> WindowOwners()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return names; }

            try
            {
                foreach (var process in all)
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero) names.Add(process.ProcessName);
                    }
                    catch
                    {
                        // Same as above: unreadable means not focusable.
                    }
                }
            }
            finally
            {
                foreach (var process in all) process.Dispose();
            }

            return names;
        }

        /// <summary>
        /// Whether <paramref name="executablePath"/> appears in a set from
        /// <see cref="WindowOwners"/>.
        /// </summary>
        public static bool IsRunning(HashSet<string> windowOwners, string executablePath) =>
            windowOwners != null
            && !string.IsNullOrWhiteSpace(executablePath)
            && windowOwners.Contains(Path.GetFileNameWithoutExtension(executablePath));
    }
}
