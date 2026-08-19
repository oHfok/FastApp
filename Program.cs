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
            // ==========================================================
            using var singleInstanceMutex = new Mutex(initiallyOwned: true, name: "FastApp_SingleInstance_Mutex", createdNew: out bool isNewInstance);
            if (!isNewInstance)
            {
                // Another full instance is already running — just exit quietly.
                Environment.Exit(0);
                return;
            }

            App app = new App();
            app.InitializeComponent();
            app.Run();

            // Keep the mutex alive for the entire lifetime of the app.
            GC.KeepAlive(singleInstanceMutex);
        }
    }
}