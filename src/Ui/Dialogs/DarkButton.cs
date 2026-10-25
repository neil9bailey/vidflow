using System;
using System.Drawing;
using System.Windows.Forms;

namespace VidFlow.Ui.Dialogs
{
    // Flat dark push button: accent fill for the primary action, Theme.PanelAlt otherwise,
    // hover/pressed states, keyboard focus ring, optional leading glyph. Also usable as a
    // toggle (Checked) for segmented choices.
    public class DarkButton : Button
    {
        private bool _hover, _pressed, _accent, _checked;
        private string _glyph;

        public DarkButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Theme.PanelAlt;
            ForeColor = Theme.Text;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        // Primary action look (accent fill, dark text).
        public bool Accent
        {
            get { return _accent; }
            set { if (_accent != value) { _accent = value; Invalidate(); } }
        }

        // Selected state when used as a toggle / segment.
        public bool Checked
        {
            get { return _checked; }
            set { if (_checked != value) { _checked = value; Invalidate(); } }
        }

        // Optional Theme.IconFontFamily glyph drawn before the text.
        public string Glyph
        {
            get { return _glyph; }
            set { _glyph = value; Invalidate(); if (AutoSize && Parent != null) Parent.PerformLayout(this, "Glyph"); }
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int textW = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font, Size.Empty, flags).Width;
            int glyphW = 0;
            if (!string.IsNullOrEmpty(_glyph))
                glyphW = TextRenderer.MeasureText(_glyph, DarkControls.IconFont(this, 10f), Size.Empty, flags).Width + (textW > 0 ? DarkControls.Scale(this, 8) : 0);
            int padX = DarkControls.Scale(this, textW > 0 ? 16 : 9);
            int h = Math.Max(DarkControls.Scale(this, 32), Font.Height + DarkControls.Scale(this, 14));
            int w = Math.Max(textW + glyphW + padX * 2, textW > 0 ? 0 : h);
            return DarkControls.Constrain(this, new Size(w, h));
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_pressed) { _pressed = false; Invalidate(); }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled) { _hover = false; _pressed = false; }
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            pevent.Graphics.Clear(DarkControls.BackOf(Parent));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(DarkControls.BackOf(Parent));
            bool enabled = Enabled;
            Color fill, text;
            Color? border;
            if (_accent)
            {
                fill = !enabled ? DarkControls.AccentDisabled : _pressed ? DarkControls.AccentPressed : _hover ? DarkControls.AccentHover : Theme.Accent;
                text = enabled ? DarkControls.OnAccent : DarkControls.OnAccentDisabled;
                border = null;
            }
            else if (_checked)
            {
                fill = !enabled ? Theme.Panel : _pressed ? Theme.AccentDim : _hover ? DarkControls.Blend(Theme.AccentDim, Theme.Accent, 0.15f) : Theme.AccentDim;
                text = enabled ? Color.White : Theme.TextDisabled;
                border = enabled ? DarkControls.Blend(Theme.AccentDim, Theme.Accent, 0.5f) : Theme.Border;
            }
            else
            {
                fill = !enabled ? Theme.Panel : _pressed ? Theme.Pressed : _hover ? Theme.Hover : Theme.PanelAlt;
                text = enabled ? ForeColor : Theme.TextDisabled;
                border = _hover && enabled ? DarkControls.ButtonBorderHover : DarkControls.ButtonBorder;
            }

            float radius = DarkControls.ScaleF(this, 4f);
            var r = new Rectangle(0, 0, Width, Height);
            DarkControls.RoundedBox(g, r, radius, fill, border);

            if (Focused && ShowFocusCues && enabled)
            {
                int inset = DarkControls.Scale(this, 2);
                var fr = Rectangle.Inflate(r, -inset, -inset);
                DarkControls.RoundedBox(g, fr, Math.Max(1f, radius - inset), fill, _accent ? DarkControls.OnAccent : Theme.Text);
            }

            var flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            var gf = DarkControls.IconFont(this, 10f);
            bool hasGlyph = !string.IsNullOrEmpty(_glyph);
            bool hasText = !string.IsNullOrEmpty(Text);
            var measure = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding; // no ellipsis: it would clip the measurement
            int textW = hasText ? TextRenderer.MeasureText(g, Text, Font, Size.Empty, measure).Width : 0;
            int glyphW = hasGlyph ? TextRenderer.MeasureText(g, _glyph, gf, Size.Empty, measure).Width : 0;
            int gap = hasGlyph && hasText ? DarkControls.Scale(this, 8) : 0;
            int total = Math.Min(Width - DarkControls.Scale(this, 8), textW + glyphW + gap);
            int x = (Width - total) / 2;
            if (hasGlyph)
            {
                var gr = new Rectangle(x, 0, glyphW, Height);
                TextRenderer.DrawText(g, _glyph, gf, gr, text, fill, flags | TextFormatFlags.HorizontalCenter);
                x += glyphW + gap;
            }
            if (hasText)
            {
                var tr = new Rectangle(x, 0, Math.Max(0, Width - x - DarkControls.Scale(this, 4)), Height);
                TextRenderer.DrawText(g, Text, Font, tr, text, fill, flags);
            }
        }
    }
}
