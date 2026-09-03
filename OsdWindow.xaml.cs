using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace FastApp
{
    /// <summary>Why the OSD is on screen.</summary>
    public enum OsdKind
    {
        /// <summary>A hotkey launched or focused an application.</summary>
        App,
        /// <summary>A hotkey ran an action: mute, centre window, paste.</summary>
        Action,
        /// <summary>A hotkey was deliberately not passed through to a game.</summary>
        Blocked
    }

    public partial class OsdWindow : Window
    {
        // Per theme, for the same reason as the progress list: a frozen literal
        // is a colour the theme cannot reach.
        private static bool Light => Services.SystemTheme.IsLight;

        private static Brush Brass => Light ? LightBrass : DarkBrass;
        private static Brush Violet => Light ? LightViolet : DarkViolet;
        private static Brush Rose => Light ? LightRose : DarkRose;

        private static readonly Brush DarkBrass = Frozen("#E8A33D");
        private static readonly Brush DarkViolet = Frozen("#8B7CFF");
        private static readonly Brush DarkRose = Frozen("#FF6B6B");

        private static readonly Brush LightBrass = Frozen("#8A6321");
        private static readonly Brush LightViolet = Frozen("#5B4FC7");
        private static readonly Brush LightRose = Frozen("#A32929");

        private readonly DispatcherTimer _hideTimer;

        public OsdWindow()
        {
            InitializeComponent();
            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _hideTimer.Tick += (s, e) => HideOsd();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // WIN32 MAGIC: Makes the window un-clickable (clicks pass through)
            // and prevents it from ever stealing focus.
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        }

        /// <summary>
        /// Say what just happened, and to what.
        ///
        /// The name and the event arrive separately. They used to be joined into
        /// one string by the caller, which left the window printing a sentence
        /// beside a generic icon with no idea which half was which.
        /// </summary>
        public void ShowMessage(string name, OsdKind kind)
        {
            OsdText.Text = name;

            switch (kind)
            {
                case OsdKind.Action:
                    KindText.Text = "ACTION";
                    MarkerText.Text = "●";
                    MarkerText.Foreground = Violet;
                    break;
                case OsdKind.Blocked:
                    // The same words the setting uses ("Don't pass through"),
                    // rather than a second phrase for one behaviour. The old
                    // "blocked in game" could also be read as FastApp being the
                    // thing that was blocked.
                    KindText.Text = "NOT PASSED THROUGH";
                    MarkerText.Text = "✕";
                    MarkerText.Foreground = Rose;
                    break;
                default:
                    KindText.Text = "HOTKEY";
                    MarkerText.Text = "●";
                    MarkerText.Foreground = Brass;
                    break;
            }

            // Laid out before it is placed: the window sizes itself to the name,
            // so its width is not known until the content has measured, and
            // positioning from a stale width left it hanging off the edge.
            UpdateLayout();
            PositionBottomRight();

            Show();
            Dispatcher.BeginInvoke(new Action(PositionBottomRight), DispatcherPriority.Loaded);

            OsdBorder.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));

            // Slid by a transform rather than by animating Margin. A margin is
            // layout, so the old animation resized the window on every frame,
            // which a window that sizes itself to its content cannot survive.
            SlideTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });

            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void PositionBottomRight()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - ActualWidth - 30;
            Top = workArea.Bottom - ActualHeight - 30;
        }

        private void HideOsd()
        {
            _hideTimer.Stop();

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (s, e) => Hide();
            OsdBorder.BeginAnimation(OpacityProperty, fadeOut);
        }

        private static Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
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
