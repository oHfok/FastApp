using FastApp.Services;
using FastApp.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
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

        // This is called ONLY by the "Exit" button in our TrayService's right-click menu
        public void ForceExit()
        {
            _isForceExiting = true;
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