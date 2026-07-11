using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FastApp.ViewModels
{
    public class DailyUsageLog
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string AppName { get; set; }

        // --- OLD COLUMNS (Currently mapped as TEXT in SQLite) ---
        public TimeSpan TimeSpent { get; set; }
        public TimeSpan AfkTimeSpent { get; set; }
        public TimeSpan TimeFocused { get; set; }

        // --- NEW COLUMNS (Will be created as INTEGER in SQLite) ---
        // We make them nullable (long?) for a moment so the migration doesn't crash on existing rows
        public long? TimeSpentTicks { get; set; }
        public long? AfkTimeSpentTicks { get; set; }
        public long? TimeFocusedTicks { get; set; }

        // --- RESTORED PROPERTY ---
        [NotMapped]
        public TimeSpan ActiveRunningTime => TimeSpent - AfkTimeSpent;
    }
}