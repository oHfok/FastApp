using System;

namespace FastApp.ViewModels
{
    public class DailyUsageLog
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string AppName { get; set; }

        public TimeSpan TimeSpent { get; set; }      // Total time the app was running/visible
        public TimeSpan AfkTimeSpent { get; set; }   // Time the user was away from the keyboard
        public TimeSpan TimeFocused { get; set; }    // NEW: Time the app was actually the active window!
    }
}