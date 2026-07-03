using System;
using System.Runtime.InteropServices;

namespace FastApp.Services
{
    public static class SystemIdleTracker
    {
        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        public static TimeSpan GetIdleTime()
        {
            var lastInputInfo = new LASTINPUTINFO();
            lastInputInfo.cbSize = (uint)Marshal.SizeOf(lastInputInfo);

            if (GetLastInputInfo(ref lastInputInfo))
            {
                // Environment.TickCount is time since PC booted. 
                // dwTime is the tick count of the last mouse/keyboard event.
                uint idleTicks = (uint)Environment.TickCount - lastInputInfo.dwTime;
                return TimeSpan.FromMilliseconds(idleTicks);
            }
            return TimeSpan.Zero;
        }
    }
}