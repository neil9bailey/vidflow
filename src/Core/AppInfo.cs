using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VidFlow.Core
{
    // Public identity shown in About and used for links. About points at ProjectUrl for the source
    // code (GPL "corresponding source"); Repo is the default for Check for Updates.
    public static class AppInfo
    {
        public const string Name = "VidFlow Player";
        public const string ShortName = "VidFlow";
        public const string Repo = "neil9bailey/vidflow";
        public const string ProjectUrl = "https://github.com/" + Repo;

        // "1.0.0-beta.3": the informational version build.ps1 stamps from VERSION or the release tag.
        public static string Version
        {
            get
            {
                var asm = typeof(AppInfo).Assembly;
                var info = (AssemblyInformationalVersionAttribute)System.Attribute.GetCustomAttribute(asm, typeof(AssemblyInformationalVersionAttribute));
                if (info != null && !string.IsNullOrEmpty(info.InformationalVersion)) return info.InformationalVersion;
                var v = asm.GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref int length, StringBuilder fullName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder familyName);

        private static bool? _packaged;
        private static string _packageData;
        private static bool _packageDataKnown;

        // Where settings live: VidFlow.ini, cull decisions, mpv config and watch-later data.
        public static string SettingsFolder
        {
            get { return Path.Combine(PackageDataFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ShortName); }
        }

        // Where logs, crash.log and the unpacked engine live.
        public static string LocalDataFolder
        {
            get { return Path.Combine(PackageDataFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ShortName); }
        }

        // The Store edition's own data folder, %LOCALAPPDATA%\Packages\<family>\LocalState. Windows
        // removes it on uninstall, and it sits outside the AppData redirection that would otherwise
        // read and write the portable exe's existing %APPDATA%\VidFlow files in place while sending
        // new ones to a hidden copy. Null when not packaged (also when only simulated).
        public static string PackageDataFolder
        {
            get
            {
                if (!_packageDataKnown)
                {
                    try
                    {
                        int length = 0;
                        if (GetCurrentPackageFamilyName(ref length, null) == 122 && length > 0)
                        {
                            var name = new StringBuilder(length);
                            if (GetCurrentPackageFamilyName(ref length, name) == 0)
                                _packageData = PackageDataFolderFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name.ToString());
                        }
                    }
                    catch (EntryPointNotFoundException) { }
                    _packageDataKnown = true;
                }
                return _packageData;
            }
        }

        internal static string PackageDataFolderFor(string localAppData, string familyName)
        {
            return string.IsNullOrEmpty(familyName) ? null : Path.Combine(localAppData, "Packages", familyName, "LocalState");
        }

        // True when running from an MSIX package, i.e. the Microsoft Store edition. There the Store
        // delivers updates and Windows owns file-type registration, so VidFlow leaves both alone.
        // VIDFLOW_SIMULATE_PACKAGED=1 lets the end-to-end tests exercise this on an unpackaged build.
        public static bool IsPackaged
        {
            get
            {
                if (_packaged == null)
                {
                    bool packaged = Environment.GetEnvironmentVariable("VIDFLOW_SIMULATE_PACKAGED") == "1";
                    if (!packaged)
                    {
                        try
                        {
                            int length = 0;
                            // ERROR_INSUFFICIENT_BUFFER (122) means there is a package name to return;
                            // APPMODEL_ERROR_NO_PACKAGE (15700) means an ordinary process.
                            packaged = GetCurrentPackageFullName(ref length, null) == 122;
                        }
                        catch (EntryPointNotFoundException) { }
                    }
                    _packaged = packaged;
                }
                return _packaged.Value;
            }
        }
    }
}
