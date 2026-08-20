using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FastApp
{
    public partial class AutoLaunchProgressWindow : Window
    {
        private DispatcherTimer _hideTimer;

        public AutoLaunchProgressWindow()
        {
            InitializeComponent();
            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _hideTimer.Tick += (s, e) => HideWindow();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Same WIN32 click-through/no-activate treatment as OsdWindow —
            // this is an informational popup, not something to interact with.
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        }

        // Called once per app as RunAutoLaunchAsync works through the list.
        // Repositions on every call (not just the first) in case the primary
        // screen's work area changed since the window was created.
        public void ShowProgress(int current, int total, string appName)
        {
            _hideTimer.Stop(); // still in progress -- don't let a stale timer hide this mid-batch

            TitleText.Text = $"Opening {current} of {total}";
            SubtitleText.Text = $"Starting: {appName}";
            SubtitleText.Visibility = Visibility.Visible;
            ProgressBarControl.Visibility = Visibility.Visible;
            ProgressBarControl.Maximum = total;
            ProgressBarControl.Value = current;

            PositionCentered();
            this.Show();

            var fadeIn = new DoubleAnimation(ProgressBorder.Opacity, 1, TimeSpan.FromMilliseconds(200));
            ProgressBorder.BeginAnimation(OpacityProperty, fadeIn);
        }

        // Swaps to a one-line completion summary, then auto-hides after 2.5s —
        // same auto-dismiss shape as OsdWindow, just a longer hold since this
        // is read after the fact rather than glanced at during an event.
        public void ShowSummary(string summaryText)
        {
            TitleText.Text = summaryText;
            SubtitleText.Visibility = Visibility.Collapsed;
            ProgressBarControl.Visibility = Visibility.Collapsed;

            PositionCentered();
            this.Show();

            var fadeIn = new DoubleAnimation(ProgressBorder.Opacity, 1, TimeSpan.FromMilliseconds(200));
            ProgressBorder.BeginAnimation(OpacityProperty, fadeIn);

            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void PositionCentered()
        {
            var workArea = SystemParameters.WorkArea;
            this.Left = workArea.Left + (workArea.Width - this.Width) / 2;
            this.Top = workArea.Top + (workArea.Height - this.Height) / 2;
        }

        private void HideWindow()
        {
            _hideTimer.Stop();

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (s, e) => this.Hide();
            ProgressBorder.BeginAnimation(OpacityProperty, fadeOut);
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
