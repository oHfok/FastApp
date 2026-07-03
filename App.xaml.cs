using System;
using System.Configuration;
using System.Data;
using System.Windows;
using System.Linq;

namespace FastApp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 1. Global Error Traps: Catch silent crashes before the app closes!
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                System.Windows.MessageBox.Show(ex.ExceptionObject.ToString(), "Fatal App Crash");
            };

            DispatcherUnhandledException += (s, ex) =>
            {
                System.Windows.MessageBox.Show(ex.Exception.ToString(), "Fatal UI Crash");
                ex.Handled = true;
            };

            base.OnStartup(e);

            // 2. Create the window in memory. 
            // Because your TrayService is initialized in the MainWindow constructor, 
            // your system tray icon will instantly appear right here!
            var mainWindow = new MainWindow();

            // 3. Check if Windows launched us via the Registry on boot
            if (e.Args.Contains("--minimized"))
            {
                // Do NOTHING. We created the window in memory so the tray works, 
                // but we NEVER call .Show(). The app remains completely invisible!
            }
            else
            {
                // The user double-clicked the .exe manually. Show the UI normally.
                mainWindow.Show();
            }
        }
    }
}