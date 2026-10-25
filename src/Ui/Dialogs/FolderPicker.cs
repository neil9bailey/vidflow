using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    public static class FolderPicker
    {
        private const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const int ERROR_CANCELLED_HR = unchecked((int)0x800704C7);

        // Modern Vista+ folder picker (IFileOpenDialog with FOS_PICKFOLDERS). Null if cancelled.
        public static string Show(IWin32Window owner, string title, string initialFolder)
        {
            bool failed;
            string result = ShowModern(owner, title, initialFolder, out failed);
            return failed ? ShowClassic(owner, title, initialFolder) : result;
        }

        private static string ShowModern(IWin32Window owner, string title, string initialFolder, out bool failed)
        {
            failed = false;
            IFileDialog dialog = null;
            IShellItem folder = null, item = null;
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"));
                dialog = (IFileDialog)Activator.CreateInstance(type);
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR | FOS_PATHMUSTEXIST);
                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                string start = ExistingFolder(initialFolder);
                if (start != null)
                {
                    var iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(start, IntPtr.Zero, ref iid, out folder) == 0 && folder != null)
                        dialog.SetFolder(folder);
                }
                int hr = dialog.Show(owner != null ? owner.Handle : IntPtr.Zero);
                if (hr == ERROR_CANCELLED_HR) return null;
                if (hr != 0) { failed = true; return null; }
                dialog.GetResult(out item);
                IntPtr name;
                item.GetDisplayName(SIGDN_FILESYSPATH, out name);
                try { return Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            catch (Exception)
            {
                failed = true;
                return null;
            }
            finally
            {
                if (item != null) Marshal.ReleaseComObject(item);
                if (folder != null) Marshal.ReleaseComObject(folder);
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }

        private static string ShowClassic(IWin32Window owner, string title, string initialFolder)
        {
            using (var fb = new FolderBrowserDialog { Description = title ?? "", ShowNewFolderButton = true })
            {
                string start = ExistingFolder(initialFolder);
                if (start != null) fb.SelectedPath = start;
                return fb.ShowDialog(owner) == DialogResult.OK ? fb.SelectedPath : null;
            }
        }

        // Nearest existing folder at or above `path`, or null.
        private static string ExistingFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string p = Path.GetFullPath(path.Trim());
                while (!string.IsNullOrEmpty(p) && !Directory.Exists(p)) p = Path.GetDirectoryName(p);
                return string.IsNullOrEmpty(p) ? null : p;
            }
            catch (Exception)
            {
                return null;
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        // IModalWindow + IFileDialog vtable, in declaration order.
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filterSpec);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindCtx, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
