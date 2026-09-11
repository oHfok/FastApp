using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FastApp
{
    /// <summary>
    /// The hairline-plus-tag across the top of the screen while FastApp
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
            Hairline.Width = Width;

            // Tag sizes itself to its own padding + content; its width is not
            // known until a layout pass has actually measured it.
            UpdateLayout();
            Canvas.SetLeft(TagBorder, Math.Round((Width - TagBorder.ActualWidth) / 2));
        }

        // A slow, steady pulse on the marker dot -- the same 2.6s breathing
        // rhythm the tray icon's own status dot already uses (base.css'
        // pulse-dot), so "something is actively being watched" reads the same
        // way in both places rather than this one sitting perfectly still.
        private static readonly DoubleAnimation Pulse = new(1, 0.35, TimeSpan.FromSeconds(1.3))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        public void ShowBar()
        {
            PositionAtTop();
            Opacity = 0;
            TagSlide.Y = -6;
            Show();

            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            TagSlide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
            MarkerDot.BeginAnimation(OpacityProperty, Pulse);
        }

        /// <summary>
        /// How long the away streak this bar is reporting has actually run
        /// for. "AFK DETECTED" alone never said whether that was ten seconds
        /// ago or twenty minutes ago -- the bar only appears once you're
        /// already past the threshold, so the raw fact carries no sense of
        /// duration on its own. Called from AfkBarService's own fast-hide
        /// timer with the same GetIdleTime() reading it already has in hand,
        /// so this adds no new polling of its own.
        /// </summary>
        public void SetDuration(TimeSpan idle)
        {
            DurationText.Text = idle.TotalHours >= 1
                ? $"{(int)idle.TotalHours}h {idle.Minutes:00}m"
                : $"{idle.Minutes}m {idle.Seconds:00}s";

            // The tag's width changes as the label grows or shrinks a digit
            // ("9m 59s" -> "10m 00s"), so it has to be re-centred each time
            // rather than drifting off-centre as it widens around a fixed
            // left edge.
            UpdateLayout();
            Canvas.SetLeft(TagBorder, Math.Round((Width - TagBorder.ActualWidth) / 2));
        }

        public void HideBar()
        {
            MarkerDot.BeginAnimation(OpacityProperty, null);
            MarkerDot.Opacity = 1;

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
