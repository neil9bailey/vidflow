using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VidFlow.Core;

namespace VidFlow.Ui
{
    // One dynamic menu entry (recent files, tracks, chapters, playlist...).
    public sealed class MenuEntry
    {
        public string Text { get; set; }
        public string ActionId { get; set; }          // executed via ActionRegistry when clicked
        public bool Checked { get; set; }
        public bool Enabled { get; set; } = true;
        public bool IsSeparator { get; set; }
        public static MenuEntry Separator() { return new MenuEntry { IsSeparator = true }; }
    }

    // Supplies the dynamic parts of menus. Implemented by MainForm; called each time a menu opens.
    public interface IMenuDataSource
    {
        IList<MenuEntry> RecentFiles();     // ActionId "OpenRecent:<index>"
        IList<MenuEntry> VideoTracks();     // "VideoTrack:<id>" (+ "VideoTrack:no")
        IList<MenuEntry> AudioTracks();     // "AudioTrack:<id>" (+ "AudioTrack:no")
        IList<MenuEntry> SubtitleTracks();  // "SubTrack:<id>" (+ "SubTrack:no")
        IList<MenuEntry> Chapters();        // "Chapter:<index>"
    }

    // Dark ToolStrip renderer used by every menu in the app.
    public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        // Logical (96-dpi) metrics; the item/separator heights are applied by MenuTheme's layout pass.
        internal const int ItemHeight = 26, SeparatorHeight = 9, DropDownPaddingY = 4, CheckColumn = 16;
        private const int HoverInsetX = 4, HoverInsetY = 1, HoverRadius = 4, SeparatorInsetX = 8,
            CheckGlyphPx = 12, ArrowGlyphPx = 9, RadioDotPx = 6;

        // Glyphs and text are drawn with GDI; keep the item's clip/offset when ToolStrip hands us a transformed Graphics.
        private const TextFormatFlags GlyphFormat = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
            TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        private const TextFormatFlags PreserveFlags =
            TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;

        private static readonly SolidBrush PanelBrush = new SolidBrush(Theme.Panel);
        private static readonly SolidBrush HoverBrush = new SolidBrush(Theme.Hover);
        private static readonly SolidBrush PressedBrush = new SolidBrush(Theme.Pressed);
        private static readonly SolidBrush DisabledHoverBrush = new SolidBrush(Theme.PanelAlt);
        private static readonly SolidBrush AccentBrush = new SolidBrush(Theme.Accent);
        private static readonly SolidBrush DisabledBrush = new SolidBrush(Theme.TextDisabled);
        private static readonly Pen BorderPen = new Pen(Theme.Border);
        private static readonly Dictionary<int, Font> CheckFonts = new Dictionary<int, Font>();
        private static readonly Dictionary<int, Font> ArrowFonts = new Dictionary<int, Font>();

        // Only one item is highlighted at a time, so one cached outline covers nearly every paint.
        private static GraphicsPath _hoverPath;
        private static Rectangle _hoverRect;
        private static int _hoverRadius;

        public DarkMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Color back = e.ToolStrip.BackColor;
            var r = new Rectangle(Point.Empty, e.ToolStrip.Size);
            if (back.ToArgb() == Theme.Panel.ToArgb())
            {
                e.Graphics.FillRectangle(PanelBrush, r);
                return;
            }
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // Bars stay borderless; drop-downs get a 1 px frame unless DWM already draws a rounded one.
            var drop = e.ToolStrip as ToolStripDropDown;
            if (drop == null || MenuTheme.HasDwmFrame(drop) || drop.Width < 2 || drop.Height < 2) return;
            e.Graphics.DrawRectangle(BorderPen, 0, 0, drop.Width - 1, drop.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // The check column shares the drop-down background: no gutter band.
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            ToolStripItem item = e.Item;
            if (!item.Selected && !item.Pressed) return;
            int dpi = MenuTheme.DpiOf(e.ToolStrip);
            var r = new Rectangle(Point.Empty, item.Size);
            Brush brush;
            if (item.IsOnDropDown)
            {
                r.Inflate(-MenuTheme.Px(dpi, HoverInsetX), -MenuTheme.Px(dpi, HoverInsetY));
                brush = item.Enabled ? HoverBrush : DisabledHoverBrush;
            }
            else
            {
                r.Inflate(-MenuTheme.Px(dpi, 1), -MenuTheme.Px(dpi, 2));
                brush = item.Pressed ? PressedBrush : HoverBrush;
            }
            FillRounded(e.Graphics, brush, r, MenuTheme.Px(dpi, HoverRadius));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            ToolStripItem item = e.Item;
            Rectangle r = e.TextRectangle;
            if (string.IsNullOrEmpty(e.Text) || r.Width <= 0 || r.Height <= 0) return;
            TextFormatFlags format = e.TextFormat | PreserveFlags;
            if (item.IsOnDropDown && item is ToolStripMenuItem)
            {
                // The menu layout places text as if items had no vertical padding; centre on the real item height.
                r = new Rectangle(r.X, 0, r.Width, item.Height);
                format = (format & ~TextFormatFlags.Bottom) | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            }
            Color color = !item.Enabled ? Theme.TextDisabled : IsShortcutText(e) ? Theme.TextDim : item.ForeColor;
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r, color, format);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            ToolStripItem item = e.Item;
            int dpi = MenuTheme.DpiOf(e.ToolStrip);
            Rectangle ir = e.ImageRectangle;
            // Centre on the check column horizontally and on the whole item vertically, whatever padding the item has.
            int cx = ir.Width > 0 ? ir.X + ir.Width / 2 : MenuTheme.Px(dpi, 14);
            int cy = item.Height / 2;
            var mi = item as ActionMenuItem;
            if (mi != null && mi.Radio)
            {
                float d = MenuTheme.PxF(dpi, RadioDotPx);
                Graphics g = e.Graphics;
                SmoothingMode oldMode = g.SmoothingMode;
                PixelOffsetMode oldOffset = g.PixelOffsetMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.FillEllipse(item.Enabled ? AccentBrush : DisabledBrush, cx - d / 2f, cy - d / 2f, d, d);
                g.SmoothingMode = oldMode;
                g.PixelOffsetMode = oldOffset;
                return;
            }
            int half = MenuTheme.Px(dpi, 10);
            var box = new Rectangle(cx - half, cy - half, half * 2, half * 2);
            TextRenderer.DrawText(e.Graphics, Glyphs.Check, IconFont(CheckFonts, dpi, CheckGlyphPx), box,
                item.Enabled ? Theme.Accent : Theme.TextDisabled, GlyphFormat);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            Rectangle r = e.ArrowRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            ToolStripItem item = e.Item;
            if (item != null && item.IsOnDropDown) r = new Rectangle(r.X, 0, r.Width, item.Height); // same centring as the text
            int dpi = MenuTheme.DpiOf(item != null ? item.GetCurrentParent() ?? item.Owner : null);
            string glyph;
            switch (e.Direction)
            {
                case ArrowDirection.Left: glyph = Glyphs.StepBack; break;
                case ArrowDirection.Up: glyph = Glyphs.Up; break;
                case ArrowDirection.Down: glyph = Glyphs.Down; break;
                default: glyph = Glyphs.StepForward; break;
            }
            Color color = item != null && !item.Enabled ? Theme.TextDisabled
                : item != null && (item.Selected || item.Pressed) ? Theme.Text : Theme.TextDim;
            TextRenderer.DrawText(e.Graphics, glyph, IconFont(ArrowFonts, dpi, ArrowGlyphPx), r, color, GlyphFormat);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int dpi = MenuTheme.DpiOf(e.ToolStrip);
            Size s = e.Item.Size;
            if (e.Vertical)
            {
                int x = s.Width / 2, m = MenuTheme.Px(dpi, 4);
                if (s.Height - 1 - m > m) e.Graphics.DrawLine(BorderPen, x, m, x, s.Height - 1 - m);
                return;
            }
            int y = s.Height / 2;
            int inset = MenuTheme.Px(dpi, e.Item.IsOnDropDown ? SeparatorInsetX : 4);
            if (s.Width - 1 - inset > inset) e.Graphics.DrawLine(BorderPen, inset, y, s.Width - 1 - inset, y);
        }

        // Right-aligned text inside a drop-down menu item is the shortcut column (left-aligned when RTL).
        private static bool IsShortcutText(ToolStripItemTextRenderEventArgs e)
        {
            var mi = e.Item as ToolStripMenuItem;
            if (mi == null || !mi.IsOnDropDown) return false;
            bool right = (e.TextFormat & TextFormatFlags.Right) != 0;
            return mi.RightToLeft == RightToLeft.Yes ? !right : right;
        }

        private static void FillRounded(Graphics g, Brush brush, Rectangle r, int radius)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            radius = Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
            if (_hoverPath == null || _hoverRect != r || _hoverRadius != radius)
            {
                if (_hoverPath != null) _hoverPath.Dispose();
                _hoverPath = RoundedRect(r, radius);
                _hoverRect = r;
                _hoverRadius = radius;
            }
            SmoothingMode oldMode = g.SmoothingMode;
            PixelOffsetMode oldOffset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half; // integer edges land on pixel boundaries: crisp straight sides
            g.FillPath(brush, _hoverPath);
            g.SmoothingMode = oldMode;
            g.PixelOffsetMode = oldOffset;
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Font IconFont(Dictionary<int, Font> cache, int dpi, int logicalPx)
        {
            Font f;
            if (!cache.TryGetValue(dpi, out f))
            {
                f = new Font(Theme.IconFontFamily, MenuTheme.PxF(dpi, logicalPx), FontStyle.Regular, GraphicsUnit.Pixel);
                cache[dpi] = f;
            }
            return f;
        }

        // Fallback palette for everything ToolStripProfessionalRenderer still draws itself
        // (tool bar buttons, overflow button, grips, status strips): flat, no gradients.
        private sealed class DarkColorTable : ProfessionalColorTable
        {
            public DarkColorTable() { UseSystemColors = false; }

            public override Color ButtonSelectedHighlight { get { return Theme.Hover; } }
            public override Color ButtonSelectedHighlightBorder { get { return Theme.Hover; } }
            public override Color ButtonPressedHighlight { get { return Theme.Pressed; } }
            public override Color ButtonPressedHighlightBorder { get { return Theme.Pressed; } }
            public override Color ButtonCheckedHighlight { get { return Theme.PanelAlt; } }
            public override Color ButtonCheckedHighlightBorder { get { return Theme.Border; } }
            public override Color ButtonPressedBorder { get { return Theme.Pressed; } }
            public override Color ButtonSelectedBorder { get { return Theme.Hover; } }
            public override Color ButtonCheckedGradientBegin { get { return Theme.PanelAlt; } }
            public override Color ButtonCheckedGradientMiddle { get { return Theme.PanelAlt; } }
            public override Color ButtonCheckedGradientEnd { get { return Theme.PanelAlt; } }
            public override Color ButtonSelectedGradientBegin { get { return Theme.Hover; } }
            public override Color ButtonSelectedGradientMiddle { get { return Theme.Hover; } }
            public override Color ButtonSelectedGradientEnd { get { return Theme.Hover; } }
            public override Color ButtonPressedGradientBegin { get { return Theme.Pressed; } }
            public override Color ButtonPressedGradientMiddle { get { return Theme.Pressed; } }
            public override Color ButtonPressedGradientEnd { get { return Theme.Pressed; } }
            public override Color CheckBackground { get { return Theme.Panel; } }
            public override Color CheckSelectedBackground { get { return Theme.Hover; } }
            public override Color CheckPressedBackground { get { return Theme.Pressed; } }
            public override Color GripDark { get { return Theme.Border; } }
            public override Color GripLight { get { return Theme.Panel; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
            public override Color ImageMarginRevealedGradientBegin { get { return Theme.Panel; } }
            public override Color ImageMarginRevealedGradientMiddle { get { return Theme.Panel; } }
            public override Color ImageMarginRevealedGradientEnd { get { return Theme.Panel; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemBorder { get { return Theme.Hover; } }
            public override Color MenuItemSelected { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return Theme.Pressed; } }
            public override Color MenuItemPressedGradientMiddle { get { return Theme.Pressed; } }
            public override Color MenuItemPressedGradientEnd { get { return Theme.Pressed; } }
            public override Color MenuStripGradientBegin { get { return Theme.Panel; } }
            public override Color MenuStripGradientEnd { get { return Theme.Panel; } }
            public override Color OverflowButtonGradientBegin { get { return Theme.PanelAlt; } }
            public override Color OverflowButtonGradientMiddle { get { return Theme.PanelAlt; } }
            public override Color OverflowButtonGradientEnd { get { return Theme.PanelAlt; } }
            public override Color RaftingContainerGradientBegin { get { return Theme.Panel; } }
            public override Color RaftingContainerGradientEnd { get { return Theme.Panel; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Panel; } }
            public override Color StatusStripGradientBegin { get { return Theme.Panel; } }
            public override Color StatusStripGradientEnd { get { return Theme.Panel; } }
            public override Color ToolStripBorder { get { return Theme.Border; } }
            public override Color ToolStripContentPanelGradientBegin { get { return Theme.Window; } }
            public override Color ToolStripContentPanelGradientEnd { get { return Theme.Window; } }
            public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
            public override Color ToolStripGradientBegin { get { return Theme.Panel; } }
            public override Color ToolStripGradientMiddle { get { return Theme.Panel; } }
            public override Color ToolStripGradientEnd { get { return Theme.Panel; } }
            public override Color ToolStripPanelGradientBegin { get { return Theme.Window; } }
            public override Color ToolStripPanelGradientEnd { get { return Theme.Window; } }
        }
    }

    // Menu item bound to an action id (also stored in Tag, which is what other code reads).
    internal sealed class ActionMenuItem : ToolStripMenuItem
    {
        public ActionMenuItem(string text, string actionId, bool radio) : base(text)
        {
            Tag = actionId;
            Radio = radio;
            ShowShortcutKeys = true;
        }

        // One-of-many choice: the renderer draws a dot instead of a check mark.
        public bool Radio { get; private set; }

        // Built from a MenuEntry: its checked/enabled state comes from the entry, not the registry.
        public bool FromEntry { get; set; }

        // A command next to checkable siblings (e.g. "Clear Rating" under the star ratings): never shows a check.
        public bool NoCheck { get; set; }

        // Hidden rather than greyed out while its action is disabled (e.g. Check for Updates without a repository).
        public bool HideWhenDisabled { get; set; }

        // Text re-read every time the menu refreshes (null or empty = DefaultText); plain text, not escaped.
        public Func<string> LabelProvider { get; set; }
        public string DefaultText { get; set; }
    }

    // ToolStripDropDownMenu rewrites Padding from DefaultPadding on every layout (top/bottom fixed at 2 px),
    // so the roomier DPI-scaled vertical padding has to come from an override.
    internal sealed class DarkContextMenu : ContextMenuStrip
    {
        protected override Padding DefaultPadding { get { return MenuTheme.DropDownPadding(this, base.DefaultPadding); } }
    }

    internal sealed class DarkDropDownMenu : ToolStripDropDownMenu
    {
        protected override Padding DefaultPadding { get { return MenuTheme.DropDownPadding(this, base.DefaultPadding); } }
    }

    // Submenu parent that owns a DarkDropDownMenu (not auto-generated, so it is disposed here).
    internal abstract class DarkSubMenuItemBase : ToolStripMenuItem
    {
        private readonly DarkDropDownMenu _menu = new DarkDropDownMenu();

        protected DarkSubMenuItemBase(string text) : base(text)
        {
            DropDown = _menu;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && !_menu.IsDisposed) _menu.Dispose();
        }
    }

    // Static submenu whose visibility follows its children.
    internal sealed class SubMenuItem : DarkSubMenuItemBase
    {
        public SubMenuItem(string text) : base(text) { }
    }

    // Submenu rebuilt from IMenuDataSource every time it opens.
    internal sealed class DynamicMenuItem : DarkSubMenuItemBase
    {
        private readonly Func<bool> _shown;

        public DynamicMenuItem(string text, Func<IList<MenuEntry>> source, bool radio, Func<bool> shown) : base(text)
        {
            Source = source;
            Radio = radio;
            _shown = shown;
        }

        public Func<IList<MenuEntry>> Source { get; private set; }
        public bool Radio { get; private set; }

        // False hides the submenu (e.g. its provider is not set); otherwise it shows, "(none)" when empty.
        public bool Shown { get { return _shown == null || _shown(); } }
    }

    // Invisible marker inside a static menu: each time the menu refreshes, the entries of Source are
    // inserted right after it (replacing the previous ones), between the static items around it.
    internal sealed class DynamicSlot : ToolStripMenuItem
    {
        public DynamicSlot(Func<IList<MenuEntry>> source)
        {
            Source = source;
            Available = false;
        }

        public Func<IList<MenuEntry>> Source { get; private set; }
        public readonly List<ToolStripItem> Inserted = new List<ToolStripItem>();
    }

    // Separator with the menu's own (taller, DPI-aware) height.
    internal sealed class MenuSeparator : ToolStripSeparator
    {
        public override Size GetPreferredSize(Size constrainingSize)
        {
            Size s = base.GetPreferredSize(constrainingSize);
            if (IsOnDropDown) s.Height = MenuTheme.Px(MenuTheme.DpiOf(Owner), DarkMenuRenderer.SeparatorHeight);
            return s;
        }
    }

    // Theming plumbing shared by MenuBuilder.ApplyTheme and the renderer: per-strip DPI,
    // fonts, item heights and the Windows 11 rounded frame.
    internal static class MenuTheme
    {
        private sealed class State
        {
            public int Dpi;
            public bool DwmFrame;
            public Point? OpenAt;   // screen point a root menu is being shown at (MenuBuilder.ShowAt)
        }

        private static readonly DarkMenuRenderer SharedRenderer = new DarkMenuRenderer();
        private static readonly ConditionalWeakTable<ToolStrip, State> States = new ConditionalWeakTable<ToolStrip, State>();
        private static readonly Dictionary<int, Font> UiFonts = new Dictionary<int, Font>();

        public static int Px(int dpi, int logical) { return (int)Math.Round(logical * dpi / 96.0); }
        public static float PxF(int dpi, float logical) { return logical * dpi / 96f; }

        public static int DpiOf(ToolStrip strip)
        {
            if (strip == null) return Theme.SystemDpi;
            State st;
            if (States.TryGetValue(strip, out st) && st.Dpi > 0) return st.Dpi;
            return WindowDpi(strip);
        }

        public static Padding DropDownPadding(ToolStrip strip, Padding basePadding)
        {
            int y = Px(DpiOf(strip), DarkMenuRenderer.DropDownPaddingY);
            return new Padding(basePadding.Left, y, basePadding.Right, y);
        }

        // Control.DeviceDpi stays at 96 unless WinForms' own DPI handling is enabled by app.config,
        // so ask Windows for the window's real DPI.
        private static int WindowDpi(Control c)
        {
            if (c == null || c.IsDisposed || !c.IsHandleCreated) return Theme.SystemDpi;
            try
            {
                int dpi = (int)NativeMethods.GetDpiForWindow(c.Handle);
                if (dpi >= 48 && dpi <= 960) return dpi;
            }
            catch (EntryPointNotFoundException) { }
            return c.DeviceDpi;
        }

        // Set around Show() so the Opening pass sizes the menu for the monitor it opens on.
        public static void SetOpenPoint(ToolStrip strip, Point? screen)
        {
            if (strip == null) return;
            States.GetValue(strip, _ => new State()).OpenAt = screen;
        }

        public static bool HasDwmFrame(ToolStrip strip)
        {
            State st;
            return strip != null && States.TryGetValue(strip, out st) && st.DwmFrame;
        }

        public static void Apply(ToolStrip strip)
        {
            if (strip == null || strip.IsDisposed) return;
            States.GetValue(strip, _ => new State());
            strip.Renderer = SharedRenderer;
            strip.BackColor = Theme.Panel;
            strip.ForeColor = Theme.Text;
            var menu = strip as ToolStripDropDownMenu;
            if (menu != null)
            {
                menu.ShowImageMargin = false;
                menu.ShowCheckMargin = true;
            }
            var drop = strip as ToolStripDropDown;
            if (drop != null)
            {
                // -= then += keeps exactly one subscription however often a strip is themed.
                drop.Opening -= OnOpening;
                drop.Opening += OnOpening;
                drop.HandleCreated -= OnHandleCreated;
                drop.HandleCreated += OnHandleCreated;
                if (drop.IsHandleCreated) ApplyDwmFrame(drop);
            }
            else
            {
                strip.GripStyle = ToolStripGripStyle.Hidden;
            }
            strip.ItemAdded -= OnItemAdded;
            strip.ItemAdded += OnItemAdded;
            UpdateMetrics(strip, ResolveDpi(strip));
            foreach (ToolStripItem item in strip.Items) ThemeItem(item);
        }

        private static void ThemeItem(ToolStripItem item)
        {
            var ddi = item as ToolStripDropDownItem;
            if (ddi == null) return;
            ddi.DropDownOpening -= OnItemDropDownOpening;
            ddi.DropDownOpening += OnItemDropDownOpening;
            if (ddi.HasDropDownItems) Apply(ddi.DropDown);
        }

        private static void OnItemAdded(object sender, ToolStripItemEventArgs e)
        {
            if (e.Item != null) ThemeItem(e.Item);
        }

        // Catches drop-downs that were created or filled after the parent was themed.
        private static void OnItemDropDownOpening(object sender, EventArgs e)
        {
            var ddi = sender as ToolStripDropDownItem;
            if (ddi == null) return;
            State st;
            ToolStripDropDown drop = ddi.DropDown;
            if (!States.TryGetValue(drop, out st) || drop.Renderer != SharedRenderer) Apply(drop);
        }

        private static void OnOpening(object sender, CancelEventArgs e)
        {
            var strip = sender as ToolStrip;
            if (strip != null && !strip.IsDisposed) UpdateMetrics(strip, ResolveDpi(strip));
        }

        private static void OnHandleCreated(object sender, EventArgs e)
        {
            var drop = sender as ToolStripDropDown;
            if (drop != null) ApplyDwmFrame(drop);
        }

        private static void UpdateMetrics(ToolStrip strip, int dpi)
        {
            State st = States.GetValue(strip, _ => new State());
            st.Dpi = dpi;
            Font font = UiFont(dpi);
            if (!ReferenceEquals(strip.Font, font)) strip.Font = font;
            if (!(strip is ToolStripDropDown)) return;

            var check = new Size(Px(dpi, DarkMenuRenderer.CheckColumn), Px(dpi, DarkMenuRenderer.CheckColumn));
            if (strip.ImageScalingSize != check) strip.ImageScalingSize = check;

            // Menu items in a drop-down share one content height; pad each up to the comfortable target.
            int target = Px(dpi, DarkMenuRenderer.ItemHeight);
            foreach (ToolStripItem item in strip.Items)
            {
                var mi = item as ToolStripMenuItem;
                if (mi == null) continue;
                Padding p = mi.Padding;
                int content = mi.GetPreferredSize(Size.Empty).Height - p.Vertical;
                int extra = Math.Max(0, target - content);
                var wanted = new Padding(p.Left, extra / 2, p.Right, extra - extra / 2);
                if (p != wanted) mi.Padding = wanted;
            }
        }

        // Segoe UI 9 pt at the menu's DPI (point fonts render at the system DPI, so rescale for other monitors).
        private static Font UiFont(int dpi)
        {
            Font f;
            if (!UiFonts.TryGetValue(dpi, out f))
            {
                f = Theme.UiFont(9f * dpi / Theme.SystemDpi);
                UiFonts[dpi] = f;
            }
            return f;
        }

        // A submenu opens beside its parent. A root menu uses the monitor at the point it is being shown at
        // (MenuBuilder.ShowAt), else its source control's, else the cursor's (a keyboard-opened menu can open
        // away from the cursor, possibly on another monitor).
        private static int ResolveDpi(ToolStrip strip)
        {
            var drop = strip as ToolStripDropDown;
            if (drop == null) return WindowDpi(strip);
            if (drop.OwnerItem != null && drop.OwnerItem.Owner != null) return DpiOf(drop.OwnerItem.Owner);
            int dpi = 0;
            try
            {
                State st;
                Point? at = States.TryGetValue(drop, out st) ? st.OpenAt : null;
                var cms = drop as ContextMenuStrip;
                Control src = cms != null ? cms.SourceControl : null;
                IntPtr monitor;
                if (at.HasValue)
                    monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = at.Value.X, Y = at.Value.Y }, NativeMethods.MonitorDefaultToNearest);
                else if (src != null && !src.IsDisposed && src.IsHandleCreated)
                    monitor = NativeMethods.MonitorFromWindow(src.Handle, NativeMethods.MonitorDefaultToNearest);
                else
                    monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = Cursor.Position.X, Y = Cursor.Position.Y }, NativeMethods.MonitorDefaultToNearest);
                uint dx, dy;
                if (monitor != IntPtr.Zero && NativeMethods.GetDpiForMonitor(monitor, 0, out dx, out dy) == 0) dpi = (int)dx;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return dpi >= 48 && dpi <= 960 ? dpi : Theme.SystemDpi;
        }

        // Windows 11: small rounded corners with a DWM border in our colour. Older systems reject
        // the attribute and the renderer draws a square 1 px frame instead.
        private static void ApplyDwmFrame(ToolStripDropDown drop)
        {
            State st = States.GetValue(drop, _ => new State());
            st.DwmFrame = false;
            try
            {
                int corner = NativeMethods.DwmwcpRoundSmall;
                if (NativeMethods.DwmSetWindowAttribute(drop.Handle, NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int)) != 0) return;
                int border = ColorTranslator.ToWin32(Theme.Border);
                st.DwmFrame = NativeMethods.DwmSetWindowAttribute(drop.Handle, NativeMethods.DwmwaBorderColor, ref border, sizeof(int)) == 0;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static class NativeMethods
        {
            public const uint MonitorDefaultToNearest = 2;
            public const int DwmwaWindowCornerPreference = 33, DwmwaBorderColor = 34, DwmwcpRoundSmall = 3;

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X, Y; }

            [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
            [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
            [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
            [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
            [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        }
    }

    // Builds the app's menus from ActionRegistry ids. Item text = ActionRegistry.TitleOf(id)
    // (or a menu-specific label), shortcut text = registry.ShortcutText(id), checked/enabled
    // state queried from the registry every time a menu opens. Clicking runs registry.Execute(id).
    // Keyboard shortcuts are NOT registered on the items (MainForm dispatches keys itself).
    public sealed class MenuBuilder
    {
        private const int MaxDynamicTextLength = 72;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly ActionRegistry _registry;
        private readonly IMenuDataSource _data;

        public MenuBuilder(ActionRegistry registry, IMenuDataSource data)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            _registry = registry;
            _data = data;
        }

        // Extra dynamic parts (null = hidden). Audio ▸ Audio Device lists "AudioDevice:<name>" entries (radio);
        // the Markers submenu lists "GoToMarker:<index>" entries between its static items.
        public Func<IList<MenuEntry>> AudioDevicesProvider { get; set; }
        public Func<IList<MenuEntry>> MarkersProvider { get; set; }

        // Label of Cull ▸ Move Clip to Bin ▸ <n> (1..5), e.g. "Bin 1: Best shots"; null or empty = "Bin <n>".
        // Read each time the menu opens.
        public Func<int, string> CullBinLabelProvider { get; set; }

        // Shows a menu at a screen point, sized for the DPI of the monitor at that point (not the cursor's
        // monitor). Use this for every menu MainForm opens.
        public static void ShowAt(ContextMenuStrip menu, Point screen)
        {
            if (menu == null || menu.IsDisposed) return;
            MenuTheme.SetOpenPoint(menu, screen);
            try
            {
                menu.Show(screen);
            }
            finally
            {
                MenuTheme.SetOpenPoint(menu, null);
            }
        }

        // The full right-click menu (also used by the control bar's "…" button).
        public ContextMenuStrip BuildContextMenu()
        {
            return Root(
                Item(ActionIds.OpenFile),
                Item(ActionIds.OpenFolder),
                Item(ActionIds.OpenUrl),
                Dynamic("Recent Files", () => _data == null ? null : _data.RecentFiles(), false),
                Sep(),
                Item(ActionIds.PlayPause),
                Item(ActionIds.Stop),
                Item(ActionIds.FrameStep),
                Item(ActionIds.FrameBackStep),
                Item(ActionIds.ToggleReverse),
                Item(ActionIds.GoTo),
                Sub("Navigate",
                    Item(ActionIds.NextFile),
                    Item(ActionIds.PrevFile),
                    Item(ActionIds.FirstFile),
                    Item(ActionIds.LastFile),
                    Sub("Playlist",
                        Item(ActionIds.SortPlaylist + ":Name", null, true),
                        Item(ActionIds.SortPlaylist + ":DateModified", null, true),
                        Item(ActionIds.SortPlaylist + ":Size", null, true),
                        Item(ActionIds.SortPlaylist + ":Type", null, true),
                        Item(ActionIds.TogglePlaylistSortOrder),
                        Sep(),
                        Item(ActionIds.SavePlaylist)),
                    Sep(),
                    Item(ActionIds.NextChapter),
                    Item(ActionIds.PrevChapter),
                    Dynamic("Chapters", () => _data == null ? null : _data.Chapters(), true)),
                Sub("Cull", CullItems()),
                Sub("Markers",
                    Item(ActionIds.AddMarker),
                    Item(ActionIds.NextMarker),
                    Item(ActionIds.PrevMarker),
                    Sep(),
                    Slot(() => MarkersProvider == null ? null : MarkersProvider()),
                    Sep(),
                    Item(ActionIds.CopyMarkers),
                    Item(ActionIds.ExportMarkersCsv, "Export as CSV…"),
                    Item(ActionIds.ExportMarkersEdl, "Export as EDL (DaVinci Resolve)…"),
                    Sep(),
                    Item(ActionIds.ClearMarkers)),
                Sub("Speed", SpeedItems()),
                Sub("Repeat & Loop", RepeatItems()),
                Sep(),
                Sub("Video",
                    Sub("Zoom", ZoomItems()),
                    Sub("Aspect Ratio", AspectItems()),
                    Item(ActionIds.ToggleFill),
                    Sub("Rotate", RotateItems()),
                    Item(ActionIds.FlipH),
                    Item(ActionIds.FlipV),
                    Sep(),
                    Item(ActionIds.ToggleDeinterlace),
                    Item(ActionIds.ToggleHwDec),
                    Sep(),
                    Sub("Picture",
                        Item(ActionIds.BrightnessUp),
                        Item(ActionIds.BrightnessDown),
                        Item(ActionIds.ContrastUp),
                        Item(ActionIds.ContrastDown),
                        Item(ActionIds.SaturationUp),
                        Item(ActionIds.SaturationDown),
                        Item(ActionIds.GammaUp),
                        Item(ActionIds.GammaDown),
                        Item(ActionIds.HueUp),
                        Item(ActionIds.HueDown),
                        Sep(),
                        Item(ActionIds.ResetVideoAdjust)),
                    Dynamic("Video Track", () => _data == null ? null : _data.VideoTracks(), true)),
                Sub("Audio",
                    Dynamic("Audio Track", () => _data == null ? null : _data.AudioTracks(), true),
                    Item(ActionIds.CycleAudioTrack),
                    Dynamic("Audio Device", () => AudioDevicesProvider == null ? null : AudioDevicesProvider(), true,
                        () => AudioDevicesProvider != null),
                    Sep(),
                    Item(ActionIds.VolumeUp),
                    Item(ActionIds.VolumeDown),
                    Item(ActionIds.ToggleMute),
                    Item(ActionIds.ToggleAudioNormalize),
                    Sub("Channels",
                        Item(ActionIds.SetAudioChannels + ":auto", null, true),
                        Item(ActionIds.SetAudioChannels + ":stereo", null, true),
                        Item(ActionIds.SetAudioChannels + ":mono", null, true)),
                    Sep(),
                    Item(ActionIds.AudioDelayUp),
                    Item(ActionIds.AudioDelayDown),
                    Item(ActionIds.AudioDelayReset)),
                Sub("Subtitles",
                    Dynamic("Subtitle Track", () => _data == null ? null : _data.SubtitleTracks(), true),
                    Item(ActionIds.CycleSubtitle),
                    Item(ActionIds.ToggleSubtitles),
                    Item(ActionIds.LoadSubtitle),
                    Sep(),
                    Item(ActionIds.SubDelayUp),
                    Item(ActionIds.SubDelayDown),
                    Item(ActionIds.SubDelayReset),
                    Sep(),
                    Item(ActionIds.SubScaleUp),
                    Item(ActionIds.SubScaleDown)),
                Sub("Screenshot",
                    Item(ActionIds.Screenshot),
                    Item(ActionIds.ScreenshotDisplayed),
                    Item(ActionIds.CopyFrame),
                    Sep(),
                    Item(ActionIds.OpenScreenshotFolder)),
                Sep(),
                Sub("View",
                    Item(ActionIds.ToggleFullscreen),
                    Item(ActionIds.ToggleOnTop),
                    Item(ActionIds.ToggleBorderless),
                    Item(ActionIds.ToggleKeepAspect),
                    Item(ActionIds.ToggleControls),
                    Item(ActionIds.TogglePlaylist),
                    Item(ActionIds.ToggleStats),
                    Item(ActionIds.ToggleOsd),
                    Item(ActionIds.ToggleOsdStatus),
                    Item(ActionIds.ShowInfoOsd)),
                Sub("File",
                    Item(ActionIds.ShowInFolder),
                    Item(ActionIds.RenameFile),
                    Item(ActionIds.CopyPath),
                    Item(ActionIds.MediaInfo),
                    Sep(),
                    Item(ActionIds.RegisterFileTypes, "Add VidFlow to “Open With”…"),
                    Item(ActionIds.UnregisterFileTypes),
                    Sep(),
                    Item(ActionIds.DeleteFile),
                    Sep(),
                    Item(ActionIds.CloseFile)),
                Sep(),
                Item(ActionIds.Settings),
                Item(ActionIds.Shortcuts),
                Item(ActionIds.ShowQuickStart),
                HiddenWhenDisabled(Item(ActionIds.CheckForUpdates)),   // disabled = no update repository configured
                Item(ActionIds.About),
                Sep(),
                Item(ActionIds.Exit));
        }

        // Small drop-downs for control-bar buttons.
        public ContextMenuStrip BuildSpeedMenu() { return Root(SpeedItems()); }
        public ContextMenuStrip BuildRepeatMenu() { return Root(RepeatItems()); }
        public ContextMenuStrip BuildZoomMenu() { return Root(ZoomItems()); }
        // The Cull submenu on its own, e.g. for the control bar's cull chip.
        public ContextMenuStrip BuildCullMenu() { return Root(CullItems()); }

        // Applies the dark theme (renderer, colours, font) to any ToolStrip/ContextMenuStrip.
        public static void ApplyTheme(ToolStrip strip)
        {
            MenuTheme.Apply(strip);
        }

        // Brings a built menu up to date without opening it (checked/enabled/shortcut state,
        // hidden unregistered ids, dynamic submenus rebuilt), e.g. before describing it for automation.
        // Item texts escape '&' as "&&" (WinForms mnemonic syntax).
        public void Refresh(ToolStrip menu)
        {
            if (menu == null || menu.IsDisposed) return;
            RefreshTree(menu.Items);
        }

        // ---- item lists shared by the context menu and the control-bar drop-downs ----

        private ToolStripItem[] SpeedItems()
        {
            var items = new List<ToolStripItem>();
            foreach (double s in Presets.Speeds)
                items.Add(Item(ActionIds.SetSpeed + ":" + Fmt.Num(s), Fmt.Speed(s), true));
            items.Add(Sep());
            items.Add(Item(ActionIds.SpeedUp));
            items.Add(Item(ActionIds.SpeedDown));
            items.Add(Item(ActionIds.SpeedFineUp));
            items.Add(Item(ActionIds.SpeedFineDown));
            items.Add(Item(ActionIds.SpeedReset));
            items.Add(Sep());
            items.Add(Item(ActionIds.TogglePitchCorrection));
            return items.ToArray();
        }

        private ToolStripItem[] RepeatItems()
        {
            return new ToolStripItem[]
            {
                Item(ActionIds.SetRepeat + ":Off", null, true),
                Item(ActionIds.SetRepeat + ":One", null, true),
                Item(ActionIds.SetRepeat + ":All", null, true),
                Sep(),
                Item(ActionIds.AbLoop),
                Item(ActionIds.AbSetA),
                Item(ActionIds.AbSetB),
                Sub("Nudge Loop",
                    Item(ActionIds.AbNudge + ":A-1"),
                    Item(ActionIds.AbNudge + ":A+1"),
                    Sep(),
                    Item(ActionIds.AbNudge + ":B-1"),
                    Item(ActionIds.AbNudge + ":B+1")),
                Item(ActionIds.AbLoopClear),
                Sep(),
                Item(ActionIds.ToggleShuffle),
            };
        }

        // Decide clip by clip (the Cull key layer), then apply the decisions in one batch.
        private ToolStripItem[] CullItems()
        {
            var rate = new List<ToolStripItem>();
            for (int stars = 1; stars <= 5; stars++)
                rate.Add(Item(ActionIds.CullRate + ":" + stars.ToString(Inv), new string('★', stars), true));
            rate.Add(Sep());
            ActionMenuItem clear = Item(ActionIds.CullRate + ":0", "Clear Rating");
            clear.NoCheck = true;
            rate.Add(clear);

            var bins = new List<ToolStripItem>();
            for (int bin = 1; bin <= 5; bin++)
            {
                int n = bin;
                ActionMenuItem item = Item(ActionIds.CullToBin + ":" + n.ToString(Inv), "Bin " + n.ToString(Inv));
                item.LabelProvider = () => CullBinLabelProvider == null ? null : CullBinLabelProvider(n);
                bins.Add(item);
            }

            return new ToolStripItem[]
            {
                Item(ActionIds.ToggleCullMode),
                Sep(),
                Item(ActionIds.CullPick),
                Item(ActionIds.CullReject),
                Item(ActionIds.CullUnflag),
                Sub("Rate", rate.ToArray()),
                Item(ActionIds.CullNextUndecided),
                Sep(),
                Item(ActionIds.CullUndo),
                Item(ActionIds.CullRedo),
                Item(ActionIds.CullAutoAdvance),
                Sub("Show",
                    Item(ActionIds.CullFilter + ":all", "All Clips", true),
                    Item(ActionIds.CullFilter + ":picked", "Picks Only", true),
                    Item(ActionIds.CullFilter + ":rejected", "Rejects Only", true),
                    Item(ActionIds.CullFilter + ":undecided", "Undecided Only", true),
                    Item(ActionIds.CullFilter + ":rated", "Rated Only", true)),
                Sub("Move Clip to Bin", bins.ToArray()),
                Sep(),
                Item(ActionIds.CullCommit, "Apply Decisions…"),
            };
        }

        private ToolStripItem[] ZoomItems()
        {
            // "Fit" belongs to the same one-of-many group as the pixel zoom presets.
            var items = new List<ToolStripItem> { Item(ActionIds.ZoomReset, null, true) };
            foreach (int z in Presets.PixelZooms)
            {
                string pct = z.ToString(Inv) + "%";
                items.Add(Item(ActionIds.SetZoom + ":" + z.ToString(Inv), z == 100 ? pct + " (1:1)" : pct, true));
            }
            items.Add(Sep());
            items.Add(Item(ActionIds.ZoomIn));
            items.Add(Item(ActionIds.ZoomOut));
            items.Add(Sep());
            var sizes = new List<ToolStripItem>();
            foreach (string w in Presets.WindowSizes)
                sizes.Add(Item(ActionIds.WindowSize + ":" + w, w == "fit" ? "Fit to Screen" : w + "%", true));
            items.Add(Sub("Window Size", sizes.ToArray()));
            items.Add(Sep());
            items.Add(Item(ActionIds.ToggleFill));
            return items.ToArray();
        }

        private ToolStripItem[] AspectItems()
        {
            var items = new List<ToolStripItem>();
            foreach (string a in Presets.Aspects)
                items.Add(Item(ActionIds.SetAspect + ":" + a, Presets.AspectLabel(a), true));
            return items.ToArray();
        }

        private ToolStripItem[] RotateItems()
        {
            var items = new List<ToolStripItem>();
            foreach (int r in Presets.Rotations)
                items.Add(Item(ActionIds.SetRotate + ":" + r.ToString(Inv), r.ToString(Inv) + "°", true));
            items.Add(Sep());
            items.Add(Item(ActionIds.RotateCW));
            items.Add(Item(ActionIds.RotateCCW));
            return items.ToArray();
        }

        // ---- construction helpers ----

        private ContextMenuStrip Root(params ToolStripItem[] items)
        {
            var menu = new DarkContextMenu();
            menu.SuspendLayout();
            menu.Items.AddRange(items);
            menu.Opening += OnStaticOpening;
            menu.ResumeLayout(false);
            ApplyTheme(menu);
            return menu;
        }

        private ActionMenuItem Item(string id, string label = null, bool radio = false)
        {
            string text = Escape(label ?? ActionRegistry.TitleOf(id));
            var item = new ActionMenuItem(text, id, radio) { DefaultText = text };
            item.Click += OnItemClick;
            return item;
        }

        private static ActionMenuItem HiddenWhenDisabled(ActionMenuItem item)
        {
            item.HideWhenDisabled = true;
            return item;
        }

        // The keys shown beside an item. Keys of the Cull layer only work in Cull mode, so they say so
        // ("P (Cull mode)"): the same key may do something else the rest of the time.
        private string ShortcutLabel(string id)
        {
            string keys = _registry.ShortcutText(id);
            if (keys.Length > 0 && ActionRegistry.LayerOf(id) == ActionCatalog.LayerCull) keys += " (Cull mode)";
            return keys;
        }

        private SubMenuItem Sub(string text, params ToolStripItem[] items)
        {
            var sub = new SubMenuItem(Escape(text));
            sub.DropDownItems.AddRange(items);
            sub.DropDown.Opening += OnStaticOpening;
            return sub;
        }

        private DynamicMenuItem Dynamic(string text, Func<IList<MenuEntry>> source, bool radio, Func<bool> shown = null)
        {
            var dyn = new DynamicMenuItem(Escape(text), source, radio, shown);
            // Keep a placeholder so the submenu arrow shows and hovering opens (and rebuilds) it.
            Populate(dyn, null);
            dyn.DropDownOpening += OnDynamicOpening;
            return dyn;
        }

        private static DynamicSlot Slot(Func<IList<MenuEntry>> source) { return new DynamicSlot(source); }

        private static MenuSeparator Sep() { return new MenuSeparator(); }

        // WinForms treats '&' as a mnemonic marker ("Repeat & Loop", file names); double it to show it literally.
        private static string Escape(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : text.Replace("&", "&&");
        }

        // Very long names (recent files) are shortened in the middle so the menu stays a sane width.
        private static string DynamicText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(untitled)";
            text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            if (text.Length > MaxDynamicTextLength)
            {
                int head = (MaxDynamicTextLength - 1) * 2 / 3, tail = MaxDynamicTextLength - 1 - head;
                text = text.Substring(0, head) + "…" + text.Substring(text.Length - tail);
            }
            return Escape(text);
        }

        // ---- state refresh ----

        private void OnStaticOpening(object sender, CancelEventArgs e)
        {
            var drop = sender as ToolStripDropDown;
            if (drop != null && !drop.IsDisposed) RefreshItems(drop.Items);
        }

        private void RefreshTree(ToolStripItemCollection items)
        {
            RefreshItems(items);
            foreach (ToolStripItem item in items)
            {
                var dyn = item as DynamicMenuItem;
                if (dyn != null) Rebuild(dyn);
                else if (item is SubMenuItem) RefreshTree(((SubMenuItem)item).DropDownItems);
            }
        }

        private void RefreshItems(ToolStripItemCollection items)
        {
            FillSlots(items);
            foreach (ToolStripItem item in items)
            {
                var dynItem = item as DynamicMenuItem;
                if (dynItem != null)
                {
                    dynItem.Available = dynItem.Shown;
                    continue;
                }
                var sub = item as SubMenuItem;
                if (sub != null)
                {
                    sub.Available = AnyAvailable(sub.DropDownItems);
                    continue;
                }
                var mi = item as ActionMenuItem;
                string id = mi != null && !mi.FromEntry ? mi.Tag as string : null;
                if (id == null) continue;
                bool known = _registry.IsKnown(id);
                bool enabled = known && _registry.IsEnabled(id);
                mi.Available = known && (enabled || !mi.HideWhenDisabled);
                if (!mi.Available) continue;
                mi.Enabled = enabled;
                mi.Checked = !mi.NoCheck && _registry.IsChecked(id);
                mi.ShortcutKeyDisplayString = ShortcutLabel(id);
                if (mi.LabelProvider != null) mi.Text = ProvidedText(mi);
            }
            CollapseSeparators(items);
        }

        private static string ProvidedText(ActionMenuItem mi)
        {
            string label = null;
            try { label = mi.LabelProvider(); }
            catch (Exception ex) { Log.Error("menu: building the label of '" + mi.DefaultText + "' failed", ex); }
            return string.IsNullOrWhiteSpace(label) ? mi.DefaultText ?? "" : DynamicText(label);
        }

        private bool AnyAvailable(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                var dyn = item as DynamicMenuItem;
                if (dyn != null)
                {
                    if (dyn.Shown) return true;
                    continue;
                }
                var sub = item as SubMenuItem;
                if (sub != null)
                {
                    if (AnyAvailable(sub.DropDownItems)) return true;
                    continue;
                }
                var mi = item as ActionMenuItem;
                string id = mi != null ? mi.Tag as string : null;
                if (id != null && _registry.IsKnown(id) && (!mi.HideWhenDisabled || _registry.IsEnabled(id))) return true;
            }
            return false;
        }

        // Replaces the entries after each DynamicSlot with fresh ones from its source.
        private void FillSlots(ToolStripItemCollection items)
        {
            List<DynamicSlot> slots = null;
            foreach (ToolStripItem item in items)
            {
                var slot = item as DynamicSlot;
                if (slot == null) continue;
                if (slots == null) slots = new List<DynamicSlot>();
                slots.Add(slot);
            }
            if (slots == null) return;
            foreach (var slot in slots)
            {
                ToolStrip owner = slot.Owner;
                if (owner != null) owner.SuspendLayout();
                foreach (var old in slot.Inserted)
                {
                    items.Remove(old);
                    old.Dispose();
                }
                slot.Inserted.Clear();
                slot.Available = false;
                IList<MenuEntry> entries = null;
                try { entries = slot.Source(); }
                catch (Exception ex) { Log.Error("menu: building a dynamic section failed", ex); }
                if (entries != null)
                {
                    int at = items.IndexOf(slot) + 1;
                    foreach (MenuEntry entry in entries)
                    {
                        if (entry == null) continue;
                        ToolStripItem it = entry.IsSeparator ? (ToolStripItem)Sep() : EntryItem(entry, false);
                        items.Insert(at++, it);
                        slot.Inserted.Add(it);
                    }
                }
                if (owner != null) owner.ResumeLayout(false);
            }
        }

        // Hides leading, trailing and doubled separators left behind by hidden items.
        private static void CollapseSeparators(ToolStripItemCollection items)
        {
            ToolStripItem pending = null;
            bool seenContent = false;
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripSeparator)
                {
                    item.Available = false;
                    if (seenContent && pending == null) pending = item;
                    continue;
                }
                if (!item.Available) continue;
                if (pending != null)
                {
                    pending.Available = true;
                    pending = null;
                }
                seenContent = true;
            }
        }

        private void OnDynamicOpening(object sender, EventArgs e)
        {
            var dyn = sender as DynamicMenuItem;
            if (dyn != null && !dyn.IsDisposed) Rebuild(dyn);
        }

        private void Rebuild(DynamicMenuItem dyn)
        {
            IList<MenuEntry> entries = null;
            try { entries = dyn.Source(); }
            catch (Exception ex) { Log.Error("menu: building '" + dyn.Text + "' failed", ex); }
            Populate(dyn, entries);
        }

        private void Populate(DynamicMenuItem dyn, IList<MenuEntry> entries)
        {
            ToolStripDropDown drop = dyn.DropDown;
            drop.SuspendLayout();
            DisposeItems(drop);

            bool any = false;
            if (entries != null)
            {
                foreach (MenuEntry entry in entries)
                {
                    if (entry == null) continue;
                    if (entry.IsSeparator)
                    {
                        drop.Items.Add(Sep());
                        continue;
                    }
                    drop.Items.Add(EntryItem(entry, dyn.Radio));
                    any = true;
                }
            }
            if (any)
            {
                CollapseSeparators(drop.Items);
            }
            else
            {
                DisposeItems(drop);
                drop.Items.Add(new ToolStripMenuItem("(none)") { Enabled = false });
            }
            drop.ResumeLayout(true);
        }

        // Menu item for one dynamic entry: state from the entry, enabled only if its action is registered.
        private ActionMenuItem EntryItem(MenuEntry entry, bool radio)
        {
            string id = string.IsNullOrEmpty(entry.ActionId) ? null : entry.ActionId;
            bool known = id != null && _registry.IsKnown(id);
            var item = new ActionMenuItem(DynamicText(entry.Text), id, radio)
            {
                FromEntry = true,
                Checked = entry.Checked,
                Enabled = entry.Enabled && known,
                ShortcutKeyDisplayString = known ? ShortcutLabel(id) : "",
            };
            if (known) item.Click += OnItemClick;
            return item;
        }

        private static void DisposeItems(ToolStripDropDown drop)
        {
            var old = new ToolStripItem[drop.Items.Count];
            drop.Items.CopyTo(old, 0);
            drop.Items.Clear();
            foreach (ToolStripItem it in old) it.Dispose();
        }

        // ---- execution ----

        private void OnItemClick(object sender, EventArgs e)
        {
            var item = sender as ToolStripItem;
            string id = item != null ? item.Tag as string : null;
            if (string.IsNullOrEmpty(id)) return;
            // Run once the menu has closed so dialogs opened by the action don't fight the menu's modal loop.
            Control owner = OwnerControl(item);
            if (owner != null)
            {
                try
                {
                    owner.BeginInvoke(new Action(() => _registry.Execute(id)));
                    return;
                }
                catch (InvalidOperationException) { } // handle destroyed between the check and the post
            }
            _registry.Execute(id);
        }

        private static Control OwnerControl(ToolStripItem item)
        {
            ToolStrip strip = item.Owner;
            var drop = strip as ToolStripDropDown;
            while (drop != null && drop.OwnerItem != null && drop.OwnerItem.Owner != null)
            {
                strip = drop.OwnerItem.Owner;
                drop = strip as ToolStripDropDown;
            }
            var cms = strip as ContextMenuStrip;
            if (cms != null && Usable(cms.SourceControl)) return cms.SourceControl;
            return Usable(strip) ? strip : null;
        }

        private static bool Usable(Control c)
        {
            return c != null && !c.IsDisposed && c.IsHandleCreated;
        }
    }
}
