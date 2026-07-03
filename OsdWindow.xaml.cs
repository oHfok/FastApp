using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FastApp
{
    public partial class OsdWindow : Window
    {
        private DispatcherTimer _hideTimer;

        public OsdWindow()
        {
            InitializeComponent();
            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _hideTimer.Tick += (s, e) => HideOsd();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // WIN32 MAGIC: Makes the window un-clickable (clicks pass through) and prevents it from ever stealing focus
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        }

        public void ShowMessage(string message, bool isAction)
        {
            // Set Text and Icon
            OsdText.Text = message;
            ActionIcon.Visibility = isAction ? Visibility.Visible : Visibility.Collapsed;
            AppIcon.Visibility = isAction ? Visibility.Collapsed : Visibility.Visible;

            // Position at bottom-right of the primary screen
            var workArea = SystemParameters.WorkArea;
            this.Left = workArea.Right - this.Width - 30;
            this.Top = workArea.Bottom - this.Height - 30;

            this.Show();

            // Smooth Slide-In & Fade-In Animation
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
            var slideIn = new ThicknessAnimation(new Thickness(50, 0, -50, 0), new Thickness(0), TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            OsdBorder.BeginAnimation(OpacityProperty, fadeIn);
            OsdBorder.BeginAnimation(MarginProperty, slideIn);

            // Reset the 2-second auto-close timer
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void HideOsd()
        {
            _hideTimer.Stop();

            // Smooth Fade-Out
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (s, e) => this.Hide();
            OsdBorder.BeginAnimation(OpacityProperty, fadeOut);
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