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
        // The tag's own size, independent of screen width -- it's sized to
        // the label it holds, not to the monitor it's drawn on. Depth is how
        // far the tag hangs below the hairline; width is the flat span the
        // arc curves across.
        private const double DomeWidth = 200;
        private const double DomeDepth = 32;

        public AfkBarWindow()
        {
            InitializeComponent();
            DomeText.Width = DomeWidth;
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

            // A single elliptical arc from the tag's top-left corner to its
            // top-right one, bulging downward by DomeDepth -- the mini-language
            // is the same as SVG's path syntax, confirmed against a rendered
            // prototype before this was written into WPF. The figure closes
            // itself back along the top (the "Z"), which sits flush against
            // the hairline above it, so the two never show a seam.
            Dome.Data = Geometry.Parse(FormattableString.Invariant(
                $"M 0,0 A {DomeWidth / 2},{DomeDepth} 0 0 0 {DomeWidth},0 Z"));

            double domeLeft = Math.Round((Width - DomeWidth) / 2);
            Canvas.SetLeft(Dome, domeLeft);
            Canvas.SetLeft(DomeText, domeLeft);
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
