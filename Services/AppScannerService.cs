using FastApp.ViewModels;
using IWshRuntimeLibrary; // This is the COM reference you just added
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FastApp.ViewModels; // To access AppItemModel

namespace FastApp.Services
{
    public static class AppScannerService
    {
        // The scan is slow enough to be felt: the Start Menu pass opens a few
        // hundred shortcuts through COM (~270ms here) and the packaged pass adds
        // ~1.3s, split fairly evenly between enumerating the package manager and
        // walking the Apps folder. Both are Windows API cost rather than
        // something to optimise away, so the scan runs off the UI thread instead
        // of freezing the window while it works.
        //
        // Its own thread rather than Task.Run: WshShell and Shell.Application are
        // STA COM objects, and the thread pool is MTA, which would marshal every
        // one of those several hundred calls across an apartment boundary.
        public static Task<List<AppItemModel>> GetInstalledAppsAsync()
        {
            var completion = new TaskCompletionSource<List<AppItemModel>>();
            var thread = new Thread(() =>
            {
                try { completion.SetResult(GetInstalledApps()); }
                catch (Exception ex) { completion.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return completion.Task;
        }

        public static List<AppItemModel> GetInstalledApps()
        {
            var detectedApps = new List<AppItemModel>();

            AddStartMenuApps(detectedApps);

            // Store/MSIX apps never appear above: they do not create a .lnk at all.
            // Windows registers them with the package manager and launches them
            // through shell:AppsFolder, so a Start-Menu-shortcut scan is blind to
            // every one of them -- Claude, Netflix, Teams, Instagram and the rest.
            AddPackagedApps(detectedApps);

            // Return the list sorted alphabetically
            return detectedApps.OrderBy(a => a.Name).ToList();
        }

        private static void AddStartMenuApps(List<AppItemModel> detectedApps)
        {
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

                            TryAdd(detectedApps, cleanName, targetPath);
                        }
                    }
                    catch
                    {
                        // If a shortcut is corrupted or requires admin rights, just skip it and move on
                    }
                }
            }
        }

        // The Apps folder is the same list the Start menu's "All apps" shows. It is
        // used here only for the NAME and identity: display names come out already
        // localised and resolved, where reading them from the package manifest
        // yields raw "ms-resource:AppName" placeholders for well over half of them.
        private const string AppsFolderShellId = "shell:::{4234d49b-0245-4df3-b780-3893943456e1}";

        private static void AddPackagedApps(List<AppItemModel> detectedApps)
        {
            try
            {
                var installLocations = GetPackageInstallLocations();
                if (installLocations.Count == 0) return;

                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return;

                object shell = Activator.CreateInstance(shellType);
                object folder = shellType.InvokeMember("NameSpace",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { AppsFolderShellId });
                if (folder == null) return;

                object items = folder.GetType().InvokeMember("Items",
                    System.Reflection.BindingFlags.InvokeMethod, null, folder, null);
                int count = (int)items.GetType().InvokeMember("Count",
                    System.Reflection.BindingFlags.GetProperty, null, items, null);

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        object item = items.GetType().InvokeMember("Item",
                            System.Reflection.BindingFlags.InvokeMethod, null, items, new object[] { i });
                        if (item == null) continue;

                        string name = item.GetType().InvokeMember("Name",
                            System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                        string parsingName = item.GetType().InvokeMember("Path",
                            System.Reflection.BindingFlags.GetProperty, null, item, null) as string;

                        string exe = ResolvePackagedExecutable(parsingName, installLocations);
                        // parsingName is already "<PackageFamilyName>!<ApplicationId>",
                        // which is the AUMID. It used to be discarded in favour of the
                        // path, and the path is the part that goes stale on update.
                        if (exe != null && !string.IsNullOrWhiteSpace(name)) TryAdd(detectedApps, name, exe, parsingName);
                    }
                    catch
                    {
                        // One unreadable entry must not cost the rest of the list.
                    }
                }
            }
            catch
            {
                // No packaged apps rather than no scan at all: the Start Menu
                // results above are already in the list and stay there.
            }
        }

        // A packaged entry's parsing name is "<PackageFamilyName>!<ApplicationId>";
        // a plain Win32 entry's is a file path. Testing for '!' alone is not enough
        // to tell them apart -- "osu!" installs to ...\osu!\osu!.exe -- so the
        // family name has to actually match an installed package.
        private static string ResolvePackagedExecutable(string parsingName, Dictionary<string, string> installLocations)
        {
            if (string.IsNullOrWhiteSpace(parsingName)) return null;

            int bang = parsingName.IndexOf('!');
            if (bang <= 0 || bang == parsingName.Length - 1) return null;

            string family = parsingName.Substring(0, bang);
            string appId = parsingName.Substring(bang + 1);
            if (!installLocations.TryGetValue(family, out string installLocation)) return null;

            string manifestPath = Path.Combine(installLocation, "AppxManifest.xml");
            if (!System.IO.File.Exists(manifestPath)) return null;

            XDocument manifest;
            try { manifest = XDocument.Load(manifestPath); }
            catch { return null; }

            // Matched on LocalName so the several manifest schema namespaces in
            // circulation all parse the same way.
            var application = manifest.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Application"
                                     && (string)e.Attribute("Id") == appId);
            if (application == null) return null;

            // Hosted apps (Netflix, Instagram, Messenger and other PWA-style
            // packages) declare no Executable -- they run inside a Windows host
            // process. There is no path to point a managed app at, and the tracker
            // would see the host process rather than them, so they are skipped
            // instead of being added as an entry that could never work.
            string executable = (string)application.Attribute("Executable");
            if (string.IsNullOrWhiteSpace(executable)) return null;

            string fullPath = Path.Combine(installLocation, executable);
            return System.IO.File.Exists(fullPath) ? fullPath : null;
        }

        // PackageFamilyName -> install folder, for the current user's packages.
        private static Dictionary<string, string> GetPackageInstallLocations()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var manager = new Windows.Management.Deployment.PackageManager();
                foreach (var package in manager.FindPackagesForUser(string.Empty))
                {
                    try
                    {
                        // Frameworks and resource packages carry no launchable app.
                        if (package.IsFramework || package.IsResourcePackage) continue;

                        string location = package.InstalledLocation?.Path;
                        if (!string.IsNullOrEmpty(location)) map[package.Id.FamilyName] = location;
                    }
                    catch
                    {
                        // A staged or partially-installed package throws on access.
                    }
                }
            }
            catch
            {
                // Leaves the map empty, which skips packaged apps entirely.
            }
            return map;
        }

        private static void TryAdd(List<AppItemModel> detectedApps, string name, string executablePath,
                                   string packagedAppId = null)
        {
            // Avoid adding duplicates if it exists in both Start Menus, or if a
            // packaged app also happens to have shipped a shortcut.
            if (detectedApps.Any(a => a.ExecutablePath.Equals(executablePath, StringComparison.OrdinalIgnoreCase))) return;

            detectedApps.Add(new AppItemModel(name, executablePath)
            {
                PackagedAppId = packagedAppId ?? string.Empty
            });
        }

        /// <summary>
        /// The AUMID for an installed package family, or null if it is not
        /// installed. Used to relink entries stored before the AUMID was kept,
        /// whose WindowsApps path died with the version folder it named.
        /// </summary>
        public static string TryResolveAumid(string packageFamilyName)
        {
            try
            {
                var installLocations = GetPackageInstallLocations();
                if (!installLocations.TryGetValue(packageFamilyName, out string installLocation)) return null;

                string manifestPath = Path.Combine(installLocation, "AppxManifest.xml");
                if (!System.IO.File.Exists(manifestPath)) return null;

                var manifest = XDocument.Load(manifestPath);

                // The first Application that actually declares an executable:
                // hosted/PWA-style entries declare none and cannot be launched
                // this way either.
                var application = manifest.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Application"
                                         && !string.IsNullOrWhiteSpace((string)e.Attribute("Executable"))
                                         && !string.IsNullOrWhiteSpace((string)e.Attribute("Id")));

                string appId = (string)application?.Attribute("Id");
                return string.IsNullOrWhiteSpace(appId) ? null : $"{packageFamilyName}!{appId}";
            }
            catch
            {
                return null;
            }
        }
    }
}
