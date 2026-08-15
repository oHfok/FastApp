using System;
using System.Collections.Generic;
using System.Text;

namespace FastApp.ViewModels
{
    public class SessionLog
    {
        public int Id { get; set; }
        public string AppName { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        // Opt-in only — see the CaptureWindowTitles setting. Null for every
        // session recorded before the setting was turned on, or when it's off.
        // Nullable annotation matters here: this project has NRT enabled, and
        // EF Core reads it to decide the column is optional, not required.
        public string? WindowTitle { get; set; }
    }
}
