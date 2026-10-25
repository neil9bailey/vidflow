using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    // Owner-drawn, multi-column list with group header rows, keyboard navigation, hover
    // and selection states, a column header and a thin dark scrollbar of its own
    // (ListView headers, groups and scrollbars cannot be fully darkened). Columns can draw
    // their text as key-cap chips ("Ctrl+Right, ." -> [Ctrl+Right] [.]).
    public class DarkList : Control
    {
        public sealed class Column
        {
            public Column(string title, float weight, bool keyChips = false)
            {
                Title = title;
                Weight = weight > 0 ? weight : 1f;
                KeyChips = keyChips;
            }

            public string Title { get; private set; }
            public float Weight { get; private set; }
            public bool KeyChips { get; private set; }
            public bool Dim { get; set; }
        }

        public sealed class Row
        {
            public Row(object tag, params string[] cells)
            {
                Tag = tag;
                Cells = cells ?? new string[0];
            }

            public static Row Group(string title)
            {
                return new Row(null, title) { IsGroup = true };
            }

            public string[] Cells { get; private set; }
            public object Tag { get; private set; }
            public bool IsGroup { get; private set; }
            public bool Warning { get; set; }  // chips/text drawn in Theme.Warning
            public bool Dim { get; set; }      // de-emphasised (e.g. unbound action)
            // With Warning: only the key chips with these texts are drawn as warnings (null = all).
            public ICollection<string> WarningChips { get; set; }
        }

        private readonly List<Column> _columns = new List<Column>();
        private List<Row> _rows = new List<Row>();
        private int[] _offsets = new int[0];
        private int _contentHeight, _scroll, _selected = -1, _hover = -1;
        private bool _thumbHover, _dragging;
        private int _dragStartY, _dragStartScroll;
        private string _emptyText = "Nothing to show";

        public event EventHandler SelectedIndexChanged;
        public event EventHandler RowActivated;

        public DarkList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
        }

        public IList<Column> Columns { get { return _columns; } }

        public string EmptyText
        {
            get { return _emptyText; }
            set { _emptyText = value ?? ""; Invalidate(); }
        }

        public IList<Row> Rows { get { return _rows.AsReadOnly(); } }

        public void SetRows(IEnumerable<Row> rows)
        {
            object selTag = SelectedRow != null ? SelectedRow.Tag : null;
            bool hadSelection = _selected >= 0;
            _rows = rows != null ? new List<Row>(rows) : new List<Row>();
            _hover = -1;
            _selected = -1;
            Recalc();
            int idx = selTag != null ? _rows.FindIndex(r => !r.IsGroup && Equals(r.Tag, selTag)) : -1;
            if (idx >= 0) SetSelected(idx, true);
            else if (hadSelection)
            {
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public Row SelectedRow
        {
            get { return _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null; }
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set { SetSelected(value, true); }
        }

        // Swaps in updated rows with the same layout (e.g. edited cells) without touching
        // the scroll position or raising selection events.
        public void ReplaceRows(IEnumerable<Row> rows)
        {
            _rows = rows != null ? new List<Row>(rows) : new List<Row>();
            if (_selected >= _rows.Count || (_selected >= 0 && _rows[_selected].IsGroup)) _selected = -1;
            if (_hover >= _rows.Count) _hover = -1;
            Recalc();
        }

        private int RowHeight { get { return Font.Height + DarkControls.Scale(this, 14); } }
        private int GroupHeight { get { return Font.Height + DarkControls.Scale(this, 20); } }
        private int HeaderHeight { get { return Font.Height + DarkControls.Scale(this, 14); } }
        private int Viewport { get { return Math.Max(0, Height - HeaderHeight); } }
        private int MaxScroll { get { return Math.Max(0, _contentHeight - Viewport); } }
        private bool ScrollVisible { get { return MaxScroll > 0; } }
        private int ScrollBarWidth { get { return DarkControls.Scale(this, 12); } }
        private int ContentWidth { get { return Math.Max(0, Width - (ScrollVisible ? ScrollBarWidth : 0)); } }

        private void Recalc()
        {
            _offsets = new int[_rows.Count];
            int y = 0, rh = RowHeight, gh = GroupHeight;
            for (int i = 0; i < _rows.Count; i++)
            {
                _offsets[i] = y;
                y += _rows[i].IsGroup ? gh : rh;
            }
            _contentHeight = y;
            _scroll = Math.Max(0, Math.Min(_scroll, MaxScroll));
            Invalidate();
        }

        private int HeightOf(int i) { return _rows[i].IsGroup ? GroupHeight : RowHeight; }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            // Row heights follow the font (DPI change): keep the same rows in view, not the
            // same pixel offset, and the selection visible.
            int top = FirstVisibleRow();
            Recalc();
            if (top >= 0 && top < _offsets.Length) SetScroll(_offsets[top]);
            if (_selected >= 0) EnsureVisible(_selected);
        }

        // First row at least partly below the scroll offset (from the current layout), or -1.
        private int FirstVisibleRow()
        {
            for (int i = 0; i < _offsets.Length; i++)
            {
                int end = i + 1 < _offsets.Length ? _offsets[i + 1] : _contentHeight;
                if (end > _scroll) return i;
            }
            return -1;
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Recalc(); }

        private void SetSelected(int index, bool ensureVisible)
        {
            if (index < -1 || index >= _rows.Count || (index >= 0 && _rows[index].IsGroup)) index = -1;
            bool changed = index != _selected;
            _selected = index;
            if (ensureVisible && index >= 0) EnsureVisible(index);
            Invalidate();
            if (changed)
            {
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private void EnsureVisible(int index)
        {
            if (index < 0 || index >= _rows.Count) return;
            int top = _offsets[index], bottom = top + HeightOf(index);
            // Keep a group header visible above the first row of its group.
            if (index > 0 && _rows[index - 1].IsGroup && top - _offsets[index - 1] <= GroupHeight) top = _offsets[index - 1];
            if (top < _scroll) SetScroll(top);
            else if (bottom > _scroll + Viewport) SetScroll(bottom - Viewport);
        }

        private void SetScroll(int value)
        {
            value = Math.Max(0, Math.Min(value, MaxScroll));
            if (value == _scroll) return;
            _scroll = value;
            Invalidate();
        }

        private int RowAt(Point p)
        {
            if (p.Y < HeaderHeight || p.X >= ContentWidth) return -1;
            int y = p.Y - HeaderHeight + _scroll;
            for (int i = 0; i < _rows.Count; i++)
                if (y >= _offsets[i] && y < _offsets[i] + HeightOf(i)) return i;
            return -1;
        }

        // ---- scrollbar ----

        private Rectangle TrackRect
        {
            get
            {
                int pad = DarkControls.Scale(this, 3);
                return new Rectangle(Width - ScrollBarWidth, HeaderHeight + pad, ScrollBarWidth, Math.Max(0, Viewport - pad * 2));
            }
        }

        private Rectangle ThumbRect
        {
            get
            {
                var t = TrackRect;
                if (_contentHeight <= 0 || t.Height <= 0) return Rectangle.Empty;
                int h = Math.Max(DarkControls.Scale(this, 28), (int)((long)t.Height * Viewport / _contentHeight));
                h = Math.Min(h, t.Height);
                int y = t.Top + (MaxScroll > 0 ? (int)((long)(t.Height - h) * _scroll / MaxScroll) : 0);
                return new Rectangle(t.Left, y, t.Width, h);
            }
        }

        // ---- input ----

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End:
                    return (keyData & (Keys.Control | Keys.Alt)) == 0;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int page = Math.Max(1, Viewport / Math.Max(1, RowHeight) - 1);
            int target = -1;
            switch (e.KeyCode)
            {
                case Keys.Down: target = NextSelectable(_selected < 0 ? -1 : _selected, 1, 1); break;
                case Keys.Up: target = NextSelectable(_selected < 0 ? _rows.Count : _selected, -1, 1); break;
                case Keys.PageDown: target = NextSelectable(_selected < 0 ? -1 : _selected, 1, page); break;
                case Keys.PageUp: target = NextSelectable(_selected < 0 ? _rows.Count : _selected, -1, page); break;
                case Keys.Home: target = NextSelectable(-1, 1, 1); break;
                case Keys.End: target = NextSelectable(_rows.Count, -1, 1); break;
                default: return;
            }
            if (target >= 0) SetSelected(target, true);
            e.Handled = true;
        }

        // Moves `steps` selectable rows from `from` in direction `dir`, stopping at the ends.
        private int NextSelectable(int from, int dir, int steps)
        {
            int result = -1, i = from;
            while (steps > 0)
            {
                i += dir;
                if (i < 0 || i >= _rows.Count) break;
                if (_rows[i].IsGroup) continue;
                result = i;
                steps--;
            }
            return result >= 0 ? result : (_selected >= 0 ? _selected : -1);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            if (ScrollVisible && e.X >= Width - ScrollBarWidth && e.Y >= HeaderHeight)
            {
                var thumb = ThumbRect;
                if (thumb.Contains(e.Location))
                {
                    _dragging = true;
                    _dragStartY = e.Y;
                    _dragStartScroll = _scroll;
                }
                else SetScroll(_scroll + (e.Y < thumb.Top ? -Viewport : Viewport));
                Invalidate();
                return;
            }
            int row = RowAt(e.Location);
            if (row >= 0 && !_rows[row].IsGroup) SetSelected(row, true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                var t = TrackRect;
                int travel = t.Height - ThumbRect.Height;
                if (travel > 0) SetScroll(_dragStartScroll + (int)((long)(e.Y - _dragStartY) * MaxScroll / travel));
                return;
            }
            bool thumbHover = ScrollVisible && e.X >= Width - ScrollBarWidth && e.Y >= HeaderHeight;
            int row = thumbHover ? -1 : RowAt(e.Location);
            if (row >= 0 && _rows[row].IsGroup) row = -1;
            if (row != _hover || thumbHover != _thumbHover)
            {
                _hover = row;
                _thumbHover = thumbHover;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging) { _dragging = false; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1 || _thumbHover) { _hover = -1; _thumbHover = false; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            int row = RowAt(e.Location);
            if (row >= 0 && row == _selected)
            {
                var h = RowActivated;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            SetScroll(_scroll - e.Delta * lines * RowHeight / 120);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        // ---- painting ----

        private int[] ColumnEdges(int width)
        {
            var edges = new int[_columns.Count + 1];
            float total = 0;
            foreach (var c in _columns) total += c.Weight;
            float x = 0;
            for (int i = 0; i < _columns.Count; i++)
            {
                edges[i] = (int)Math.Round(x);
                x += width * _columns[i].Weight / total;
            }
            edges[_columns.Count] = width;
            return edges;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int width = ContentWidth;
            int inset = DarkControls.Scale(this, 4);
            int padX = DarkControls.Scale(this, 10);
            var edges = ColumnEdges(width - inset * 2);
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        TextFormatFlags.PreserveGraphicsClipping;

            int headerH = HeaderHeight;
            var dlg = DarkControls.DialogOf(this);
            var groupFont = dlg != null ? dlg.GetFont(9f, FontStyle.Bold) : Font;

            var viewport = new Rectangle(0, headerH, Width, Viewport);
            g.SetClip(viewport);
            bool any = false;
            int rh = RowHeight;
            for (int i = 0; i < _rows.Count; i++)
            {
                int top = headerH + _offsets[i] - _scroll;
                int h = HeightOf(i);
                if (top + h < headerH) continue;
                if (top > Height) break;
                var row = _rows[i];
                if (row.IsGroup)
                {
                    var gr = new Rectangle(inset + padX, top + DarkControls.Scale(this, 6), Math.Max(0, width - inset * 2 - padX * 2), h - DarkControls.Scale(this, 6));
                    TextRenderer.DrawText(g, row.Cells.Length > 0 ? row.Cells[0] : "", groupFont, gr, Theme.Text, BackColor, flags);
                    continue;
                }
                any = true;
                var rowRect = new Rectangle(inset, top + DarkControls.Scale(this, 1), Math.Max(0, width - inset * 2), rh - DarkControls.Scale(this, 2));
                Color bg = BackColor;
                if (i == _selected) bg = Focused ? DarkControls.RowSelectedFocused : DarkControls.RowSelected;
                else if (i == _hover) bg = DarkControls.RowHover;
                if (bg != BackColor) DarkControls.RoundedBox(g, rowRect, DarkControls.ScaleF(this, 4f), bg, null);
                if (i == _selected && Focused)
                {
                    int barH = Math.Max(2, rowRect.Height - DarkControls.Scale(this, 14));
                    int barW = Math.Max(2, DarkControls.Scale(this, 3));
                    DarkControls.RoundedBox(g, new Rectangle(rowRect.Left, rowRect.Top + (rowRect.Height - barH) / 2, barW, barH), barW / 2f, Theme.Accent, null);
                }
                for (int c = 0; c < _columns.Count && c < row.Cells.Length; c++)
                {
                    var cr = new Rectangle(inset + edges[c] + padX, top, Math.Max(0, edges[c + 1] - edges[c] - padX * 2), rh);
                    string text = row.Cells[c] ?? "";
                    if (_columns[c].KeyChips) DrawChips(g, text, cr, row, bg);
                    else
                    {
                        Color fg = row.Dim || _columns[c].Dim ? Theme.TextDim : Theme.Text;
                        if (c > 0 && row.Warning && !_columns[c].Dim) fg = Theme.Warning;
                        TextRenderer.DrawText(g, text, Font, cr, fg, bg, flags);
                    }
                }
            }
            g.ResetClip();

            // Header last, so partially scrolled rows never show through it.
            if (headerH > 0)
            {
                g.FillRectangle(DarkControls.Brush(BackColor), 0, 0, Width, headerH);
                var hf = dlg != null ? dlg.GetFont(8.5f, FontStyle.Bold) : Font;
                for (int c = 0; c < _columns.Count; c++)
                {
                    var r = new Rectangle(inset + edges[c] + padX, 0, Math.Max(0, edges[c + 1] - edges[c] - padX * 2), headerH);
                    TextRenderer.DrawText(g, _columns[c].Title, hf, r, Theme.TextDim, BackColor, flags);
                }
                g.FillRectangle(DarkControls.Brush(Theme.Border), 0, headerH - 1, Width, 1);
            }

            if (!any && _emptyText.Length > 0)
            {
                var er = new Rectangle(0, headerH + DarkControls.Scale(this, 24), Width, Font.Height * 2);
                TextRenderer.DrawText(g, _emptyText, Font, er, Theme.TextDim, BackColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
            }

            if (ScrollVisible)
            {
                var thumb = ThumbRect;
                bool wide = _thumbHover || _dragging;
                int w = DarkControls.Scale(this, wide ? 8 : 4);
                var tr = new Rectangle(thumb.Right - w - DarkControls.Scale(this, 2), thumb.Top, w, thumb.Height);
                DarkControls.RoundedBox(g, tr, w / 2f, wide ? DarkControls.ScrollThumbHover : DarkControls.ScrollThumb, null);
            }
        }

        private void DrawChips(Graphics g, string text, Rectangle cell, Row row, Color rowBg)
        {
            if (string.IsNullOrEmpty(text)) return;
            var keys = text.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;
            int padX = DarkControls.Scale(this, 7), gap = DarkControls.Scale(this, 5);
            int h = Font.Height + DarkControls.Scale(this, 5);
            int y = cell.Top + (cell.Height - h) / 2;
            int x = cell.Left;
            foreach (var key in keys)
            {
                bool warning = row.Warning && (row.WarningChips == null || row.WarningChips.Contains(key));
                Color fg = warning ? Theme.Warning : row.Dim ? Theme.TextDim : Theme.Text;
                Color border = warning ? Theme.Warning : DarkControls.ChipBorder;
                int w = TextRenderer.MeasureText(g, key, Font, Size.Empty, flags).Width + padX * 2;
                if (x + w > cell.Right)
                {
                    // No room for this chip: say there is more rather than clipping it.
                    TextRenderer.DrawText(g, "…", Font, new Rectangle(x, cell.Top, Math.Max(0, cell.Right - x), cell.Height), Theme.TextDim, rowBg,
                        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
                    return;
                }
                var r = new Rectangle(x, y, w, h);
                DarkControls.RoundedBox(g, r, DarkControls.ScaleF(this, 3f), DarkControls.Chip, border);
                TextRenderer.DrawText(g, key, Font, r, fg, DarkControls.Chip, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                x += w + gap;
            }
        }
    }
}
