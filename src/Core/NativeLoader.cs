using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VidFlow.Core
{
    // Locates and loads libmpv-2.dll before the first P/Invoke into it.
    //
    // Order: (1) libmpv-2.dll next to VidFlow.exe (portable/dev builds, lets users swap
    // in a newer engine), (2) the copy embedded in VidFlow.exe, extracted once to
    // %LOCALAPPDATA%\VidFlow\engine\<hash>\. Once a module named libmpv-2.dll is loaded,
    // every [DllImport("libmpv-2.dll")] binds to it.
    internal static class NativeLoader
    {
        public const string ResourceDll = "libmpv-2.dll.gz";
        public const string ResourceId = "libmpv.id"; // "<sha256 hex> <size in bytes>"

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        public static string LoadedFrom { get; private set; }

        public static string AdjacentPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LibMpv.Dll); }
        }

        // True when the embedded engine must be extracted first (shows a "first run" splash).
        public static bool NeedsExtraction()
        {
            if (File.Exists(AdjacentPath)) return false;
            string target; long size;
            if (!TryGetEmbeddedTarget(out target, out size)) return false;
            return !IsExtracted(target, size);
        }

        public static void Load()
        {
            string path = AdjacentPath;
            if (!File.Exists(path))
            {
                string target; long size;
                if (!TryGetEmbeddedTarget(out target, out size))
                    throw new FileNotFoundException(LibMpv.Dll + " was not found next to VidFlow.exe and this build does not embed it.");
                if (!IsExtracted(target, size)) Extract(target, size);
                path = target;
            }

            if (LoadLibraryW(path) == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                string hint = err == 126
                    ? " A system dependency is missing — most often vulkan-1.dll, which comes with current GPU drivers."
                    : err == 193 ? " The file is not a valid 64-bit DLL." : "";
                throw new Win32Exception(err, "Could not load " + path + " (" + new Win32Exception(err).Message + ")." + hint);
            }
            LoadedFrom = path;
            Log.Info("libmpv loaded from " + path);
        }

        private static bool TryGetEmbeddedTarget(out string target, out long size)
        {
            target = null; size = 0;
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream(ResourceId))
            {
                if (s == null) return false;
                string[] parts = new StreamReader(s).ReadToEnd().Trim().Split(' ');
                if (parts.Length < 2 || parts[0].Length < 16 || !long.TryParse(parts[1], out size)) return false;
                string root = Path.Combine(AppInfo.LocalDataFolder, "engine");
                target = Path.Combine(root, parts[0].Substring(0, 16).ToLowerInvariant(), LibMpv.Dll);
                return true;
            }
        }

        private static bool IsExtracted(string target, long size)
        {
            try { return File.Exists(target) && new FileInfo(target).Length == size; }
            catch { return false; }
        }

        private static void Extract(string target, long size)
        {
            var sw = Stopwatch.StartNew();
            string dir = Path.GetDirectoryName(target);
            Directory.CreateDirectory(dir);
            string tmp = target + "." + Process.GetCurrentProcess().Id + ".tmp";
            var asm = Assembly.GetExecutingAssembly();
            string expectedHash;
            using (var s = asm.GetManifestResourceStream(ResourceId))
                expectedHash = new StreamReader(s).ReadToEnd().Trim().Split(' ')[0].ToLowerInvariant();

            using (var res = asm.GetManifestResourceStream(ResourceDll))
            {
                if (res == null) throw new FileNotFoundException("Embedded engine resource is missing.");
                using (var gz = new GZipStream(res, CompressionMode.Decompress))
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
                using (var sha = SHA256.Create())
                {
                    var buf = new byte[1 << 20];
                    int n;
                    while ((n = gz.Read(buf, 0, buf.Length)) > 0)
                    {
                        sha.TransformBlock(buf, 0, n, null, 0);
                        fs.Write(buf, 0, n);
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    string got = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    if (fs.Length != size || got != expectedHash)
                    {
                        fs.Dispose();
                        TryDelete(tmp);
                        throw new InvalidDataException("Embedded engine is corrupt (hash mismatch).");
                    }
                }
            }

            try { File.Move(tmp, target); }
            catch (IOException)
            {
                // Another instance finished extracting first; use its copy.
                TryDelete(tmp);
                if (!IsExtracted(target, size)) throw;
            }
            Log.Info("extracted engine to " + target + " in " + sw.ElapsedMilliseconds + " ms");
            RemoveStaleEngines(Path.GetDirectoryName(dir), Path.GetFileName(dir));
        }

        // Deletes engines left behind by older VidFlow builds (best effort; skips ones in use).
        private static void RemoveStaleEngines(string root, string keep)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(root))
                    if (!string.Equals(Path.GetFileName(d), keep, StringComparison.OrdinalIgnoreCase))
                        try { Directory.Delete(d, true); } catch { }
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
