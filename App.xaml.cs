using System;
using System.Configuration;
using System.Data;
using System.Windows;
using System.Linq;
using Wpf.Ui.Appearance;

namespace FastApp
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        // Kept in step with BrandBrassColor in Themes/Brand.xaml and --brass in
        // wwwroot/css/base.css. WPF-UI's accent has to be applied in code, so
        // this one value cannot live in the dictionary with the others.
        private const string BrandAccentHex = "#E8A33D";

        private static System.Windows.Media.Color Brand(string hex) =>
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

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

            // Accent the Fluent controls with the product's brass rather than
            // whatever blue Windows happens to be set to. Everything with
            // Appearance="Primary" picks this up -- most visibly the "Open Web
            // Dashboard" button, which is the one place a user crosses from the
            // desktop app into the dashboard and would otherwise click a blue
            // button and land in a brass interface.
            //
            // Driven through WPF-UI rather than by overriding its resource keys
            // by hand: this overload derives the light/dark variants the control
            // templates expect, so hover, pressed and disabled states stay
            // consistent instead of only the resting colour changing.
            // The four-colour overload, not the theme one: the theme overload
            // derives a pale cream for the button fill, and the point is for a
            // Primary button to be the same brass as the dashboard's own.
            // Verified against WPF-UI 4.3.0 with sentinel colours --
            // AccentFillColorDefaultBrush, the resting fill, comes from the
            // second argument.
            ApplicationAccentColorManager.Apply(
                Brand(BrandAccentHex),   // systemAccent  -> SystemAccentBrush
                Brand(BrandAccentHex),   // primaryAccent -> accent fill, matches .btn-brass
                Brand("#F0B155"),   // secondary     -> accent-coloured text on dark
                Brand("#F5C57E"));  // tertiary      -> quieter accent text

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