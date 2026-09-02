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

        public AdvancedKeyboardHook()
        {
            _proc = HookCallback;
            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule curModule = curProcess.MainModule;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
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
            // Guarded and zeroed: Dispose is called from both OnClosed and the
            // force-exit path, and unhooking a stale handle twice is undefined.
            if (_hookId == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

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