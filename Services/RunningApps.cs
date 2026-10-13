using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

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
        /// Where a process's executable is on disk, or null if Windows will not say.
        ///
        /// Asks for exactly that one fact. Process.MainModule gets it by enumerating
        /// every module the process has loaded, which needs more access and, for some
        /// processes (suspended Store apps, protected or anti-cheat-guarded ones),
        /// fails slowly. The tracker's stalls in the diagnostics came in steps of about
        /// 0.8 seconds (0.8, 1.6, 2.4, 4.0, 4.7), which is what one, two, three or more
        /// such processes met in the same tick would look like.
        /// </summary>
        public static string ImagePath(int processId)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                int size = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0 ? buffer.ToString(0, size) : null;
            }
            catch { return null; }
            finally { CloseHandle(handle); }
        }

        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref int size);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);

        private const uint GW_OWNER = 4;

        /// <summary>
        /// Which processes own a main window, in one pass over every window on the
        /// machine: process id -> whether that window has a title.
        ///
        /// "Main window" means what Process.MainWindowHandle means -- the first
        /// visible top-level window that has no owner. That property, though,
        /// enumerates every window on the system FOR EACH PROCESS it is asked
        /// about, so asking it of ~380 processes meant ~380 full window passes
        /// (about 55 ms) on every 5-second tracker tick and on every palette
        /// summon. One pass gives the same answer for all of them.
        /// </summary>
        public static Dictionary<int, bool> MainWindows()
        {
            var found = new Dictionary<int, bool>();
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                // The first qualifying window per process is its main window.
                if (!found.ContainsKey((int)pid)) found[(int)pid] = GetWindowTextLength(hwnd) > 0;
                return true;
            }, IntPtr.Zero);
            return found;
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
                var windows = MainWindows();
                foreach (var process in all)
                {
                    try
                    {
                        if (windows.ContainsKey(process.Id)) names.Add(process.ProcessName);
                    }
                    catch
                    {
                        // A process can exit between being listed and being asked about;
                        // unreadable means not focusable.
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
