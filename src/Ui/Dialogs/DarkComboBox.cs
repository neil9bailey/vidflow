using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    // Combo box painted entirely by us: the closed field (WM_PAINT) and the drop-down
    // items (owner draw), because the stock flat style paints a light drop-down button.
    // Works for DropDownList and editable DropDown styles; the drop-down list gets dark
    // scrollbars.
    public class DarkComboBox : ComboBox
    {
        private const int WM_PAINT = 0x000F, WM_PRINTCLIENT = 0x0318;
        private bool _hover;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct COMBOBOXINFO
        {
            public int cbSize;
            public RECT rcItem, rcButton;
            public int stateButton;
            public IntPtr hwndCombo, hwndItem, hwndList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore, fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr hwnd, ref COMBOBOXINFO info);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
        [DllImport("gdi32.dll")] private static extern int ExcludeClipRect(IntPtr hdc, int left, int top, int right, int bottom);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref RECT rect, int count);
        private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

        public DarkComboBox()
        {
            SetStyle(ControlStyles.ResizeRedraw, true);
            DrawMode = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            BackColor = DarkControls.Field;
            ForeColor = Theme.Text;
            IntegralHeight = false;
            MaxDropDownItems = 12;
            // The native control picks its own height from ItemHeight; AutoSize makes layout
            // engines ask GetPreferredSize (the real height) instead of the last requested bounds.
            AutoSize = true;
        }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        // Width last requested by code (Width = ...); layout passes do not change it.
        private int _requestedWidth;

        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if ((specified & BoundsSpecified.Width) != 0) _requestedWidth = width;
            base.SetBoundsCore(x, y, width, height, specified);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int h = ItemHeight + 6;
            RECT r;
            if (IsHandleCreated && GetWindowRect(Handle, out r) && r.Bottom > r.Top) h = r.Bottom - r.Top;
            return new Size(_requestedWidth > 0 ? _requestedWidth : Width, h);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateItemHeight();
            var info = Info();
            if (info.hwndList != IntPtr.Zero) DarkControls.UseDarkTheme(info.hwndList);
            if (EditHandle(info) != IntPtr.Zero)
            {
                int m = DarkControls.Scale(this, 6);
                DarkControls.SendMessage(info.hwndItem, DarkControls.EM_SETMARGINS, (IntPtr)DarkControls.EC_LEFTMARGIN, (IntPtr)m);
            }
            PositionEdit();
        }

        private IntPtr EditHandle(COMBOBOXINFO info)
        {
            if (DropDownStyle == ComboBoxStyle.DropDownList) return IntPtr.Zero;
            return info.hwndItem != Handle ? info.hwndItem : IntPtr.Zero;
        }

        // Native edit child of an editable combo (zero for DropDownList or before the handle exists).
        internal IntPtr EditWindow { get { return EditHandle(Info()); } }

        // Editable combos get the dark edit menu; the native combo forwards its edit child's
        // WM_CONTEXTMENU to us whenever a ContextMenuStrip is set.
        protected override void OnDropDownStyleChanged(EventArgs e)
        {
            base.OnDropDownStyleChanged(e);
            if (DropDownStyle == ComboBoxStyle.DropDownList)
            {
                if (ContextMenuStrip == EditMenu.Shared) ContextMenuStrip = null;
            }
            else if (ContextMenuStrip == null) ContextMenuStrip = EditMenu.Shared;
        }

        // Centres the edit child vertically (the native layout top-aligns it) and keeps it
        // clear of the arrow; its text then lines up with DropDownList fields.
        private void PositionEdit()
        {
            if (!IsHandleCreated) return;
            IntPtr edit = EditHandle(Info());
            if (edit == IntPtr.Zero) return;
            int h = Math.Min(Height - 4, Font.Height + 2);
            int left = DarkControls.Scale(this, 4), right = Width - DarkControls.Scale(this, 28);
            SetWindowPos(edit, IntPtr.Zero, left, Math.Max(2, (Height - h) / 2), Math.Max(1, right - left), Math.Max(1, h), SWP_NOZORDER | SWP_NOACTIVATE);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PositionEdit();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            UpdateItemHeight();
            PositionEdit();
        }

        // Field height matches DarkTextBox; list rows use the same height.
        private void UpdateItemHeight()
        {
            int field = Math.Max(DarkControls.Scale(this, 30), Font.Height + DarkControls.Scale(this, 13));
            int item = Math.Max(Font.Height + 2, field - 6); // the native frame adds 3 px per side
            if (ItemHeight == item) return;
            ItemHeight = item;
            // The native control resizes itself, which WinForms does not report to the parent's
            // layout engine; ask for a re-layout so rows are measured with the real height.
            if (Parent != null) Parent.PerformLayout(this, "Bounds");
        }

        private COMBOBOXINFO Info()
        {
            var info = new COMBOBOXINFO();
            info.cbSize = Marshal.SizeOf(typeof(COMBOBOXINFO));
            if (IsHandleCreated) GetComboBoxInfo(Handle, ref info);
            return info;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnDropDown(EventArgs e)
        {
            base.OnDropDown(e);
            var info = Info();
            if (info.hwndList != IntPtr.Zero) DarkControls.UseDarkTheme(info.hwndList);
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PAINT)
            {
                PAINTSTRUCT ps;
                IntPtr hdc = BeginPaint(Handle, out ps);
                try
                {
                    ExcludeEditChild(hdc);
                    using (var buffer = BufferedGraphicsManager.Current.Allocate(hdc, ClientRectangle))
                    {
                        PaintField(buffer.Graphics);
                        buffer.Render(hdc);
                    }
                }
                finally
                {
                    EndPaint(Handle, ref ps);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_PRINTCLIENT)
            {
                ExcludeEditChild(m.WParam);
                using (var g = Graphics.FromHdc(m.WParam)) PaintField(g);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        // The edit child of an editable combo paints itself; keep our pixels off it.
        private void ExcludeEditChild(IntPtr hdc)
        {
            IntPtr edit = EditHandle(Info());
            if (edit == IntPtr.Zero) return;
            RECT r;
            if (!GetWindowRect(edit, out r)) return;
            MapWindowPoints(IntPtr.Zero, Handle, ref r, 2);
            ExcludeClipRect(hdc, r.Left, r.Top, r.Right, r.Bottom);
        }

        private void PaintField(Graphics g)
        {
            bool editable = DropDownStyle != ComboBoxStyle.DropDownList;
            g.Clear(DarkControls.BackOf(Parent));
            bool enabled = Enabled;
            bool active = DroppedDown || Focused || (editable && ContainsFocus);
            Color fill = !enabled ? Theme.Panel : DroppedDown ? Theme.Hover : _hover ? DarkControls.FieldHover : DarkControls.Field;
            Color border = !enabled ? Theme.Border : active ? Theme.Accent : _hover ? DarkControls.FieldBorderHover : DarkControls.FieldBorder;
            if (editable) fill = enabled ? DarkControls.Field : Theme.Panel; // must match the edit child's background
            var r = new Rectangle(0, 0, Width, Height);
            DarkControls.RoundedBox(g, r, DarkControls.ScaleF(this, 4f), fill, border);

            int arrowW = DarkControls.Scale(this, 28);
            var gf = DarkControls.IconFont(this, 7f);
            Color arrowColor = enabled ? (_hover || DroppedDown ? Theme.Text : Theme.TextDim) : Theme.TextDisabled;
            var ar = new Rectangle(Width - arrowW, 0, arrowW - DarkControls.Scale(this, 4), Height);
            TextRenderer.DrawText(g, Glyphs.Down, gf, ar, arrowColor, fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            if (!editable)
            {
                string text = SelectedIndex >= 0 ? GetItemText(SelectedItem) : Text;
                int padX = DarkControls.Scale(this, 10);
                var tr = new Rectangle(padX, 0, Math.Max(0, Width - padX - arrowW), Height);
                TextRenderer.DrawText(g, text, Font, tr, enabled ? ForeColor : Theme.TextDisabled, fill,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (Focused && !DroppedDown && enabled)
            {
                int rr = DarkControls.Scale(this, 4);
                g.FillRectangle(DarkControls.Brush(Theme.Accent), rr, Height - DarkControls.Scale(this, 2), Width - rr * 2, DarkControls.Scale(this, 2));
            }
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if ((e.State & DrawItemState.ComboBoxEdit) != 0)
            {
                // The closed field is painted in WM_PAINT; just make sure it refreshes.
                Invalidate();
                return;
            }
            var g = e.Graphics;
            bool hot = (e.State & DrawItemState.Selected) != 0;
            Color fill = hot ? Theme.Hover : DarkControls.Field;
            g.FillRectangle(DarkControls.Brush(fill), e.Bounds);
            if (e.Index < 0 || e.Index >= Items.Count) return;
            if (e.Index == SelectedIndex)
            {
                int barH = Math.Max(2, e.Bounds.Height - DarkControls.Scale(this, 12));
                int barW = Math.Max(2, DarkControls.Scale(this, 3));
                var bar = new Rectangle(e.Bounds.Left + DarkControls.Scale(this, 3), e.Bounds.Top + (e.Bounds.Height - barH) / 2, barW, barH);
                DarkControls.RoundedBox(g, bar, barW / 2f, Theme.Accent, null);
            }
            int padX = DarkControls.Scale(this, 10);
            var tr = new Rectangle(e.Bounds.Left + padX, e.Bounds.Top, Math.Max(0, e.Bounds.Width - padX * 2), e.Bounds.Height);
            TextRenderer.DrawText(g, GetItemText(Items[e.Index]), Font, tr, Theme.Text, fill,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
