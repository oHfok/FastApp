using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FastApp
{
    /// <summary>What is happening to one app in the startup sequence.</summary>
    public enum LaunchStep
    {
        Pending,
        Waiting,
        Opening,
        Started,
        AlreadyRunning,
        Failed
    }

    public partial class AutoLaunchProgressWindow : Window
    {
        // At most this many rows are drawn; the rest are counted. Ten startup
        // apps is already a tall window in the middle of the screen, and beyond
        // that the individual names stop being the point.
        private const int MaxVisibleRows = 10;

        private readonly ObservableCollection<LaunchRow> _rows = new();
        private readonly DispatcherTimer _hideTimer;
        private int _total;

        public AutoLaunchProgressWindow()
        {
            InitializeComponent();
            RowList.ItemsSource = _rows;

            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _hideTimer.Tick += (s, e) => HideWindow();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Same WIN32 click-through/no-activate treatment as OsdWindow --
            // this is an informational popup, not something to interact with.
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        }

        /// <summary>
        /// Show the whole plan before anything starts. The sequence is decided
        /// up front, so revealing it one app at a time only hid what was coming.
        /// </summary>
        public void ShowPlan(IReadOnlyList<string> names)
        {
            _hideTimer.Stop();
            _total = names?.Count ?? 0;

            _rows.Clear();
            for (int i = 0; i < Math.Min(_total, MaxVisibleRows); i++)
                _rows.Add(new LaunchRow(names[i]));

            int hidden = _total - _rows.Count;
            MoreText.Text = hidden > 0 ? $"and {hidden} more" : string.Empty;
            MoreText.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;

            RowList.Visibility = Visibility.Visible;
            ProgressTrack.Visibility = Visibility.Visible;
            SummaryText.Visibility = Visibility.Collapsed;
            HeadingText.Text = "STARTING YOUR APPS";

            UpdateCount(0);
            Appear();
        }

        /// <summary>Move one app to a new state.</summary>
        public void SetStep(int index, LaunchStep step, string detail = null)
        {
            _hideTimer.Stop();

            if (index >= 0 && index < _rows.Count) _rows[index].Apply(step, detail);

            // Counted from the step, not from the index: an app that is still
            // waiting out its delay has not started yet, and saying "3 of 4"
            // over a row that reads "waiting 10s" is the sort of small lie the
            // old window told constantly.
            int done = 0;
            foreach (var row in _rows)
            {
                if (row.Step is LaunchStep.Started or LaunchStep.AlreadyRunning or LaunchStep.Failed) done++;
            }

            UpdateCount(done);
            Appear();
        }

        /// <summary>
        /// The one-line result, replacing the list. Auto-hides, since by now
        /// there is nothing left to watch.
        /// </summary>
        public void ShowSummary(string summaryText)
        {
            HeadingText.Text = "STARTUP COMPLETE";
            CountText.Text = string.Empty;
            RowList.Visibility = Visibility.Collapsed;
            MoreText.Visibility = Visibility.Collapsed;
            ProgressTrack.Visibility = Visibility.Collapsed;

            SummaryText.Text = summaryText;
            SummaryText.Visibility = Visibility.Visible;

            Appear();

            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void UpdateCount(int done)
        {
            CountText.Text = _total > 0 ? $"{done} / {_total}" : string.Empty;

            // Expressed as a ratio of two star columns rather than a pixel width.
            // A width has to be computed from the track's measured size, which is
            // zero until the window has laid out at least once -- so the first
            // call had to guess, and a guessed bar either overshoots its track or
            // stops short of it. Star columns are exact from the first frame and
            // stay exact whatever the padding becomes.
            FillColumn.Width = new GridLength(done, GridUnitType.Star);
            RestColumn.Width = new GridLength(Math.Max(0, _total - done), GridUnitType.Star);
        }

        private void Appear()
        {
            PositionCentered();
            Show();

            // Re-centre after the layout settles: the window grows a row at a
            // time as states resolve, and a fixed position taken before that
            // leaves it drifting off centre.
            Dispatcher.BeginInvoke(new Action(PositionCentered), DispatcherPriority.Loaded);

            ProgressBorder.BeginAnimation(OpacityProperty,
                new DoubleAnimation(ProgressBorder.Opacity, 1, TimeSpan.FromMilliseconds(200)));
        }

        private void PositionCentered()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
            Top = workArea.Top + (workArea.Height - ActualHeight) / 2;
        }

        private void HideWindow()
        {
            _hideTimer.Stop();

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (s, e) => Hide();
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

    /// <summary>
    /// One line of the list.
    ///
    /// It carries its own brushes rather than leaving the template to pick them
    /// with a converter or a StaticResource: both of those fail at render time
    /// rather than at build time, and this window renders during login, where a
    /// crash is both most likely to happen and least likely to be noticed.
    /// </summary>
    public sealed class LaunchRow : INotifyPropertyChanged
    {
        private static readonly Brush Text = Frozen("#F3F1EA");
        private static readonly Brush Dim = Frozen("#9C9FAE");
        private static readonly Brush Faint = Frozen("#7C8194");
        private static readonly Brush Brass = Frozen("#E8A33D");
        private static readonly Brush Teal = Frozen("#34D3C4");
        private static readonly Brush Rose = Frozen("#FF6B6B");

        public LaunchRow(string name) { Name = name; }

        public string Name { get; }
        public LaunchStep Step { get; private set; } = LaunchStep.Pending;

        public string Marker { get; private set; } = "·";
        public string Detail { get; private set; } = string.Empty;
        public Brush MarkerBrush { get; private set; } = Faint;
        public Brush NameBrush { get; private set; } = Faint;
        public Brush DetailBrush { get; private set; } = Faint;

        public void Apply(LaunchStep step, string detail)
        {
            Step = step;

            switch (step)
            {
                case LaunchStep.Waiting:
                    Marker = "·"; MarkerBrush = Brass; NameBrush = Text; DetailBrush = Brass;
                    Detail = detail ?? "waiting";
                    break;
                case LaunchStep.Opening:
                    Marker = "●"; MarkerBrush = Brass; NameBrush = Text; DetailBrush = Brass;
                    Detail = detail ?? "opening";
                    break;
                case LaunchStep.Started:
                    Marker = "✓"; MarkerBrush = Teal; NameBrush = Text; DetailBrush = Dim;
                    Detail = detail ?? "opened";
                    break;
                case LaunchStep.AlreadyRunning:
                    Marker = "✓"; MarkerBrush = Dim; NameBrush = Dim; DetailBrush = Faint;
                    Detail = detail ?? "already open";
                    break;
                case LaunchStep.Failed:
                    Marker = "✕"; MarkerBrush = Rose; NameBrush = Text; DetailBrush = Rose;
                    Detail = detail ?? "failed";
                    break;
                default:
                    Marker = "·"; MarkerBrush = Faint; NameBrush = Faint; DetailBrush = Faint;
                    Detail = string.Empty;
                    break;
            }

            foreach (var property in new[] { nameof(Marker), nameof(Detail), nameof(MarkerBrush),
                                             nameof(NameBrush), nameof(DetailBrush), nameof(Step) })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            }
        }

        private static Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
