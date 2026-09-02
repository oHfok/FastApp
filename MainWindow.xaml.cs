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
    // No longer a FluentWindow: it is never shown, so the Fluent chrome, backdrop
    // and title bar it brought are all cost with nothing to render.
    public partial class MainWindow : Window
    {

        private MainViewModel _viewModel;
        public MainViewModel ViewModel => _viewModel;

        // The 2.0 surface. Created once and hidden between uses rather than
        // built on demand: a cold WebView2 costs a few hundred milliseconds to
        // first paint, which is the difference between a palette you reach for
        // and one you stop bothering with.
        private PaletteWindow _palette;
        public PaletteWindow Palette => _palette;

        public void ShowPalette()
        {
            if (_palette != null && _palette.Unavailable == null)
            {
                _palette.ShowPalette();
                return;
            }

            ExplainMissingInterface();
        }

        // FastApp has no other interface now, so a palette that cannot start
        // leaves an app that appears to do nothing at all when you click it.
        // WebView2 ships with Windows 11 and with recent Windows 10, but it can
        // be absent or broken, and silence is the worst possible answer.
        private bool _explained;

        private void ExplainMissingInterface()
        {
            if (_explained) return;
            _explained = true;

            string reason = _palette?.Unavailable ?? "The interface could not be created.";
            var result = System.Windows.MessageBox.Show(
                "FastApp needs the Microsoft Edge WebView2 runtime to show its "
                + "interface, and it could not be started on this PC.\n\n"
                + "Tracking, hotkeys and daily limits are all still running in the "
                + "background, and the statistics dashboard still works in your "
                + "browser.\n\nOpen the WebView2 download page?\n\n"
                + "Details: " + reason,
                "FastApp",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            _explained = false;

            if (result != System.Windows.MessageBoxResult.Yes) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://developer.microsoft.com/microsoft-edge/webview2/",
                    UseShellExecute = true
                });
            }
            catch { /* nothing useful to do if the shell refuses */ }
        }

        /// <summary>
        /// Show the palette as soon as it exists. Launching FastApp by hand
        /// arrives before the warm-up has finished, and dropping that request
        /// would mean double-clicking the app and getting nothing at all.
        /// </summary>
        public void ShowPaletteWhenReady()
        {
            if (_palette != null) { ShowPalette(); return; }
            _showPaletteWhenWarm = true;
        }

        private bool _showPaletteWhenWarm;

        // NEW: The advanced global hook
        private AdvancedKeyboardHook _keyboardHook;

        private TrayService _trayService;
        private bool _isForceExiting = false;

        // NEW: Variables to track keys while recording a hotkey on the UI

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
            _keyboardHook.ShouldSuppress = _viewModel.ShouldSuppressHotkey;
            _keyboardHook.KeysChanged += CheckPaletteHotkey;

            // 5. SHUTDOWN SAFETY NET: Windows shutting down / restarting / logging
            // off never goes through the tray Exit path, so without this the most
            // COMMON way this app ends (the user turning their PC off) was also the
            // only one that skipped the graceful flush entirely -- losing the open
            // session plus up to 60s of tracking, and leaving the WAL unchecked-
            // pointed. That last part is the same exposure that preceded the
            // 2026-08-19 corruption; the update path was hardened against it at the
            // time, but this far more frequent path was not.
            System.Windows.Application.Current.SessionEnding += OnSessionEnding;

            // Warmed after the window is up so it never competes with first
            // paint of the manager, and never on the startup path's critical
            // section.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    _palette = new PaletteWindow(_viewModel);
                    await _palette.PrewarmAsync();
                    if (_showPaletteWhenWarm)
                    {
                        _showPaletteWhenWarm = false;
                        ShowPalette();
                    }
                }
                catch (Exception ex)
                {
                    // No WebView2 runtime, most likely. The manager window is
                    // untouched, so this costs the palette and nothing else.
                    System.Diagnostics.Debug.WriteLine($"Palette unavailable: {ex.Message}");
                    _palette = null;
                    if (_showPaletteWhenWarm)
                    {
                        _showPaletteWhenWarm = false;
                        ExplainMissingInterface();
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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

        // Ctrl+Shift+Space summons the palette. Reserved rather than bindable:
        // it is the way into the app, so it cannot be something the user can
        // accidentally reassign to Notepad and then have no way back.
        private static readonly HashSet<System.Windows.Input.Key> PaletteCombo = new()
        {
            System.Windows.Input.Key.LeftCtrl,
            System.Windows.Input.Key.LeftShift,
            System.Windows.Input.Key.Space
        };

        private static readonly HashSet<System.Windows.Input.Key> PaletteComboRight = new()
        {
            System.Windows.Input.Key.RightCtrl,
            System.Windows.Input.Key.RightShift,
            System.Windows.Input.Key.Space
        };

        // ---- Hotkey capture for the palette -------------------------------
        // The palette cannot capture a combination itself: a WebView2 only sees
        // keys the browser chooses to surface, and never the ones another app
        // has already swallowed. The low-level hook sees everything, so capture
        // borrows it and reports the result back.
        private bool _capturingHotkey;
        private readonly HashSet<System.Windows.Input.Key> _paletteCapture = new();

        /// <summary>Raised with (sequence, displayText) once the keys are released.</summary>
        public event Action<string, string> HotkeyCaptured;

        public void BeginHotkeyCapture()
        {
            _paletteCapture.Clear();
            _capturingHotkey = true;
        }

        public void CancelHotkeyCapture() => _capturingHotkey = false;

        private void CheckPaletteHotkey(HashSet<System.Windows.Input.Key> pressed)
        {
            if (_capturingHotkey)
            {
                // Grow the set while keys go down; report it once the last one
                // comes up, so "Ctrl then Shift then S" records all three
                // rather than just whichever arrived first.
                if (pressed.Count > 0)
                {
                    foreach (var key in pressed) _paletteCapture.Add(key);
                    return;
                }

                if (_paletteCapture.Count == 0) return;

                string sequence = string.Join(",", _paletteCapture.Select(k => k.ToString()));
                string display = string.Join(" + ", _paletteCapture.Select(k => k.ToString()));
                _capturingHotkey = false;
                _paletteCapture.Clear();

                Dispatcher.BeginInvoke(new Action(() => HotkeyCaptured?.Invoke(sequence, display)));
                return;
            }

            if (!pressed.SetEquals(PaletteCombo) && !pressed.SetEquals(PaletteComboRight)) return;

            // Hop to the UI thread: this arrives on the keyboard hook's thread,
            // where touching a Window is not allowed and being slow gets the
            // hook uninstalled.
            Dispatcher.BeginInvoke(new Action(() => _palette?.ShowPalette()));
        }

        // This fires the millisecond the window is actually created by the OS
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // There used to be a "self-healing" startup check here: if the
            // registered path did not match this exe, it called SetStartup(true)
            // on the spot. That fires a UAC prompt, on the UI thread, with the
            // window frozen behind it, without asking -- and it re-enabled
            // startup for anyone who had deliberately turned it off. When two
            // copies of FastApp exist (an installed build and a debug or
            // portable one) each rewrites the other's task on launch: the
            // startup log recorded four such re-registrations in a single day.
            //
            // The state is now reported instead. MainViewModel refreshes it and
            // surfaces a Fix button in Settings when a different copy owns the
            // registration, so the prompt only ever appears because it was asked
            // for. See RefreshStartupStateAsync.
            _viewModel?.RefreshStartupState();
        }

        // Cleanup to prevent memory leaks when the app closes
        protected override void OnClosed(EventArgs e)
        {
            _palette?.Close();
            _keyboardHook?.Dispose();
            base.OnClosed(e);
        }
    }
}