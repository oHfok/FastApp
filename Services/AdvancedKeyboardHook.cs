using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace FastApp.Services
{
    public class AdvancedKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;

        private readonly HashSet<Key> _pressedKeys = new();
        public event Action<HashSet<Key>> KeysChanged;

        /// <summary>
        /// Asked, on the key-down that completes a combination, whether that
        /// combination should be swallowed instead of passed to the focused app.
        /// Called synchronously from the hook callback, so it must be fast and
        /// must not throw -- a slow callback gets the hook uninstalled by
        /// Windows, and an exception here escapes into the message pump.
        ///
        /// Only the completing key is withheld. The modifiers were delivered as
        /// they were pressed and cannot be recalled, but on their own they do
        /// nothing, so the app sees Ctrl+Shift held and no V.
        /// </summary>
        public Func<HashSet<Key>, bool> ShouldSuppress { get; set; }

        private uint _threadId;

        public AdvancedKeyboardHook()
        {
            _proc = HookCallback;

            // The hook gets its own thread and message loop. It used to be
            // installed on the UI thread, so a UI freeze (4.5s at startup on
            // 2026-11-02) left keystrokes unanswered past Windows' hook timeout,
            // and Windows silently removed the hook: every hotkey, the palette's
            // included, stayed dead until FastApp was restarted.
            using var installed = new System.Threading.ManualResetEventSlim();
            var thread = new System.Threading.Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                using (Process curProcess = Process.GetCurrentProcess())
                using (ProcessModule curModule = curProcess.MainModule)
                    _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
                installed.Set();

                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0) { }

                if (_hookId != IntPtr.Zero) UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            })
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.AboveNormal,
                Name = "Keyboard hook"
            };
            thread.Start();

            // Live on return, as it was when this was installed inline.
            installed.Wait();
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vkCode = Marshal.ReadInt32(lParam);

                // Convert the raw OS code into a WPF Key
                Key currentKey = KeyInterop.KeyFromVirtualKey(vkCode);
                bool changed = false;

                if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
                {
                    changed = _pressedKeys.Add(currentKey);
                }
                else if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP)
                {
                    changed = _pressedKeys.Remove(currentKey);
                }

                // THE FIX: We check for ghost keys, but we EXPLICITLY ignore the currentKey.
                // Because we intercepted the stroke, Windows doesn't know it's pressed yet!
                int removedCount = _pressedKeys.RemoveWhere(k =>
                    k != currentKey &&
                    (GetAsyncKeyState(KeyInterop.VirtualKeyFromKey(k)) & 0x8000) == 0);

                bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;

                if (changed || removedCount > 0)
                {
                    KeysChanged?.Invoke(new HashSet<Key>(_pressedKeys));
                }

                if (isKeyDown && ShouldSuppress != null)
                {
                    try
                    {
                        // Returning a non-zero value here ends the chain: the
                        // keystroke never reaches the focused application.
                        if (ShouldSuppress(_pressedKeys)) return (IntPtr)1;
                    }
                    catch
                    {
                        // Never let a predicate fault break every key on the
                        // machine -- fall through and pass the key on.
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            // Guarded: Dispose is called from both OnClosed and the force-exit
            // path. The hook thread unhooks itself once its loop ends.
            uint threadId = System.Threading.Interlocked.Exchange(ref _threadId, 0);
            if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        private const uint WM_QUIT = 0x0012;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}