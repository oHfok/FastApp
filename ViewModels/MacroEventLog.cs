using System;
using System.Collections.Generic;
using System.Text;

namespace FastApp.ViewModels
{
    public class MacroEventLog
    {
        public int Id { get; set; }
        public string AppName { get; set; }
        public DateTime Timestamp { get; set; }

        // Whether ActionHookEngine.Execute actually succeeded. True for every
        // row logged before this column existed (the only outcome that used
        // to get a row at all), so old data reads as "succeeded" rather than
        // "unknown" -- the honest default, since a failure has always also
        // fired a toast, and nobody reported a hotkey silently failing.
        public bool Success { get; set; } = true;

        // Set only when Success is false -- ActionHookEngine's own failure
        // message (bad path, launch error, etc.), the same text the toast
        // already showed.
        public string? FailureReason { get; set; }

        // True when the trigger fired but never reached ActionHookEngine at
        // all, because a foreground game blocked it (the "airtight gaming
        // guard" in ProcessTriggersAsync). A blocked attempt is neither a
        // success nor a failure of the macro itself -- it's its own outcome,
        // and previously left no trace whatsoever.
        public bool Blocked { get; set; }
    }
}
