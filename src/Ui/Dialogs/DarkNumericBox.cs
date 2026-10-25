using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    // Dark numeric field (NumericUpDown's spin buttons are always light): a borderless
    // TextBox, an optional dim unit suffix and painted up/down buttons with auto-repeat.
    // Up/Down keys step, PageUp/PageDown step ×10, the wheel steps while focused. Typed
    // values apply live when valid (so Enter → OK picks them up); invalid text shows a
    // warning border and is replaced by the last valid value when focus leaves.
    public class DarkNumericBox : Control
    {
        private readonly TextBox _box;
        private readonly Timer _repeat;
        private decimal _value, _min, _max = 100, _inc = 1;
        private int _decimals;
        private string _suffix;
        private bool _invalid, _hoverField, _updating;
        private int _hoverPart, _pressedPart, _repeatDir; // 0 none, 1 up, -1 down

        public event EventHandler ValueChanged;

        public DarkNumericBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = DarkControls.Field;
            ForeColor = Theme.Text;
            _box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = DarkControls.Field,
                ForeColor = Theme.Text,
                TextAlign = HorizontalAlignment.Right, // keeps the number next to its unit
            };
            _box.GotFocus += (s, e) => Invalidate();
            _box.LostFocus += (s, e) => { Commit(); Invalidate(); };
            _box.TextChanged += (s, e) => OnTextEdited();
            _box.KeyDown += OnBoxKeyDown;
            _box.MouseWheel += (s, e) =>
            {
                if (!_box.Focused) return;
                Step(Math.Sign(e.Delta));
                // Stop the edit control forwarding the same wheel message to us.
                var handled = e as HandledMouseEventArgs;
                if (handled != null) handled.Handled = true;
            };
            _box.MouseEnter += (s, e) => SetFieldHover(true);
            _box.MouseLeave += (s, e) => SetFieldHover(ClientRectangle.Contains(PointToClient(MousePosition)));
            EditMenu.Attach(_box);
            Controls.Add(_box);
            _repeat = new Timer { Interval = 400 };
            _repeat.Tick += (s, e) => { _repeat.Interval = 50; Step(_repeatDir); };
            Size = new Size(120, 30);
            UpdateText();
        }

        public TextBox TextBox { get { return _box; } }

        public decimal Minimum
        {
            get { return _min; }
            set { _min = value; if (_max < _min) _max = _min; Value = _value; }
        }

        public decimal Maximum
        {
            get { return _max; }
            set { _max = value; if (_min > _max) _min = _max; Value = _value; }
        }

        public decimal Increment
        {
            get { return _inc; }
            set { _inc = value > 0 ? value : 1; }
        }

        public int DecimalPlaces
        {
            get { return _decimals; }
            set { _decimals = Math.Max(0, Math.Min(6, value)); UpdateText(); }
        }

        // Unit drawn dim after the number ("s", "%", "×").
        public string Suffix
        {
            get { return _suffix; }
            set { _suffix = value; PerformLayout(); Invalidate(); }
        }

        public decimal Value
        {
            get { return _value; }
            set
            {
                decimal v = Clamp(value);
                bool changed = v != _value;
                _value = v;
                UpdateText();
                SetInvalid(false);
                if (changed) OnValueChanged();
            }
        }

        private decimal Clamp(decimal v)
        {
            v = Math.Round(v, _decimals, MidpointRounding.AwayFromZero);
            return v < _min ? _min : v > _max ? _max : v;
        }

        private void OnValueChanged()
        {
            var h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void UpdateText()
        {
            _updating = true;
            try { _box.Text = _value.ToString("F" + _decimals, CultureInfo.InvariantCulture); }
            finally { _updating = false; }
        }

        private bool TryParse(string text, out decimal v)
        {
            string t = (text ?? "").Trim().Replace(',', '.');
            if (!string.IsNullOrEmpty(_suffix) && t.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase))
                t = t.Substring(0, t.Length - _suffix.Length).Trim();
            return decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private void OnTextEdited()
        {
            if (_updating) return;
            decimal v;
            if (TryParse(_box.Text, out v) && v >= _min && v <= _max)
            {
                SetInvalid(false);
                v = Math.Round(v, _decimals, MidpointRounding.AwayFromZero);
                if (v != _value) { _value = v; OnValueChanged(); }
            }
            else SetInvalid(true);
        }

        // Normalises the text: clamps an out-of-range number, restores the last valid value otherwise.
        private void Commit()
        {
            decimal v;
            if (TryParse(_box.Text, out v)) Value = v;
            else { UpdateText(); SetInvalid(false); }
        }

        private void SetInvalid(bool invalid)
        {
            if (_invalid == invalid) return;
            _invalid = invalid;
            Invalidate();
        }

        private void Step(int dir)
        {
            if (dir == 0 || !Enabled) return;
            Commit();
            Value = _value + dir * _inc;
            _box.SelectAll();
        }

        private void OnBoxKeyDown(object sender, KeyEventArgs e)
        {
            int dir = 0, mult = 1;
            switch (e.KeyCode)
            {
                case Keys.Up: dir = 1; break;
                case Keys.Down: dir = -1; break;
                case Keys.PageUp: dir = 1; mult = 10; break;
                case Keys.PageDown: dir = -1; mult = 10; break;
                case Keys.Enter: Commit(); return; // let the dialog's accept button run too
            }
            if (dir == 0) return;
            for (int i = 0; i < mult; i++) Step(dir);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private int SpinWidth { get { return DarkControls.Scale(this, 22); } }

        private Rectangle SpinRect(int part)
        {
            int w = SpinWidth, inset = DarkControls.Scale(this, 3);
            int h = (Height - inset * 2) / 2;
            int x = Width - w - inset + DarkControls.Scale(this, 1);
            return part > 0 ? new Rectangle(x, inset, w - DarkControls.Scale(this, 1), h) : new Rectangle(x, inset + h, w - DarkControls.Scale(this, 1), Height - inset * 2 - h);
        }

        private int PartAt(Point p)
        {
            if (SpinRect(1).Contains(p)) return 1;
            if (SpinRect(-1).Contains(p)) return -1;
            return 0;
        }

        private int SuffixWidth
        {
            get { return string.IsNullOrEmpty(_suffix) ? 0 : TextRenderer.MeasureText(_suffix, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width; }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            UpdateHeight();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateHeight();
        }

        // Same height as DarkTextBox fields.
        private void UpdateHeight()
        {
            Height = Math.Max(DarkControls.Scale(this, 30), _box.PreferredHeight + DarkControls.Scale(this, 13));
            PerformLayout();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int padX = DarkControls.Scale(this, 9);
            int suffix = SuffixWidth;
            int right = SpinWidth + DarkControls.Scale(this, 8) + (suffix > 0 ? suffix + DarkControls.Scale(this, 4) : 0);
            int h = _box.PreferredHeight;
            _box.SetBounds(padX, Math.Max(0, (Height - h) / 2), Math.Max(1, Width - padX - right), h);
        }

        private void SetFieldHover(bool hover)
        {
            if (_hoverField == hover) return;
            _hoverField = hover;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int part = PartAt(e.Location);
            if (part != _hoverPart) { _hoverPart = part; Invalidate(); }
            SetFieldHover(true);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverPart = 0;
            SetFieldHover(_box.ClientRectangle.Contains(_box.PointToClient(MousePosition)));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _box.Focus();
            if (e.Button != MouseButtons.Left || !Enabled) return;
            int part = PartAt(e.Location);
            if (part == 0) return;
            _pressedPart = part;
            _repeatDir = part;
            Step(part);
            _repeat.Interval = 400;
            _repeat.Start();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _repeat.Stop();
            _pressedPart = 0;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_box.Focused) Step(Math.Sign(e.Delta));
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            _box.BackColor = Enabled ? DarkControls.Field : Theme.Panel;
            if (!Enabled) _repeat.Stop();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(DarkControls.BackOf(Parent));
            bool enabled = Enabled, focused = _box.Focused;
            Color fill = enabled ? DarkControls.Field : Theme.Panel;
            Color border = !enabled ? Theme.Border : _invalid ? Theme.Warning : focused ? Theme.Accent
                : _hoverField ? DarkControls.FieldBorderHover : DarkControls.FieldBorder;
            DarkControls.RoundedBox(g, new Rectangle(0, 0, Width, Height), DarkControls.ScaleF(this, 4f), fill, border);
            if (focused && !_invalid)
            {
                int r = DarkControls.Scale(this, 4);
                g.FillRectangle(DarkControls.Brush(Theme.Accent), r, Height - DarkControls.Scale(this, 2), Width - r * 2, DarkControls.Scale(this, 2));
            }

            if (!string.IsNullOrEmpty(_suffix))
            {
                int sw = SuffixWidth;
                var sr = new Rectangle(_box.Right + DarkControls.Scale(this, 4), 0, sw + DarkControls.Scale(this, 2), Height);
                TextRenderer.DrawText(g, _suffix, Font, sr, enabled ? Theme.TextDim : Theme.TextDisabled, fill,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }

            var gf = DarkControls.IconFont(this, 6.5f);
            for (int part = 1; part >= -1; part -= 2)
            {
                var r = SpinRect(part);
                Color bg = !enabled ? fill : _pressedPart == part ? Theme.Pressed : _hoverPart == part ? Theme.Hover : fill;
                if (bg != fill) DarkControls.RoundedBox(g, r, DarkControls.ScaleF(this, 3f), bg, null);
                Color fg = !enabled ? Theme.TextDisabled : _hoverPart == part ? Theme.Text : Theme.TextDim;
                TextRenderer.DrawText(g, part > 0 ? Glyphs.Up : Glyphs.Down, gf, r, fg, bg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _repeat.Dispose();
            base.Dispose(disposing);
        }
    }
}
