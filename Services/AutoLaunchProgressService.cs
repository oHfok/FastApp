using System.Windows;

namespace FastApp.Services
{
    public static class AutoLaunchProgressService
    {
        private static AutoLaunchProgressWindow _progressWindow;

        /// <summary>
        /// Announce the whole sequence before it starts. The old API took
        /// (current, total, name) once per app, so the window could only ever
        /// know about one of them at a time and had nothing to show but a
        /// counter.
        /// </summary>
        public static void ShowPlan(System.Collections.Generic.IReadOnlyList<string> names)
        {
            OnWindow(w => w.ShowPlan(names));
        }

        public static void SetStep(int index, LaunchStep step, string detail = null)
        {
            OnWindow(w => w.SetStep(index, step, detail));
        }

        private static void OnWindow(System.Action<AutoLaunchProgressWindow> act)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _progressWindow ??= new AutoLaunchProgressWindow();

                // Re-asserted every time: another window opening during startup
                // -- which is the entire point of this sequence -- otherwise
                // ends up on top of the thing reporting it.
                _progressWindow.Topmost = false;
                _progressWindow.Topmost = true;

                act(_progressWindow);
            });
        }

        public static void ShowSummary(string summaryText)
        {
            OnWindow(w => w.ShowSummary(summaryText));
        }
    }
}
