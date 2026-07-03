using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Forms;

namespace FastApp.Services
{
    public static class ActionHookEngine
    {
        // Win32 APIs for hardware-level control
        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        public struct RECT { public int Left, Top, Right, Bottom; }

        public static void Execute(ViewModels.AppItemModel app)
        {
            switch (app.ActionType)
            {
                case 0: // 0 = Launch App
                    if (!string.IsNullOrEmpty(app.ExecutablePath))
                    {
                        try { Process.Start(new ProcessStartInfo { FileName = app.ExecutablePath, UseShellExecute = true }); } catch { }
                    }
                    break;

                case 1: // 1 = Mute/Unmute Volume
                    keybd_event(0xAD, 0, 0, 0); // VK_VOLUME_MUTE Down
                    keybd_event(0xAD, 0, 2, 0); // VK_VOLUME_MUTE Up
                    break;

                case 2: // 2 = Center Active Window
                    IntPtr hWnd = GetForegroundWindow();
                    if (hWnd != IntPtr.Zero && GetWindowRect(hWnd, out RECT rect))
                    {
                        int width = rect.Right - rect.Left;
                        int height = rect.Bottom - rect.Top;
                        // Find which monitor the window is currently on
                        var screen = Screen.FromHandle(hWnd).WorkingArea;

                        int newX = screen.Left + (screen.Width - width) / 2;
                        int newY = screen.Top + (screen.Height - height) / 2;

                        MoveWindow(hWnd, newX, newY, width, height, true);
                    }
                    break;

                case 3: // 3 = Paste Custom Text
                    if (!string.IsNullOrEmpty(app.ActionPayload))
                    {
                        // Safely put text on clipboard from background thread
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            System.Windows.Clipboard.SetText(app.ActionPayload);
                        });

                        Thread.Sleep(50); // Give Windows clipboard a millisecond to catch up

                        // Simulate Ctrl + V at the hardware level
                        keybd_event(0x11, 0, 0, 0); // Ctrl Down
                        keybd_event(0x56, 0, 0, 0); // V Down
                        keybd_event(0x56, 0, 2, 0); // V Up
                        keybd_event(0x11, 0, 2, 0); // Ctrl Up
                    }
                    break;
            }
        }
    }
}