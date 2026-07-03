using System;

namespace FastApp.ViewModels
{
    public class DailyUsageLog
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string AppName { get; set; }
        public TimeSpan TimeSpent { get; set; }

        // NEW: Track the idle time separately
        public TimeSpan AfkTimeSpent { get; set; }
    }
}