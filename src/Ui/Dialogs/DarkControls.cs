using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    // Shared colours, cached GDI+ objects, painting helpers and Win32 interop for the
    // dark dialog controls.
    internal static class DarkControls
    {
        // Field (text box / combo / number) and control-state colours derived from Theme.
        public static readonly Color Field = Theme.PanelAlt;
        public static readonly Color FieldHover = Color.FromArgb(44, 44, 44);
        public static readonly Color FieldBorder = Color.FromArgb(64, 64, 64);
        public static readonly Color FieldBorderHover = Color.FromArgb(92, 92, 92);
        public static readonly Color ButtonBorder = Color.FromArgb(62, 62, 62);
        public static readonly Color ButtonBorderHover = Color.FromArgb(84, 84, 84);
        public static readonly Color AccentHover = Color.FromArgb(98, 176, 255);
        public static readonly Color AccentPressed = Color.FromArgb(52, 134, 224);
        public static readonly Color AccentDisabled = Color.FromArgb(44, 70, 98);
        public static readonly Color OnAccent = Color.FromArgb(10, 14, 20);
        public static readonly Color OnAccentDisabled = Color.FromArgb(120, 140, 160);
        public static readonly Color CheckBorder = Color.FromArgb(118, 118, 118);
        public static readonly Color CheckBorderHover = Color.FromArgb(150, 150, 150);
        public static readonly Color RowHover = Color.FromArgb(32, 32, 32);
        public static readonly Color RowSelected = Color.FromArgb(46, 46, 46);
        public static readonly Color RowSelectedFocused = Color.FromArgb(30, 62, 99);
        public static readonly Color Chip = Color.FromArgb(44, 44, 44);
        public static readonly Color ChipBorder = Color.FromArgb(70, 70, 70);
        public static readonly Color ScrollThumb = Color.FromArgb(92, 92, 92);
        public static readonly Color ScrollThumbHover = Color.FromArgb(130, 130, 130);

        private static readonly Dictionary<int, SolidBrush> Brushes = new Dictionary<int, SolidBrush>();
        private static readonly Dictionary<long, Pen> Pens = new Dictionary<long, Pen>();

        // Brushes/pens are shared for the life of the process (a small fixed palette).
        public static SolidBrush Brush(Color c)
        {
            int key = c.ToArgb();
            SolidBrush b;
            if (!Brushes.TryGetValue(key, out b)) { b = new SolidBrush(c); Brushes[key] = b; }
            return b;
        }

        public static Pen Pen(Color c, float width = 1f)
        {
            long key = ((long)c.ToArgb() << 16) ^ (long)Math.Round(width * 100);
            Pen p;
            if (!Pens.TryGetValue(key, out p))
            {
                p = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                Pens[key] = p;
            }
            return p;
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Fills (and optionally outlines) a rounded rectangle aligned to the pixel grid.
        public static void RoundedBox(Graphics g, Rectangle r, float radius, Color fill, Color? border)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1f, r.Height - 1f);
            using (var path = RoundRect(rf, radius))
            {
                g.FillPath(Brush(fill), path);
                if (border.HasValue) g.DrawPath(Pen(border.Value), path);
            }
            g.SmoothingMode = old;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // First opaque background up the parent chain (labels etc. use Transparent).
        public static Color BackOf(Control c)
        {
            for (var p = c; p != null; p = p.Parent)
                if (p.BackColor.A == 255) return p.BackColor;
            return Theme.Window;
        }

        public static DarkDialog DialogOf(Control c)
        {
            for (var p = c; p != null; p = p.Parent)
            {
                var d = p as DarkDialog;
                if (d != null) return d;
            }
            return null;
        }

        // The DPI the control is laid out for: its dialog's DPI (tracked from the real
        // monitor), else the window's own DPI, else the system DPI.
        public static int DpiOf(Control c)
        {
            var d = DialogOf(c);
            if (d != null) return d.Dpi;
            if (c != null && c.IsHandleCreated) return WindowDpi(c.Handle, c.DeviceDpi);
            return Theme.SystemDpi;
        }

        public static int Scale(Control c, int logicalPx)
        {
            return (int)Math.Round(logicalPx * DpiOf(c) / 96.0);
        }

        public static float ScaleF(Control c, float logicalPx)
        {
            return logicalPx * DpiOf(c) / 96f;
        }

        private static readonly Dictionary<long, Font> LooseIconFonts = new Dictionary<long, Font>();

        // Icon-font glyph font from the dialog's per-DPI cache; outside a dialog a small
        // process-wide cache keyed by size and DPI.
        public static Font IconFont(Control c, float pt)
        {
            var d = DialogOf(c);
            if (d != null) return d.GetFont(pt, FontStyle.Regular, Theme.IconFontFamily);
            int dpi = DpiOf(c);
            long key = ((long)dpi << 32) | (uint)Math.Round(pt * 100);
            Font f;
            if (!LooseIconFonts.TryGetValue(key, out f))
            {
                f = new Font(Theme.IconFontFamily, pt * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
                LooseIconFonts[key] = f;
            }
            return f;
        }

        // GetPreferredSize overrides bypass Control's own Minimum/MaximumSize clamping;
        // layout engines would otherwise reserve less room than the control then takes.
        public static Size Constrain(Control c, Size size)
        {
            Size min = c.MinimumSize, max = c.MaximumSize;
            int w = Math.Max(size.Width, min.Width), h = Math.Max(size.Height, min.Height);
            if (max.Width > 0) w = Math.Min(w, max.Width);
            if (max.Height > 0) h = Math.Min(h, max.Height);
            return new Size(w, h);
        }

        // ---- Win32 -------------------------------------------------------------------

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subApp, string idList);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        public const int EM_SETCUEBANNER = 0x1501, EM_SETMARGINS = 0xD3, EC_LEFTMARGIN = 1, EC_RIGHTMARGIN = 2;

        public static int WindowDpi(IntPtr hwnd, int fallback)
        {
            if (hwnd == IntPtr.Zero) return fallback;
            try
            {
                uint d = GetDpiForWindow(hwnd);
                return d > 0 ? (int)d : fallback;
            }
            catch (EntryPointNotFoundException)
            {
                return fallback; // Windows 10 before 1607
            }
        }

        // Dark scrollbars (and other themed parts) for native scrollable controls.
        public static void UseDarkTheme(Control c, string subApp = "DarkMode_Explorer")
        {
            if (c == null) return;
            if (c.IsHandleCreated) Apply(c.Handle, subApp);
            c.HandleCreated += (s, e) => Apply(((Control)s).Handle, subApp);
        }

        public static void UseDarkTheme(IntPtr hwnd, string subApp = "DarkMode_Explorer")
        {
            Apply(hwnd, subApp);
        }

        private static void Apply(IntPtr hwnd, string subApp)
        {
            try { SetWindowTheme(hwnd, subApp, null); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        // Dark title bar (attribute 20; 19 on early Windows 10 builds) and, on Windows 11,
        // a caption that blends into the dialog body.
        public static void UseDarkTitleBar(IntPtr hwnd, Color caption, Color border)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                int cap = ColorTranslator.ToWin32(caption), brd = ColorTranslator.ToWin32(border);
                DwmSetWindowAttribute(hwnd, 35, ref cap, 4); // DWMWA_CAPTION_COLOR (Win11; ignored before)
                DwmSetWindowAttribute(hwnd, 34, ref brd, 4); // DWMWA_BORDER_COLOR
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        public static void SetCueBanner(TextBox box, string text)
        {
            if (box == null || !box.IsHandleCreated || box.Multiline) return;
            SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text ?? "");
        }
    }
}
