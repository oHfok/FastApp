using FastApp.ViewModels;
using IWshRuntimeLibrary; // This is the COM reference you just added
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastApp.ViewModels; // To access AppItemModel

namespace FastApp.Services
{
    public static class AppScannerService
    {
        public static List<AppItemModel> GetInstalledApps()
        {
            var detectedApps = new List<AppItemModel>();
            var wshShell = new WshShell();

            // Windows stores shortcuts in two places: The current user's profile, and the global system profile
            string[] startMenuPaths = {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            };

            foreach (var startMenu in startMenuPaths)
            {
                string programsFolder = Path.Combine(startMenu, "Programs");
                if (!Directory.Exists(programsFolder)) continue;

                // Grab every single shortcut file (.lnk) recursively
                string[] shortcuts = Directory.GetFiles(programsFolder, "*.lnk", SearchOption.AllDirectories);

                foreach (var shortcut in shortcuts)
                {
                    try
                    {
                        // Open the shortcut and extract where it actually points
                        IWshShortcut link = (IWshShortcut)wshShell.CreateShortcut(shortcut);
                        string targetPath = link.TargetPath;

                        // Only grab actual .exe files. 
                        // Ignore website links, folders, and uninstaller executables.
                        if (!string.IsNullOrEmpty(targetPath) &&
                            targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            !targetPath.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                        {
                            // Clean up the name (e.g. "Visual Studio 2022.lnk" -> "Visual Studio 2022")
                            string cleanName = Path.GetFileNameWithoutExtension(shortcut);

                            // Avoid adding duplicates if it exists in both Start Menus
                            if (!detectedApps.Any(a => a.ExecutablePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase)))
                            {
                                detectedApps.Add(new AppItemModel(cleanName, targetPath));
                            }
                        }
                    }
                    catch
                    {
                        // If a shortcut is corrupted or requires admin rights, just skip it and move on
                    }
                }
            }

            // Return the list sorted alphabetically
            return detectedApps.OrderBy(a => a.Name).ToList();
        }
    }
}