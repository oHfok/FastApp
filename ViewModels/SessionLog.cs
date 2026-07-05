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
    }
}
