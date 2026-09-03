using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace FastApp.Services
{
    /// <summary>
    /// Today's focused time, per app and in total.
    ///
    /// Read from DailyLogs rather than from AppItemModel.TimeRunning: that
    /// property is the running total since the app was added, so using it here
    /// once reported 233 hours "today". Its own short-lived context, so a
    /// palette summon or a tray click never queues behind the tracker's writes.
    ///
    /// Shared because two surfaces show the same figure now, and a second copy
    /// of this query is exactly how they would drift apart.
    /// </summary>
    public static class TodayUsage
    {
        public static (Dictionary<string, TimeSpan> PerApp, TimeSpan Total) Read()
        {
            var perApp = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
            TimeSpan total = TimeSpan.Zero;

            try
            {
                using var db = new AppDbContext();
                foreach (var log in db.DailyLogs.AsNoTracking().Where(l => l.Date == DateTime.Today))
                {
                    if (string.Equals(log.AppName, "SYSTEM_PC", StringComparison.OrdinalIgnoreCase))
                    {
                        total = log.TimeFocused;
                        continue;
                    }
                    perApp[log.AppName] = log.TimeFocused;
                }
            }
            catch
            {
                // An unreadable log is not worth failing a summon over; the
                // caller simply shows no figures.
            }

            return (perApp, total);
        }

        /// <summary>
        /// "2h 14m", "47m", or "nothing tracked yet" -- short enough for a menu
        /// header, and never an awkward "0m".
        /// </summary>
        public static string Describe(TimeSpan span)
        {
            if (span < TimeSpan.FromMinutes(1)) return "nothing tracked yet";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m today";
            return $"{span.Minutes}m today";
        }
    }
}
