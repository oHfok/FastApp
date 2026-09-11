using System;
using System.IO;

namespace FastApp.Services
{
    /// <summary>
    /// Where an unhandled exception's full details go before the crash dialog
    /// is shown. The dialog itself stays plain-language -- a full .NET stack
    /// trace in a MessageBox told nobody anything useful and gave no way to
    /// send it to whoever was going to look into it. The trace still isn't
    /// lost; it's just not the first thing the user sees.
    ///
    /// Same shape as DatabaseHealth's own log: a single append-only file next
    /// to the database, and logging itself must never throw -- there is
    /// nowhere left to say so if it does.
    /// </summary>
    public static class CrashLog
    {
        public static string LogPath =>
            Path.Combine(AppDbContext.GetDbFolder(), "crash.log");

        public static void Log(string kind, Exception ex)
        {
            try
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}{Environment.NewLine}" +
                    $"{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // If even the log cannot be written there is nowhere left to
                // say so, and failing here would defeat the point of the class.
            }
        }
    }
}
