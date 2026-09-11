using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace FastApp
{
    /// <summary>
    /// The full-width red strip across the top of the screen while FastApp
    /// thinks nobody is at the keyboard. Opt-in (see MainViewModel.ShowAfkBar) —
    /// a screen-spanning bar is a bold, unmissable thing, not something to turn
    /// on for someone who never asked for it.
    ///
    /// Same shape as OsdWindow: click-through, never activates, always on top,
    /// created once and Shown/Hidden rather than recreated per appearance.
    /// </summary>
    public partial class AfkBarWindow : Window
    {
        public AfkBarWindow()
        {
            InitializeComponent();
            PositionAtTop();

            // The strip has to hold its place across a resolution change or a
            // monitor being unplugged while it's up -- both of which fire this.
            SystemParameters.StaticPropertyChanged += (_, __) =>
                Dispatcher.BeginInvoke(new Action(PositionAtTop));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Same WIN32 treatment as the OSD: un-clickable, and never steals
            // focus or shows up in Alt+Tab. A status strip is not a control.
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        }

        private void PositionAtTop()
        {
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
        }

        public void ShowBar()
        {
            PositionAtTop();
            Opacity = 0;
            Show();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        public void HideBar()
        {
            var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
            fadeOut.Completed += (s, e) => Hide();
            BeginAnimation(OpacityProperty, fadeOut);
        }

        // --- Win32 Imports ---
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    }
}
