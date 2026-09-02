using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FastApp.Services
{
    /// <summary>
    /// What a hotkey does. The values are the integers already stored in
    /// ManagedApps.ActionType, so this names them without moving anything.
    /// </summary>
    public enum HotkeyAction
    {
        LaunchApp = 0,
        ToggleMute = 1,
        CenterWindow = 2,
        PasteText = 3
    }

    /// <summary>Outcome of one macro, so the caller can say something when it fails.</summary>
    public sealed record ActionResult(bool Success, string Message)
    {
        public static readonly ActionResult Ok = new(true, null);
        public static ActionResult Fail(string message) => new(false, message);
    }

    public static class ActionHookEngine
    {
        // ---- Win32 -----------------------------------------------------------
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int SW_RESTORE = 9;

        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_V = 0x56;
        private const ushort VK_VOLUME_MUTE = 0xAD;

        /// <summary>
        /// Synthesise key presses. keybd_event, which this replaces, has been
        /// superseded since Windows 2000 and is ignored outright by anything
        /// reading raw input -- which is most games.
        /// </summary>
        private static void SendKeys(params (ushort Vk, bool Up)[] keys)
        {
            var inputs = keys.Select(k => new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = k.Vk,
                        dwFlags = k.Up ? KEYEVENTF_KEYUP : 0
                    }
                }
            }).ToArray();

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        public static ActionResult Execute(ViewModels.AppItemModel app)
        {
            try
            {
                return (HotkeyAction)app.ActionType switch
                {
                    HotkeyAction.LaunchApp => LaunchOrFocus(app),
                    HotkeyAction.ToggleMute => ToggleMute(),
                    HotkeyAction.CenterWindow => CenterActiveWindow(),
                    HotkeyAction.PasteText => PasteText(app),
                    _ => ActionResult.Fail($"Unknown action type {app.ActionType}")
                };
            }
            catch (Exception ex)
            {
                // Everything here used to be inside a bare catch { }, so a hotkey
                // that did nothing looked identical to one that was not bound.
                return ActionResult.Fail(ex.Message);
            }
        }

        /// <summary>
        /// Start the app, or bring it to the front if it is already running.
        /// Previously this always started a new process, which for most apps
        /// either opened a duplicate or silently did nothing at all.
        /// </summary>
        private static ActionResult LaunchOrFocus(ViewModels.AppItemModel app)
        {
            if (string.IsNullOrEmpty(app.ExecutablePath))
                return ActionResult.Fail("No executable is set for this entry.");

            string exeName = System.IO.Path.GetFileNameWithoutExtension(app.ExecutablePath);

            var existing = Process.GetProcesses();
            try
            {
                var target = existing.FirstOrDefault(p =>
                    string.Equals(p.ProcessName, exeName, StringComparison.OrdinalIgnoreCase)
                    && p.MainWindowHandle != IntPtr.Zero);

                if (target != null && FocusWindow(target.MainWindowHandle))
                    return ActionResult.Ok;
            }
            finally
            {
                foreach (var p in existing) p.Dispose();
            }

            if (!System.IO.File.Exists(app.ExecutablePath))
                return ActionResult.Fail($"Not found at {app.ExecutablePath}");

            var psi = new ProcessStartInfo
            {
                FileName = app.ExecutablePath,
                // Matched to auto-launch, which has always set this. Some apps
                // resolve their own resources relative to the working directory
                // and misbehave without it.
                WorkingDirectory = System.IO.Path.GetDirectoryName(app.ExecutablePath),
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(app.LaunchArguments))
                psi.Arguments = app.LaunchArguments;

            Process.Start(psi);
            return ActionResult.Ok;
        }

        /// <summary>
        /// SetForegroundWindow is refused unless the caller owns the foreground
        /// or supplied the last input, and FastApp is a background tray app.
        /// Attaching to the foreground window's input queue borrows that right
        /// for the call, which is less invasive than the other common trick of
        /// synthesising an ALT press (that one opens menus in the target app).
        /// </summary>
        private static bool FocusWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

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
                return SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        private static ActionResult ToggleMute()
        {
            SendKeys((VK_VOLUME_MUTE, false), (VK_VOLUME_MUTE, true));
            return ActionResult.Ok;
        }

        private static ActionResult CenterActiveWindow()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero || !GetWindowRect(hWnd, out RECT rect))
                return ActionResult.Fail("No active window to centre.");

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            // The monitor the window is actually on, not the primary one.
            var screen = Screen.FromHandle(hWnd).WorkingArea;
            int newX = screen.Left + (screen.Width - width) / 2;
            int newY = screen.Top + (screen.Height - height) / 2;

            MoveWindow(hWnd, newX, newY, width, height, true);
            return ActionResult.Ok;
        }

        /// <summary>
        /// Paste a snippet, then put back whatever was on the clipboard.
        /// This used to overwrite the clipboard permanently: using the macro
        /// silently destroyed whatever the user had copied.
        /// </summary>
        private static ActionResult PasteText(ViewModels.AppItemModel app)
        {
            if (string.IsNullOrEmpty(app.ActionPayload))
                return ActionResult.Fail("No text is set for this action.");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return ActionResult.Fail("Application is shutting down.");

            // Clipboard access is STA-only and can throw outright when another
            // process holds the clipboard open, which is common and transient.
            object saved = null;
            dispatcher.Invoke(() =>
            {
                try { saved = System.Windows.Clipboard.GetDataObject(); }
                catch { /* nothing to put back, carry on */ }

                System.Windows.Clipboard.SetText(app.ActionPayload);
            });

            Thread.Sleep(50); // let the clipboard settle before the paste
            SendKeys((VK_CONTROL, false), (VK_V, false), (VK_V, true), (VK_CONTROL, true));

            // Restore after the target app has had time to read the clipboard.
            // Doing it immediately races the paste and pastes the old contents.
            if (saved is System.Windows.IDataObject previous)
            {
                _ = System.Threading.Tasks.Task.Delay(400).ContinueWith(_ =>
                {
                    dispatcher.Invoke(() =>
                    {
                        try { System.Windows.Clipboard.SetDataObject(previous, true); }
                        catch { /* best effort -- better than guaranteeing the loss */ }
                    });
                });
            }

            return ActionResult.Ok;
        }
    }
}
