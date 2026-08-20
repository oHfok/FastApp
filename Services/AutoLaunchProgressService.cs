using System.Windows;

namespace FastApp.Services
{
    public static class AutoLaunchProgressService
    {
        private static AutoLaunchProgressWindow _progressWindow;

        public static void ShowProgress(int current, int total, string appName)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (_progressWindow == null)
                {
                    _progressWindow = new AutoLaunchProgressWindow();
                }

                _progressWindow.Topmost = false;
                _progressWindow.Topmost = true;

                _progressWindow.ShowProgress(current, total, appName);
            });
        }

        public static void ShowSummary(string summaryText)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (_progressWindow == null)
                {
                    _progressWindow = new AutoLaunchProgressWindow();
                }

                _progressWindow.Topmost = false;
                _progressWindow.Topmost = true;

                _progressWindow.ShowSummary(summaryText);
            });
        }
    }
}
