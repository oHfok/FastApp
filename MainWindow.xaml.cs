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

        public void ShowPalette(PaletteView view = PaletteView.Search)
        {
            if (_palette != null && _palette.Unavailable == null)
            {
                _palette.ShowPalette(view);
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
        /// <summary>Tell the tray to re-read the summon combination.</summary>
        public void RefreshTrayHotkey() => _trayService?.RefreshHotkeyText();

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

            // Read before the hook is wired, so the very first key press is
            // matched against the stored combination rather than the default.
            // After the view model, because that is what migrates the database.
            LoadPaletteHotkey();

            // 4. WIRE THE HOOK: Connect the live hook to the loaded ViewModel.
            //
            // Not subscribed directly: while a hotkey is being recorded, the keys
            // being pressed are the recording and must not also mean whatever
            // they meant before. Re-binding Valorant's V+A+L launched Valorant
            // on the way to replacing it.
            _keyboardHook.KeysChanged += keys =>
            {
                if (_capturingHotkey) return;
                _viewModel.CheckForHotkeys(keys);
            };
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

        // ---- The combination that summons the palette ---------------------
        //
        // This was a pair of hardcoded checks with a comment explaining that it
        // was "reserved rather than bindable" so nobody could reassign their way
        // out of the app. That risk is smaller than it reads: the tray icon
        // opens the palette too, and it cannot be rebound. So the combination is
        // a setting now, and the tray is the way back if a choice turns out to
        // be unreachable.
        //
        // Stored the same way an app's hotkey is, as WPF key names, so
        // HotkeyText can describe both and there is one format to understand.
        public const string DefaultPaletteHotkey = "LeftCtrl,LeftShift,Space";
        public const string PaletteHotkeyKey = "PaletteHotkey";

        private static string _paletteSequence = DefaultPaletteHotkey;

        // The parsed form, rebuilt whenever the sequence changes: which modifier
        // kinds must be held, and which ordinary keys. Matched by kind rather
        // than by side, because this used to compare against {LeftCtrl,
        // LeftShift, Space} or {RightCtrl, RightShift, Space}, so the perfectly
        // ordinary habit of holding left Ctrl and right Shift matched neither
        // and the hotkey simply did nothing.
        private static bool _needCtrl, _needShift, _needAlt, _needWin;
        private static HashSet<System.Windows.Input.Key> _needPlain = new();

        /// <summary>The stored combination, as key names.</summary>
        public static string PaletteHotkeySequence => _paletteSequence;

        /// <summary>How it is written wherever it is shown.</summary>
        public static string PaletteHotkeyDisplay => Services.HotkeyText.Describe(_paletteSequence);

        /// <summary>Read the stored combination, falling back to the default.</summary>
        public static void LoadPaletteHotkey()
        {
            string stored = Services.AppSettingsStore.Get(PaletteHotkeyKey, DefaultPaletteHotkey);
            ApplyPaletteHotkey(string.IsNullOrWhiteSpace(stored) ? DefaultPaletteHotkey : stored);
        }

        /// <summary>Record a new combination and start answering to it.</summary>
        public static void SetPaletteHotkey(string sequence)
        {
            string next = string.IsNullOrWhiteSpace(sequence) ? DefaultPaletteHotkey : sequence;
            ApplyPaletteHotkey(next);
            Services.AppSettingsStore.Set(PaletteHotkeyKey, next);
        }

        private static void ApplyPaletteHotkey(string sequence)
        {
            var keys = new HashSet<System.Windows.Input.Key>();
            foreach (var name in (sequence ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Enum.TryParse<System.Windows.Input.Key>(name.Trim(), out var key)) keys.Add(key);
            }

            // A combination with no ordinary key would fire on Ctrl alone, and
            // an unparsable one would never fire at all. Either way the app
            // would have no way in, so both fall back rather than being obeyed.
            if (keys.Count == 0 || !keys.Any(k => !IsModifier(k)))
            {
                sequence = DefaultPaletteHotkey;
                keys = new HashSet<System.Windows.Input.Key>
                {
                    System.Windows.Input.Key.LeftCtrl,
                    System.Windows.Input.Key.LeftShift,
                    System.Windows.Input.Key.Space
                };
            }

            _paletteSequence = sequence;
            _needCtrl = _needShift = _needAlt = _needWin = false;
            var plain = new HashSet<System.Windows.Input.Key>();

            foreach (var key in keys)
            {
                switch (key)
                {
                    case System.Windows.Input.Key.LeftCtrl:
                    case System.Windows.Input.Key.RightCtrl: _needCtrl = true; break;
                    case System.Windows.Input.Key.LeftShift:
                    case System.Windows.Input.Key.RightShift: _needShift = true; break;
                    case System.Windows.Input.Key.LeftAlt:
                    case System.Windows.Input.Key.RightAlt:
                    case System.Windows.Input.Key.System: _needAlt = true; break;
                    case System.Windows.Input.Key.LWin:
                    case System.Windows.Input.Key.RWin: _needWin = true; break;
                    default: plain.Add(key); break;
                }
            }
            _needPlain = plain;
        }

        private static bool IsPaletteCombo(HashSet<System.Windows.Input.Key> pressed)
        {
            if (pressed == null || pressed.Count == 0) return false;

            foreach (var key in _needPlain)
            {
                if (!pressed.Contains(key)) return false;
            }

            bool ctrl = pressed.Contains(System.Windows.Input.Key.LeftCtrl)
                        || pressed.Contains(System.Windows.Input.Key.RightCtrl);
            bool shift = pressed.Contains(System.Windows.Input.Key.LeftShift)
                         || pressed.Contains(System.Windows.Input.Key.RightShift);
            bool alt = pressed.Contains(System.Windows.Input.Key.LeftAlt)
                       || pressed.Contains(System.Windows.Input.Key.RightAlt)
                       || pressed.Contains(System.Windows.Input.Key.System);
            bool win = pressed.Contains(System.Windows.Input.Key.LWin)
                       || pressed.Contains(System.Windows.Input.Key.RWin);

            // Exactly the modifiers asked for, no more. Ctrl+Shift+Space is
            // ours; Ctrl+Alt+Shift+Space belongs to whatever the user has bound
            // it to, and swallowing it would make FastApp the thing that broke
            // their shortcut.
            if (ctrl != _needCtrl || shift != _needShift || alt != _needAlt || win != _needWin) return false;

            // And nothing else held at all.
            foreach (var key in pressed)
            {
                if (_needPlain.Contains(key) || IsModifier(key)) continue;
                return false;
            }

            return true;
        }

        // ---- Hotkey capture for the palette -------------------------------
        // The palette cannot capture a combination itself: a WebView2 only sees
        // keys the browser chooses to surface, and never the ones another app
        // has already swallowed. The low-level hook sees everything, so capture
        // borrows it and reports the result back.
        // volatile: written on the UI thread when capture starts and stops, read
        // on the keyboard hook's thread on every keystroke.
        private volatile bool _capturingHotkey;

        // A list rather than a set, so the combination reads back in the order
        // it was pressed: "LeftCtrl + LeftShift + K" rather than whatever order
        // a hash set happens to enumerate.
        //
        // Guarded, because it is added to on the keyboard hook's thread and
        // cleared on the UI thread when recording is cancelled. Clicking away
        // mid-combination could otherwise land inside a List resize. The lock is
        // uncontended in practice, which is what the hook needs.
        private readonly object _captureGate = new();
        private readonly List<System.Windows.Input.Key> _paletteCapture = new();

        /// <summary>Raised with (sequence, displayText) once the keys are released.</summary>
        public event Action<string, string> HotkeyCaptured;

        /// <summary>
        /// Raised as keys go down, with the combination so far. Recording used
        /// to show nothing at all until every key came back up, which reads as
        /// an unresponsive control rather than as one that is listening.
        /// </summary>
        public event Action<string> HotkeyCaptureProgress;

        /// <summary>
        /// A modifier cannot be a binding on its own: it would fire every time
        /// the user held it for anything else.
        /// </summary>
        private static bool IsModifier(System.Windows.Input.Key key) => key
            is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.System;

        public void BeginHotkeyCapture()
        {
            lock (_captureGate) _paletteCapture.Clear();
            _capturingHotkey = true;
        }

        public void CancelHotkeyCapture()
        {
            _capturingHotkey = false;
            lock (_captureGate) _paletteCapture.Clear();
        }

        // The same formatter the rest of the app uses, so what you see while
        // recording is what the row shows afterwards.
        private static string Describe(IEnumerable<System.Windows.Input.Key> keys) =>
            Services.HotkeyText.Describe(keys);

        private void CheckPaletteHotkey(HashSet<System.Windows.Input.Key> pressed)
        {
            if (_capturingHotkey)
            {
                // Grow the list while keys go down; commit once the last one
                // comes up, so "Ctrl then Shift then S" records all three
                // rather than just whichever arrived first.
                if (pressed.Count > 0)
                {
                    string sofar = null;
                    lock (_captureGate)
                    {
                        foreach (var key in pressed)
                        {
                            if (_paletteCapture.Contains(key)) continue;
                            _paletteCapture.Add(key);
                            sofar = Describe(_paletteCapture);
                        }
                    }

                    // Shown immediately, so the field fills in as the keys go
                    // down instead of waiting for the release.
                    if (sofar != null)
                        Dispatcher.BeginInvoke(new Action(() => HotkeyCaptureProgress?.Invoke(sofar)));
                    return;
                }

                string sequence, display;
                lock (_captureGate)
                {
                    if (_paletteCapture.Count == 0) return;

                    // Modifiers alone are not a binding. Stay in recording
                    // rather than saving something that would fire constantly,
                    // and say why.
                    if (!_paletteCapture.Any(k => !IsModifier(k)))
                    {
                        _paletteCapture.Clear();
                        Dispatcher.BeginInvoke(new Action(() =>
                            HotkeyCaptureProgress?.Invoke("Add a key to those modifiers")));
                        return;
                    }

                    sequence = string.Join(",", _paletteCapture.Select(k => k.ToString()));
                    display = Describe(_paletteCapture);
                    _paletteCapture.Clear();
                }

                _capturingHotkey = false;
                Dispatcher.BeginInvoke(new Action(() => HotkeyCaptured?.Invoke(sequence, display)));
                return;
            }

            if (!IsPaletteCombo(pressed)) return;

            // Hop to the UI thread: this arrives on the keyboard hook's thread,
            // where touching a Window is not allowed and being slow gets the
            // hook uninstalled.
            //
            // ShowPaletteWhenReady rather than _palette?.ShowPalette(): the
            // palette is warmed a moment after launch, and pressing the hotkey
            // before that finished used to fall through the null-conditional
            // and do nothing at all.
            Dispatcher.BeginInvoke(new Action(ShowPaletteWhenReady));
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