using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using VidFlow.Media;
using Microsoft.Win32;

namespace VidFlow.Ui
{
    // Per-user "Open With" registration (HKCU only; never the machine hive and never the user's
    // chosen defaults). Written:
    //   Software\Classes\VidFlow.Media                ProgID: icon, open (+ multi-select) and
    //                                                "Add to VidFlow playlist" (--enqueue) verbs
    //   Software\Classes\.<ext>\OpenWithProgids      value "VidFlow.Media" for every media extension
    //   Software\Classes\Applications\<exe name>     friendly name, open command, SupportedTypes
    //   Software\VidFlow\Capabilities                 name, description, FileAssociations ext -> ProgID
    //   Software\RegisteredApplications              "VidFlow" -> the Capabilities key (Default Apps)
    // Unregister removes exactly these, and only entries that point at VidFlow.
    internal static class FileAssociations
    {
        internal const string ProgId = "VidFlow.Media";
        internal const string RegisteredName = "VidFlow";
        internal const string DisplayName = "VidFlow Player";   // what Explorer's Open-with list and Default apps show
        private const string Description = "Lightweight video player for quick reviewing";

        // The registry root everything is written under. Tests point BasePath at a throwaway
        // subtree (e.g. "Software\VidFlowTest_1234") so the real Software\Classes is never touched.
        internal static RegistryKey Hive = Registry.CurrentUser;
        internal static string BasePath = "Software";

        private static string ClassesPath { get { return BasePath + @"\Classes"; } }
        private static string AppKeyPath { get { return BasePath + @"\" + RegisteredName; } }
        private static string CapabilitiesPath { get { return AppKeyPath + @"\Capabilities"; } }
        private static string RegisteredAppsPath { get { return BasePath + @"\RegisteredApplications"; } }

        // Every video and audio extension the player opens (lower-case, with the dot).
        internal static List<string> DefaultExtensions()
        {
            var list = new List<string>();
            foreach (var group in new[] { Playlist.VideoExtensions, Playlist.AudioExtensions })
                if (group != null)
                    foreach (var e in group) AddExtension(list, e);
            return list;
        }

        private static void AddExtension(List<string> list, string ext)
        {
            string e = (ext ?? "").Trim().ToLowerInvariant();
            if (!IsValidExtension(e)) return;
            if (!list.Contains(e)) list.Add(e);
        }

        private static bool IsValidExtension(string e)
        {
            if (e.Length < 2 || e.Length > 16 || e[0] != '.') return false;
            for (int i = 1; i < e.Length; i++)
                if (!char.IsLetterOrDigit(e[i]) && e[i] != '_' && e[i] != '-') return false;
            return true;
        }

        private static string Quote(string exePath) { return "\"" + exePath + "\""; }

        // Returns the number of extensions registered.
        internal static int Register(string exePath, IEnumerable<string> extensions)
        {
            if (string.IsNullOrEmpty(exePath) || !Path.IsPathRooted(exePath)) throw new ArgumentException("exe path must be absolute", "exePath");
            var exts = new List<string>();
            if (extensions != null) foreach (var e in extensions) AddExtension(exts, e);
            string exeName = Path.GetFileName(exePath);
            string icon = Quote(exePath) + ",0";
            string open = Quote(exePath) + " \"%1\"";
            string enqueue = Quote(exePath) + " --enqueue \"%1\"";

            using (var classes = Hive.CreateSubKey(ClassesPath))
            {
                using (var prog = classes.CreateSubKey(ProgId))
                {
                    prog.SetValue("", "VidFlow media file");
                    prog.SetValue("FriendlyTypeName", "VidFlow media file");
                    using (var k = prog.CreateSubKey("DefaultIcon")) k.SetValue("", icon);
                    using (var shell = prog.CreateSubKey("shell"))
                    {
                        shell.SetValue("", "open");
                        using (var k = shell.CreateSubKey("open"))
                        {
                            k.SetValue("MultiSelectModel", "Player");
                            using (var c = k.CreateSubKey("command")) c.SetValue("", open);
                        }
                        using (var k = shell.CreateSubKey("enqueue"))
                        {
                            k.SetValue("", "Add to VidFlow playlist");
                            k.SetValue("MultiSelectModel", "Player");
                            using (var c = k.CreateSubKey("command")) c.SetValue("", enqueue);
                        }
                    }
                }

                foreach (var e in exts)
                    using (var k = classes.CreateSubKey(e + @"\OpenWithProgids"))
                        k.SetValue(ProgId, "");

                using (var app = classes.CreateSubKey(@"Applications\" + exeName))
                {
                    app.SetValue("FriendlyAppName", DisplayName);
                    using (var k = app.CreateSubKey("DefaultIcon")) k.SetValue("", icon);
                    using (var k = app.CreateSubKey(@"shell\open"))
                    {
                        k.SetValue("MultiSelectModel", "Player");
                        using (var c = k.CreateSubKey("command")) c.SetValue("", open);
                    }
                    using (var types = app.CreateSubKey("SupportedTypes"))
                        foreach (var e in exts) types.SetValue(e, "");
                }
            }

            using (var caps = Hive.CreateSubKey(CapabilitiesPath))
            {
                caps.SetValue("ApplicationName", DisplayName);
                caps.SetValue("ApplicationDescription", Description);
                caps.SetValue("ApplicationIcon", icon);
                using (var fa = caps.CreateSubKey("FileAssociations"))
                    foreach (var e in exts) fa.SetValue(e, ProgId);
            }
            using (var reg = Hive.CreateSubKey(RegisteredAppsPath))
                reg.SetValue(RegisteredName, CapabilitiesPath);
            return exts.Count;
        }

        internal static bool IsRegistered(string exePath)
        {
            try
            {
                using (var k = Hive.OpenSubKey(ClassesPath + @"\" + ProgId + @"\shell\open\command"))
                    return k != null && PointsAtVidFlow(k.GetValue("") as string, string.IsNullOrEmpty(exePath) ? null : Path.GetFileName(exePath));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
                return false;
            }
        }

        // A command value written by Register: it runs this exe (exeName) or some VidFlow*.exe.
        private static bool PointsAtVidFlow(string value, string exeName)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (!string.IsNullOrEmpty(exeName) && value.IndexOf(exeName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return value.IndexOf("vidflow", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Returns the number of registry entries (keys or values) removed.
        internal static int Unregister(string exePath)
        {
            string exeName = string.IsNullOrEmpty(exePath) ? null : Path.GetFileName(exePath);
            int removed = 0;

            // Extensions we may have written: from our own Capabilities and SupportedTypes lists,
            // plus everything the player knows (an older build may have registered more).
            var exts = DefaultExtensions();
            using (var fa = Hive.OpenSubKey(CapabilitiesPath + @"\FileAssociations"))
                if (fa != null)
                    foreach (var n in fa.GetValueNames())
                        if (string.Equals(fa.GetValue(n) as string, ProgId, StringComparison.OrdinalIgnoreCase)) AddExtension(exts, n);
            if (exeName != null)
                using (var st = Hive.OpenSubKey(ClassesPath + @"\Applications\" + exeName + @"\SupportedTypes"))
                    if (st != null) foreach (var n in st.GetValueNames()) AddExtension(exts, n);

            using (var classes = Hive.OpenSubKey(ClassesPath, true))
            {
                if (classes != null)
                {
                    foreach (var e in exts)
                    {
                        using (var owp = classes.OpenSubKey(e + @"\OpenWithProgids", true))
                        {
                            if (owp == null || owp.GetValue(ProgId) == null) continue;
                            owp.DeleteValue(ProgId, false);
                            removed++;
                        }
                        DeleteIfEmpty(classes, e + @"\OpenWithProgids");
                        DeleteIfEmpty(classes, e);
                    }

                    if (CommandPointsAtVidFlow(classes, ProgId + @"\shell\open\command", exeName))
                    {
                        classes.DeleteSubKeyTree(ProgId, false);
                        removed++;
                    }
                    if (exeName != null && CommandPointsAtVidFlow(classes, @"Applications\" + exeName + @"\shell\open\command", exeName))
                    {
                        classes.DeleteSubKeyTree(@"Applications\" + exeName, false);
                        DeleteIfEmpty(classes, "Applications");
                        removed++;
                    }
                }
            }

            using (var reg = Hive.OpenSubKey(RegisteredAppsPath, true))
            {
                if (reg != null && string.Equals(reg.GetValue(RegisteredName) as string, CapabilitiesPath, StringComparison.OrdinalIgnoreCase))
                {
                    reg.DeleteValue(RegisteredName, false);
                    removed++;
                }
            }

            bool ours;
            using (var caps = Hive.OpenSubKey(CapabilitiesPath))
                ours = caps != null && string.Equals(caps.GetValue("ApplicationName") as string, DisplayName, StringComparison.Ordinal);
            if (ours)
            {
                using (var app = Hive.OpenSubKey(AppKeyPath, true))
                    if (app != null) app.DeleteSubKeyTree("Capabilities", false);
                removed++;
                using (var parent = Hive.OpenSubKey(BasePath, true))
                    if (parent != null) DeleteIfEmpty(parent, RegisteredName);
            }
            return removed;
        }

        private static bool CommandPointsAtVidFlow(RegistryKey classes, string path, string exeName)
        {
            using (var k = classes.OpenSubKey(path))
                return k != null && PointsAtVidFlow(k.GetValue("") as string, exeName);
        }

        private static void DeleteIfEmpty(RegistryKey parent, string path)
        {
            using (var k = parent.OpenSubKey(path))
                if (k == null || k.ValueCount > 0 || k.SubKeyCount > 0) return;
            parent.DeleteSubKey(path, false);
        }

        // Tells Explorer the associations changed (refreshes "Open with" and Default Apps).
        internal static void NotifyShell()
        {
            try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }

        private const int SHCNE_ASSOCCHANGED = 0x08000000, SHCNF_IDLIST = 0;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
    }
}
