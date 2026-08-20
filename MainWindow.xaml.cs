using FastApp.Services;
using FastApp.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace FastApp
{
    public partial class MainWindow : FluentWindow
    {
        private System.Windows.Point _dragStartPoint;

        private MainViewModel _viewModel;
        public MainViewModel ViewModel => _viewModel;

        // NEW: The advanced global hook
        private AdvancedKeyboardHook _keyboardHook;

        private TrayService _trayService;
        private bool _isForceExiting = false;

        // NEW: Variables to track keys while recording a hotkey on the UI
        private HashSet<Key> _currentCaptureKeys = new();
        private bool _isCapturing = false;

        public MainWindow()
        {
            InitializeComponent();

            // 1. TRAY FIRST: Instant visibility in the taskbar.
            _trayService = new TrayService(this);

            // 2. HOOK SECOND: Macros are live immediately, even if the DB is still loading.
            _keyboardHook = new AdvancedKeyboardHook();

            // 3. VIEWMODEL LAST: Safe to do DB work now.
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            // 4. WIRE THE HOOK: Connect the live hook to the loaded ViewModel.
            _keyboardHook.KeysChanged += _viewModel.CheckForHotkeys;

            // 5. SHUTDOWN SAFETY NET: Windows shutting down / restarting / logging
            // off never goes through the tray Exit path, so without this the most
            // COMMON way this app ends (the user turning their PC off) was also the
            // only one that skipped the graceful flush entirely -- losing the open
            // session plus up to 60s of tracking, and leaving the WAL unchecked-
            // pointed. That last part is the same exposure that preceded the
            // 2026-08-19 corruption; the update path was hardened against it at the
            // time, but this far more frequent path was not.
            System.Windows.Application.Current.SessionEnding += OnSessionEnding;
        }

        // Windows gives an app only a few seconds here before killing it, and it
        // will NOT wait for an async handler to finish -- so this has to block
        // rather than await. It deliberately blocks via Task.Run: awaiting
        // RequestShutdownFlushAsync directly on this (UI) thread and then blocking
        // on it would deadlock, because its internal await would try to resume on
        // the very thread we're blocking. Running it on the thread pool means its
        // continuations never need the UI thread. Safe to block on: the tracker's
        // final flush touches only the DB and concurrent queues, never the
        // dispatcher.
        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            try
            {
                // RequestShutdownFlushAsync is itself bounded to ~3s; this outer
                // bound is a backstop so a wedged flush can never be the reason
                // Windows shutdown appears to hang.
                Task.Run(() => _viewModel.RequestShutdownFlushAsync())
                    .Wait(TimeSpan.FromSeconds(5));
            }
            catch { /* best-effort — never block the OS shutting down */ }

            // The flush above stops the tracker, but this event fires while the
            // shutdown can still be called off (any app may veto it). If we're
            // somehow still running a while from now, the shutdown clearly didn't
            // happen — bring the tracker back rather than sitting here silently
            // recording nothing until the next launch.
            _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ =>
            {
                if (_isForceExiting) return; // a real exit is already in progress
                try { _viewModel.RestartTrackerIfStopped(); } catch { }
            });
        }

        // ==========================================
        // DRAG AND DROP PHYSICS
        // ==========================================
        private void AppList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void AppList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Only start dragging if the left mouse button is pressed
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                System.Windows.Point position = e.GetPosition(null);

                // Prevent accidental drags by requiring a minimum distance moved
                if (Math.Abs(position.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(position.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (sender is System.Windows.Controls.ListBox listBox)
                    {
                        // Find the visual UI container of the item being dragged
                        var listBoxItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
                        if (listBoxItem != null)
                        {
                            var appItem = (AppItemModel)listBox.ItemContainerGenerator.ItemFromContainer(listBoxItem);

                            // Initialize the native Windows drag-and-drop system
                            DragDrop.DoDragDrop(listBoxItem, appItem, System.Windows.DragDropEffects.Move);
                        }
                    }
                }
            }
        }

        private void AppList_Drop(object sender, System.Windows.DragEventArgs e)
        {
            // Did we drop a valid AppItemModel?
            if (e.Data.GetDataPresent(typeof(AppItemModel)))
            {
                var droppedData = (AppItemModel)e.Data.GetData(typeof(AppItemModel));
                var target = ((FrameworkElement)e.OriginalSource).DataContext as AppItemModel;

                // Ensure we aren't dropping the item onto itself
                if (target != null && droppedData != null && target != droppedData)
                {
                    if (DataContext is MainViewModel vm)
                    {
                        int oldIndex = vm.ManagedApps.IndexOf(droppedData);
                        int newIndex = vm.ManagedApps.IndexOf(target);

                        vm.ReorderApps(oldIndex, newIndex);
                    }
                }
            }
        }

        // Helper method to traverse the visual tree and find the ListBoxItem
        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T)
                {
                    return (T)current;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isForceExiting)
            {
                // The user clicked the Red X. Cancel the shutdown, and just hide the window!
                e.Cancel = true;
                this.Hide();
            }
            else
            {
                // We are actually shutting down from the tray icon. Clean up the services.
                _trayService?.Dispose();
                _keyboardHook?.Dispose();
                base.OnClosing(e);
            }
        }

        // This is called ONLY by the "Exit" button in our TrayService's right-click menu.
        // async void is fine here — it's a terminal UI-triggered action, nothing awaits
        // its completion. Flushing before Shutdown() is what stops a normal quit from
        // silently dropping up to 60s of the day's tracking (the tracker's own flush
        // cadence) plus whatever session was still open.
        public async void ForceExit()
        {
            _isForceExiting = true;
            try
            {
                await _viewModel.RequestShutdownFlushAsync();
            }
            catch { /* best-effort — never block exit on a flush failure */ }
            System.Windows.Application.Current.Shutdown();
        }

        private void ClearHotkey_Click(object sender, RoutedEventArgs e)
        {
            var button = (Wpf.Ui.Controls.Button)sender;
            var appItem = (AppItemModel)button.DataContext;

            // Reset the database model using the new string sequence
            appItem.HotkeyDisplayText = "None";
            appItem.HotkeySequence = string.Empty;

            _viewModel.SaveDatabase();
            _viewModel.RecompileHotkeys();
        }

        // 1. When you click inside the box, it resets to a clean slate
        private void HotkeyTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            _currentCaptureKeys.Clear();

            var textBox = (Wpf.Ui.Controls.TextBox)sender;
            textBox.Text = "Listening (Press keys...)";
        }

        // 2. As you press keys, it adds them and saves instantly
        private void HotkeyTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            e.Handled = true;
            Key key = (e.Key == Key.System ? e.SystemKey : e.Key);

            // Only update if it's a new key being added to our combo
            if (_currentCaptureKeys.Add(key))
            {
                var textBox = (Wpf.Ui.Controls.TextBox)sender;
                var appItem = (AppItemModel)textBox.DataContext;

                string displayCombo = string.Join(" + ", _currentCaptureKeys.Select(k => k.ToString()));

                textBox.Text = displayCombo;
                appItem.HotkeySequence = string.Join(",", _currentCaptureKeys.Select(k => k.ToString()));
                appItem.HotkeyDisplayText = displayCombo;

                _viewModel.SaveDatabase();
                _viewModel.RecompileHotkeys();
            }
        }

        // 3. We completely ignore KeyUp so you don't ruin the capture by letting go too early
        private void HotkeyTextBox_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            e.Handled = true;
        }

        // This fires the millisecond the window is actually created by the OS
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 1. Self-Healing Startup Check
            // If they moved the .exe, this instantly fixes the OS paths!
            if (!StartupTaskService.IsStartupCorrectlyRegistered())
            {
                StartupTaskService.SetStartup(true);
            }
        }

        // Cleanup to prevent memory leaks when the app closes
        protected override void OnClosed(EventArgs e)
        {
            _keyboardHook?.Dispose();
            base.OnClosed(e);
        }
    }
}