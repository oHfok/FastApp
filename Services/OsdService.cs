using System.Windows;

namespace FastApp.Services
{
    public static class OsdService
    {
        private static OsdWindow _osdWindow;

        /// <summary>
        /// The name and what happened to it, kept apart. This took a finished
        /// sentence and a bool, which meant the window could not lay out the two
        /// halves it was being shown.
        /// </summary>
        public static void Show(string name, OsdKind kind)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _osdWindow ??= new OsdWindow();

                // Keep it on top of other fullscreen apps
                _osdWindow.Topmost = false;
                _osdWindow.Topmost = true;

                _osdWindow.ShowMessage(name, kind);
            });
        }
    }
}