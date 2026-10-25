using System;
using System.Drawing;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    // Borderless TextBox inside a painted rounded field (the native border is light/black
    // and cannot be recoloured). Accent border while focused, Theme.Warning when Invalid.
    // Single-line fields size their own height from the font; multiline ones get dark
    // scrollbars. Right-click opens the dark edit menu (EditMenu) instead of the light native one.
    public class DarkTextBox : Control
    {
        private readonly TextBox _box;
        private bool _hover, _invalid, _monospace;
        private string _cue, _glyph;

        public DarkTextBox()
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
                HideSelection = true,
            };
            _box.GotFocus += (s, e) => Invalidate();
            _box.LostFocus += (s, e) => Invalidate();
            _box.MouseEnter += (s, e) => SetHover(true);
            _box.MouseLeave += (s, e) => SetHover(ClientRectangle.Contains(PointToClient(MousePosition)));
            _box.TextChanged += (s, e) => OnTextChanged(e);
            _box.HandleCreated += (s, e) => ApplyNativeStyle();
            EditMenu.Attach(_box);
            Controls.Add(_box);
            Size = new Size(200, 30);
        }

        public TextBox TextBox { get { return _box; } }

        public override string Text
        {
            get { return _box.Text; }
            set { _box.Text = value ?? ""; }
        }

        public bool Multiline
        {
            get { return _box.Multiline; }
            set
            {
                _box.Multiline = value;
                _box.AcceptsReturn = value;
                _box.ScrollBars = value ? ScrollBars.Vertical : ScrollBars.None;
                _box.WordWrap = value;
                UpdateHeight();
                PerformLayout();
            }
        }

        public bool ReadOnly
        {
            get { return _box.ReadOnly; }
            set { _box.ReadOnly = value; _box.BackColor = Enabled ? DarkControls.Field : Theme.Panel; }
        }

        // Use Theme.MonoFont-style Consolas for the inner text (reports, mpv options).
        public bool Monospace
        {
            get { return _monospace; }
            set { _monospace = value; ApplyFont(); }
        }

        // Grey placeholder shown while empty (single-line only).
        public string Cue
        {
            get { return _cue; }
            set { _cue = value; DarkControls.SetCueBanner(_box, _cue); }
        }

        // Optional Theme.IconFontFamily glyph drawn at the left (e.g. a search icon).
        public string Glyph
        {
            get { return _glyph; }
            set { _glyph = value; PerformLayout(); Invalidate(); }
        }

        private int GlyphWidth { get { return string.IsNullOrEmpty(_glyph) ? 0 : DarkControls.Scale(this, 24); } }

        // Warning-coloured border for invalid input.
        public bool Invalid
        {
            get { return _invalid; }
            set { if (_invalid != value) { _invalid = value; Invalidate(); } }
        }

        public void SelectAll() { _box.SelectAll(); }

        public new bool Focus() { return _box.Focus(); }

        private void SetHover(bool hover)
        {
            if (_hover == hover) return;
            _hover = hover;
            Invalidate();
        }

        private void ApplyNativeStyle()
        {
            DarkControls.SetCueBanner(_box, _cue);
            if (_box.Multiline) DarkControls.UseDarkTheme(_box.Handle);
            UpdateMargins();
        }

        private void UpdateMargins()
        {
            if (!_box.IsHandleCreated) return;
            // A little breathing room after the caret for multiline text; the field padding
            // provides the rest.
            int m = _box.Multiline ? DarkControls.Scale(this, 2) : 0;
            DarkControls.SendMessage(_box.Handle, DarkControls.EM_SETMARGINS, (IntPtr)(DarkControls.EC_LEFTMARGIN | DarkControls.EC_RIGHTMARGIN), (IntPtr)(m | (m << 16)));
        }

        private void ApplyFont()
        {
            var d = DarkControls.DialogOf(this);
            if (_monospace && d != null) _box.Font = d.GetFont(9.5f, FontStyle.Regular, "Consolas");
            else _box.ResetFont();
            UpdateMargins();
            UpdateHeight();
            PerformLayout();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (_monospace) ApplyFont();
        }

        // The dialog (and its font cache) is reachable by now even if this control was
        // added to a container before that container joined the dialog.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyFont();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            // A DPI change re-creates the dialog fonts; pick up the matching monospace one.
            ApplyFont();
        }

        private void UpdateHeight()
        {
            if (_box.Multiline) return;
            int h = _box.PreferredHeight + DarkControls.Scale(this, 13);
            Height = Math.Max(h, DarkControls.Scale(this, 30));
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int padX = DarkControls.Scale(this, 9);
            if (_box.Multiline)
            {
                int pad = DarkControls.Scale(this, 6);
                _box.SetBounds(padX - DarkControls.Scale(this, 2), pad, Math.Max(1, Width - padX - DarkControls.Scale(this, 3)), Math.Max(1, Height - pad * 2));
            }
            else
            {
                int h = _box.PreferredHeight, gw = GlyphWidth;
                _box.SetBounds(padX + gw, Math.Max(0, (Height - h) / 2), Math.Max(1, Width - padX * 2 - gw), h);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); SetHover(true); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHover(_box.ClientRectangle.Contains(_box.PointToClient(MousePosition))); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _box.Focus();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            _box.BackColor = Enabled ? DarkControls.Field : Theme.Panel;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(DarkControls.BackOf(Parent));
            bool focused = _box.Focused;
            Color border = !Enabled ? Theme.Border
                : _invalid ? Theme.Warning
                : focused ? Theme.Accent
                : _hover ? DarkControls.FieldBorderHover : DarkControls.FieldBorder;
            Color fill = Enabled ? DarkControls.Field : Theme.Panel;
            DarkControls.RoundedBox(g, new Rectangle(0, 0, Width, Height), DarkControls.ScaleF(this, 4f), fill, border);
            if (focused && !_invalid)
            {
                // Windows 11 style: a slightly heavier accent line along the bottom edge.
                int r = DarkControls.Scale(this, 4);
                g.FillRectangle(DarkControls.Brush(Theme.Accent), r, Height - DarkControls.Scale(this, 2), Width - r * 2, DarkControls.Scale(this, 2));
            }
            if (GlyphWidth > 0)
            {
                var gf = DarkControls.IconFont(this, 9f);
                var gr = new Rectangle(DarkControls.Scale(this, 7), 0, GlyphWidth, _box.Multiline ? DarkControls.Scale(this, 30) : Height);
                TextRenderer.DrawText(g, _glyph, gf, gr, focused ? Theme.Text : Theme.TextDim, fill,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }
    }
}
