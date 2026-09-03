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
        private static readonly Uri LightBrandUri =
            new("pack://application:,,,/Themes/Brand.Light.xaml", UriKind.Absolute);

        /// <summary>
        /// Merge the light dictionary over the brand palette, or take it away.
        ///
        /// Merged rather than swapped wholesale: Brand.Light.xaml carries only
        /// the keys whose meaning changes, so everything it does not mention
        /// keeps the value Brand.xaml gave it and the two files cannot drift
        /// apart on the values they share.
        /// </summary>
        private static void ApplySystemTheme()
        {
            var merged = Current?.Resources?.MergedDictionaries;
            if (merged == null) return;

            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].Source == LightBrandUri) merged.RemoveAt(i);
            }

            if (Services.SystemTheme.IsLight)
            {
                merged.Add(new ResourceDictionary { Source = LightBrandUri });
            }
        }

        // Kept in step with BrandBrassColor in Themes/Brand.xaml and --brass in
        // wwwroot/css/base.css. WPF-UI's accent has to be applied in code, so
        // this one value cannot live in the dictionary with the others.
        private const string BrandAccentHex = "#E8A33D";

        private static System.Windows.Media.Color Brand(string hex) =>
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

        protected override void OnStartup(StartupEventArgs e)
        {
            // Before anything renders, so the first window is already the right
            // colour rather than repainting on its first frame.
            Services.SystemTheme.Start();
            ApplySystemTheme();
            Services.SystemTheme.Changed += ApplySystemTheme;

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

            // 2. Create the host. It owns the tray icon, the keyboard hook, the
            //    view model and the palette; it is never shown itself.
            var mainWindow = new MainWindow();

            // 3. The host window is never shown -- it has no interface any more.
            //    Opening FastApp by hand means opening the palette; launching at
            //    boot means staying silent in the tray.
            if (!e.Args.Contains("--minimized"))
            {
                mainWindow.ShowPaletteWhenReady();
            }
        }
    }
}