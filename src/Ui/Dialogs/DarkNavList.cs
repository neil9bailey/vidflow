using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    // Vertical category list (settings sidebar): glyph + text rows, hover highlight and an
    // accent bar on the selected row. Up/Down/Home/End move the selection while focused.
    public class DarkNavList : Control
    {
        private readonly List<KeyValuePair<string, string>> _items = new List<KeyValuePair<string, string>>(); // glyph, text
        private int _selected = -1, _hover = -1;

        public event EventHandler SelectedIndexChanged;

        public DarkNavList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
        }

        public void AddItem(string glyph, string text)
        {
            _items.Add(new KeyValuePair<string, string>(glyph, text));
            Invalidate();
        }

        public int Count { get { return _items.Count; } }

        public string TextAt(int index)
        {
            return index >= 0 && index < _items.Count ? _items[index].Value : null;
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = value < 0 || value >= _items.Count ? -1 : value;
                if (v == _selected) return;
                _selected = v;
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private int ItemHeight { get { return Font.Height + DarkControls.Scale(this, 20); } }
        private int TopPad { get { return DarkControls.Scale(this, 8); } }

        private int ItemAt(Point p)
        {
            int i = (p.Y - TopPad) / Math.Max(1, ItemHeight);
            return p.Y >= TopPad && i >= 0 && i < _items.Count ? i : -1;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_items.Count == 0) return;
            switch (e.KeyCode)
            {
                case Keys.Up: SelectedIndex = Math.Max(0, _selected - 1); break;
                case Keys.Down: SelectedIndex = Math.Min(_items.Count - 1, _selected + 1); break;
                case Keys.Home: SelectedIndex = 0; break;
                case Keys.End: SelectedIndex = _items.Count - 1; break;
                default: return;
            }
            e.Handled = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int i = ItemAt(e.Location);
            if (i >= 0) SelectedIndex = i;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = ItemAt(e.Location);
            if (i != _hover) { _hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int ih = ItemHeight, inset = DarkControls.Scale(this, 6);
            var gf = DarkControls.IconFont(this, 10.5f);
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            for (int i = 0; i < _items.Count; i++)
            {
                var r = new Rectangle(inset, TopPad + i * ih, Math.Max(0, Width - inset * 2), ih - DarkControls.Scale(this, 2));
                bool sel = i == _selected;
                Color bg = sel ? Theme.Hover : i == _hover ? Theme.PanelAlt : BackColor;
                if (bg != BackColor) DarkControls.RoundedBox(g, r, DarkControls.ScaleF(this, 5f), bg, null);
                if (sel)
                {
                    int barH = Math.Max(2, r.Height - DarkControls.Scale(this, 18));
                    int barW = Math.Max(2, DarkControls.Scale(this, 3));
                    DarkControls.RoundedBox(g, new Rectangle(r.Left, r.Top + (r.Height - barH) / 2, barW, barH), barW / 2f, Theme.Accent, null);
                }
                int x = r.Left + DarkControls.Scale(this, 14);
                if (!string.IsNullOrEmpty(_items[i].Key))
                {
                    var gr = new Rectangle(x, r.Top, DarkControls.Scale(this, 20), r.Height);
                    TextRenderer.DrawText(g, _items[i].Key, gf, gr, sel ? Theme.Accent : Theme.TextDim, bg, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                    x += DarkControls.Scale(this, 30);
                }
                var tr = new Rectangle(x, r.Top, Math.Max(0, r.Right - x - DarkControls.Scale(this, 6)), r.Height);
                TextRenderer.DrawText(g, _items[i].Value, Font, tr, sel ? Theme.Text : DarkControls.Blend(Theme.TextDim, Theme.Text, 0.55f), bg, flags);
                if (sel && Focused && ShowFocusCues)
                    DarkControls.RoundedBox(g, r, DarkControls.ScaleF(this, 5f), Color.Transparent, Theme.TextDim);
            }
        }
    }
}
