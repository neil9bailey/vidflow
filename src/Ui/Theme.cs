using System;
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VidFlow.Ui
{
    // Shared dark palette, fonts, icon glyphs and DPI helpers.
    public static class Theme
    {
        public static readonly Color Video = Color.Black;
        public static readonly Color Window = Color.FromArgb(18, 18, 18);
        public static readonly Color Panel = Color.FromArgb(28, 28, 28);
        public static readonly Color PanelAlt = Color.FromArgb(36, 36, 36);
        public static readonly Color Hover = Color.FromArgb(52, 52, 52);
        public static readonly Color Pressed = Color.FromArgb(66, 66, 66);
        public static readonly Color Border = Color.FromArgb(58, 58, 58);
        public static readonly Color Text = Color.FromArgb(232, 232, 232);
        public static readonly Color TextDim = Color.FromArgb(150, 150, 150);
        public static readonly Color TextDisabled = Color.FromArgb(95, 95, 95);
        public static readonly Color Accent = Color.FromArgb(64, 156, 255);
        public static readonly Color AccentDim = Color.FromArgb(40, 98, 160);
        public static readonly Color Track = Color.FromArgb(70, 70, 70);
        public static readonly Color Buffered = Color.FromArgb(105, 105, 105);
        public static readonly Color LoopRange = Color.FromArgb(120, 255, 196, 64);
        public static readonly Color LoopMarker = Color.FromArgb(255, 196, 64);
        public static readonly Color ChapterTick = Color.FromArgb(200, 200, 200);
        public static readonly Color Warning = Color.FromArgb(255, 170, 60);

        private static string _iconFamily;

        // "Segoe Fluent Icons" (Windows 11) or "Segoe MDL2 Assets" (Windows 10).
        public static string IconFontFamily
        {
            get
            {
                if (_iconFamily != null) return _iconFamily;
                _iconFamily = "Segoe MDL2 Assets";
                using (var fonts = new InstalledFontCollection())
                    foreach (var f in fonts.Families)
                        if (f.Name == "Segoe Fluent Icons") { _iconFamily = f.Name; break; }
                return _iconFamily;
            }
        }

        public static Font IconFont(float sizePt) { return new Font(IconFontFamily, sizePt, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font UiFont(float sizePt, FontStyle style = FontStyle.Regular) { return new Font("Segoe UI", sizePt, style, GraphicsUnit.Point); }
        public static Font MonoFont(float sizePt) { return new Font("Consolas", sizePt, FontStyle.Regular, GraphicsUnit.Point); }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        // The real DPI of the monitor a control is on. Control.DeviceDpi is useless here:
        // WinForms 4.8 only tracks per-monitor DPI when an exe.config opts in, which a
        // single-file exe cannot ship, so it reports 96 even on a 125% display.
        public static int DpiOf(Control c)
        {
            if (c != null && c.IsHandleCreated && !c.IsDisposed)
            {
                try
                {
                    uint dpi = GetDpiForWindow(c.Handle);
                    if (dpi >= 48 && dpi <= 960) return (int)dpi;
                }
                catch (EntryPointNotFoundException) { }
            }
            return SystemDpi;
        }

        // Logical (96-dpi) pixels -> device pixels for a control's current monitor.
        public static int Scale(Control c, int logicalPx)
        {
            return (int)Math.Round(logicalPx * DpiOf(c) / 96.0);
        }

        public static float ScaleF(Control c, float logicalPx)
        {
            return logicalPx * DpiOf(c) / 96f;
        }

        private static int _systemDpi;
        public static int SystemDpi
        {
            get
            {
                if (_systemDpi == 0)
                    using (var g = Graphics.FromHwnd(IntPtr.Zero)) _systemDpi = (int)Math.Round(g.DpiX);
                return _systemDpi;
            }
        }
    }

    // Segoe MDL2 / Fluent icon code points (same in both fonts).
    public static class Glyphs
    {
        public const string Play = "", Pause = "", Stop = "";
        public const string Previous = "", Next = "";
        public const string StepBack = "", StepForward = "";       // chevrons
        public const string RepeatAll = "", RepeatOne = "", Shuffle = "";
        public const string Volume0 = "", Volume1 = "", Volume2 = "", Volume3 = "", Mute = "";
        public const string FullScreen = "", BackToWindow = "";
        public const string More = "", Settings = "", Playlist = "", Info = "";
        public const string ZoomIn = "", ZoomOut = "", Zoom = "";
        public const string Camera = "", OpenFile = "", Folder = "", Delete = "";
        public const string Rotate = "", Subtitles = "", Audio = "", Speed = "";
        public const string Pin = "", Close = "", Search = "", Up = "", Down = "";
        public const string Check = "", Add = "", Remove = "";
    }
}
