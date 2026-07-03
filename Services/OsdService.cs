using System.Windows;

namespace FastApp.Services
{
    public static class OsdService
    {
        private static OsdWindow _osdWindow;

        public static void Show(string message, bool isAction)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (_osdWindow == null)
                {
                    _osdWindow = new OsdWindow();
                }

                // Keep it on top of other fullscreen apps
                _osdWindow.Topmost = false;
                _osdWindow.Topmost = true;

                _osdWindow.ShowMessage(message, isAction);
            });
        }
    }
}