using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace VidFlow.Ui
{
    public sealed partial class MainForm
    {
        // Win32 interop used by the main window.
        private static class Native
        {
            public const int GWL_STYLE = -16;
            public const int WS_OVERLAPPEDWINDOW = 0x00CF0000;  // caption, sysmenu, thick frame, min/max boxes
            public const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
            public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
                SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200;

            public const int WM_MOUSEMOVE = 0x0200, WM_NCMOUSEMOVE = 0x00A0, WM_MOUSEACTIVATE = 0x0021,
                WM_SYSCOMMAND = 0x0112, WM_APPCOMMAND = 0x0319, WM_NCLBUTTONDOWN = 0x00A1, WM_DPICHANGED = 0x02E0,
                WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_MOUSEWHEEL = 0x020A,
                WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207,
                WM_NCRBUTTONDOWN = 0x00A4, WM_NCMBUTTONDOWN = 0x00A7;
            public const int MA_NOACTIVATE = 3, HTCAPTION = 2, SC_KEYMENU = 0xF100;
            public const int FAPPCOMMAND_MASK = 0xF000, FAPPCOMMAND_MOUSE = 0x8000;

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X, Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left, Top, Right, Bottom; }

            [StructLayout(LayoutKind.Sequential)]
            public struct WINDOWPLACEMENT
            {
                public int length, flags, showCmd;
                public POINT ptMinPosition, ptMaxPosition;
                public RECT rcNormalPosition;
            }

            [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
            public static extern int GetWindowLong(IntPtr hWnd, int index);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
            public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);

            [DllImport("user32.dll")]
            private static extern IntPtr WindowFromPoint(POINT p);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ReleaseCapture();

            [DllImport("user32.dll")]
            public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            [DllImport("dwmapi.dll")]
            private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

            // ---- shell delete / Recycle Bin ----

            private const uint FO_DELETE = 3;
            private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040,
                FOF_WANTNUKEWARNING = 0x4000;

            // x64 layout (shellapi.h packs these structs to 1 byte only on 32-bit).
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct SHFILEOPSTRUCT
            {
                public IntPtr hwnd;
                public uint wFunc;
                [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
                [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
                public ushort fFlags;
                [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
                public IntPtr hNameMappings;
                [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct SHQUERYRBINFO
            {
                public int cbSize;
                public long i64Size, i64NumItems;
            }

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            private static extern int SHQueryRecycleBin(string rootPath, ref SHQUERYRBINFO info);

            // Deletes one file through the shell. recycle: FOF_ALLOWUNDO plus FOF_WANTNUKEWARNING, so
            // the shell still asks before destroying a file it cannot recycle; otherwise a plain
            // permanent delete the user already agreed to. Returns the shell's code (0 = success);
            // aborted = the user cancelled.
            public static int ShellDelete(IntPtr owner, string path, bool recycle, out bool aborted)
            {
                var op = new SHFILEOPSTRUCT
                {
                    hwnd = owner,
                    wFunc = FO_DELETE,
                    pFrom = path + "\0\0", // double-NUL-terminated list of one
                    fFlags = (ushort)(FOF_SILENT | FOF_NOCONFIRMATION | (recycle ? FOF_ALLOWUNDO | FOF_WANTNUKEWARNING : 0)),
                };
                int rc = SHFileOperation(ref op);
                aborted = op.fAnyOperationsAborted;
                return rc;
            }

            // Items in a drive's Recycle Bin ("C:\"), or -1 when the drive has no working bin.
            public static long RecycleBinItemCount(string root)
            {
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
                try
                {
                    return SHQueryRecycleBin(root, ref info) == 0 ? info.i64NumItems : -1;
                }
                catch (EntryPointNotFoundException)
                {
                    return -1;
                }
            }

            public static WINDOWPLACEMENT GetPlacement(IntPtr h)
            {
                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                GetWindowPlacement(h, ref wp);
                return wp;
            }

            public static void SetPlacement(IntPtr h, WINDOWPLACEMENT wp)
            {
                if (wp.length == 0) return;
                SetWindowPlacement(h, ref wp);
            }

            public static IntPtr WindowAt(Point screen)
            {
                return WindowFromPoint(new POINT { X = screen.X, Y = screen.Y });
            }

            // DWMWA_USE_IMMERSIVE_DARK_MODE is 20 on Windows 10 20H1+ / 11, 19 on older builds.
            public static void UseDarkTitleBar(IntPtr h)
            {
                int on = 1;
                try
                {
                    if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0)
                        DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
        }
    }
}
