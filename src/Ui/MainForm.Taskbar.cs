using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui
{
    // Taskbar button integration: playback progress (ITaskbarList3) and a thumbnail toolbar with
    // Previous / Play-Pause / Next. Explorer announces (and re-announces after a restart) the
    // button with the registered "TaskbarButtonCreated" message; buttons are added then.
    public sealed partial class MainForm
    {
        private const int ThumbPrev = 1, ThumbPlayPause = 2, ThumbNext = 3;
        private const int TaskbarProgressIntervalMs = 250;   // ~4 Hz is plenty for a progress bar

        private static readonly int WmTaskbarButtonCreated = TaskbarNative.RegisterTaskbarMessage();

        private TaskbarNative.ITaskbarList3 _taskbar;
        private bool _thumbButtonsAdded;
        private IntPtr _iconPrev, _iconPlay, _iconPause, _iconNext;
        private int _taskbarState = -1;          // last TBPF_* sent (-1 = none yet)
        private int _taskbarValueAt;             // TickCount of the last SetProgressValue
        private string _thumbSignature;          // last thumbnail-button state sent

        private void TaskbarInit()
        {
            TaskbarUpdate(true);
        }

        private string TaskbarStateName
        {
            get
            {
                if (_taskbar == null) return "unavailable";
                switch (_taskbarState)
                {
                    case TaskbarNative.TBPF_NORMAL: return "normal";
                    case TaskbarNative.TBPF_PAUSED: return "paused";
                    case TaskbarNative.TBPF_NOPROGRESS: return "none";
                    default: return "unset";
                }
            }
        }

        // Returns true when the message was consumed.
        private bool TaskbarWndProc(ref Message m)
        {
            if (WmTaskbarButtonCreated != 0 && m.Msg == WmTaskbarButtonCreated)
            {
                OnTaskbarButtonCreated();
                return false;
            }
            if (m.Msg == TaskbarNative.WM_COMMAND && _thumbButtonsAdded)
            {
                long wp = m.WParam.ToInt64();
                if (((wp >> 16) & 0xFFFF) != TaskbarNative.THBN_CLICKED) return false;
                switch ((int)(wp & 0xFFFF))
                {
                    case ThumbPrev: Run(ActionIds.PrevFile); break;
                    case ThumbPlayPause: Run(ActionIds.PlayPause); break;
                    case ThumbNext: Run(ActionIds.NextFile); break;
                    default: return false;
                }
                m.Result = IntPtr.Zero;
                return true;
            }
            return false;
        }

        private void OnTaskbarButtonCreated()
        {
            if (_closing || !IsHandleCreated) return;
            if (_taskbar == null)
            {
                try
                {
                    var list = (TaskbarNative.ITaskbarList3)new TaskbarNative.TaskbarList();
                    list.HrInit();
                    _taskbar = list;
                }
                catch (Exception ex) when (ex is COMException || ex is InvalidCastException)
                {
                    Log.Warn("taskbar unavailable: " + ex.Message);
                    return;
                }
            }
            // A new button (first show or Explorer restarted) has neither progress nor buttons.
            _taskbarState = -1;
            _thumbSignature = null;
            CreateThumbIcons();
            var buttons = ThumbButtons();
            try
            {
                _thumbButtonsAdded = _taskbar.ThumbBarAddButtons(Handle, (uint)buttons.Length, buttons) >= 0;
            }
            catch (COMException ex)
            {
                Log.Warn("taskbar buttons: " + ex.Message);
                _thumbButtonsAdded = false;
            }
            if (_thumbButtonsAdded) _thumbSignature = ThumbSignature();
            TaskbarUpdate(true);
        }

        // Progress state + value (throttled unless forced) and the thumbnail buttons.
        private void TaskbarUpdate(bool force)
        {
            if (_taskbar == null || !IsHandleCreated || _closing) return;
            IntPtr h = Handle;
            bool media = HasMedia && _st.Duration > 0;
            int state = !_settings.TaskbarProgress || !media ? TaskbarNative.TBPF_NOPROGRESS
                      : _st.Paused ? TaskbarNative.TBPF_PAUSED : TaskbarNative.TBPF_NORMAL;
            try
            {
                if (state != _taskbarState)
                {
                    _taskbar.SetProgressState(h, state);
                    _taskbarState = state;
                    force = true;
                }
                // SetProgressValue would switch NOPROGRESS back to NORMAL.
                int now = Environment.TickCount;
                if (state != TaskbarNative.TBPF_NOPROGRESS && (force || unchecked(now - _taskbarValueAt) >= TaskbarProgressIntervalMs))
                {
                    double dur = _st.Duration, pos = Math.Max(0, Math.Min(dur, _st.Position));
                    _taskbar.SetProgressValue(h, (ulong)Math.Round(pos * 1000), (ulong)Math.Max(1, Math.Round(dur * 1000)));
                    _taskbarValueAt = now;
                }
                if (force && _thumbButtonsAdded)
                {
                    string sig = ThumbSignature();
                    if (sig != _thumbSignature)
                    {
                        var buttons = ThumbButtons();
                        if (_taskbar.ThumbBarUpdateButtons(h, (uint)buttons.Length, buttons) >= 0) _thumbSignature = sig;
                    }
                }
            }
            catch (COMException ex)
            {
                // Explorer went away; TaskbarButtonCreated arrives again when it is back.
                Log.Warn("taskbar update: " + ex.Message);
                _taskbarState = -1;
            }
        }

        private bool ThumbShowsPause
        {
            get { return HasMedia && !_st.Paused; }
        }

        private string ThumbSignature()
        {
            return (_settings.TaskbarProgress ? "v" : "h") + (ThumbShowsPause ? "P" : "p") +
                   (_actions.IsEnabled(ActionIds.PrevFile) ? "1" : "0") +
                   (_actions.IsEnabled(ActionIds.PlayPause) ? "1" : "0") +
                   (_actions.IsEnabled(ActionIds.NextFile) ? "1" : "0");
        }

        private TaskbarNative.THUMBBUTTON[] ThumbButtons()
        {
            bool hidden = !_settings.TaskbarProgress;
            bool pause = ThumbShowsPause;
            Func<int, IntPtr, string, string, TaskbarNative.THUMBBUTTON> make = (id, icon, tip, action) =>
            {
                int flags = hidden ? TaskbarNative.THBF_HIDDEN
                          : _actions.IsEnabled(action) ? TaskbarNative.THBF_ENABLED : TaskbarNative.THBF_DISABLED;
                return new TaskbarNative.THUMBBUTTON
                {
                    dwMask = TaskbarNative.THB_ICON | TaskbarNative.THB_TOOLTIP | TaskbarNative.THB_FLAGS,
                    iId = (uint)id,
                    hIcon = icon,
                    szTip = tip,
                    dwFlags = flags,
                };
            };
            return new[]
            {
                make(ThumbPrev, _iconPrev, ActionRegistry.TitleOf(ActionIds.PrevFile), ActionIds.PrevFile),
                make(ThumbPlayPause, pause ? _iconPause : _iconPlay, pause ? "Pause" : "Play", ActionIds.PlayPause),
                make(ThumbNext, _iconNext, ActionRegistry.TitleOf(ActionIds.NextFile), ActionIds.NextFile),
            };
        }

        // Small white glyph icons at the system small-icon size (the taskbar scales with the system DPI).
        private void CreateThumbIcons()
        {
            if (_iconPlay != IntPtr.Zero) return;
            int size = TaskbarNative.GetSystemMetrics(TaskbarNative.SM_CXSMICON);
            if (size < 16) size = 16;
            _iconPrev = GlyphIcon(Glyphs.Previous, size);
            _iconPlay = GlyphIcon(Glyphs.Play, size);
            _iconPause = GlyphIcon(Glyphs.Pause, size);
            _iconNext = GlyphIcon(Glyphs.Next, size);
        }

        private static IntPtr GlyphIcon(string glyph, int size)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var font = new Font(Theme.IconFontFamily, size * 0.75f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var sf = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.NoClip))
                {
                    g.Clear(Color.Transparent);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(glyph, font, Brushes.White, new RectangleF(0, 0, size, size), sf);
                }
                return bmp.GetHicon();
            }
        }

        private void TaskbarDispose()
        {
            if (_taskbar != null)
            {
                try { Marshal.ReleaseComObject(_taskbar); }
                catch (ArgumentException) { }
                _taskbar = null;
            }
            _thumbButtonsAdded = false;
            foreach (var icon in new[] { _iconPrev, _iconPlay, _iconPause, _iconNext })
                if (icon != IntPtr.Zero) TaskbarNative.DestroyIcon(icon);
            _iconPrev = _iconPlay = _iconPause = _iconNext = IntPtr.Zero;
        }

        private static class TaskbarNative
        {
            public const int WM_COMMAND = 0x0111, THBN_CLICKED = 0x1800, SM_CXSMICON = 49;
            public const int TBPF_NOPROGRESS = 0, TBPF_NORMAL = 0x2, TBPF_PAUSED = 0x8;
            public const int THB_ICON = 0x2, THB_TOOLTIP = 0x4, THB_FLAGS = 0x8;
            public const int THBF_ENABLED = 0, THBF_DISABLED = 0x1, THBF_HIDDEN = 0x8;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct THUMBBUTTON
            {
                public int dwMask;
                public uint iId;
                public uint iBitmap;
                public IntPtr hIcon;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szTip;
                public int dwFlags;
            }

            [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface ITaskbarList3
            {
                // ITaskbarList
                void HrInit();
                void AddTab(IntPtr hwnd);
                void DeleteTab(IntPtr hwnd);
                void ActivateTab(IntPtr hwnd);
                void SetActiveAlt(IntPtr hwnd);
                // ITaskbarList2
                void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
                // ITaskbarList3
                void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
                void SetProgressState(IntPtr hwnd, int flags);
                void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
                void UnregisterTab(IntPtr hwndTab);
                void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
                void SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);
                [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
                [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
                void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
                void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
                void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
                void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
            }

            [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
            public class TaskbarList { }

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int RegisterWindowMessage(string name);

            [DllImport("user32.dll")]
            public static extern int GetSystemMetrics(int index);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DestroyIcon(IntPtr icon);

            public static int RegisterTaskbarMessage()
            {
                try { return RegisterWindowMessage("TaskbarButtonCreated"); }
                catch (EntryPointNotFoundException) { return 0; }
            }
        }
    }
}
