using System;
using System.Runtime.InteropServices;

namespace FastApp.Services
{
    /// <summary>
    /// Bring a window to the front and give it the keyboard.
    ///
    /// SetForegroundWindow on its own is not enough and has not been since
    /// Windows 2000: a process that does not already own the foreground is
    /// refused, and the call quietly returns false while flashing the taskbar
    /// button instead. That is precisely the position FastApp is in every time
    /// the summon hotkey is pressed -- whatever you were using owns the
    /// foreground, and FastApp is a background process asking to take it.
    ///
    /// The accepted way through is to attach this thread's input queue to the
    /// foreground window's thread for the length of the call, which makes the
    /// two threads share a notion of focus and lifts the restriction.
    ///
    /// Named WindowFocus rather than Foreground because a WPF Window already
    /// has a Foreground property, and inside one that name resolves to a Brush.
    ///
    /// The hotkey engine has done this for years when focusing an app you
    /// launched. The palette did not, which is why summoning it often left you
    /// looking at a window you had to click before you could type.
    /// </summary>
    public static class WindowFocus
    {
        private const int SwRestore = 9;

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);

        public static bool Bring(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;

            try
            {
                if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);

                IntPtr foreground = GetForegroundWindow();
                uint currentThread = GetCurrentThreadId();
                uint foregroundThread = foreground == IntPtr.Zero
                    ? 0
                    : GetWindowThreadProcessId(foreground, out _);

                bool attached = foregroundThread != 0
                                && foregroundThread != currentThread
                                && AttachThreadInput(currentThread, foregroundThread, true);
                try
                {
                    bool ok = SetForegroundWindow(hWnd);

                    // While the queues are attached, this also moves the caret,
                    // which is the half that makes typing work rather than just
                    // making the window visible.
                    if (attached) SetFocus(hWnd);
                    return ok;
                }
                finally
                {
                    if (attached) AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
            catch
            {
                // Losing the race for the foreground is a worse experience, not
                // a crash.
                return false;
            }
        }
    }
}
