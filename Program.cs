using FastApp.Services;
using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Velopack;

namespace FastApp
{
    public class Program
    {
        [STAThread] // THIS IS THE MAGIC LINE
        public static void Main(string[] args)
        {
            // ==========================================================
            // MUST BE ABSOLUTE FIRST, before even the elevated-relaunch check
            // below: on a freshly installed/updated/uninstalled build, Velopack
            // launches the exe with special --veloapp-* args to run its own
            // install/update/uninstall hooks, then exits the process itself.
            // On a normal launch (no such args) this just returns immediately.
            // ==========================================================
            VelopackApp.Build().Run();

            // ==========================================================
            // MUST BE FIRST: handle the brief, elevated, UI-less relaunch
            // used purely to register/unregister the scheduled task.
            // If this check isn't first, the elevated relaunch runs the
            // ENTIRE app (full UI + constructor), which can re-trigger
            // SetStartup() again and spawn another relaunch — recursively,
            // without limit. Do not move anything above this block.
            // ==========================================================
            if (args.Contains(StartupTaskService.ElevatedRegisterArg))
            {
                Environment.Exit(StartupTaskService.RunElevatedRegistrationWorker(enable: true));
                return;
            }
            if (args.Contains(StartupTaskService.ElevatedUnregisterArg))
            {
                Environment.Exit(StartupTaskService.RunElevatedRegistrationWorker(enable: false));
                return;
            }

            // ==========================================================
            // SAFETY NET: never allow more than one full instance of the
            // normal app to run at the same time. Even if some future bug
            // causes an unexpected relaunch, this stops it from spiraling
            // into dozens/hundreds of running processes.
            //
            // Retries a few times before giving up: both the auto-updater and
            // the backup-restore feature self-restart by launching a new
            // instance and then calling Environment.Exit() on the old one.
            // Windows releases a named mutex as soon as its owning process
            // exits, but there's no guarantee the new process won't reach
            // this check before that's happened — a single failed attempt
            // here isn't proof another real instance is running.
            // ==========================================================
            Mutex singleInstanceMutex = null;
            bool isNewInstance = false;
            for (int attempt = 0; attempt < 10 && !isNewInstance; attempt++)
            {
                if (attempt > 0) Thread.Sleep(200);
                singleInstanceMutex?.Dispose();
                singleInstanceMutex = new Mutex(initiallyOwned: true, name: "FastApp_SingleInstance_Mutex", createdNew: out isNewInstance);
            }
            if (!isNewInstance)
            {
                // Another full instance is already running. Exiting silently here
                // meant launching FastApp from the desktop/Start menu while it sat
                // minimized in the tray did visibly nothing at all, which reads as
                // the app being broken. Poke the running instance so it brings its
                // window up, which is what clicking the icon was asking for.
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var showEvent))
                    {
                        using (showEvent) showEvent.Set();
                    }
                }
                catch { /* the other instance may be mid-exit — nothing useful to do */ }

                Environment.Exit(0);
                return;
            }

            // Owned by this (the only) instance: later launches set it to ask for
            // the window. Created before App so a launch racing ours still finds it.
            using var showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            StartShowWindowListener(showWindowEvent);

            App app = new App();
            app.InitializeComponent();
            app.Run();

            // Keep the mutex alive for the entire lifetime of the app.
            GC.KeepAlive(singleInstanceMutex);
        }

        private const string ShowWindowEventName = "FastApp_ShowWindow_Event";

        // Background, IsBackground so it can never keep the process alive on exit.
        private static void StartShowWindowListener(EventWaitHandle showWindowEvent)
        {
            var listener = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        showWindowEvent.WaitOne();

                        var current = System.Windows.Application.Current;
                        if (current == null) continue; // not up yet (or already gone)

                        current.Dispatcher.Invoke(() =>
                        {
                            var window = current.MainWindow;
                            if (window == null) return;
                            window.Show();
                            window.WindowState = WindowState.Normal;
                            window.Activate();
                        });
                    }
                    catch
                    {
                        // Shutting down mid-wait, or the dispatcher is gone. Either
                        // way there's no window left to raise.
                        return;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "FastApp show-window listener"
            };
            listener.Start();
        }
    }
}